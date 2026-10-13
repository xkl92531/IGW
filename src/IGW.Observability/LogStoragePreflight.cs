namespace IGW.Observability;

public static class LogStoragePreflight
{
    internal const long MinimumFreeBytes = 1024L * 1024 * 1024;

    public static void Validate(string directory) =>
        Validate(directory, AvailableFreeSpace);

    internal static void Validate(string directory, Func<string, long> availableFreeSpace)
    {
        ArgumentNullException.ThrowIfNull(availableFreeSpace);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("日志目录必须是绝对路径。", nameof(directory));

        string normalized = Path.GetFullPath(directory);
        Directory.CreateDirectory(normalized);
        VerifyWritable(normalized);

        long freeBytes;
        try { freeBytes = availableFreeSpace(normalized); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException($"无法读取日志目录的剩余空间：{normalized}", error);
        }

        if (freeBytes < MinimumFreeBytes)
            throw new IOException($"日志目录所在磁盘剩余空间不足 1 GiB：{normalized}");
    }

    private static void VerifyWritable(string directory)
    {
        string probe = Path.Combine(directory, $".igw-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"日志目录不可写：{directory}", error);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch { }
        }
    }

    private static long AvailableFreeSpace(string directory)
    {
        string root = Path.GetPathRoot(directory)
            ?? throw new IOException($"无法确定日志目录所在磁盘：{directory}");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
