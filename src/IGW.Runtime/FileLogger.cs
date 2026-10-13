using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace IGW.Runtime;

public sealed record RuntimeLogQuery(string? Level, string? Search, DateOnly? Date, int Limit = 300);
public sealed record RuntimeLogEntry(DateTimeOffset Timestamp, string Level, string Category, string Message);
public sealed record RuntimeLogSnapshot(int RetentionDays, IReadOnlyList<RuntimeLogEntry> Items,
    IReadOnlyList<string>? Warnings = null, string StorageDirectory = "", string DefaultDirectory = "");

/// <summary>
/// Runtime 文件日志。按天写入 logs\igw-yyyyMMdd.log，单文件超过 10MB 后在当天继续分卷；
/// 过期文件按配置天数清理。写入和清理失败不会影响采集 Runtime。
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 10 * 1024 * 1024;
    private string _directory;
    private string? _storageWarning;
    private readonly object _lock = new();
    private readonly LogLevel _min;
    private readonly TimeProvider _time;
    private int _retentionDays;

    public int RetentionDays => Volatile.Read(ref _retentionDays);
    public string StorageDirectory => Volatile.Read(ref _directory);
    public string DefaultDirectory { get; }

    public FileLoggerProvider(
        string dir,
        LogLevel min = LogLevel.Information,
        int retentionDays = 30,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        _directory = DefaultDirectory = Path.GetFullPath(dir);
        _min = min;
        _time = timeProvider ?? TimeProvider.System;
        _retentionDays = ValidateRetentionDays(retentionDays);
        try { Directory.CreateDirectory(dir); } catch { }
        CleanupExpiredFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    public void UpdateRetentionDays(int days)
    {
        Volatile.Write(ref _retentionDays, ValidateRetentionDays(days));
        CleanupExpiredFiles();
    }

    public string PrepareStorageDirectory(string? directory)
    {
        string path = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory.Trim();
        path = RuntimeLogDirectories.Normalize(path);
        Directory.CreateDirectory(path);
        // Use the Runtime service identity, not the browser user's filesystem permissions.
        string probePath = Path.Combine(path, ".igw-write-probe-" + Guid.NewGuid().ToString("N"));
        using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(0);
        probe.Flush();
        return path;
    }

    internal void ApplyStorageSettings(int days, string preparedDirectory)
    {
        ValidateRetentionDays(days);
        lock (_lock)
        {
            Volatile.Write(ref _directory, preparedDirectory);
            Volatile.Write(ref _retentionDays, days);
            Volatile.Write(ref _storageWarning, null);
        }
        CleanupExpiredFiles();
    }

    public void RestoreStorageSettings(int days, string? directory)
    {
        UpdateRetentionDays(days);
        try { ApplyStorageSettings(days, PrepareStorageDirectory(directory)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string warning = $"日志存储目录不可用：{directory}；当前使用 {StorageDirectory}。请在日志设置中重新选择目录。原因：{ex.Message}";
            Volatile.Write(ref _storageWarning, warning);
            CreateLogger("RuntimeLogs").LogWarning("{warning}", warning);
        }
    }

    public RuntimeLogSnapshot Query(RuntimeLogQuery query, CancellationToken ct = default)
    {
        int limit = Math.Clamp(query.Limit, 1, 2_000);
        string level = NormalizeLevel(query.Level);
        string search = (query.Search ?? "").Trim();
        var collected = new List<RuntimeLogEntry>(limit);
        var warnings = new List<string>();
        string directory = StorageDirectory;
        if (Volatile.Read(ref _storageWarning) is { } storageWarning) warnings.Add(storageWarning);

        // Readers share files with the writer and rotation. Never hold the write/cleanup lock
        // while scanning history: a slow query must not stall logging or settings requests.
        foreach (var file in LogFilesNewestFirst(directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in ReadFileNewestFirst(file, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (query.Date is { } date && DateOnly.FromDateTime(entry.Timestamp.LocalDateTime) != date) continue;
                    if (level.Length > 0 && !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                    if (search.Length > 0 &&
                        !entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                        !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    collected.Add(entry);
                    if (collected.Count == limit) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法读取日志文件 {Path.GetFileName(file)}：{ex.Message}");
            }
            if (collected.Count == limit) break;
        }

        var items = collected
            .OrderByDescending(static entry => entry.Timestamp)
            .Take(limit)
            .ToArray();
        return new RuntimeLogSnapshot(RetentionDays, items, warnings, directory, DefaultDirectory);
    }

    public void CleanupExpiredFiles()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_directory)) return;
                DateTimeOffset now = _time.GetLocalNow();
                DateOnly firstDayToKeep = DateOnly.FromDateTime(now.Date).AddDays(1 - RetentionDays);
                foreach (string file in Directory.EnumerateFiles(_directory, "igw*.log", SearchOption.TopDirectoryOnly))
                {
                    DateOnly fileDay = DateFromFileName(file) ?? DateOnly.FromDateTime(File.GetLastWriteTime(file));
                    if (fileDay < firstDayToKeep)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        DateTimeOffset now = _time.GetLocalNow();
        var sb = new StringBuilder(160);
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(LevelCode(level))
          .Append(" [").Append(category).Append("] ").Append(message);
        if (ex != null) sb.AppendLine().Append(ex);
        sb.AppendLine();

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string file = Path.Combine(_directory, $"igw-{now:yyyyMMdd}.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes) Roll(file, now);
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 磁盘满/被锁：丢弃本条，不能拖垮 Runtime */ }
        }
    }

    private void Roll(string activeFile, DateTimeOffset now)
    {
        for (int sequence = 1; sequence < 10_000; sequence++)
        {
            string archive = Path.Combine(_directory, $"igw-{now:yyyyMMdd}.{sequence}.log");
            if (File.Exists(archive)) continue;
            try { File.Move(activeFile, archive); } catch { }
            return;
        }
    }

    private static IEnumerable<string> LogFilesNewestFirst(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "igw*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
        : [];

    private static IEnumerable<RuntimeLogEntry> ReadFileNewestFirst(string file, CancellationToken ct)
    {
        var continuation = new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var line in ReadLinesNewestFirst(stream, ct))
        {
            var match = LogLinePattern().Match(line);
            if (match.Success && DateTime.TryParseExact(match.Groups["timestamp"].Value,
                    "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
            {
                continuation.Reverse();
                string message = match.Groups["message"].Value;
                if (continuation.Count > 0) message += Environment.NewLine + string.Join(Environment.NewLine, continuation);
                continuation.Clear();
                yield return new RuntimeLogEntry(
                    new DateTimeOffset(timestamp),
                    match.Groups["level"].Value,
                    match.Groups["category"].Value,
                    message);
            }
            else continuation.Add(line);
        }
    }

    private static IEnumerable<string> ReadLinesNewestFirst(FileStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        var line = new List<byte>();
        long position = stream.Length;
        bool atEnd = true;
        while (position > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, position);
            position -= count;
            stream.Position = position;
            stream.ReadExactly(buffer.AsSpan(0, count));
            for (int i = count - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    if (!atEnd)
                    {
                        line.Reverse();
                        yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r').TrimStart('\uFEFF');
                    }
                    line.Clear();
                }
                else line.Add(buffer[i]);
                atEnd = false;
            }
        }
        if (line.Count > 0)
        {
            line.Reverse();
            yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r').TrimStart('\uFEFF');
        }
    }

    private static int ValidateRetentionDays(int days) => days is >= 1 and <= 3650
        ? days
        : throw new ArgumentOutOfRangeException(nameof(days), "日志保留天数必须在 1 到 3650 天之间。");

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    private static string NormalizeLevel(string? level) => (level ?? "").Trim().ToUpperInvariant() switch
    {
        "TRACE" => "TRC", "DEBUG" => "DBG", "INFORMATION" or "INFO" => "INF",
        "WARNING" or "WARN" => "WRN", "ERROR" => "ERR", "CRITICAL" => "CRT",
        "ALL" => "", var value => value,
    };

    private static DateOnly? DateFromFileName(string file)
    {
        var match = LogFileDatePattern().Match(Path.GetFileName(file));
        return match.Success && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<level>[A-Z?]{3}) \[(?<category>[^\]]*)\] ?(?<message>.*)$")]
    private static partial Regex LogLinePattern();

    [GeneratedRegex(@"^igw-(?<date>\d{8})(?:\.\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LogFileDatePattern();

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._min;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
