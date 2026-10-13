using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IGW.Observability;

public enum LogStorageChangeStatus
{
    Pending,
    Copying,
    Copied,
    Completed,
    Failed
}

public sealed class LogStorageChange
{
    public const string FileName = "observability-storage-change.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    [JsonInclude] public string SourceDirectory { get; private set; } = "";
    [JsonInclude] public string TargetDirectory { get; private set; } = "";
    [JsonInclude] public LogStorageChangeStatus Status { get; private set; }
    [JsonInclude] public long TotalBytes { get; private set; }
    [JsonInclude] public long CopiedBytes { get; private set; }
    [JsonInclude] public DateTimeOffset CreatedAt { get; private set; }
    [JsonInclude] public DateTimeOffset UpdatedAt { get; private set; }
    [JsonInclude] public DateTimeOffset? CompletedAt { get; private set; }
    [JsonInclude] public string? FailureReason { get; private set; }

    [JsonIgnore] private string StateDirectory { get; set; } = "";

    public static LogStorageChange Begin(string stateDirectory, string sourceDirectory, string targetDirectory)
    {
        string state = NormalizeAbsolute(stateDirectory, nameof(stateDirectory));
        string source = NormalizeAbsolute(sourceDirectory, nameof(sourceDirectory));
        string target = NormalizeAbsolute(targetDirectory, nameof(targetDirectory));
        RejectOverlap(source, target);
        Directory.CreateDirectory(state);
        var now = DateTimeOffset.UtcNow;
        var change = new LogStorageChange
        {
            StateDirectory = state,
            SourceDirectory = source,
            TargetDirectory = target,
            Status = LogStorageChangeStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        change.Persist();
        return change;
    }

    public static LogStorageChange? Load(string stateDirectory)
    {
        string state = NormalizeAbsolute(stateDirectory, nameof(stateDirectory));
        string path = Path.Combine(state, FileName);
        if (!File.Exists(path)) return null;
        LogStorageChange change = JsonSerializer.Deserialize<LogStorageChange>(
            File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("日志存储迁移状态为空。");
        change.StateDirectory = state;
        change.SourceDirectory = NormalizeAbsolute(change.SourceDirectory, nameof(SourceDirectory));
        change.TargetDirectory = NormalizeAbsolute(change.TargetDirectory, nameof(TargetDirectory));
        RejectOverlap(change.SourceDirectory, change.TargetDirectory);
        return change;
    }

    public async Task CopyHistoryAsync(CancellationToken cancellationToken)
    {
        if (Status is LogStorageChangeStatus.Completed or LogStorageChangeStatus.Failed)
            throw new InvalidOperationException($"状态 {Status} 不能复制日志存储。");

        FileInfo[] files = EnumerateSourceFiles().ToArray();
        TotalBytes = files.Sum(file => file.Length);
        CopiedBytes = 0;
        Status = LogStorageChangeStatus.Copying;
        TouchAndPersist();
        Directory.CreateDirectory(TargetDirectory);

        foreach (FileInfo source in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(SourceDirectory, source.FullName);
            string destination = Path.GetFullPath(Path.Combine(TargetDirectory, relative));
            EnsureInsideTarget(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long sourceLength = source.Length;
            if (!File.Exists(destination) || new FileInfo(destination).Length != sourceLength)
                await CopyFileOrReplacementAsync(source.FullName, destination, cancellationToken).ConfigureAwait(false);
            CopiedBytes += sourceLength;
            TouchAndPersist();
        }

        Status = LogStorageChangeStatus.Copied;
        TouchAndPersist();
    }

    public void Complete()
    {
        Status = LogStorageChangeStatus.Completed;
        FailureReason = null;
        CompletedAt = DateTimeOffset.UtcNow;
        TouchAndPersist();
    }

    public void Fail(string reason)
    {
        Status = LogStorageChangeStatus.Failed;
        FailureReason = SanitizeReason(reason);
        CompletedAt = DateTimeOffset.UtcNow;
        TouchAndPersist();
    }

    private IEnumerable<FileInfo> EnumerateSourceFiles()
    {
        if (!Directory.Exists(SourceDirectory)) yield break;
        string stateFile = Path.GetFullPath(Path.Combine(StateDirectory, FileName));
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(SourceDirectory));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                // .tmp 都是写到一半的临时文件(日志分段、报文、迁移本身)，复制过去也会被丢弃
                if (!file.FullName.Equals(stateFile, PathComparison)
                    && !file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            foreach (DirectoryInfo child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
        }
    }

    /// <summary>
    /// 复制一个文件；列出之后、复制之前源文件可能已被网关自己改掉：刚封存的 .jsonl 被后台压缩成 .seg + .idx 后删除，
    /// 或旧分段/报文被保留期清理删除。压缩替换的补上压缩后的文件，删掉的跳过，不能因此让整个迁移失败。
    /// </summary>
    internal static async Task CopyFileOrReplacementAsync(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            await CopyAtomicallyAsync(source, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException && !File.Exists(source))
        {
            if (!source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
            // 压缩先落 .idx 再落 .seg，最后才删 .jsonl，所以 .jsonl 不在时两者都已完整
            foreach (string extension in new[] { ".idx", ".seg" })
            {
                string replacement = Path.ChangeExtension(source, extension);
                string target = Path.ChangeExtension(destination, extension);
                if (!File.Exists(replacement)) continue;
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(replacement).Length) continue;
                try { await CopyAtomicallyAsync(replacement, target, cancellationToken).ConfigureAwait(false); }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException && !File.Exists(replacement)) { }
            }
        }
    }

    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".migration.tmp";
        try
        {
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            // 保留修改时间：接口报文按它计算保留期，迁移不能让它们重新计时
            DateTime written = File.GetLastWriteTimeUtc(source);
            TransientFileRetry.Run(() => File.SetLastWriteTimeUtc(temporary, written));
            TransientFileRetry.Run(() => File.Move(temporary, destination, overwrite: true));
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private void EnsureInsideTarget(string destination)
    {
        string root = Path.TrimEndingDirectorySeparator(TargetDirectory) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, PathComparison))
            throw new InvalidDataException("日志迁移目标路径越界。");
    }

    private void TouchAndPersist()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Persist();
    }

    private void Persist()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        PrivateFiles.AtomicWrite(Path.Combine(StateDirectory, FileName), json);
    }

    private static string SanitizeReason(string? reason)
    {
        string value = string.IsNullOrWhiteSpace(reason) ? "日志存储迁移失败。" : reason;
        value = Regex.Replace(value, "(?i)(authorization\\s*:\\s*)[^\\r\\n]+", "$1***");
        value = Regex.Replace(value, "(?i)\\b(password|token|secret)\\s*=\\s*[^\\s;]+", "$1=***");
        value = Regex.Replace(value, "[\\r\\n\\t]+", " ");
        value = Regex.Replace(value, "\\s{2,}", " ").Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("目录必须是绝对路径。", parameterName);
        return Path.GetFullPath(path);
    }

    private static void RejectOverlap(string source, string target)
    {
        string sourceRoot = Path.TrimEndingDirectorySeparator(source);
        string targetRoot = Path.TrimEndingDirectorySeparator(target);
        if (sourceRoot.Equals(targetRoot, PathComparison)
            || IsChild(sourceRoot, targetRoot)
            || IsChild(targetRoot, sourceRoot))
            throw new ArgumentException("源日志目录和目标日志目录不能相同或互相包含。");
    }

    private static bool IsChild(string parent, string candidate) =>
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
        || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
            && candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison));
}
