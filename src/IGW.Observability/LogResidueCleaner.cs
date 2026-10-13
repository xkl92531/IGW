using System.Text.Json;
using System.Text.Json.Serialization;

namespace IGW.Observability;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogCleanupState { Idle, Running, Completed, Failed }

/// <summary>
/// 一处可清理的残留：Kind 是给操作人员看的类别。FilePattern 为空时清掉整个目录，
/// 否则只清目录里匹配的文件(旧版文本日志和用户自己的文件可能放在同一个文件夹里)。
/// </summary>
public sealed record LogResidue(string Path, string Kind, string? FilePattern = null);

/// <summary>开始清理的请求：Paths 为勾选的残留，不传则清理全部。</summary>
public sealed record LogCleanupRequest(IReadOnlyList<string>? Paths);

public sealed record LogCleanupStatus(
    LogCleanupState State,
    IReadOnlyList<LogResidue> Residues,
    long DeletedFiles,
    long DeletedBytes,
    IReadOnlyList<string> Skipped,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool Scanning = false,
    string? BlockedReason = null);

/// <summary>
/// 一键清理网关已经不再写入的日志残留，全程不停服务：
/// <list type="bullet">
/// <item>旧版本写在数据目录 log-payloads 下的接口报文(旧版不随日志目录走，现场 C 盘堆了几百万个文件)；</item>
/// <item>更换日志目录后留在原位置的旧日志(只删网关自己建的 igw-logs 子目录，所选文件夹里的其它内容不动)；</item>
/// <item>强制重装脚本挪出来的 IGW-大目录-时间戳；</item>
/// <item>更早版本的文本日志(数据目录 logs 下的 igw-yyyyMMdd.log)和默认日志目录；</item>
/// <item>打开清理窗口时在各本地硬盘上找到的网关旧日志(装过好几个盘、卸载重装后没有记录的，见 <see cref="LogResidueScanner"/>)。</item>
/// </list>
/// 先在同一目录下改名为 IGW-清理中-编号(瞬间完成，和正在写的新日志互不干扰)，再在后台多线程删除；
/// 资源管理器删几百万个文件会先清点、进回收站，界面像卡死一样。待删目录记在数据目录的
/// log-cleanup.json 里，删到一半网关重启会接着删。当前日志目录、报文目录和数据目录本身永远不会被列为残留。
/// </summary>
public sealed class LogResidueCleaner
{
    public const string StateFileName = "log-cleanup.json";
    private const string TrashPrefix = "IGW-清理中-";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dataDirectory;
    private readonly Func<ObservabilitySettings> _settings;
    private readonly LogPayloadStore _payloads;
    private readonly object _gate = new();
    private LogCleanupState _state = LogCleanupState.Idle;
    private long _deletedFiles, _deletedBytes;
    private List<string> _skipped = [];
    private string? _error;
    private DateTimeOffset? _startedAt, _finishedAt;
    private Task _run = Task.CompletedTask;
    private readonly Func<IReadOnlyList<string>> _scanRoots;
    private readonly Func<string?> _blockedReason;
    private IReadOnlyList<LogResidue> _discovered = [];
    private Task _scan = Task.CompletedTask;
    private bool _scanning;

    public LogResidueCleaner(string dataDirectory, Func<ObservabilitySettings> settings, LogPayloadStore payloads,
        Func<IReadOnlyList<string>>? scanRoots = null, Func<string?>? blockedReason = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _settings = settings;
        _payloads = payloads;
        _scanRoots = scanRoots ?? LogResidueScanner.FixedDriveRoots;
        _blockedReason = blockedReason ?? (() => null);
    }

    public LogCleanupStatus Status
    {
        get
        {
            lock (_gate)
                return new LogCleanupStatus(_state, _state == LogCleanupState.Running ? [] : Find(),
                    Interlocked.Read(ref _deletedFiles), Interlocked.Read(ref _deletedBytes),
                    _skipped.ToArray(), _error, _startedAt, _finishedAt, _scanning, _blockedReason());
        }
    }

    /// <summary>后台任务(测试等它结束用)。</summary>
    public Task Completion { get { lock (_gate) return _run; } }

    /// <summary>各盘查找任务(测试等它结束用)。</summary>
    public Task ScanCompletion { get { lock (_gate) return _scan; } }

    /// <summary>
    /// 在各本地硬盘上查找网关以前留下的日志(后台进行，立即返回)。打开清理窗口时调用；
    /// 正在查找或正在清理时不重复开始。
    /// </summary>
    public void ScanOtherLocations()
    {
        lock (_gate)
        {
            if (_scanning || _state == LogCleanupState.Running) return;
            _scanning = true;
            string currentRoot = LogSegmentStore.RootFor(_settings().StorageDirectory);
            _scan = Task.Run(() =>
            {
                IReadOnlyList<LogResidue> found = [];
                try
                {
                    // 数据目录和当前日志目录整棵跳过；它们的上级(如 C:\、D:\logs)照常往下找
                    found = LogResidueScanner.Scan(_scanRoots(), path =>
                        Same(path, _dataDirectory) || Inside(path, _dataDirectory)
                        || Same(path, currentRoot) || Inside(path, currentRoot));
                }
                catch (Exception) { }   // 查找失败不影响固定位置的清理
                finally
                {
                    lock (_gate) { _discovered = found; _scanning = false; }
                }
            });
        }
    }

    /// <summary>当前能清理的残留(只列路径，不逐个统计文件，几百万个文件时也能立即返回)。</summary>
    public IReadOnlyList<LogResidue> Find()
    {
        ObservabilitySettings settings = _settings();
        string currentRoot = LogSegmentStore.RootFor(settings.StorageDirectory);
        var found = new List<LogResidue>();
        void Add(string path, string kind, string? filePattern = null)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (_payloads.FallbackDirectories.Any(fallback => Same(full, fallback) || Inside(fallback, full))) return;
            if (!Directory.Exists(full)) return;
            if (filePattern is null)
            {
                if (IsProtected(full, currentRoot)) return;
            }
            // 只删文件时，所在文件夹可以是当前日志目录的上级(比如旧文本日志和新日志都放在 D:\logs)
            else if (Inside(full, currentRoot) || Same(full, currentRoot)
                     || !LogResidueScanner.HasTextLogs(new DirectoryInfo(full))) return;
            if (found.Any(item => Same(item.Path, full) && item.FilePattern == filePattern)) return;
            if (filePattern is null && found.Any(item => item.FilePattern is null && Inside(full, item.Path))) return;
            found.Add(new LogResidue(full, kind, filePattern));
        }

        Add(Path.Combine(_dataDirectory, RuntimeTelemetry.LegacyPayloadDirectoryName), "旧版本接口报文");
        foreach (string previous in settings.PreviousStorageDirectories)
            if (!string.IsNullOrWhiteSpace(previous) && Path.IsPathFullyQualified(previous))
                Add(LogSegmentStore.RootFor(previous), "更换日志目录前的旧日志");
        if (Path.GetDirectoryName(_dataDirectory) is { } parent && Directory.Exists(parent))
            foreach (string parked in Directory.EnumerateDirectories(parent, "IGW-大目录-*"))
                Add(parked, "重装脚本挪出的日志目录");
        // 默认日志目录(换到其它盘后留下的；卸载重装后换盘记录可能已经没了)
        Add(LogSegmentStore.RootFor(Path.Combine(_dataDirectory, "observability")), "更换日志目录前的旧日志");
        // 更早版本的文本日志：数据目录 logs\igw-yyyyMMdd.log
        Add(Path.Combine(_dataDirectory, "logs"), LogResidueScanner.TextLogsKind, LogResidueScanner.TextLogPattern);
        IReadOnlyList<LogResidue> discovered;
        lock (_gate) discovered = _discovered;
        foreach (LogResidue residue in discovered) Add(residue.Path, residue.Kind, residue.FilePattern);
        return found;
    }

    /// <summary>开始清理；已在清理时返回 false。立即返回，删除在后台进行。</summary>
    /// <param name="paths">只清理勾选的这些(残留的 Path)；为空时清理全部。只接受当前列出的残留，别的路径忽略。</param>
    public bool Start(IReadOnlyCollection<string>? paths = null)
    {
        lock (_gate)
        {
            if (_blockedReason() is not null) return false;
            if (_state == LogCleanupState.Running) return false;
            IReadOnlyList<LogResidue> residues = Find();
            if (paths is not null)
                residues = residues.Where(item => paths.Any(path => Same(path, item.Path))).ToArray();
            Begin();
            _run = Task.Run(() => Run(residues, []));
            return true;
        }
    }

    /// <summary>网关启动时调用：上次没删完的继续删。</summary>
    public void ResumePending()
    {
        if (_blockedReason() is not null) return;
        List<string> pending = LoadPending().Where(Directory.Exists).ToList();
        if (pending.Count == 0) { TryDeleteStateFile(); return; }
        lock (_gate)
        {
            if (_state == LogCleanupState.Running) return;
            Begin();
            _run = Task.Run(() => Run([], pending));
        }
    }

    private void Begin()
    {
        _state = LogCleanupState.Running;
        _deletedFiles = 0;
        _deletedBytes = 0;
        _skipped = [];
        _error = null;
        _startedAt = DateTimeOffset.Now;
        _finishedAt = null;
    }

    private void Run(IReadOnlyList<LogResidue> residues, List<string> pending)
    {
        try
        {
            foreach (LogResidue residue in residues)
            {
                if (_blockedReason() is { } blocked)
                { lock (_gate) _skipped.Add(blocked); break; }
                if (residue.FilePattern is not null)
                {
                    // 只清旧文本日志：先挪进同一文件夹下的待删目录(瞬间完成)，文件夹里的其它东西不动
                    string bin = Path.Combine(residue.Path, TrashPrefix + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(bin);
                    pending.Add(bin);
                    SavePending(pending);
                    foreach (FileInfo file in LogResidueScanner.TextLogs(new DirectoryInfo(residue.Path)))
                    {
                        try { file.MoveTo(Path.Combine(bin, file.Name)); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            lock (_gate) _skipped.Add($"{file.FullName}：{error.Message}");
                        }
                    }
                    continue;
                }
                // 旧报文可能正被新版本在后台搬往日志目录：先让搬迁停下，读取不再回退到这里
                _payloads.AbandonFallback(residue.Path);
                string trash = Path.Combine(Path.GetDirectoryName(residue.Path)!, TrashPrefix + Guid.NewGuid().ToString("N")[..8]);
                if (TryRename(residue.Path, trash, out string? reason))
                {
                    pending.Add(trash);
                    SavePending(pending);
                }
                else lock (_gate) _skipped.Add($"{residue.Path}：{reason}");
            }
            foreach (string trash in pending.ToArray())
            {
                DeleteTree(trash);
                pending.Remove(trash);
                SavePending(pending);
            }
            TryDeleteStateFile();
            lock (_gate) { _state = LogCleanupState.Completed; _finishedAt = DateTimeOffset.Now; }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _state = LogCleanupState.Failed;
                _error = error.Message;
                _finishedAt = DateTimeOffset.Now;
            }
        }
    }

    /// <summary>同目录改名(瞬间完成)。目录里有文件正被占用时 Windows 不允许改名，稍等重试几次后跳过。</summary>
    private static bool TryRename(string source, string target, out string? reason)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, target);
                reason = null;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4)
                {
                    reason = "目录正被其它程序占用，已跳过(关闭打开了该目录的窗口或程序后可再点一次清理)。" + error.Message;
                    return false;
                }
                Thread.Sleep(500);
            }
        }
    }

    private void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        var directories = new List<string> { root };
        var pending = new Stack<string>();
        pending.Push(root);
        IEnumerable<FileInfo> Files()
        {
            while (pending.Count > 0)
            {
                var directory = new DirectoryInfo(pending.Pop());
                foreach (FileInfo file in directory.EnumerateFiles()) yield return file;
                foreach (DirectoryInfo child in directory.EnumerateDirectories())
                {
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) { child.Delete(); continue; }   // 链接只删链接本身
                    directories.Add(child.FullName);
                    pending.Push(child.FullName);
                }
            }
        }
        // 几百万个小文件：多线程逐个删，比单线程快数倍
        Parallel.ForEach(Files(), new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
        {
            long length = 0;
            try
            {
                length = file.Length;
                file.Delete();
            }
            catch (UnauthorizedAccessException)
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
            }
            catch (FileNotFoundException) { return; }
            Interlocked.Increment(ref _deletedFiles);
            Interlocked.Add(ref _deletedBytes, length);
        });
        foreach (string directory in directories.OrderByDescending(path => path.Length))
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    /// <summary>数据目录本身、当前日志目录(含报文)及其上级，永远不算残留。</summary>
    private bool IsProtected(string path, string currentRoot) =>
        Same(path, _dataDirectory) || Inside(_dataDirectory, path)
        || Same(path, currentRoot) || Inside(path, currentRoot) || Inside(currentRoot, path);

    private string StatePath => Path.Combine(_dataDirectory, StateFileName);

    private List<string> LoadPending()
    {
        try
        {
            if (!File.Exists(StatePath)) return [];
            return JsonSerializer.Deserialize<CleanupStateFile>(File.ReadAllBytes(StatePath), Json)?.Pending ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException) { return []; }
    }

    private void SavePending(List<string> pending) =>
        PrivateFiles.AtomicWrite(StatePath, JsonSerializer.SerializeToUtf8Bytes(new CleanupStateFile(pending), Json));

    private void TryDeleteStateFile()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch (IOException) { }
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Inside(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record CleanupStateFile([property: JsonPropertyName("pending")] List<string> Pending);
}
