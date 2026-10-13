using IGW.Abstractions;
using Microsoft.Extensions.Logging;
using IGW.Observability;

namespace IGW.Runtime;

public static class RuntimeLogging
{
    public static RuntimeTelemetry Configure(ILoggingBuilder logging, RuntimeTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(telemetry);
        logging.ClearProviders();
        logging.AddProvider(telemetry.CreateLoggerProvider());
        return telemetry;
    }

    /// <summary>
    /// 安装器用：IGW.Runtime.exe --initialize-logs [--data-dir 目录] [--log-dir 日志目录]。
    /// 写入日志存储配置并创建目录；已有配置时保持原目录不变。成功返回 0。
    /// </summary>
    public static int InitializeStorage(string[] args)
    {
        try
        {
            string dataDirectory = Value(args, "--data-dir") ?? IgwPaths.DataDir;
            string? logDirectory = Value(args, "--log-dir");
            ObservabilitySettings settings = ObservabilitySettings.Initialize(
                Path.GetFullPath(dataDirectory),
                string.IsNullOrWhiteSpace(logDirectory) ? null : Path.GetFullPath(logDirectory),
                null);
            Directory.CreateDirectory(LogSegmentStore.RootFor(settings.StorageDirectory));
            Console.WriteLine($"[IGW] 日志存储目录：{settings.StorageDirectory}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[IGW] 日志存储初始化失败：{error.Message}");
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.FindIndex(args, arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1] : null;
    }

    public static FileLoggerProvider Configure(ILoggingBuilder logging, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        // UseWindowsService registers the Windows EventLog provider.  A portable/non-admin
        // launch cannot create or write its default event source, and an attempted log then
        // crashes the entire Runtime.  The IGW rolling file is valid in both service and
        // portable modes and does not depend on a pre-registered Windows event source.
        logging.ClearProviders();
        var provider = new FileLoggerProvider(logDirectory);
        logging.AddProvider(provider);
        return provider;
    }
}
