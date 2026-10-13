namespace IGW.Core;

/// <summary>
/// 宿主层批量注册点位的结果：Applied=已写库且运行态已应用；Conflict=修订号不符（未写库）；
/// ErrorCode 非空时为失败原因（请求被拒/写库失败/已写库但运行态应用失败）。Timing 为分段耗时文本。
/// </summary>
public sealed record ProjectBulkTagResponse(
    bool Applied,
    bool Conflict,
    string Revision,
    string? ChannelId,
    string? DeviceId,
    int Created,
    int Updated,
    int Deleted,
    bool DeviceRestarted,
    string? ErrorCode,
    string? Error,
    string Timing,
    bool Retryable = false);
