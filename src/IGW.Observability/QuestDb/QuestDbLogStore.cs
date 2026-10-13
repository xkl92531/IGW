using System.Text;
using System.Threading.Channels;

namespace IGW.Observability;

/// <summary>PLC-facing Append only reserves bounded memory. One writer fsyncs batches; a separate sender retries them.</summary>
public sealed class QuestDbLogStore : ILogRecordSink, IAsyncDisposable
{
    private sealed record Item(LogRecord? Record, Guid Id, string? PayloadLine, long Bytes);
    private readonly Channel<Item> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer, _sender;
    private readonly QuestDbClient _client;
    private readonly long _diskLimit;
    private readonly Func<CancellationToken, Task>? _prepare;
    private readonly Func<CancellationToken, Task>? _prepareHistory;
    private readonly object _spoolGate = new();
    private bool _spoolReady;
    public string? LastError { get; private set; }
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private long _queuedBytes, _diskBytes, _failed, _dropped;
    private int _queued, _durable, _disposed;
    private int _historyPending;
    private int _historyRejected;
    public int HistoryPending => Math.Max(0, Volatile.Read(ref _historyPending));
    public int HistoryRejected => Volatile.Read(ref _historyRejected);
    public long MaxHistoryBatchBytes => _diskLimit / 4;
    private const long MemoryLimit = 128L * 1024 * 1024;
    public string SpoolDirectory { get; }
    public long WriteFailures => Interlocked.Read(ref _failed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public int Pending => Math.Max(0, Volatile.Read(ref _queued)) + Math.Max(0, Volatile.Read(ref _durable));
    public SemaphoreSlim DatabaseGate => _databaseGate;

    public QuestDbLogStore(QuestDbClient client, string spoolDirectory, int queueCapacity = 20_000, long diskLimitBytes = 2L * 1024 * 1024 * 1024,
        Func<CancellationToken, Task>? prepare = null, Func<CancellationToken, Task>? prepareHistory = null)
    {
        _client = client;
        SpoolDirectory = Path.GetFullPath(spoolDirectory);
        _diskLimit = diskLimitBytes;
        _prepare = prepare;
        _prepareHistory = prepareHistory;
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(queueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _writer = Task.Run(WriteLoopAsync);
        _sender = Task.Run(SendLoopAsync);
    }

    private void EnsureSpool()
    {
        lock (_spoolGate)
        {
            if (_spoolReady) return;
            Directory.CreateDirectory(SpoolDirectory);
            foreach (string temp in Directory.EnumerateFiles(SpoolDirectory, "*.ilp.*.tmp"))
            {
                string name = Path.GetFileName(temp);
                int at = name.LastIndexOf(".ilp.", StringComparison.Ordinal);
                string original = name[..(at + 4)];
                byte[] bytes = new FileInfo(temp).Length <= 64L * 1024 * 1024 ? File.ReadAllBytes(temp) : [];
                int expected = Rows(original);
                if (expected > 0 && bytes.Length > 0 && bytes[^1] == (byte)'\n' && bytes.Count(b => b == (byte)'\n') == expected)
                {
                    string target = Path.Combine(SpoolDirectory, original);
                    if (File.Exists(target)) File.Delete(temp); else File.Move(temp, target);
                }
                else
                {
                    string rejected = (name.StartsWith("h-", StringComparison.Ordinal) ? "h-q-" : "q-") + Guid.NewGuid().ToString("N") + $"-{expected}.ilp.rejected";
                    File.Move(temp, Path.Combine(SpoolDirectory, rejected));
                }
            }
            long disk = 0;
            int pending = 0, history = 0, historyRejected = 0;
            long rejectedRows = 0;
            foreach (FileInfo file in new DirectoryInfo(SpoolDirectory).EnumerateFiles())
            {
                disk += file.Length; // Include unknown residue/temp files, so none can bypass the cap.
                if (file.Extension == ".ilp")
                { pending += Rows(file.Name); if (file.Name.StartsWith("h-", StringComparison.Ordinal)) history += Rows(file.Name); }
                if (file.Extension == ".rejected")
                { rejectedRows += Rows(Path.GetFileNameWithoutExtension(file.Name)); if (file.Name.StartsWith("h-", StringComparison.Ordinal)) historyRejected += Rows(Path.GetFileNameWithoutExtension(file.Name)); }
            }
            Interlocked.Exchange(ref _diskBytes, disk); Interlocked.Exchange(ref _durable, pending);
            Interlocked.Exchange(ref _historyPending, history); Interlocked.Exchange(ref _historyRejected, historyRejected);
            Interlocked.Add(ref _dropped, rejectedRows);
            _spoolReady = true;
        }
    }
    public Task InitializeSpoolAsync(CancellationToken cancellationToken) => Task.Run(EnsureSpool, cancellationToken);

    public void Append(LogRecord record) => Append(record, Guid.NewGuid());
    public bool Append(LogRecord record, Guid id)
    {
        long bytes = (record.Message.Length + record.Fields.Sum(field => field.Key.Length + (field.Value?.ToString()?.Length ?? 0))) * 4L + 2048;
        return Enqueue(new Item(record, id, null, bytes));
    }

    public bool AppendPayload(Guid id, long timestamp, string contentType, string content)
    {
        if (content.Length > 16 * 1024 * 1024) { Interlocked.Increment(ref _dropped); return false; }
        // JSON encoding here is bounded; neither disk nor database is touched by the caller.
        string line = QuestDbLogCodec.Payload(id, timestamp, contentType, content);
        return Enqueue(new Item(null, id, line, line.Length * 2L));
    }

    private bool Enqueue(Item item)
    {
        long reserved = Interlocked.Add(ref _queuedBytes, item.Bytes);
        Interlocked.Increment(ref _queued);
        if (reserved <= MemoryLimit && Volatile.Read(ref _disposed) == 0 && _queue.Writer.TryWrite(item)) return true;
        Interlocked.Add(ref _queuedBytes, -item.Bytes);
        Interlocked.Decrement(ref _queued);
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Flush to the durable local spool, even if the database is unavailable.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _queued) > 0) await Task.Delay(20, cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistHistoryAsync(string batch, CancellationToken cancellationToken)
    {
        EnsureSpool();
        byte[] bytes = Encoding.UTF8.GetBytes(batch);
        int count = bytes.Count(b => b == (byte)'\n');
        if (bytes.LongLength > _diskLimit / 4) throw new IOException("历史日志批次超过缓存容量。");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long reserved = Interlocked.Add(ref _diskBytes, bytes.Length);
            if (reserved <= _diskLimit / 4)
            {
                try
                {
                    PrivateFiles.AtomicWrite(Path.Combine(SpoolDirectory, $"h-{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}-{count}.ilp"), bytes);
                    Interlocked.Add(ref _durable, count);
                    Interlocked.Add(ref _historyPending, count);
                    return;
                }
                catch { Interlocked.Add(ref _diskBytes, -bytes.Length); throw; }
            }
            Interlocked.Add(ref _diskBytes, -bytes.Length);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            await Task.Delay(100).ConfigureAwait(false);
            var items = new List<Item>();
            var text = new StringBuilder();
            while (items.Count < 1000 && text.Length < 1024 * 1024 && _queue.Reader.TryRead(out Item? item))
            {
                items.Add(item);
                try { text.Append(item.PayloadLine ?? QuestDbLogCodec.Encode(item.Record!, item.Id)); }
                catch { Interlocked.Increment(ref _failed); Interlocked.Increment(ref _dropped); }
            }
            try
            {
                EnsureSpool();
                byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
                int count = bytes.Count(b => b == (byte)'\n');
                long disk = Interlocked.Add(ref _diskBytes, bytes.Length);
                if (disk > _diskLimit) { Interlocked.Add(ref _diskBytes, -bytes.Length); Interlocked.Add(ref _dropped, count); }
                else if (count > 0)
                {
                    try
                    {
                        string file = Path.Combine(SpoolDirectory, $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}-{count}.ilp");
                        PrivateFiles.AtomicWrite(file, bytes);
                        Interlocked.Add(ref _durable, count);
                    }
                    catch { Interlocked.Add(ref _diskBytes, -bytes.Length); throw; }
                }
            }
            catch (Exception error) { LastError = error.Message; Interlocked.Increment(ref _failed); Interlocked.Add(ref _dropped, items.Count); }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -items.Sum(item => item.Bytes));
                Interlocked.Add(ref _queued, -items.Count);
            }
        }
    }

    private async Task SendLoopAsync()
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            string? attemptedFile = null;
            try
            {
                EnsureSpool();
                string? file = NextBatch();
                await _databaseGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    if (_prepare is not null) await _prepare(_stop.Token).ConfigureAwait(false);
                    if (file is not null && Path.GetFileName(file).StartsWith("h-", StringComparison.Ordinal) && _prepareHistory is not null)
                        await _prepareHistory(_stop.Token).ConfigureAwait(false);
                }
                finally { _databaseGate.Release(); }
                if (file is null) { await Task.Delay(100, _stop.Token); continue; }
                attemptedFile = file;
                byte[] bytes = await File.ReadAllBytesAsync(file, _stop.Token).ConfigureAwait(false);
                await _databaseGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try { await _client.WriteAsync(bytes, _stop.Token).ConfigureAwait(false); }
                finally { _databaseGate.Release(); }
                File.Delete(file); // Lost reply/crash here replays the same UUID, so no duplicate rows.
                Interlocked.Add(ref _diskBytes, -bytes.Length);
                Interlocked.Add(ref _durable, -Rows(file));
                if (Path.GetFileName(file).StartsWith("h-", StringComparison.Ordinal)) Interlocked.Add(ref _historyPending, -Rows(file));
                failures = 0;
                LastError = null;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (QuestDbBackpressureException) { await DelayAsync(250).ConfigureAwait(false); }
            catch (QuestDbWriteException error) when (error.Permanent)
            {
                // Preserve malformed batches for diagnosis, but do not let one poison batch block good logs.
                string? file = attemptedFile;
                if (file is not null)
                {
                    try
                    {
                        if (Rows(file) > 1) await SplitBatchAsync(file).ConfigureAwait(false);
                        else
                        {
                            File.Move(file, file + ".rejected");
                            Interlocked.Add(ref _durable, -Rows(file));
                            Interlocked.Add(ref _dropped, Rows(file));
                            if (Path.GetFileName(file).StartsWith("h-", StringComparison.Ordinal))
                            { Interlocked.Add(ref _historyPending, -Rows(file)); Interlocked.Add(ref _historyRejected, Rows(file)); }
                        }
                        // Rejected files count against the disk cap until the operator removes them.
                    }
                    catch { }
                }
                Interlocked.Increment(ref _failed);
                await DelayAsync(10).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LastError = error.Message;
                Interlocked.Increment(ref _failed);
                failures = Math.Min(failures + 1, 5);
                await DelayAsync(Math.Min(5000, 100 << failures)).ConfigureAwait(false);
            }
        }
    }

    private async Task DelayAsync(int milliseconds)
    {
        try { await Task.Delay(milliseconds, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private string? NextBatch() => Directory.EnumerateFiles(SpoolDirectory, "*.ilp")
        .OrderBy(file => Path.GetFileName(file).StartsWith("h-", StringComparison.Ordinal)).ThenBy(file => file, StringComparer.Ordinal).FirstOrDefault();

    private async Task SplitBatchAsync(string file)
    {
        string[] lines = (await File.ReadAllTextAsync(file, _stop.Token).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool history = Path.GetFileName(file).StartsWith("h-", StringComparison.Ordinal);
        int split = lines.Length / 2;
        foreach (var (start, end) in new[] { (0, split), (split, lines.Length) })
        {
            int count = end - start;
            string name = (history ? "h-s-" : "s-") + QuestDbLogCodec.HistoricalId(Path.GetFileName(file), start).ToString("N") + $"-{count}.ilp";
            string child = Path.Combine(SpoolDirectory, name);
            if (File.Exists(child)) continue; // A crash during splitting retries the same child batch.
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines[start..end]) + "\n");
            PrivateFiles.AtomicWrite(child, bytes);
            Interlocked.Add(ref _diskBytes, bytes.Length);
            Interlocked.Add(ref _durable, count);
            if (history) Interlocked.Add(ref _historyPending, count);
        }
        long length = new FileInfo(file).Length;
        File.Delete(file);
        Interlocked.Add(ref _diskBytes, -length);
        Interlocked.Add(ref _durable, -Rows(file));
        if (history) Interlocked.Add(ref _historyPending, -Rows(file));
    }

    private static int Rows(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).Split('-')[^1], out int count) ? Math.Max(0, count) : 0;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        _stop.Cancel();
        await _sender.ConfigureAwait(false);
        _stop.Dispose();
        _databaseGate.Dispose();
    }
}
