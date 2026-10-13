using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace IGW.Observability;

/// <summary>一个日志分段的索引：时间范围、各级别条数、常用字段出现过的值(超过上限记为溢出，不能据此跳过)。</summary>
public sealed class LogSegmentIndex
{
    public const int MaxValuesPerField = 256;
    public static readonly string[] IndexedFields =
        ["igw_category", "igw_channel", "igw_device", "igw_db", "igw_task_id", "igw_module", "igw_feature"];

    public long FromMicros { get; set; } = long.MaxValue;
    public long ToMicros { get; set; } = long.MinValue;
    public int Count { get; set; }
    public int[] Levels { get; set; } = new int[6];
    public Dictionary<string, HashSet<string>> Values { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Overflow { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// 每分钟各级别条数(键为 Unix 分钟数，值按级别 0..5)。只按时间/分类/级别查询时，完整落在查询范围内的已封存分段
    /// 直接用它出总数和趋势图(趋势图的桶至少 1 分钟)，不用解压整段。旧版本的 .idx 没有这一项(为 null)，照旧扫描。
    /// </summary>
    public Dictionary<long, int[]>? Minutes { get; set; }

    public const long MicrosPerMinute = 60_000_000;

    public void Add(LogRecord record)
    {
        FromMicros = Math.Min(FromMicros, record.TimestampMicros);
        ToMicros = Math.Max(ToMicros, record.TimestampMicros);
        Count++;
        int level = Math.Clamp(record.Level, 0, 5);
        Levels[level]++;
        // 只在第一条时建：旧索引(Minutes 为 null)上再追加也不会得到一份残缺的分钟计数
        if (Count == 1) Minutes ??= new Dictionary<long, int[]>();
        if (Minutes is not null)
        {
            long minute = MinuteOf(record.TimestampMicros);
            if (!Minutes.TryGetValue(minute, out int[]? counts)) Minutes[minute] = counts = new int[6];
            counts[level]++;
        }
        foreach (string field in IndexedFields)
        {
            if (Overflow.Contains(field)) continue;
            string value = field == "igw_category" ? record.Category
                : record.Fields.TryGetValue(field, out object? raw) ? Convert.ToString(raw) ?? "" : "";
            if (!Values.TryGetValue(field, out HashSet<string>? set))
                Values[field] = set = new HashSet<string>(StringComparer.Ordinal);
            if (set.Count >= MaxValuesPerField && !set.Contains(value)) { Overflow.Add(field); Values.Remove(field); }
            else set.Add(value);
        }
    }

    /// <summary>该分段是否可能含有 field=value 的记录。</summary>
    public bool MayContain(string field, string value) =>
        Overflow.Contains(field) || !Values.TryGetValue(field, out HashSet<string>? set) || set.Contains(value);

    /// <summary>该分段的记录是否全部是 field=value(索引没溢出、只出现过这一个值)。</summary>
    public bool OnlyContains(string field, string value) =>
        !Overflow.Contains(field) && Values.TryGetValue(field, out HashSet<string>? set) && set.Count == 1 && set.Contains(value);

    internal static long MinuteOf(long micros) =>
        micros >= 0 ? micros / MicrosPerMinute : (micros + 1) / MicrosPerMinute - 1;

    /// <summary>分钟计数和总条数对不上(文件被改过、写了一半)时不能拿来代替扫描。</summary>
    internal bool MinutesConsistent()
    {
        if (Minutes is null) return false;
        long sum = 0;
        foreach (int[] counts in Minutes.Values)
        {
            if (counts is not { Length: 6 }) return false;
            foreach (int count in counts) sum += count;
        }
        return sum == Count;
    }
}

/// <summary>
/// 查询时看到的一个分段快照。Active 表示正在写入的分段，只读到 Length 为止。
/// Category 是分段所在的分类文件夹；旧版本各分类混在一起写的分段为 null。
/// </summary>
public sealed record LogSegmentView(string Path, bool Compressed, long Length, LogSegmentIndex Index, bool Active, string? Category = null);

/// <summary>
/// 网关自带的日志存储：按时间分段、压缩保存，只给常用字段建小索引，关键词靠并行扫描压缩块。
/// 目录结构：{存储目录}/igw-logs/{分类}/yyyyMMdd/HHmmss-xxxx.jsonl(日期、时间按网关本地时间；正在写) → .seg(Brotli 压缩) + .idx(索引)。
/// 分类文件夹和日志中心左侧的分类一一对应(采集 collection、接口 interface、状态 state、性能 performance、系统 system)：
/// 现场可以直接按文件夹找、拷某一类日志，按分类查询时也只读对应文件夹。各分类各写各的分段，时间上互相重叠，
/// 查询时按时间合并(见 LogQueryEngine)。旧版本写在 igw-logs/yyyyMMdd/ 下各分类混在一起的分段照常可查，到保留期自然清掉。
/// 进程崩溃时正在写的 .jsonl 仍在磁盘上，下次启动自动补做压缩和索引。
/// </summary>
public sealed class LogSegmentStore : IAsyncDisposable, ILogRecordSink
{
    public const string DirectoryName = "igw-logs";
    /// <summary>分类文件夹；不认识的分类写到 system。</summary>
    public static readonly string[] CategoryFolders = ["collection", "interface", "state", "performance", "system"];
    public static readonly TimeSpan SegmentSpan = TimeSpan.FromMinutes(10);
    public const long SegmentMaxBytes = 32L * 1024 * 1024;
    private const int QueueCapacity = 200_000;
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Channel<LogRecord> _queue = Channel.CreateBounded<LogRecord>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly List<LogSegmentView> _segments = [];
    private readonly Channel<LogSegmentView> _compressQueue = Channel.CreateUnbounded<LogSegmentView>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _time;
    private readonly Task _writer;
    private readonly Task _compressor;
    private readonly Task _maintenance;
    private readonly bool _readOnly;
    public Task Recovery { get; }
    private readonly List<string> _recoveryErrors = [];
    public IReadOnlyList<string> RecoveryErrors { get { lock (_gate) return _recoveryErrors.ToArray(); } }
    /// <summary>各分类正在写入的分段(分类文件夹 → 分段)。</summary>
    private readonly Dictionary<string, ActiveSegment> _active = new(StringComparer.Ordinal);
    private string _root;
    private int _retentionDays;
    private long _dropped;
    private long _writeFailures;
    private long _pending;
    private int _disposed;
    private int _compressing;

    public LogSegmentStore(string storageDirectory, int retentionDays, TimeProvider? time = null, bool readOnly = false)
    {
        _time = time ?? TimeProvider.System;
        _root = RootFor(storageDirectory);
        _retentionDays = Math.Max(1, retentionDays);
        _readOnly = readOnly;
        if (readOnly)
        {
            Recovery = Task.Run(() => { if (Directory.Exists(_root)) lock (_gate) Recover(); });
            _writer = _compressor = _maintenance = Task.CompletedTask;
        }
        else
        {
            Directory.CreateDirectory(_root); Recover(); Recovery = Task.CompletedTask;
            _writer = Task.Run(WriteLoopAsync); _compressor = Task.Run(CompressLoopAsync); _maintenance = Task.Run(MaintenanceLoopAsync);
        }
    }

    public string Root { get { lock (_gate) return _root; } }
    public long Dropped => Interlocked.Read(ref _dropped);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public int Pending => (int)Math.Max(0, Interlocked.Read(ref _pending));
    public int RetentionDays { get => Volatile.Read(ref _retentionDays); set => Volatile.Write(ref _retentionDays, Math.Max(1, value)); }

    public static string RootFor(string storageDirectory) =>
        Path.Combine(Path.GetFullPath(storageDirectory), DirectoryName);

    /// <summary>写入一条日志；队列满时丢弃并计数，绝不阻塞调用方。</summary>
    public void Append(LogRecord record)
    {
        if (_readOnly) { Interlocked.Increment(ref _dropped); return; }
        if (_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _pending);
        else Interlocked.Increment(ref _dropped);
    }

    /// <summary>等待已入队的日志写到文件(测试与关闭时用)。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Interlocked.Read(ref _pending) > 0)
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        lock (_gate) foreach (ActiveSegment active in _active.Values) active.Flush(toDisk: false);
    }

    /// <summary>时间范围内的分段，按时间从新到旧；正在写入的分段排在最前。</summary>
    public IReadOnlyList<LogSegmentView> Snapshot(long fromMicros, long toMicros)
    {
        lock (_gate)
        {
            var result = new List<LogSegmentView>(_segments.Count + _active.Count);
            foreach (ActiveSegment active in _active.Values)
            {
                if (active.Index.Count == 0) continue;
                active.Flush(toDisk: false);
                if (active.Index.FromMicros < toMicros && active.Index.ToMicros >= fromMicros)
                    result.Add(new LogSegmentView(active.Path, false, active.Length, active.Index, Active: true, active.Category));
            }
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                LogSegmentView segment = _segments[i];
                if (segment.Index.FromMicros < toMicros && segment.Index.ToMicros >= fromMicros) result.Add(segment);
            }
            return result;
        }
    }

    /// <summary>读出一个分段的原始内容(压缩的会解压)。分段已被清理时返回空。解压时按块检查取消，放弃的查询不再占着磁盘和 CPU。</summary>
    public static byte[] ReadRaw(LogSegmentView segment, CancellationToken cancellationToken = default, bool requireExists = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var file = new FileStream(segment.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (!segment.Compressed)
            {
                long length = Math.Min(segment.Length, file.Length);
                byte[] buffer = new byte[length];
                file.ReadExactly(buffer);
                return buffer;
            }
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var output = new MemoryStream(capacity: (int)Math.Min(int.MaxValue, file.Length * 8));
            byte[] chunk = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                for (int read; (read = brotli.Read(chunk)) > 0;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(chunk, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
            return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // 查询拿到快照后，后台压缩可能刚把 .jsonl 换成了 .seg。
            string compressed = System.IO.Path.ChangeExtension(segment.Path, ".seg");
            if (!segment.Compressed && File.Exists(compressed))
                return ReadRaw(segment with { Path = compressed, Compressed = true }, cancellationToken, requireExists);
            if (requireExists) throw;
            return [];
        }
    }

    /// <summary>已封存分段的计数结果缓存(见 LogQueryEngine)；保留期清理删掉分段时一并清掉。</summary>
    internal LogCountCache CountCache { get; } = new();

    /// <summary>测试用：每次查询读分段文件时回调，用来确认哪些分段真的被读了。</summary>
    internal Action<LogSegmentView>? OnRead { get; set; }

    /// <summary>查询读分段都走这里(便于测试统计读了哪些分段)。</summary>
    internal byte[] Read(LogSegmentView segment, CancellationToken cancellationToken)
    {
        OnRead?.Invoke(segment);
        return ReadRaw(segment, cancellationToken);
    }

    /// <summary>
    /// 切换存储目录：先在后台复制历史分段，再短暂停写，收尾复制后切到新目录。旧目录保留不删。
    /// </summary>
    public async Task SwitchDirectoryAsync(string storageDirectory, LogStorageChange change, CancellationToken cancellationToken)
    {
        await Recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        string target = RootFor(storageDirectory);
        await change.CopyHistoryAsync(cancellationToken).ConfigureAwait(false);   // 第一遍：写入不中断
        lock (_gate)
        {
            SealAllLocked();
            change.CopyHistoryAsync(cancellationToken).GetAwaiter().GetResult();  // 第二遍：只补新增/变化的文件
            _root = target;
            _segments.Clear();
            Recover();
        }
    }

    /// <summary>
    /// 日志根目录下的分段相关文件，不进入报文子目录。报文目录里可能有几十万到几百万个文件，
    /// 启动时逐个扫一遍会拖慢网关启动，Windows 服务 30 秒内没起来就被判启动失败(现场升级后看起来就是装不上)；
    /// 那里的 .tmp 是报文存储正在进行的原子写，本来也不归这里清理。
    /// </summary>
    private IEnumerable<string> SegmentFiles(string pattern)
    {
        foreach (string file in Directory.EnumerateFiles(_root, pattern, SearchOption.TopDirectoryOnly))
            yield return file;
        foreach (string directory in Directory.EnumerateDirectories(_root))
        {
            if (string.Equals(Path.GetFileName(directory), LogPayloadStore.DirectoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(directory), "questdb", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
                yield return file;
        }
    }

    private void Recover()
    {
        if (_readOnly) { RecoverReadOnly(); return; }
        foreach (string temporary in _readOnly ? [] : SegmentFiles("*.tmp"))
            TryDelete(temporary);
        var recovered = new List<LogSegmentView>();
        foreach (string data in SegmentFiles("*.seg"))
        {
            if (_readOnly) _stopping.Token.ThrowIfCancellationRequested();
            LogSegmentIndex? index = LoadIndex(Path.ChangeExtension(data, ".idx"));
            if (index is null)
            {
                var view = new LogSegmentView(data, true, new FileInfo(data).Length, new LogSegmentIndex(), false);
                index = BuildIndex(ReadRaw(view, _stopping.Token));
                if (!_readOnly) SaveIndex(Path.ChangeExtension(data, ".idx"), index);
            }
            if (!_readOnly) TryDelete(Path.ChangeExtension(data, ".jsonl"));
            if (index.Count > 0) recovered.Add(new LogSegmentView(data, true, new FileInfo(data).Length, index, false, CategoryOf(data)));
        }
        foreach (string data in SegmentFiles("*.jsonl"))
        {
            if (_readOnly) _stopping.Token.ThrowIfCancellationRequested();
            if (File.Exists(Path.ChangeExtension(data, ".seg"))) continue;
            long length = new FileInfo(data).Length;
            var raw = new LogSegmentView(data, false, length, new LogSegmentIndex(), false, CategoryOf(data));
            LogSegmentIndex index = BuildIndex(ReadRaw(raw, _stopping.Token));
            if (index.Count == 0) { if (!_readOnly) TryDelete(data); continue; }
            var view = raw with { Index = index };
            recovered.Add(view);
            if (!_readOnly) _compressQueue.Writer.TryWrite(view);
        }
        _segments.AddRange(recovered.OrderBy(segment => segment.Index.FromMicros));
    }

    private void RecoverReadOnly()
    {
        _recoveryErrors.Clear();
        var recovered = new List<LogSegmentView>();
        foreach (string path in SegmentFiles("*.seg").Concat(SegmentFiles("*.jsonl")))
        {
            _stopping.Token.ThrowIfCancellationRequested();
            bool compressed = path.EndsWith(".seg", StringComparison.OrdinalIgnoreCase);
            if (!compressed && File.Exists(Path.ChangeExtension(path, ".seg"))) continue;
            try
            {
                var view = new LogSegmentView(path, compressed, new FileInfo(path).Length, new LogSegmentIndex(), false, CategoryOf(path));
                LogSegmentIndex? index = compressed ? LoadIndex(Path.ChangeExtension(path, ".idx")) : null;
                index ??= BuildIndex(ReadRaw(view, _stopping.Token, requireExists: true));
                if (index.Count == 0 && view.Length > 0) throw new InvalidDataException("旧分段没有可识别的记录。");
                if (index.Count > 0) recovered.Add(view with { Index = index });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            { _recoveryErrors.Add(Path.GetFileName(path) + "：" + error.Message); }
        }
        _segments.AddRange(recovered.OrderBy(segment => segment.Index.FromMicros));
    }

    internal void UseExistingDirectory(string storageDirectory)
    {
        lock (_gate) { SealAllLocked(); _root = RootFor(storageDirectory); _segments.Clear(); Recover(); }
    }

    /// <summary>分段文件所在的分类文件夹：{根}/{分类}/yyyyMMdd/文件；旧版本 {根}/yyyyMMdd/文件 返回 null。</summary>
    private string? CategoryOf(string file)
    {
        string? folder = Path.GetDirectoryName(Path.GetDirectoryName(file));
        if (folder is null || !string.Equals(Path.GetDirectoryName(folder), _root, PathComparison)) return null;
        string name = Path.GetFileName(folder);
        return CategoryFolders.FirstOrDefault(category => category.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    internal static string FolderFor(string category) =>
        CategoryFolders.FirstOrDefault(folder => folder.Equals(category, StringComparison.OrdinalIgnoreCase)) ?? "system";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task WriteLoopAsync()
    {
        ChannelReader<LogRecord> reader = _queue.Reader;
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        long lastDiskFlush = _time.GetTimestamp();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                lock (_gate)
                {
                    int written = 0;
                    while (written < 5_000 && reader.TryRead(out LogRecord? record))
                    {
                        written++;
                        try
                        {
                            ActiveSegment active = EnsureActiveLocked(FolderFor(record.Category), record.TimestampMicros);
                            buffer.ResetWrittenCount();
                            LogRecordCodec.Write(buffer, record);
                            active.Write(buffer.WrittenSpan, record);
                            if (active.Length >= SegmentMaxBytes) SealLocked(active.Category);
                        }
                        catch (Exception) { Interlocked.Increment(ref _writeFailures); }
                        finally { Interlocked.Decrement(ref _pending); }
                    }
                    bool toDisk = _time.GetElapsedTime(lastDiskFlush) >= TimeSpan.FromSeconds(2);
                    foreach (ActiveSegment active in _active.Values)
                        try { active.Flush(toDisk); } catch (Exception) { Interlocked.Increment(ref _writeFailures); }
                    if (toDisk) lastDiskFlush = _time.GetTimestamp();
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                try { SealAllLocked(); } catch (Exception) { Interlocked.Increment(ref _writeFailures); }
            }
            _compressQueue.Writer.TryComplete();
        }
    }

    private ActiveSegment EnsureActiveLocked(string category, long timestampMicros)
    {
        long spanMicros = SegmentSpan.Ticks / 10;
        long bucket = timestampMicros - Math.Abs(timestampMicros % spanMicros);
        _active.TryGetValue(category, out ActiveSegment? active);
        // 只向后换段：多线程在段边界附近的记录可能晚到几毫秒，写进当前段即可(索引时间范围随之扩展)，
        // 不能因此来回封段产生大量碎片。
        if (active is not null && timestampMicros >= active.BucketMicros + spanMicros && active.Index.Count > 0)
        {
            SealLocked(category);
            active = null;
        }
        if (active is not null) return active;
        // 日期文件夹和文件名按网关本地时间：现场按本地日期找文件(北京时间凌晨的日志不会落到前一天的文件夹)。
        // 查询、保留期都看分段索引里的 UTC 时间，不依赖文件夹名。
        DateTimeOffset start = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(bucket * 10), _time.LocalTimeZone);
        string directory = Path.Combine(_root, category, start.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        string name = $"{start:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.jsonl";
        return _active[category] = new ActiveSegment(Path.Combine(directory, name), bucket, category);
    }

    private void SealLocked(string category)
    {
        if (!_active.Remove(category, out ActiveSegment? active)) return;
        active.Dispose();
        if (active.Index.Count == 0) { TryDelete(active.Path); return; }
        var view = new LogSegmentView(active.Path, false, active.Length, active.Index, false, category);
        InsertSegmentLocked(view);
        _compressQueue.Writer.TryWrite(view);
    }

    private void SealAllLocked()
    {
        foreach (string category in _active.Keys.ToArray()) SealLocked(category);
    }

    private void InsertSegmentLocked(LogSegmentView view)
    {
        int at = _segments.FindLastIndex(segment => segment.Index.FromMicros <= view.Index.FromMicros);
        _segments.Insert(at + 1, view);
    }

    private async Task CompressLoopAsync()
    {
        await foreach (LogSegmentView segment in _compressQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Increment(ref _compressing);
            try { Compress(segment); }
            catch (Exception) { Interlocked.Increment(ref _writeFailures); }
            finally { Interlocked.Decrement(ref _compressing); }
        }
    }

    private void Compress(LogSegmentView segment)
    {
        if (!File.Exists(segment.Path)) return;
        string data = Path.ChangeExtension(segment.Path, ".seg");
        string index = Path.ChangeExtension(segment.Path, ".idx");
        string dataTemp = data + ".tmp", indexTemp = index + ".tmp";
        using (var input = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(dataTemp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
            input.CopyTo(brotli);
        File.WriteAllBytes(indexTemp, JsonSerializer.SerializeToUtf8Bytes(segment.Index, IndexJson));
        File.Move(indexTemp, index, overwrite: true);
        File.Move(dataTemp, data, overwrite: true);
        var compressed = segment with { Path = data, Compressed = true, Length = new FileInfo(data).Length };
        lock (_gate)
        {
            int at = _segments.FindIndex(item => item.Path == segment.Path);
            if (at >= 0) _segments[at] = compressed;
        }
        TryDelete(segment.Path);
    }

    private async Task MaintenanceLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
        DateTimeOffset nextCleanup = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                long nowMicros = LogRecordCodec.ToMicros(_time.GetUtcNow());
                lock (_gate)
                {
                    // 安静时段也要按时封段，让压缩和保留期清理能处理到最近的日志。
                    foreach (ActiveSegment active in _active.Values.ToArray())
                        if (active.Index.Count > 0 && nowMicros >= active.BucketMicros + SegmentSpan.Ticks / 10)
                            SealLocked(active.Category);
                }
                if (_time.GetUtcNow() >= nextCleanup)
                {
                    nextCleanup = _time.GetUtcNow().AddHours(1);
                    try { CleanupExpired(); } catch (Exception) { Interlocked.Increment(ref _writeFailures); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>删除超过保留期的分段；磁盘剩余空间不足 1GB 时再从最旧的开始删(保留最近一天)。</summary>
    public void CleanupExpired()
    {
        long cutoff = LogRecordCodec.ToMicros(_time.GetUtcNow().AddDays(-RetentionDays));
        long keepRecent = LogRecordCodec.ToMicros(_time.GetUtcNow().AddDays(-1));
        List<LogSegmentView> doomed;
        string root;
        lock (_gate)
        {
            root = _root;
            doomed = _segments.Where(segment => segment.Index.ToMicros < cutoff).ToList();
            long free = FreeBytes(root);
            if (free is >= 0 and < 1L << 30)
            {
                foreach (LogSegmentView segment in _segments.Where(item => item.Index.ToMicros < keepRecent && !doomed.Contains(item)))
                {
                    if (free >= 1L << 30) break;
                    doomed.Add(segment);
                    free += segment.Length;
                }
            }
            foreach (LogSegmentView segment in doomed) _segments.Remove(segment);
        }
        CountCache.Remove(doomed.Select(segment => LogCountCache.SegmentId(segment.Path)));
        foreach (LogSegmentView segment in doomed)
        {
            TryDelete(segment.Path);
            TryDelete(Path.ChangeExtension(segment.Path, ".idx"));
            TryDelete(Path.ChangeExtension(segment.Path, ".jsonl"));
        }
        // 删掉清空的日期文件夹：分类文件夹下的 yyyyMMdd，以及旧版本直接建在根下的 yyyyMMdd。分类文件夹本身保留。
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (CategoryFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                foreach (string day in Directory.EnumerateDirectories(folder)) DeleteIfEmpty(day);
            else if (!name.Equals(LogPayloadStore.DirectoryName, StringComparison.OrdinalIgnoreCase))
                DeleteIfEmpty(folder);
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public long TotalBytes()
    {
        if (_readOnly && !Recovery.IsCompleted) return 0;
        lock (_gate) return _segments.Sum(segment => segment.Length) + _active.Values.Sum(active => active.Length);
    }

    private static long FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch { return -1; }
    }

    internal static LogSegmentIndex BuildIndex(byte[] raw)
    {
        var index = new LogSegmentIndex();
        LogRecordCodec.ForEachLine(raw, (line, _) =>
        {
            if (LogRecordCodec.Parse(line) is { } record) index.Add(record);
            return true;
        });
        return index;
    }

    private static LogSegmentIndex? LoadIndex(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            LogSegmentIndex? index = JsonSerializer.Deserialize<LogSegmentIndex>(File.ReadAllBytes(path), IndexJson);
            if (index is not null && !index.MinutesConsistent()) index.Minutes = null;   // 旧版本没有分钟计数：查询时照旧扫描
            return index;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static void SaveIndex(string path, LogSegmentIndex index)
    {
        try { File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(index, IndexJson)); }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>立即封存正在写入的分段(测试和切换目录用)。</summary>
    internal void SealNow()
    {
        lock (_gate) SealAllLocked();
    }

    /// <summary>等待已封存分段全部压缩完成(测试用)。</summary>
    internal async Task WaitForCompressionAsync()
    {
        for (int i = 0; i < 500; i++)
        {
            bool pending;
            lock (_gate) pending = _segments.Any(segment => !segment.Compressed);
            if (!pending && Volatile.Read(ref _compressing) == 0) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        try { await Recovery.ConfigureAwait(false); } catch { }
        _queue.Writer.TryComplete();
        _stopping.Cancel();
        try { await _writer.ConfigureAwait(false); } catch { }
        try { await _maintenance.ConfigureAwait(false); } catch { }
        try { await _compressor.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        _stopping.Dispose();
    }

    private sealed class ActiveSegment : IDisposable
    {
        private readonly FileStream _stream;

        public ActiveSegment(string path, long bucketMicros, string category)
        {
            Path = path;
            BucketMicros = bucketMicros;
            Category = category;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 16);
        }

        public string Path { get; }
        public long BucketMicros { get; }
        public string Category { get; }
        public LogSegmentIndex Index { get; } = new();
        public long Length { get; private set; }

        public void Write(ReadOnlySpan<byte> line, LogRecord record)
        {
            _stream.Write(line);
            Length += line.Length;
            Index.Add(record);
        }

        public void Flush(bool toDisk) => _stream.Flush(toDisk);

        public void Dispose()
        {
            try { _stream.Flush(flushToDisk: true); } catch (IOException) { }
            _stream.Dispose();
        }
    }
}
