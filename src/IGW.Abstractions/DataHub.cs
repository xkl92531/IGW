namespace IGW.Abstractions;

/// <summary>一次点位变化事件(用于北向发布订阅)。携带整型句柄(热路径寻址)与 TagId 引用(零分配取得,仅边界输出用)。</summary>
public readonly struct TagChange
{
    public int Handle { get; }
    public string TagId { get; }
    public DataValue Value { get; }
    public TagChange(int handle, string tagId, DataValue value) { Handle = handle; TagId = tagId; Value = value; }
}

/// <summary>按点注册的订阅句柄:以句柄/点名声明兴趣,只收所注册点的变化;注册计数化(同点多次 Add 需对应次数 Remove 才停投)。</summary>
public interface IPointSubscription : IDisposable
{
    /// <summary>注册一个点(按名解析);点不存在返回 false(不注册,与"该点永不触发"的旧过滤语义一致)。</summary>
    bool Add(string tagId);
    /// <summary>注册一个点(按句柄)。</summary>
    bool Add(int handle);
    void Remove(string tagId);
    void Remove(int handle);
}

/// <summary>内部点位订阅队列选项。只影响当前消费者，不能全局放大其他订阅的内存。</summary>
public sealed class PointSubscriptionOptions
{
    public int QueueCapacity { get; init; } = 10_000;
    /// <summary>本订阅过载丢弃点变化时同步通知消费方；只在异常背压路径调用。</summary>
    public Action<TagChange>? ItemDropped { get; init; }
    /// <summary>
    /// false 表示被动观察者：照常收到点变化，但不让点位进入周期采集或原生订阅。
    /// 用于 QC 监控窗这类“只在用户刷新时才读 PLC”的消费方。
    /// </summary>
    public bool CreatesDemand { get; init; } = true;
}

/// <summary>
/// 统一数据中枢(= KEP 那层内存缓存):采集引擎写、北向读+订阅。
/// 所有北向读全部走缓存(扫一次供多家,绝不穿透 PLC)。
/// 订阅倒转(1M 点):只关心子集的消费方用 <see cref="SubscribePoints"/> 按点注册(成本 ∝ 注册点数);
/// 真全量消费方(历史/MQTT)用 <see cref="SubscribeAll"/>。
/// </summary>
public interface IDataHub
{
    /// <summary>加载工程时注册点位,建立点表。设备名含点号时必须显式传 deviceName,否则按需需求键被截断。</summary>
    void Register(string tagId, TagDef def, string? deviceName = null);

    bool TryGet(string tagId, out DataValue value);
    bool TryGetDef(string tagId, out TagDef def);

    /// <summary>
    /// 保留句柄、订阅和最后值，只替换点位定义(按设备应用复用既有条目)。实现须原子发布新定义；
    /// 值类型/数组形状变化时不得再以 Good 质量提供按旧类型编码的旧值。
    /// </summary>
    void UpdateDefinition(string tagId, TagDef def) { }

    /// <summary>引擎写入(单写者:每点位只被其所属设备循环写)。</summary>
    void Update(string tagId, in DataValue value);

    IReadOnlyCollection<string> ListTagIds();

    /// <summary>
    /// 全量订阅:收到所有点的全部变化(历史记录/MQTT 发布等)。name 供背压统计标识。Dispose 即退订。
    /// createsDemand=false 表示被动记录方：只记录已经被采集到的变化，不把全部点位拉成活动点。
    /// </summary>
    IDisposable SubscribeAll(Func<TagChange, ValueTask> onChange, string name = "",
        bool createsDemand = true);

    /// <summary>点注册订阅:仅收到已 Add 的点的变化(告警/计算/OPC UA 推送等子集消费方)。name 供背压统计标识。</summary>
    IPointSubscription SubscribePoints(
        Func<TagChange, ValueTask> onChange,
        string name = "",
        PointSubscriptionOptions? options = null);

    // ===== active-tag(KEP「遵循客户端」):没人引用的点不去轮设备 =====
    /// <summary>客户端引用了该点(REST 读 / OPC UA 读):刷新最近访问时间。</summary>
    void Touch(string tagId);
    /// <summary>钉住该点(OPC UA 监控项存在期间始终视为活动)。</summary>
    void Pin(string tagId);
    /// <summary>取消钉住(OPC UA 监控项删除)。</summary>
    void Unpin(string tagId);
    /// <summary>该点是否活动:被钉住,或在 windowMs 内被客户端引用过。</summary>
    bool IsActive(string tagId, int windowMs);

    /// <summary>该点是否被钉住(存在 OPC UA 监控项):推送过滤用——没被订阅的点不值得推。</summary>
    bool IsPinned(string tagId);
}
