using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IGW.Observability;

/// <summary>
/// 在各本地硬盘上查找网关以前留下的日志：网关装过好几个盘、换过日志目录、卸载重装后，
/// 旧位置没有任何记录，只能按网关特有的名字和结构去认。
/// <list type="bullet">
/// <item>名为 igw-logs 且里面有网关日志分段(.seg/.jsonl/.idx)或报文(.payload)的文件夹：整个清掉；</item>
/// <item>文件夹里的旧版文本日志 igw-yyyyMMdd.log：只清这些文件，同一文件夹里的其它东西不动。</item>
/// </list>
/// 只看每个盘的前几层目录，跳过系统目录、回收站、隐藏的系统文件夹和目录链接，并有目录数和时间上限，
/// 不会把整块盘扫一遍。
/// </summary>
internal static partial class LogResidueScanner
{
    internal const string OtherLogsKind = "其它位置的旧日志";
    internal const string TextLogsKind = "旧版文本日志";
    internal const string TextLogPattern = "igw-*.log";
    private const string LogRootName = "igw-logs";
    private static readonly string[] SegmentExtensions = [".seg", ".jsonl", ".idx", ".payload"];
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "$RECYCLE.BIN", "System Volume Information", "Recovery", "PerfLogs",
        "Program Files", "Program Files (x86)", "AppData", "node_modules", ".git", "WindowsApps", "WinSxS",
    };

    /// <summary>本机所有就绪的本地硬盘(不含 U 盘、光驱、网络盘)。</summary>
    public static IReadOnlyList<string> FixedDriveRoots() =>
        DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && SafeIsReady(drive))
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();

    public static IReadOnlyList<LogResidue> Scan(
        IEnumerable<string> roots,
        Func<string, bool> isProtected,
        int maxDepth = 4,
        int maxDirectories = 200_000,
        TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        var found = new List<LogResidue>();
        var clock = Stopwatch.StartNew();
        TimeSpan limit = budget ?? TimeSpan.FromSeconds(90);
        int visited = 0;
        foreach (string root in roots)
        {
            var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
            pending.Push((new DirectoryInfo(root), 0));
            while (pending.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || clock.Elapsed > limit || ++visited > maxDirectories)
                    return found;
                (DirectoryInfo directory, int depth) = pending.Pop();
                if (isProtected(directory.FullName)) continue;
                if (string.Equals(directory.Name, LogRootName, StringComparison.OrdinalIgnoreCase))
                {
                    if (HasGatewaySegments(directory)) found.Add(new LogResidue(directory.FullName, OtherLogsKind));
                    continue;   // 整个清掉，不再往里看
                }
                if (HasTextLogs(directory)) found.Add(new LogResidue(directory.FullName, TextLogsKind, TextLogPattern));
                if (depth >= maxDepth) continue;
                foreach (DirectoryInfo child in Children(directory))
                    pending.Push((child, depth + 1));
            }
        }
        return found;
    }

    /// <summary>文件夹里是否有旧版文本日志 igw-yyyyMMdd.log(按天分卷时是 igw-yyyyMMdd.N.log)。</summary>
    public static bool HasTextLogs(DirectoryInfo directory) => TextLogs(directory).Any();

    public static IEnumerable<FileInfo> TextLogs(DirectoryInfo directory)
    {
        IEnumerable<FileInfo> files;
        try { files = directory.EnumerateFiles(TextLogPattern).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { yield break; }
        foreach (FileInfo file in files)
            if (TextLogName().IsMatch(file.Name)) yield return file;
    }

    private static bool HasGatewaySegments(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Take(500)
                .Any(file => SegmentExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static IEnumerable<DirectoryInfo> Children(DirectoryInfo directory)
    {
        DirectoryInfo[] children;
        try { children = directory.GetDirectories(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { yield break; }
        foreach (DirectoryInfo child in children)
        {
            FileAttributes attributes;
            try { attributes = child.Attributes; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System)) continue;
            if (SkippedNames.Contains(child.Name) || child.Name.StartsWith("IGW-清理中-", StringComparison.Ordinal)) continue;
            yield return child;
        }
    }

    private static bool SafeIsReady(DriveInfo drive)
    {
        try { return drive.IsReady; }
        catch (IOException) { return false; }
    }

    [GeneratedRegex(@"^igw-\d{8}(?:\.\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex TextLogName();
}
