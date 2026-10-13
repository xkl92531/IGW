using System.Collections.ObjectModel;
namespace IGW.Abstractions;

/// <summary>点位绑定:运行时把 TagDef 解析为 (TagId 全路径, 解析后地址)。</summary>
public sealed class TagBinding
{
    public required string TagId { get; init; }   // Channel.Device.Group...Tag
    public required TagDef Def { get; init; }
    public required TagAddress Address { get; init; }
    /// <summary>当前消费者合并后的订阅采样周期；null 时使用点位默认 ScanRateMs。</summary>
    public int? RequestedSamplingIntervalMs { get; init; }
}

/// <summary>一次采集得到的点位样本。</summary>
public readonly struct TagSample
{
    public string TagId { get; }
    public DataValue Value { get; }
    public TagSample(string tagId, DataValue value) { TagId = tagId; Value = value; }
}

public sealed class WriteRequest
{
    public required string TagId { get; init; }
    public required TagDef Def { get; init; }
    public required TagAddress Address { get; init; }
    public required object Value { get; init; }

    /// <summary>写后怎么确认。不填按 <see cref="WritePolicy.Default"/>：回读验证、最多重写 10 次。</summary>
    public WritePolicy Policy { get; init; } = WritePolicy.Default;
}

/// <summary>
/// 单次写入的确认策略。调用方按场景声明，不再由引擎一刀切。
/// </summary>
/// <param name="Verify">
/// true = 写完回读 PLC 比对实际值，不一致就重写。false = 协议返回成功即认成功，
/// 结果为 GoodUnverified——快，但 PLC 真实值没人看过。
/// </param>
/// <param name="MaxRewrites">
/// 回读不一致时最多重写几次，仅 <paramref name="Verify"/> 为 true 时有意义。
/// 0 表示写一次就算数,回读不一致直接判 BadNotVerified。
/// </param>
public readonly record struct WritePolicy(bool Verify, int MaxRewrites)
{
    /// <summary>引擎历来的行为:回读验证、最多重写 10 次。没显式声明策略的调用方都走这个。</summary>
    public static readonly WritePolicy Default = new(true, 10);
}

public sealed class WriteResult
{
    public required string TagId { get; init; }
    public StatusCode Status { get; init; }
    /// <summary>由运行引擎主动回读确认，而不是仅相信协议 Write 响应。</summary>
    public bool Verified { get; init; }
    public int Attempts { get; init; }
    public object? ActualValue { get; init; }
    public string? Error { get; init; }
}

/// <summary>驱动的批量/合并约束。</summary>
public sealed class DriverLimits
{
    /// <summary>单帧最大读取单位数(Modbus≈125 寄存器)。</summary>
    public int MaxReadUnits { get; init; } = 120;
    /// <summary>块合并间隙阈值(单位):相邻点位间隙 ≤ 此值才并入同一读块。</summary>
    public int MaxGap { get; init; } = 8;
    /// <summary>单次 ReadAsync 最大点数。符号协议的解析地址可能全部重叠,必须额外按点数拆批。</summary>
    public int MaxTagsPerRead { get; init; } = 10_000;
    /// <summary>单次 WriteAsync 最大点数；驱动连接后可由会话能力覆盖。</summary>
    public int MaxTagsPerWrite { get; init; } = 10_000;
}

/// <summary>连接后由真实服务器/协议协商得到的会话能力，不向工程用户暴露调优开关。</summary>
public sealed record DriverCapabilitySnapshot(
    int MaxTagsPerRead,
    int MaxTagsPerWrite,
    int MaxReadBytes,
    int MaxWriteBytes,
    int MaxInFlight,
    bool SupportsBatchRead,
    bool SupportsBatchWrite,
    bool SupportsSubscriptions,
    bool SupportsReadBackVerification,
    long Version = 0,
    int SuggestedVerificationDelayMs = 1,
    bool PartitionsReadRequests = false)
{
    public static DriverCapabilitySnapshot FromLimits(DriverLimits limits) => new(
        Math.Max(1, limits.MaxTagsPerRead),
        Math.Max(1, limits.MaxTagsPerWrite),
        int.MaxValue,
        int.MaxValue,
        1,
        true,
        true,
        false,
        true);
}

public interface IDriverCapabilityProvider
{
    DriverCapabilitySnapshot Capabilities { get; }
}

/// <summary>
/// 单会话协议在一次 ReadAsync 内执行多个安全报文时，通过该探针在报文边界给高优先级写入让行。
/// 驱动只能在不会破坏协议帧/会话的边界检查，不得中断正在进行的网络请求。
/// </summary>
public interface IDriverPriorityWorkAware
{
    void SetPriorityWorkProbe(Func<bool> priorityWorkPending);
}

/// <summary>
/// 可选的写后回读入口。实现方应使用写入优先级调度回读，但不得中断已经在途的协议请求。
/// </summary>
public interface IWriteVerificationReader
{
    Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(
        IReadOnlyList<TagBinding> tags,
        CancellationToken ct);
}

/// <summary>
/// 驱动已在安全协议边界主动结束本次读取，让设备调度器先处理写入。
/// 这不是读取失败，不改变缓存质量，也不触发连接诊断或重连。
/// </summary>
public sealed class DriverReadYieldException : Exception
{
}

/// <summary>驱动原生变化订阅租约；释放租约时必须删除远端监控项，且不得关闭共享设备会话。</summary>
public interface IDriverSubscription : IAsyncDisposable
{
    IReadOnlyCollection<string> TagIds { get; }
}

/// <summary>
/// 支持增量摘除监控项的订阅租约。没有这个能力时，少订阅一个点也只能整组拆除重建；
/// 几十万点会被切成几十个订阅，一次重建就是几十轮删除加创建的协议往返。
/// </summary>
public interface IIncrementalDriverSubscription : IDriverSubscription
{
    /// <summary>摘除指定点位的监控项，保留其余点位继续推送。返回实际摘除的点数。</summary>
    Task<int> RemoveAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct);
}

/// <summary>
/// 支持原生变化通知的驱动实现此接口。实时消费者和周期业务都可使用订阅填充的
/// DataHub 快照；订阅首值缺失、质量异常、断线或不支持时运行引擎自动回退批量读取。
/// </summary>
public interface IDriverSubscriptionProvider
{
    Task<IDriverSubscription> SubscribeAsync(
        IReadOnlyList<TagBinding> tags,
        Action<IReadOnlyList<TagSample>> onSamples,
        CancellationToken ct);
}

/// <summary>驱动参数描述(供 WPF 动态生成属性表单)。</summary>
public sealed class DriverSetting
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Default { get; init; } = "";
}

public sealed class DriverDescriptor
{
    public required string DriverId { get; init; }
    public required string DisplayName { get; init; }
    public string AddressHelp { get; init; } = "";
    public IReadOnlyList<DriverSetting> ChannelSettings { get; init; } = Array.Empty<DriverSetting>();
    public IReadOnlyList<DriverSetting> DeviceSettings { get; init; } = Array.Empty<DriverSetting>();
    /// <summary>授权码是否包含这个驱动；未授权的驱动不能新建通道，运行时也不采集。</summary>
    public bool Licensed { get; init; } = true;
}

/// <summary>
/// 设备级驱动:管理一个逻辑设备的批量读写。运行时可以让连接配置完全相同的逻辑设备
/// 共享底层物理会话，但点表、调度、订阅租约、状态和诊断仍按 DeviceDef 隔离。
/// 驱动内部负责地址块合并、整块读、切片解码、字节序处理。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    string DriverId { get; }
    ConnectionState State { get; }
    event Action<ConnectionState>? StateChanged;

    IAddressParser AddressParser { get; }
    DriverLimits Limits { get; }

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();

    /// <summary>批量读一组点位(驱动内部合并地址块、整块读、按偏移切片解码)。</summary>
    Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct);

    /// <summary>
    /// 批量写点。点位拒写、类型、范围、权限和单次超时应通过 <see cref="WriteResult"/> 返回；
    /// 只有已经确认传输/协议会话失效时才抛出 <see cref="IOException"/> 等通信异常。
    /// </summary>
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct);

    /// <summary>
    /// 驱动能解释清楚的点位级故障原因（TagId → 原因），例如地址形式与这台 PLC 的协议不匹配。
    /// 质量码只能说明「坏」，说不清「为什么坏、该怎么改」；现场要按点位定位问题就得靠这里。
    /// 默认空：驱动说不出具体原因时，由网关按质量码推断。
    /// </summary>
    IReadOnlyDictionary<string, string> TagFaultReasons => ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>
/// 可选的驱动级硬重置。与普通 Disconnect 不同，它必须丢弃失效的协议会话/原生句柄，
/// 让下一次 Connect 从全新的传输状态开始；设备循环在真实通信故障后调用。
/// </summary>
public interface IConnectionResettableDriver
{
    Task ResetConnectionAsync(CancellationToken ct);
}

/// <summary>设备在没有任何数据消费需求时的连接策略。</summary>
public enum IdleConnectionPolicy
{
    /// <summary>空闲时断开连接，产生读取、写入或浏览需求后再连接。</summary>
    DisconnectWhenIdle,
    /// <summary>空闲时保持传输/会话在线，但不读取业务点位。</summary>
    KeepConnectedWhenIdle,
}

/// <summary>驱动可选的空闲连接策略；未实现此接口的驱动默认按需连接。</summary>
public interface IIdleConnectionPolicyProvider
{
    IdleConnectionPolicy IdleConnectionPolicy { get; }
}

/// <summary>驱动可选的细粒度连接诊断，供监控中心区分认证、权限、协议和传输故障。</summary>
public sealed record DeviceConnectionDetail(string Code, string Message);

public interface IDeviceConnectionDiagnostics
{
    DeviceConnectionDetail ConnectionDetail();
}

/// <summary>
/// 远端会话端点诊断。ConfiguredEndpoint 是工程/转发层明确传给驱动的地址；
/// DiscoveredEndpoint 是服务器发现阶段返回的原始地址；SessionEndpoint 是 SDK 最终建立会话所用地址。
/// </summary>
public sealed record RemoteEndpointSnapshot(
    string ConfiguredEndpoint,
    string? DiscoveredEndpoint,
    string? SessionEndpoint);

public interface IRemoteEndpointDiagnostics
{
    RemoteEndpointSnapshot RemoteEndpoint();
}

/// <summary>地址空间浏览的分阶段诊断，供 Runtime 日志中心展示远端连接与节点扫描进度。</summary>
public enum BrowseDiagnosticLevel
{
    Information,
    Warning,
    Error,
}

public sealed record BrowseDiagnosticEvent(
    BrowseDiagnosticLevel Level,
    string Stage,
    string Message,
    string? NodeId = null,
    int ScannedNodeCount = 0,
    int DiscoveredTagCount = 0,
    long ElapsedMilliseconds = 0);

public interface IBrowseDiagnosticsSource
{
    event Action<BrowseDiagnosticEvent>? BrowseDiagnostic;
}

/// <summary>浏览发现的标记(标记生成用)。</summary>
public sealed class BrowsedTag
{
    public required string Name { get; init; }     // 建议的标记名
    public required string Address { get; init; }  // 驱动地址(OPC UA NodeId / AB 标记名 等)
    public string Path { get; init; } = "";
    public IReadOnlyList<string> PathSegments { get; init; } = System.Array.Empty<string>();
    public IgwDataType DataType { get; init; } = IgwDataType.Int16;
    public bool IsArray { get; init; }
    public ArrayDef? Array { get; init; }
    /// <summary>服务器明确公开的字符串容量；String 为字节容量，WString 为字符容量。</summary>
    public int? StringLength { get; init; }
    /// <summary>协议提供的结构证据；仅在源端明确提供时持久化，禁止通过显示名猜测。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    /// <summary>当前 OPC UA 会话用户对变量的实际访问权限。</summary>
    public TagAccess Access { get; init; } = TagAccess.Read;
}

public sealed record BrowseResult(IReadOnlyList<BrowsedTag> Tags, bool Truncated,
    int? ScannedNodeCount = null, long? ElapsedMilliseconds = null,
    IReadOnlyList<OpcUaBrowseIssue>? Issues = null);

public sealed record OpcUaBrowseIssue(string NodeId, string Name,
    IReadOnlyList<string> PathSegments, string Reason);

/// <summary>可按层浏览的地址空间节点。PathSegments 包含当前节点名称。</summary>
public sealed record OpcUaBrowseNode(
    string NodeId,
    string Name,
    bool IsVariable,
    bool MayHaveChildren,
    IReadOnlyList<string> PathSegments);

public sealed record OpcUaBrowseChildrenResult(IReadOnlyList<OpcUaBrowseNode> Nodes);

public sealed record BrowsePreviewRequest(
    string Url = "",
    string SecurityPolicy = "None",
    bool Anonymous = true,
    string Username = "",
    string Password = "",
    int TimeoutMs = 10_000)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("OPC UA 地址必须是有效的 opc.tcp:// 地址。", nameof(Url));
        if (TimeoutMs is < 1_000 or > 120_000)
            throw new ArgumentOutOfRangeException(nameof(TimeoutMs), "连接超时必须在 1000 到 120000 毫秒之间。");
    }
}

/// <summary>PLC 上的一个数据块：块名与 DB 号。</summary>
public sealed record DataBlockInfo(string Name, int Number);

/// <summary>
/// 能在线列出数据块(块名 → DB 号)的驱动。TIA「从块生成源」导出的 DB 源文件不带 DB 号，
/// 导入非优化 DB 时按块名在线查到编号，不用人工填写。
/// </summary>
public interface IDataBlockDirectory
{
    /// <returns>null：当前 PLC 或原生组件不支持在线列出。</returns>
    Task<IReadOnlyList<DataBlockInfo>?> ListDataBlocksAsync(CancellationToken ct);
}

/// <summary>可浏览驱动:支持浏览设备地址空间自动生成标记(OPC UA / Allen-Bradley 等)。</summary>
public interface IBrowsableDriver
{
    Task<BrowseResult> BrowseAsync(CancellationToken ct);
}

/// <summary>按层展开并只递归所选分支，避免大型 OPC UA 地址空间全量扫描。</summary>
public interface IHierarchicalBrowsableDriver
{
    Task<OpcUaBrowseChildrenResult> BrowseChildrenAsync(
        string nodeId,
        IReadOnlyList<string> parentPathSegments,
        CancellationToken ct);

    Task<BrowseResult> BrowseBranchAsync(OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct);
}

/// <summary>驱动工厂:每个驱动程序集导出一个,供 DriverRegistry 注册。</summary>
public interface IDriverFactory
{
    string DriverId { get; }
    DriverDescriptor Describe();
    IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device);
}

/// <summary>一个读块:同区相邻点位合并后的一次读取(读回后按各 Tag 偏移切片)。</summary>
public sealed class ReadBlock
{
    public int Area { get; init; }
    public int Start { get; init; }
    public int Count { get; init; }
    public required IReadOnlyList<TagBinding> Tags { get; init; }
}

/// <summary>通用地址块合并:把同区相邻/邻近点位合并成尽量少的读块(吞吐核心)。</summary>
public static class AddressBlockOptimizer
{
    public static List<ReadBlock> Plan(IEnumerable<TagBinding> tags, int maxBlockSize, int maxGap,
        int maxTagsPerBlock = int.MaxValue)
    {
        var blocks = new List<ReadBlock>();
        foreach (var areaGroup in tags.GroupBy(t => t.Address.Area))
        {
            var ordered = areaGroup.OrderBy(t => t.Address.Offset).ToList();
            int i = 0;
            while (i < ordered.Count)
            {
                int blockStart = ordered[i].Address.Offset;
                int blockEnd = ordered[i].Address.End; // 不含
                var members = new List<TagBinding> { ordered[i] };
                int j = i + 1;
                while (j < ordered.Count)
                {
                    var addr = ordered[j].Address;
                    int gap = addr.Offset - blockEnd;          // 与当前块末尾的间隙(可为负=重叠)
                    int newEnd = Math.Max(blockEnd, addr.End);
                    if (members.Count < Math.Max(1, maxTagsPerBlock) &&
                        gap <= maxGap && (newEnd - blockStart) <= maxBlockSize)
                    {
                        members.Add(ordered[j]);
                        blockEnd = newEnd;
                        j++;
                    }
                    else break;
                }
                blocks.Add(new ReadBlock
                {
                    Area = areaGroup.Key,
                    Start = blockStart,
                    Count = blockEnd - blockStart,
                    Tags = members,
                });
                i = j;
            }
        }
        return blocks;
    }
}
