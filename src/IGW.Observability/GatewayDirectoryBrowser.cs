namespace IGW.Observability;

/// <summary>网关电脑上的一个文件夹或磁盘。磁盘项附带卷标与容量，便于挑选剩余空间足够的位置。</summary>
public sealed record GatewayDirectoryEntry(
    string Name,
    string Path,
    string? Label = null,
    long? FreeBytes = null,
    long? TotalBytes = null);

/// <summary>
/// 一次浏览结果。<see cref="CurrentPath"/> 为空表示当前显示的是磁盘列表，此时不能作为选中目录。
/// </summary>
public sealed record GatewayDirectoryListing(
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<GatewayDirectoryEntry> Directories,
    bool Truncated = false);

/// <summary>
/// 供 Web 界面挑选网关本机文件夹(日志存储位置等)。只列一层子文件夹，不读文件内容，不递归扫描磁盘。
/// </summary>
public static class GatewayDirectoryBrowser
{
    public const int MaxEntries = 500;

    // 隐藏/系统文件夹(System Volume Information、$Recycle.Bin 等)对选日志目录没有意义，
    // 点进去通常还会因为无权访问而报错。
    private static readonly EnumerationOptions ChildDirectories = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("请填写网关电脑上的绝对路径，例如 D:\\IGWLogs。");
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("请选择普通文件夹路径，不支持设备路径。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    public static GatewayDirectoryListing List(string? path, CancellationToken cancellationToken) =>
        List(path, DriveInfo.GetDrives, cancellationToken);

    internal static GatewayDirectoryListing List(
        string? path,
        Func<IEnumerable<DriveInfo>> drives,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return ListDrives(drives(), cancellationToken);

        string current = Normalize(path.Trim());
        if (!Directory.Exists(current))
            throw new DirectoryNotFoundException($"文件夹不存在或无法访问：{current}");

        var children = new List<GatewayDirectoryEntry>(Math.Min(64, MaxEntries));
        bool truncated = false;
        foreach (string child in Directory.EnumerateDirectories(current, "*", ChildDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 多取一个用来判断是否真的被截断；恰好 MaxEntries 个不算截断。
            if (children.Count == MaxEntries) { truncated = true; break; }
            children.Add(new(System.IO.Path.GetFileName(child), child));
        }
        children.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return new(current, Directory.GetParent(current)?.FullName, children, truncated);
    }

    private static GatewayDirectoryListing ListDrives(IEnumerable<DriveInfo> drives, CancellationToken cancellationToken)
    {
        var roots = new List<GatewayDirectoryEntry>();
        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown || !drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                string? label = null;
                long? free = null, total = null;
                // 卷标和容量只是辅助信息，个别网络盘/光驱读取失败时仍然列出盘符本身。
                try
                {
                    label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel;
                    free = drive.AvailableFreeSpace;
                    total = drive.TotalSize;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                roots.Add(new(drive.Name, root, label, free, total));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        roots.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return new("", null, roots);
    }
}
