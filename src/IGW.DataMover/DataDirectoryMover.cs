using System.Diagnostics;

namespace IGW.DataMover;

/// <summary>网关 Windows 服务的启停(界面里用 ServiceController 实现，测试里替换)。</summary>
public interface IGatewayService
{
    bool Exists { get; }
    bool IsRunning { get; }
    void Stop(TimeSpan timeout);
    void Start(TimeSpan timeout);
}

/// <summary>数据目录当前在哪：LinkPath 是网关和安装包认的固定路径，RealPath 是数据实际所在。</summary>
public sealed record DataLocation(string LinkPath, string RealPath, bool IsLink);

public sealed record DataMoveProgress(string Stage, long Files, long Bytes, long TotalFiles = 0, long TotalBytes = 0);

public sealed record DataMoveResult(string Target, string BackupPath, long Files, long Bytes);

/// <summary>
/// 把网关数据目录整体搬到其它盘。
///
/// 网关、安装包都固定使用 C:\ProgramData\IGW。搬走后在原位置建目录链接(junction)指向新位置，
/// 程序和升级流程照旧认原路径，实际读写都落在新盘上——包括日志目录没改到的报文、MES 上报库、
/// 历史库等，C 盘不再有任何网关数据增长。
///
/// 步骤：停服务 → 复制到新位置(保留修改时间，报文和日志按它算保留期) → 逐项核对文件数和字节数 →
/// 原目录改名留作备份 → 原位置建链接 → 启动服务。链接建立之前任何一步失败，原目录都没动过；
/// 建链接失败会把改名撤回。备份确认无误后由操作人员决定何时删除。
/// </summary>
public sealed class DataDirectoryMover(IGatewayService service, Action<string>? log = null)
{
    public const string DefaultDataDirectory = @"C:\ProgramData\IGW";
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    public static DataLocation Locate(string linkPath)
    {
        string full = Path.GetFullPath(linkPath);
        var directory = new DirectoryInfo(full);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint) && directory.LinkTarget is { } target)
            return new DataLocation(full, Path.GetFullPath(target, Path.GetDirectoryName(full)!), true);
        return new DataLocation(full, full, false);
    }

    /// <summary>目标目录是否可用；可用返回 null，否则返回给操作人员看的原因。</summary>
    public static string? ValidateTarget(DataLocation source, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return "请选择一个完整的目标路径，例如 D:\\IGWData。";
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (Path.GetPathRoot(full) is { } root && Same(full, Path.TrimEndingDirectorySeparator(root)))
            return "不能直接用磁盘根目录，请在目标盘上建一个文件夹，例如 D:\\IGWData。";
        if (Same(full, source.RealPath) || Same(full, source.LinkPath)) return "目标就是当前数据所在位置。";
        if (Inside(full, source.RealPath) || Inside(full, source.LinkPath)) return "目标不能放在当前数据目录里面。";
        if (Inside(source.RealPath, full) || Inside(source.LinkPath, full)) return "当前数据目录不能在目标目录里面。";
        if (File.Exists(full)) return "目标路径是一个文件。";
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            return "目标文件夹不是空的。为避免覆盖其它数据，请选择一个空文件夹或不存在的新文件夹。";
        return null;
    }

    /// <summary>统计目录下的文件数和总字节数(跳过目录链接，不重复计算)。</summary>
    public static (long Files, long Bytes) Measure(string root, IProgress<DataMoveProgress>? progress, CancellationToken ct)
    {
        long files = 0, bytes = 0;
        var clock = Stopwatch.StartNew();
        foreach (FileInfo file in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            files++;
            bytes += file.Length;
            if (clock.ElapsedMilliseconds >= 200) { progress?.Report(new DataMoveProgress("统计", files, bytes)); clock.Restart(); }
        }
        progress?.Report(new DataMoveProgress("统计", files, bytes));
        return (files, bytes);
    }

    public DataMoveResult Move(string linkPath, string target, IProgress<DataMoveProgress>? progress, CancellationToken ct)
    {
        DataLocation source = Locate(linkPath);
        if (!Directory.Exists(source.RealPath)) throw new DirectoryNotFoundException($"找不到数据目录 {source.RealPath}");
        if (ValidateTarget(source, target) is { } invalid) throw new InvalidOperationException(invalid);
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));

        bool restart = service.Exists && service.IsRunning;
        if (service.Exists)
        {
            Log("停止网关服务…");
            service.Stop(ServiceTimeout);
        }
        bool swapped = false;
        try
        {
            Log("统计数据量…");
            var (totalFiles, totalBytes) = Measure(source.RealPath, progress, ct);
            long free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
            if (free < totalBytes + 512L * 1024 * 1024)
                throw new InvalidOperationException(
                    $"目标盘空间不足：需要 {Gb(totalBytes)} GB(另留 0.5 GB 余量)，可用 {Gb(free)} GB。");

            PrepareTarget(target);
            Log($"复制 {totalFiles} 个文件、{Gb(totalBytes)} GB 到 {target}…");
            var (copiedFiles, copiedBytes) = Copy(source.RealPath, target, totalFiles, totalBytes, progress, ct);

            Log("核对新位置的文件…");
            var (checkFiles, checkBytes) = Measure(target, null, ct);
            if (checkFiles != copiedFiles || checkBytes != copiedBytes || copiedFiles != totalFiles || copiedBytes != totalBytes)
                throw new InvalidOperationException(
                    $"核对不一致：原目录 {totalFiles} 个/{totalBytes} 字节，已复制 {copiedFiles} 个/{copiedBytes} 字节，" +
                    $"新位置 {checkFiles} 个/{checkBytes} 字节。原数据未改动。");

            ct.ThrowIfCancellationRequested();
            string backup = Swap(source, target);
            swapped = true;
            Log($"原数据已改名保留在 {backup}");
            return new DataMoveResult(target, backup, copiedFiles, copiedBytes);
        }
        catch when (!swapped)
        {
            Log($"迁移未完成，原数据目录未改动。新位置 {target} 里复制了一部分文件，可以直接删除。");
            throw;
        }
        finally
        {
            if (service.Exists && (restart || swapped))
            {
                Log("启动网关服务…");
                try { service.Start(ServiceTimeout); }
                catch (Exception error) { Log($"网关服务启动失败：{error.Message}。请在服务管理器里手动启动 IGW Gateway。"); }
            }
        }
    }

    /// <summary>删除迁移留下的旧数据备份(文件多时需要一段时间，不影响网关运行)。</summary>
    public static void DeleteBackup(string backup, IProgress<DataMoveProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(backup)) return;
        long files = 0;
        var clock = Stopwatch.StartNew();
        foreach (FileInfo file in EnumerateFiles(backup))
        {
            ct.ThrowIfCancellationRequested();
            file.Attributes = FileAttributes.Normal;
            file.Delete();
            files++;
            if (clock.ElapsedMilliseconds >= 200) { progress?.Report(new DataMoveProgress("删除旧数据", files, 0)); clock.Restart(); }
        }
        Directory.Delete(backup, recursive: true);
        progress?.Report(new DataMoveProgress("删除旧数据", files, 0));
    }

    /// <summary>
    /// 原目录改名留作备份，再在原位置建链接指向新位置。原位置本来就是链接(之前搬过一次)时，
    /// 只拆掉链接本身，旧的实际目录改名留作备份。
    /// </summary>
    private string Swap(DataLocation source, string target)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backup = source.RealPath + ".迁移前-" + stamp;
        if (source.IsLink)
        {
            Directory.Move(source.RealPath, backup);
            Directory.Delete(source.LinkPath);   // 只删链接本身，不会删链接指向的内容
        }
        else
        {
            try { Directory.Move(source.LinkPath, backup); }
            catch (IOException error)
            {
                throw new IOException($"原数据目录正被占用，无法改名({error.Message})。请关闭打开了该目录的程序(资源管理器窗口、记事本等)后重试。", error);
            }
        }
        try
        {
            CreateJunction(source.LinkPath, target);
            if (!Same(Locate(source.LinkPath).RealPath, target))
                throw new IOException($"目录链接 {source.LinkPath} 没有指向 {target}");
        }
        catch
        {
            // 撤回：恢复原来的目录(或原来的链接)，网关照旧使用原数据
            try { if (Directory.Exists(source.LinkPath)) Directory.Delete(source.LinkPath); } catch { }
            if (source.IsLink)
            {
                Directory.Move(backup, source.RealPath);
                CreateJunction(source.LinkPath, source.RealPath);
            }
            else Directory.Move(backup, source.LinkPath);
            throw;
        }
        return backup;
    }

    private static void PrepareTarget(string target)
    {
        Directory.CreateDirectory(target);
        // 与安装包一致：所有用户可修改(服务账户和普通用户都要能写)。在复制前设好，新文件直接继承，
        // 不用事后逐个下发权限。
        Run("icacls.exe", target, "/grant", "*S-1-5-32-545:(OI)(CI)M", "/Q");
    }

    private (long Files, long Bytes) Copy(string source, string target, long totalFiles, long totalBytes,
        IProgress<DataMoveProgress>? progress, CancellationToken ct)
    {
        long files = 0, bytes = 0;
        foreach (string directory in EnumerateDirectories(source))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        var clock = Stopwatch.StartNew();
        // 报文、日志多是几 KB 的小文件，并行复制才能把磁盘跑满
        Parallel.ForEach(EnumerateFiles(source),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            file =>
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
                file.CopyTo(destination, overwrite: false);
                File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
                long done = Interlocked.Increment(ref files);
                long size = Interlocked.Add(ref bytes, file.Length);
                if (clock.ElapsedMilliseconds >= 200)
                {
                    lock (clock)
                    {
                        if (clock.ElapsedMilliseconds < 200) return;
                        clock.Restart();
                    }
                    progress?.Report(new DataMoveProgress("复制", done, size, totalFiles, totalBytes));
                }
            });
        progress?.Report(new DataMoveProgress("复制", files, bytes, totalFiles, totalBytes));
        return (files, bytes);
    }

    private static void CreateJunction(string link, string target) =>
        Run("cmd.exe", "/c", "mklink", "/J", link, target);

    private static void Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法运行 {fileName}");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"{fileName} {string.Join(' ', arguments)} 失败：{output.Trim()}");
    }

    /// <summary>递归列出文件；不进入目录链接(避免把链接指向的其它位置也算进来)。</summary>
    private static IEnumerable<FileInfo> EnumerateFiles(string root)
    {
        foreach (string directory in EnumerateDirectories(root).Prepend(root))
            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
                yield return file;
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (DirectoryInfo child in pending.Pop().EnumerateDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                yield return child.FullName;
                pending.Push(child);
            }
        }
    }

    private void Log(string message) => log?.Invoke(message);

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.00");

    private static bool Same(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
