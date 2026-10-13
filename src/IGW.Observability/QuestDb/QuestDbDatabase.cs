using System.Text.Json;

namespace IGW.Observability;

public sealed class QuestDbDatabase : IAsyncDisposable
{
    public QuestDbClient Client { get; }
    public QuestDbProcess Process { get; }
    public QuestDbLogStore Store { get; }
    public QuestDbLogQuery Query { get; }
    public QuestDbPayloadStore Payloads { get; }
    public QuestDbHistoryImporter History { get; }
    public bool Suspended { get; private set; }
    public long WalLag { get; private set; }
    private DateTimeOffset _lastWalCheck;
    private DateTimeOffset _lastSizeCheck;
    private long _storageBytes;
    private int _checkingSize;
    private readonly Func<ObservabilitySettings> _settings;
    private readonly SemaphoreSlim _walGate = new(1, 1);

    public QuestDbDatabase(ObservabilitySettings settings, string dataDirectory, LogSegmentStore legacy, string? runtimeDirectory = null)
    {
        _settings = () => ObservabilitySettings.Load(dataDirectory) ?? settings;
        Client = new QuestDbClient(new Uri($"http://127.0.0.1:{settings.Port}"), settings.Username, settings.Password);
        Process = new QuestDbProcess(settings, runtimeDirectory);
        // Cache stays in the persistent gateway data directory, independent of log directory moves.
        Store = new QuestDbLogStore(Client, Path.Combine(dataDirectory, "questdb-spool"), prepare: PrepareAsync,
            prepareHistory: _ => WalLag > 100 ? Task.FromException(new QuestDbBackpressureException()) : Task.CompletedTask);
        Query = new QuestDbLogQuery(Client);
        Payloads = new QuestDbPayloadStore(Client, Store);
        History = new QuestDbHistoryImporter(legacy, Store, dataDirectory, settings.PreviousStorageDirectories, settings.StorageDirectory, settings.RetentionDays);
    }

    public async Task CheckWalAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastWalCheck < TimeSpan.FromSeconds(5)) return;
        if (!Process.Ready) return;
        await _walGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _lastWalCheck < TimeSpan.FromSeconds(5)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using JsonDocument result = await Client.QueryAsync("select name,suspended,writerTxn,sequencerTxn from wal_tables() where name in ('IGW_logs','IGW_payloads')", timeout.Token);
            Suspended = false; WalLag = 0;
            foreach (JsonElement row in result.RootElement.GetProperty("dataset").EnumerateArray())
            { Suspended |= row[1].GetBoolean(); WalLag += Math.Max(0, row[3].GetInt64() - row[2].GetInt64()); }
            _lastWalCheck = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { Suspended = true; }
        finally { _walGate.Release(); }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await Process.EnsureReadyAsync(Client, _settings().RetentionDays, cancellationToken).ConfigureAwait(false);
        await CheckWalAsync(cancellationToken).ConfigureAwait(false);
        if (Suspended) throw new IOException("QuestDB WAL 暂停或状态检查失败，日志暂存到网关缓存。");
    }

    public long StorageBytes()
    {
        if (DateTimeOffset.UtcNow - _lastSizeCheck > TimeSpan.FromMinutes(1) && Interlocked.CompareExchange(ref _checkingSize, 1, 0) == 0)
            _ = Task.Run(() =>
            {
                try
                {
                    long bytes = Directory.Exists(Process.Root) ? Directory.EnumerateFiles(Process.Root, "*", SearchOption.AllDirectories).Sum(file =>
                    { try { return new FileInfo(file).Length; } catch { return 0L; } }) : 0;
                    Interlocked.Exchange(ref _storageBytes, bytes);
                }
                catch { }
                finally { _lastSizeCheck = DateTimeOffset.UtcNow; Volatile.Write(ref _checkingSize, 0); }
            });
        return Interlocked.Read(ref _storageBytes);
    }

    public async ValueTask DisposeAsync()
    {
        await History.DisposeAsync();
        await Store.DisposeAsync();
        await Process.DisposeAsync();
        Client.Dispose();
        _walGate.Dispose();
    }
}
