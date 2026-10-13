using IGW.Abstractions;

namespace IGW.Core;

/// <summary>建立一个实时流所需的、已经过协议边界校验的参数。</summary>
public sealed record RealtimeStreamOpenRequest(
    string ClientId,
    string UserName,
    string ProjectRevision,
    IReadOnlyList<string> TagIds,
    IReadOnlyList<string> Topics,
    bool IncludeSnapshot,
    int HeartbeatSeconds);

/// <summary>协调器向传输层交付的消息基类。传输层负责把它编码成 SSE。</summary>
public abstract record RealtimeOutboundMessage;

/// <summary>一个点位的最新状态。Handle 仅用于进程内排序和合并，不作为外部身份。</summary>
public sealed record RealtimeValueState(
    int Handle,
    string TagId,
    DataValue Value,
    long ArrivedTimestamp);

/// <summary>一个合并窗口内，每个点位只保留最终状态。</summary>
public sealed record RealtimeStateBatch(IReadOnlyList<RealtimeValueState> Values)
    : RealtimeOutboundMessage;

public sealed record RealtimeSnapshotBegin(
    string SnapshotId,
    int TotalValues,
    int ChunkCount,
    DateTime CapturedAtUtc)
    : RealtimeOutboundMessage;

public sealed record RealtimeSnapshotChunk(
    string SnapshotId,
    int ChunkIndex,
    IReadOnlyList<RealtimeValueState> Values)
    : RealtimeOutboundMessage;

public sealed record RealtimeSnapshotComplete(
    string SnapshotId,
    int TotalValues,
    int ChunkCount,
    DateTime CompletedAtUtc)
    : RealtimeOutboundMessage;

/// <summary>有业务顺序含义的运行事件，不允许按主题或节点合并。</summary>
public sealed record RealtimeRuntimeEvent(
    string Topic,
    long SourceSequence,
    string Kind,
    object? Payload,
    DateTime TimestampUtc);

/// <summary>
/// 设备运行状态的稳定外部契约。State 固定使用字符串，避免 System.Text.Json
/// 把内部枚举编码成数字后导致网页把在线设备误判为离线。
/// </summary>
public sealed record RealtimeDeviceStatus(
    string Name,
    string State,
    int TagCount,
    int ActiveTags,
    bool DemandActive,
    DeviceStats Stats,
    string? DetailState,
    string? DetailMessage,
    RealtimeConnectionDiagnostic? Diagnostic);

public sealed record RealtimeConnectionDiagnostic(
    string Device,
    string Channel,
    string Protocol,
    string Code,
    string Category,
    string Scope,
    string State,
    string Availability,
    string Cause,
    string Suggestion,
    string RawError,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    DateTimeOffset? RecoveredAt,
    int OccurrenceCount,
    bool Retrying,
    int NextRetryMs,
    bool Active);

public sealed record RealtimeOrderedEvent(RealtimeRuntimeEvent Event)
    : RealtimeOutboundMessage;

/// <summary>客户端可能丢失了增量，必须重新获取权威快照。</summary>
public sealed record RealtimeResetRequired(string Reason)
    : RealtimeOutboundMessage;

public sealed record RealtimeProjectChanged(
    string ProjectRevision,
    DateTime ServerTimeUtc)
    : RealtimeOutboundMessage;

public sealed record RealtimeSessionMetrics(
    Guid StreamId,
    string ClientId,
    int Tags,
    int Topics,
    int PendingStates,
    int PendingEvents,
    long CoalescedStates,
    long DroppedEvents,
    long ResetCount,
    long SnapshotsStarted,
    long SnapshotsCompleted,
    long SnapshotFailures,
    long SentMessages,
    long SentValues,
    long SentBytes,
    RealtimeLatencySnapshot Latency);

public sealed record RealtimeStreamMetrics(
    int ActiveStreams,
    int PeakStreams,
    int UniqueTags,
    long SubscriptionRelationships,
    long IngressChanges,
    long IngressDropped,
    long CoalescedChanges,
    long DeliveredChanges,
    int IngressQueueDepth,
    int PeakIngressQueueDepth,
    long ResetCount,
    long SnapshotsStarted,
    long SnapshotsCompleted,
    long SnapshotFailures,
    long SentMessages,
    long SentValues,
    long SentBytes,
    RealtimeLatencySnapshot Latency,
    IReadOnlyList<RealtimeSessionMetrics> Sessions);

/// <summary>实时流准入失败。Code 是稳定的协议错误码，Message 供日志和界面展示。</summary>
public sealed class RealtimeAdmissionException : Exception
{
    public RealtimeAdmissionException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
