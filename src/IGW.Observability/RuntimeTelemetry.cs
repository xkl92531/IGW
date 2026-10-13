using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IGW.Observability;

/// <summary>
/// 网关的日志与排障上下文：后台批量写入本机托管 QuestDB，旧分段兼容只读导入；
/// 保留链路追踪只为给每条日志带上排障编号(trace id)，不导出追踪数据。
/// </summary>
public sealed class RuntimeTelemetry : IAsyncDisposable, IDisposable
{
    public const string ActivitySourceName = "IGW.Runtime";
    public const string MeterName = "IGW.Runtime";
    private readonly TracerProvider _traces;
    private readonly CancellationTokenSource _maintenanceCancellation = new();
    private readonly Task _maintenanceTask;
    private readonly TimeSpan _payloadRetention;
    private readonly long _payloadMaxBytes;
    private int _disposed;

    private RuntimeTelemetry(
        ILoggerFactory loggerFactory,
        ActivitySource activities,
        Meter meter,
        TracerProvider traces,
        LogSegmentStore store,
        LogPayloadStore payloads,
        string dataDirectory,
        ObservabilitySettings settings,
        QuestDbDatabase? database = null)
    {
        LoggerFactory = loggerFactory;
        Activities = activities;
        Metrics = meter;
        _traces = traces;
        Store = store;
        Payloads = payloads;
        Database = database;
        _payloadRetention = TimeSpan.FromDays(settings.RetentionDays);
        _payloadMaxBytes = Math.Max(64, settings.PayloadMaxMegabytes <= 0
            ? ObservabilitySettings.DefaultPayloadMaxMegabytes : settings.PayloadMaxMegabytes) * 1024L * 1024L;
        Backend = new LogCenterBackend(dataDirectory, settings, store, payloads, database);
        _maintenanceTask = MaintenanceLoopAsync(_maintenanceCancellation.Token);
    }

    public ILoggerFactory LoggerFactory { get; }
    public ActivitySource Activities { get; }
    public Meter Metrics { get; }
    public LogSegmentStore Store { get; }
    public LogPayloadStore Payloads { get; }
    public LogCenterBackend Backend { get; }
    public QuestDbDatabase? Database { get; }

    /// <summary>旧版本存放接口报文的目录(数据目录下)，现在只在启动时迁移用。</summary>
    internal const string LegacyPayloadDirectoryName = "log-payloads";

    public static RuntimeTelemetry Create(ObservabilitySettings settings, string dataDirectory, bool useQuestDb = true, string? questDbRuntimeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("观测数据目录必须是绝对路径。", nameof(dataDirectory));
        string data = Path.GetFullPath(dataDirectory);
        // 报文跟着日志目录走。旧版本固定写在数据目录的 log-payloads 下，页面上换了日志目录也不动它，
        // 旧盘上的报文一直留在原处；这里先把它挂成只读回退，再在后台搬到日志目录里。
        string legacyPayloads = Path.Combine(data, LegacyPayloadDirectoryName);
        var fallbackPayloads = new[] { legacyPayloads }.Concat(settings.PreviousStorageDirectories.Select(LogPayloadStore.DirectoryFor))
            .Where(Directory.Exists).ToArray();
        var payloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(settings.StorageDirectory),
            fallbackPayloads, createDirectory: !useQuestDb);
        var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays, readOnly: useQuestDb);
        QuestDbDatabase? database = useQuestDb ? new QuestDbDatabase(settings, data, store, questDbRuntimeDirectory) : null;
        if (database is not null) payloads.UseDatabase(database.Payloads);

        ILoggerFactory loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(new LogStoreLoggerProvider(database is null ? store : database.Store));
        });

        var activities = new ActivitySource(ActivitySourceName);
        TracerProvider traces = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("IGW.Runtime", serviceInstanceId: settings.InstanceId))
            .AddSource(ActivitySourceName)
            .AddAspNetCoreInstrumentation(options =>
                options.Filter = context => !IsObservabilityPath(context.Request.Path.Value))
            .AddHttpClientInstrumentation()
            .Build();

        var telemetry = new RuntimeTelemetry(loggerFactory, activities, new Meter(MeterName), traces, store, payloads, data, settings, database);
        if (payloads.FallbackDirectories.Count > 0)
        {
            telemetry.Backend.LegacyPayloadMigration = Task.Run(() =>
            {
                ILogger log = loggerFactory.CreateLogger("IGW.Observability.LogCenter");
                try
                {
                    foreach (string from in fallbackPayloads)
                    {
                        int moved = payloads.AdoptFrom(from, move: from == legacyPayloads);
                        if (moved > 0)
                            log.LogInformation("已把旧位置 {from} 的 {count} 份接口报文迁移到日志目录 {to}",
                                from, moved, payloads.CurrentDirectory);
                    }
                }
                catch (Exception error)
                {
                    log.LogWarning("旧位置 {from} 的接口报文迁移失败，未迁完的报文仍可从原位置读取：{message}",
                        legacyPayloads, error.Message);
                }
            });
        }
        return telemetry;
    }

    public ILoggerProvider CreateLoggerProvider() => new LogContextLoggerProvider(LoggerFactory);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _maintenanceCancellation.Cancel();
        try { await _maintenanceTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        LoggerFactory.Dispose();
        await Backend.FinishStorageChangeAsync().ConfigureAwait(false);
        if (Database is not null) await Database.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
        _traces.Dispose();
        Activities.Dispose();
        Metrics.Dispose();
        _maintenanceCancellation.Dispose();
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            ObservabilitySettings current = Backend.Settings;
            if (Database?.Process.Ready == true)
            {
                try { await Database.Payloads.CleanupCapacityAsync(_payloadMaxBytes, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { }
            }
            // 报文存档按保留期和总容量清理；放在后台线程，文件多时也不影响日志写入。
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(Payloads.CurrentDirectory)) Payloads.Cleanup(TimeSpan.FromDays(current.RetentionDays), _payloadMaxBytes, now);
                    if (Database?.History.Completed == true) Store.CleanupExpired();
                    Backend.Cleanup.ResumePending();
                }
                catch { /* 清理失败下个周期再试 */ }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 旧版本 OpenObserve 留在日志目录里的数据(升级后已无法读取，可能占用数 GB)。只删除 OpenObserve 自己的
    /// 文件类型(.wal/.parquet/metadata.sqlite/GeoLite 地图库)，随后移除变空的目录；用户放在同一目录的其他文件不动。
    /// 返回释放的字节数。
    /// </summary>
    public static long RemoveObsoleteOpenObserveData(string storageDirectory)
    {
        long freed = 0;
        void DeleteMatching(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                try { long length = new FileInfo(file).Length; File.Delete(file); freed += length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        void RemoveEmpty(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string child in Directory.EnumerateDirectories(directory)) RemoveEmpty(child);
            try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        string root = Path.GetFullPath(storageDirectory);
        // 第一次清理删掉 metadata.sqlite 后，残留的 stream/files 仍能标识这是 OpenObserve 目录。
        bool looksLikeOpenObserve = File.Exists(Path.Combine(root, "db", "metadata.sqlite"))
            || Directory.Exists(Path.Combine(root, "wal", "logs"))
            || Directory.Exists(Path.Combine(root, "stream", "files"))
            || Directory.Exists(Path.Combine(root, "wal", "files"));
        if (!looksLikeOpenObserve) return 0;
        // 只在 OpenObserve 自己的子目录里、按它的文件格式删除：
        // .wal/.pb 预写日志、.parquet 数据、.bf 布隆过滤、.ttv 全文索引、cache 下的查询缓存。
        foreach (string pattern in new[] { "*.wal", "*.pb", "*.parquet" })
            DeleteMatching(Path.Combine(root, "wal"), pattern);
        foreach (string pattern in new[] { "*.parquet", "*.bf", "*.ttv", "*.pb" })
            DeleteMatching(Path.Combine(root, "stream"), pattern);
        foreach (string pattern in new[] { "*.parquet", "*.pb", "*.json", "*.arrow" })
            DeleteMatching(Path.Combine(root, "cache"), pattern);
        DeleteMatching(Path.Combine(root, "db"), "metadata.sqlite*");
        DeleteMatching(Path.Combine(root, "mmdb"), "GeoLite2-*.mmdb");
        foreach (string name in new[] { "wal", "stream", "db", "mmdb", "database", "index", "cache", "tmp" })
            RemoveEmpty(Path.Combine(root, name));
        return freed;
    }

    /// <summary>旧版本的离线遥测缓存(发往 OpenObserve 失败时暂存)已不再使用，启动时清掉。</summary>
    private static void RemoveObsoleteSpool(string dataDirectory)
    {
        string spool = Path.Combine(dataDirectory, "telemetry-spool");
        try { if (Directory.Exists(spool)) Directory.Delete(spool, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsObservabilityPath(string? path) =>
        path?.StartsWith("/api/v1/log-center", StringComparison.OrdinalIgnoreCase) == true
        || path?.StartsWith("/api/v1/runtime-logs", StringComparison.OrdinalIgnoreCase) == true;
}
