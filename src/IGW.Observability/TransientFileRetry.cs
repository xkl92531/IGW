namespace IGW.Observability;

/// <summary>
/// Windows 上刚写完的文件常被杀毒软件、索引服务短暂打开，这时改名或删除会偶发“拒绝访问/正被使用”。
/// 这类冲突通常几十到几百毫秒就过去，短暂重试几次，不让一次偶发冲突让日志迁移失败或留下旧目录。
/// </summary>
internal static class TransientFileRetry
{
    private const int Attempts = 6;

    public static void Run(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception error) when (attempt < Attempts && IsTransient(error))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static bool IsTransient(Exception error) =>
        error is UnauthorizedAccessException
        || (error is IOException && error is not FileNotFoundException and not DirectoryNotFoundException);
}
