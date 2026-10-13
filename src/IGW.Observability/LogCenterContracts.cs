using System.Text.Json;

namespace IGW.Observability;

public sealed class LogCenterRequest
{
    public string Category { get; init; } = "all";
    public DateTimeOffset From { get; init; } = DateTimeOffset.UtcNow.AddHours(-1);
    public DateTimeOffset To { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>协议(通道的驱动 Id)：采集、状态日志里的 igw_protocol。</summary>
    public string? Protocol { get; init; }
    public string? Channel { get; init; }
    public string? Device { get; init; }
    public string? Database { get; init; }
    public string? TaskId { get; init; }
    public string? Module { get; init; }
    public string? Feature { get; init; }
    public string? Task { get; init; }
    public string? Interface { get; init; }
    /// <summary>标记组名称或接口 URL 的包含查询，只查任务/接口上下文字段，不读取报文。</summary>
    public string? TaskOrInterface { get; init; }
    /// <summary>按自由输入的组名/组路径片段解析；只扩展 TaskOrInterface，不能绕过 Path 条件。</summary>
    internal IReadOnlyList<string> TaskOrInterfaceTaskIds { get; init; } = [];
    public string? Level { get; init; }
    public string? Search { get; init; }
    public string? TraceId { get; init; }
    /// <summary>
    /// 工程路径(通道 / 通道.设备 / 通道.设备.组 / 完整点位名)：工程配置右键「查看日志」用，
    /// 查出这一项及其下级的全部日志(见 LogFilter.PathMatches)。
    /// </summary>
    public string? Path { get; init; }
    /// <summary>由服务端当前工程解析的关联任务；与 Path 是 OR 关系，不是客户端额外的 AND 条件。</summary>
    internal IReadOnlyList<string> PathTaskIds { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>工程里任务绑定的组或设备路径。查询时读取当前配置，不依赖前端已加载的任务页。</summary>
public sealed record LogTaskBinding(string TaskId, string? Path);

public sealed record OpenObserveLogQuery(
    string Sql,
    string Filter,
    long StartTimeMicroseconds,
    long EndTimeMicroseconds,
    int From,
    int Size);

public sealed record LogCenterEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? TraceId,
    string? PayloadId,
    IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record LogHistogramBucket(
    DateTimeOffset Start,
    int Total,
    int Warnings,
    int Errors);

public sealed record LogDeviceSummary(string Device, string Status, DateTimeOffset Timestamp);
public sealed record LogOutageSummary(string Device, DateTimeOffset StartedAt, DateTimeOffset? RecoveredAt);

public sealed record LogCenterResponse(
    IReadOnlyList<LogCenterEntry> Items,
    long Total,
    long TookMs,
    IReadOnlyList<LogHistogramBucket> Histogram,
    IReadOnlyList<LogDeviceSummary> Devices,
    IReadOnlyList<LogOutageSummary> Outages,
    bool Partial = false);

public sealed record TelemetrySpoolStatus(int Pending, long Failed, long Dropped);

public sealed record LogCenterStatus(
    bool Available,
    string State,
    int Port,
    int RetentionDays,
    string StorageDirectory,
    TelemetrySpoolStatus Spool,
    LogStorageChangeStatus? MigrationStatus,
    long MigrationTotalBytes,
    long MigrationCopiedBytes,
    string? MigrationFailure,
    long StorageBytes = 0)
{
    public string Engine { get; init; } = "segments";
    public bool HistoryImported { get; init; } = true;
    public string? HistoryError { get; init; }
    public long WalPendingTransactions { get; init; }
    public string? DatabaseError { get; init; }
    public int HistoryTotalFiles { get; init; }
    public int HistoryImportedFiles { get; init; }
    public long HistoryImportedRows { get; init; }
    public int HistoryFailedFiles { get; init; }
}
