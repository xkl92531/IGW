#nullable enable
namespace PlcSimulator.Protocols;

/// <summary>
/// 模拟 PLC 内置 OPC UA 服务器的容量与处理速度。null 表示不限制(原有行为)。
/// </summary>
public sealed record OpcUaSimulationLimits
{
    public string Name { get; init; } = "";
    public int MaxSessions { get; init; } = 100;
    /// <summary>所有会话合计，监视项超过 <see cref="SmallSubscriptionItems"/> 的订阅数。</summary>
    public int MaxLargeSubscriptions { get; init; } = int.MaxValue;
    /// <summary>所有会话合计，监视项不超过 <see cref="SmallSubscriptionItems"/> 的订阅数。</summary>
    public int MaxSmallSubscriptions { get; init; } = int.MaxValue;
    public int SmallSubscriptionItems { get; init; } = 1000;
    public int MaxItemsPerSubscription { get; init; } = int.MaxValue;
    public int MinSamplingIntervalMs { get; init; }
    public int MinPublishingIntervalMs { get; init; }
    /// <summary>厂家建议的全部监视项上限；超过时只计数告警，不拒绝。</summary>
    public int RecommendedMonitoredItems { get; init; } = int.MaxValue;
    /// <summary>同一时刻处理的服务请求数(PLC 的 OPC UA 通信任务)。</summary>
    public int ServiceConcurrency { get; init; } = int.MaxValue;
    public double ReadBaseMs { get; init; }
    public double ReadPerNodeMs { get; init; }
    public double WriteBaseMs { get; init; }
    public double WritePerNodeMs { get; init; }
    public double MonitoredItemsBaseMs { get; init; }
    public double MonitoredItemsPerItemMs { get; init; }
    public double RegisterBaseMs { get; init; }
    public double RegisterPerNodeMs { get; init; }
    /// <summary>
    /// 整读 UDT 数组时每个成员值的耗时。逐点读取的耗时里大部分是按符号定位节点，
    /// 整读只做一次定位、其余是序列化，这里按逐点读取的 1/8 估算——**这是假设**，需要实机核对。
    /// </summary>
    public double StructArrayPerMemberMs { get; init; }

    /// <summary>
    /// S7-1515(固件 V3.0/V3.1)。容量取自西门子条目 109755846《OPC UA system limits》：
    /// 会话 48；超过 1000 项的订阅合计 10 个、不超过 1000 项的合计 20 个；单订阅最多 10000 项；
    /// 最小采样/发送周期 100 ms；建议全部监视项不超过 4000。
    /// 处理耗时按 2026-09-06 现场日志估算：1515(PLC8 1 点 17.6 ms，PLC6 28 点 75.8 ms)约 15 ms + 2.0 ms/点，
    /// 含网关排队，偏保守。创建监视项、注册节点的耗时为估算值。PLC 的 OPC UA 通信任务按一次处理一个请求模拟。
    /// </summary>
    public static OpcUaSimulationLimits S7_1515 { get; } = new()
    {
        Name = "S7-1515",
        MaxSessions = 48,
        MaxLargeSubscriptions = 10,
        MaxSmallSubscriptions = 20,
        SmallSubscriptionItems = 1000,
        MaxItemsPerSubscription = 10_000,
        MinSamplingIntervalMs = 100,
        MinPublishingIntervalMs = 100,
        RecommendedMonitoredItems = 4000,
        ServiceConcurrency = 1,
        ReadBaseMs = 15, ReadPerNodeMs = 2.0,
        WriteBaseMs = 15, WritePerNodeMs = 2.0,
        StructArrayPerMemberMs = 0.25,
        MonitoredItemsBaseMs = 15, MonitoredItemsPerItemMs = 0.5,
        RegisterBaseMs = 15, RegisterPerNodeMs = 0.2,
    };

    /// <summary>
    /// S7-1517(固件 V3.0/V3.1)。容量同一资料：会话 64；订阅规则同 1515；最小采样/发送周期 10 ms；建议监视项不超过 24000。
    /// 处理耗时按现场 PLC7(1517)日志 924 点 1589 ms 估算为 15 ms + 1.6 ms/点。
    /// </summary>
    public static OpcUaSimulationLimits S7_1517 { get; } = S7_1515 with
    {
        Name = "S7-1517",
        MaxSessions = 64,
        MinSamplingIntervalMs = 10,
        MinPublishingIntervalMs = 10,
        RecommendedMonitoredItems = 24_000,
        ReadPerNodeMs = 1.6,
        WritePerNodeMs = 1.6,
        StructArrayPerMemberMs = 0.2,
    };

    public static OpcUaSimulationLimits? ByName(string? name) => name?.Trim().ToUpperInvariant().Replace("S7-", "").Replace("CPU", "").Trim() switch
    {
        "1515" => S7_1515,
        "1517" => S7_1517,
        "NONE" or "" or null => null,
        _ => throw new ArgumentException($"未知的 PLC 性能档位：{name}(可选 1515 / 1517 / none)"),
    };
}

/// <summary>单个端点的运行统计(线程安全快照)。</summary>
public sealed record OpcUaSimulationMetrics(
    string ChannelName,
    long ReadCalls, long ReadNodes, long WriteCalls, long WriteNodes,
    long CreateItemCalls, long CreatedItems, long RejectedItems, long RejectedSubscriptions,
    int Subscriptions, int LargeSubscriptions, int MonitoredItems, int PeakMonitoredItems,
    double MaxQueueMs, double TotalQueueMs, long QueuedCalls, long MaxReadNodes = 0);
