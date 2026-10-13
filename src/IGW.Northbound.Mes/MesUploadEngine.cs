using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.Mes;

/// <summary>
/// MES 采集上报引擎(整合自 HsjmDcs 采集单元+步骤):每个启用任务一个独立循环
/// (每个采集周期现读判定点、条件满足就处理)→ 拼 JSON(自动平铺/结构体数组/自定义模板)
/// → HTTP POST → 解析响应 → 回写 PLC(走引擎写队列)+读回确认/重试。
/// 可靠性:上报失败不消费触发(下轮重发;握手时 PLC 持旗=数据不丢)。各任务独立 Task=故障隔离。
/// </summary>
public sealed partial class MesUploadEngine : IAsyncDisposable
{
    private readonly IDataHub _hub;
    private readonly MesUploadSettings _cfg;
    private readonly Func<string, object, WritePolicy, CancellationToken, Task<StatusCode>> _writeBack;
    private readonly ILogger _log;
    /// <summary>
    /// 不调接口、只把固定值回写 PLC 的流程(心跳)每半秒跑一次，逐条记「MES 回写节点完成」一天上百万条，淹没采集和状态记录、
    /// 拖慢日志查询。这种回写成功时只记第一次，之后每分钟汇总一条；调过 MES 接口的业务流程和失败照常逐条记。
    /// </summary>
    private readonly RepeatedSuccessLog _writebackLog;
    private static readonly TimeSpan WritebackSummaryWindow = TimeSpan.FromMinutes(1);
    private readonly HttpClient _http;
    private readonly MesDataRangeCatalog? _rangeCatalog;
    private readonly Func<string, ConnectionState?> _deviceState;
    private readonly ConcurrentDictionary<MesPayload, CompiledMesPayload> _compiledRanges =
        new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    private readonly HashSet<string> _demandTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScanDemandRegistry? _scanDemands;
    private readonly TimeSpan _shutdownTimeout;
    private readonly List<IScanDemandLease> _scanDemandLeases = new();
    /// <summary>分层采集下每个任务的按需层点位；触发条件满足后整批拉取一次。</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> _payloadTierTagIds = new();
    private readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> _conditionTagIds = new();   // 每周期判断前现读的判定点
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<OnDemandReadResult>>? _readNow;
    private readonly ConcurrentDictionary<string, Stat> _stats = new();
    private readonly MesLog? _mesLog;
    private readonly Func<string, CancellationToken, Task<string?>>? _saveLogPayload;
    private long _ok, _err;
    private volatile string _lastError = "";

    private sealed class Stat
    {
        public string Id = ""; public string Name = ""; public string Group = "";
        public bool Disabled;                                                         // 运行时停用(旧工程残留已下线配置),循环不启动
        public long Ok, Err; public string LastError = ""; public string LastAt = "";
        public readonly ConcurrentDictionary<string, NodeRun> Nodes = new();
        public Dictionary<string, NodeMeta> NodeCfg = new(StringComparer.Ordinal);   // 启动时从 Flow 构建:节点类型/手工阈值/标题
        public IReadOnlyList<string> Devices = Array.Empty<string>();                // 任务关联设备全名(通道.设备):根因折叠用
        public readonly FlowEventBuffer Events = new(256);                            // 节点流转事件(时间线回放)
        public long LastRealtimeStateTick;
        // 分层采集：上一轮流程没走完(如 MES 业务失败、回写失败)会在下个周期重试。握手未完成时 PLC 数据保持不变，
        // 短时间内的重试复用上次拉取的值，不再每 200ms 整批重读上千点。
        public volatile bool RetryPending;
        public long LastPayloadPullTick;
    }
    internal readonly record struct NodeMeta(bool Trigger, int ManualMs, string Title, bool Condition = false);
    private sealed class NodeRun
    {
        public volatile string State = "idle";
        public volatile string Info = "";
        public long AtTick = Environment.TickCount64;   // 进入当前状态的时刻(wait 重复标记不重置,AgeMs=已等多久)
        public long Hits;
        public volatile object? Detail;                 // 结构化归因(不可变对象整体替换;仅异常态携带)
        public long RunStartTick;                       // 进入 run 的时刻(执行耗时采样)
        public long LastFireTick;                       // 触发节点上次触发时刻(触发间隔采样)
        public long LastCheckTick;                      // 最近一次标记 wait 的时刻(条件节点每个周期判断一次)
        public long FailStreak;                         // 连续失败次数(归因面板"已重试 N 次")
        public readonly P2Quantile ExecP95 = new();     // 执行类基线:单次执行耗时
        public readonly P2Quantile GapP95 = new();      // 触发类基线:两次触发间隔
    }

    // 记录节点运行状态(任务循环写,REST 快照读;画布据此实时高亮"卡在哪/为什么")。
    // AtTick 仅在状态变化时重置(wait 每轮重复标记若重置,就永远测不出"等了多久");流转事件也只在状态变化时入缓冲。
    private void Mark(Stat st, string nodeId, string state, string info = "", object? detail = null)
    {
        var nr = st.Nodes.GetOrAdd(nodeId, _ => new NodeRun());
        long now = Environment.TickCount64;
        // 自动重试开始时保留上一轮失败态和错误详情。否则短周期任务会在
        // fail -> run -> fail 之间闪烁，监控快照恰好落在 run 窗口时会把真实故障隐藏掉。
        bool preserveFailureWhileRetrying = state == "run" && nr.State == "fail";
        string visibleState = preserveFailureWhileRetrying ? "fail" : state;
        bool transition = !string.Equals(nr.State, visibleState, StringComparison.Ordinal);
        if (state == "run") nr.RunStartTick = now;
        else if (state is "ok" or "fail" && nr.RunStartTick > 0)
        {
            nr.ExecP95.Add(Math.Max(0, now - nr.RunStartTick));   // 执行耗时采样(run/retry → ok/fail)
            nr.RunStartTick = 0;
        }
        if (state == "wait") nr.LastCheckTick = now;
        if (state == "fail") nr.FailStreak++;
        else if (state == "ok") nr.FailStreak = 0;
        if (!preserveFailureWhileRetrying) nr.Info = info;
        // 重试中(重进 run)保留上一次失败归因,面板不因重试窗口而"暂时失忆";ok/idle 由快照侧不携带
        nr.Detail = detail ?? (state == "run" ? nr.Detail : null);
        if (transition)
        {
            var evt = new MesFlowEvent(nodeId, visibleState, info, DateTime.UtcNow, Math.Max(0, now - nr.AtTick));
            st.Events.Add(evt); // 权威时间线必须先完成，再通知实时展示层。
            TryRaiseRuntimeEvent(st, evt);
            nr.AtTick = now;
        }
        else if (state != "wait" && !preserveFailureWhileRetrying) nr.AtTick = now;
        nr.State = visibleState;
        if (state is "ok" or "fail") nr.Hits++;
        TryRaiseState(st, transition);
    }

    // 记录一次采样检查:只累计次数/间隔，不改变流程状态。
    // 仅检查到条件不满足不属于“流程已触发”，否则每个采集周期都会制造 ok→run→wait 的假流转和画布闪烁。
    private void MarkFired(Stat st, string nodeId, string info)
    {
        var nr = st.Nodes.GetOrAdd(nodeId, _ => new NodeRun());
        long now = Environment.TickCount64;
        if (nr.LastFireTick > 0) nr.GapP95.Add(Math.Max(0, now - nr.LastFireTick));
        nr.LastFireTick = now;
        Interlocked.Increment(ref nr.Hits);
        TryRaiseState(st, force: false);
    }

    // 连续失败次数 +1 = 本次是第几次尝试(归因面板用;在 Mark 把 FailStreak++ 之前调用)
    private static long NextAttempt(Stat st, string nodeId) =>
        st.Nodes.TryGetValue(nodeId, out var nr) ? nr.FailStreak + 1 : 1;

    public long UploadsOk => Interlocked.Read(ref _ok);
    public long UploadsErr => Interlocked.Read(ref _err);
    public string LastError => _lastError;
    public ConcurrentQueue<string> Recent { get; } = new();
    public event Action<MesTaskStatus>? StateChanged;
    public event Action<string, string, MesFlowEvent>? RuntimeEventRaised;

    public IReadOnlyList<MesTaskStatus> Snapshot() =>
        _stats.Values.Select(SnapshotOf).ToList();

    private static MesTaskStatus SnapshotOf(Stat s) =>
        new(s.Id, s.Name, s.Group, !s.Disabled, Interlocked.Read(ref s.Ok), Interlocked.Read(ref s.Err), s.LastError, s.LastAt,
            s.Devices, s.Nodes.Select(kv => SnapNode(s, kv.Key, kv.Value)).ToList());

    private void TryRaiseState(Stat stat, bool force)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref stat.LastRealtimeStateTick);
        if (!force && now - previous < 250) return;
        if (Interlocked.CompareExchange(ref stat.LastRealtimeStateTick, now, previous) != previous) return;
        try { StateChanged?.Invoke(SnapshotOf(stat)); }
        catch (Exception ex) { _log.LogDebug(ex, "MES 实时状态订阅者异常，已隔离"); }
    }

    private void TryRaiseRuntimeEvent(Stat stat, MesFlowEvent evt)
    {
        try { RuntimeEventRaised?.Invoke(stat.Id, stat.Name, evt); }
        catch (Exception ex) { _log.LogDebug(ex, "MES 实时事件订阅者异常，已隔离"); }
    }

    // 单节点快照:卡住判定在这里完成(引擎循环不加分支)。触发节点等待时长按"距上次触发",比状态段起点更准。
    private static MesNodeStatus SnapNode(Stat st, string id, NodeRun nr)
    {
        long now = Environment.TickCount64;
        string state = nr.State;
        var meta = st.NodeCfg.TryGetValue(id, out var m) ? m : new NodeMeta(false, 0, id);
        long age = Math.Max(0, now - Volatile.Read(ref nr.AtTick));
        long fire = Volatile.Read(ref nr.LastFireTick);
        // Trigger 会在每个采集周期后回到 wait。等待时长必须表示“距最近一次周期触发”，
        // 不能沿用首次进入 wait 的时刻，否则高频任务运行越久，界面显示的等待时间反而越长。
        if (meta.Trigger && state == "wait" && fire > 0) age = Math.Max(0, now - fire);
        // 条件节点同理：每个采集周期都会重新判断一次，显示“距上次判断”，
        // 否则条件一直不满足时等待时间越积越长，看上去像没按采集周期在判断。
        long check = Volatile.Read(ref nr.LastCheckTick);
        if (meta.Condition && state == "wait" && check > 0) age = Math.Max(0, now - check);
        // 等待(等触发/等复位)是产线常态,永不判卡(StuckMath 对非 run 一律 false);
        // 只有 run(上报/回写等执行中停滞)判:执行耗时 P95×3 自动 + 手工阈值覆盖。GapP95 照采不判,留给后续"常态节奏"展示。
        var (stuck, baseline) = StuckMath.Evaluate(state, age, meta.ManualMs, nr.ExecP95.Ready, nr.ExecP95.Value);
        object? detail = state == "ok" && nr.Detail is MesDataQualityDetail
            ? nr.Detail
            : state is "ok" or "idle" ? null : nr.Detail;   // 成功但部分数据时保留质量详情
        return new MesNodeStatus(id, meta.Title, state, nr.Info, age, Volatile.Read(ref nr.Hits), stuck, baseline, Volatile.Read(ref nr.FailStreak), detail);
    }

    /// <summary>某任务的节点流转事件(时间正序;无此任务返回空)。</summary>
    public IReadOnlyList<MesFlowEvent> Timeline(string taskKey)
    {
        if (_stats.TryGetValue(taskKey, out var byId)) return byId.Events.Snapshot();

        Stat? unique = null;
        foreach (var status in _stats.Values)
        {
            if (!string.Equals(status.Name, taskKey, StringComparison.Ordinal)) continue;
            if (unique is not null) return Array.Empty<MesFlowEvent>();
            unique = status;
        }
        return unique?.Events.Snapshot() ?? Array.Empty<MesFlowEvent>();
    }

    public IReadOnlyList<MesLogRow> QueryLog(int limit) => _mesLog?.Query(limit) ?? Array.Empty<MesLogRow>();

    public MesUploadEngine(IDataHub hub, MesUploadSettings cfg,
        Func<string, object, CancellationToken, Task<bool>> writeBack, ILogger log, string? baseDir = null,
        MesDataRangeCatalog? catalog = null,
        Func<string, ConnectionState?>? deviceState = null,
        ScanDemandRegistry? scanDemands = null,
        TimeSpan? shutdownTimeout = null,
        HttpMessageHandler? httpMessageHandler = null,
        Func<string, CancellationToken, Task<string?>>? saveLogPayload = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<OnDemandReadResult>>? readNow = null)
        : this(hub, cfg,
            async (tagId, value, _, ct) => await writeBack(tagId, value, ct)
                ? StatusCode.Good
                : StatusCode.BadDeviceFailure,
            log, baseDir, catalog, deviceState, scanDemands, shutdownTimeout, httpMessageHandler, saveLogPayload, readNow)
    {
    }

    public MesUploadEngine(IDataHub hub, MesUploadSettings cfg,
        Func<string, object, WritePolicy, CancellationToken, Task<StatusCode>> writeBack, ILogger log, string? baseDir = null,
        MesDataRangeCatalog? catalog = null,
        Func<string, ConnectionState?>? deviceState = null,
        ScanDemandRegistry? scanDemands = null,
        TimeSpan? shutdownTimeout = null,
        HttpMessageHandler? httpMessageHandler = null,
        Func<string, CancellationToken, Task<string?>>? saveLogPayload = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<OnDemandReadResult>>? readNow = null)
    {
        var configuredTasks = cfg.Tasks.Where(t => t.FlowAvailable && t.Enabled).ToList();
        // 含本版本不支持配置的任务只停用它自己：不在这里抛异常，否则整个 MES 会被判为启动失败。
        var unsupportedTasks = configuredTasks
            .Select(task => (Task: task, Reason: MesUnsupportedConfig.Describe(task)))
            .Where(item => item.Reason is not null).ToList();
        var enabledTasks = configuredTasks.Except(unsupportedTasks.Select(item => item.Task)).ToList();
        foreach (var task in enabledTasks)
            MesFlowCommandValidation.Validate(task);
        _hub = hub; _cfg = cfg; _writeBack = writeBack; _log = log;
        _writebackLog = new RepeatedSuccessLog(WritebackSummaryWindow, WriteWritebackSummary);
        _readNow = readNow;
        _saveLogPayload = saveLogPayload;
        foreach (var task in enabledTasks.Where(IsInterfaceTask))
            ValidateListener(task);
        if (enabledTasks.Any(IsInterfaceTask))
            _interfaceInbox = new MesInterfaceInbox(Path.Combine(baseDir ?? AppContext.BaseDirectory, "mes-interface-inbox.db"));
        _rangeCatalog = catalog;
        _deviceState = deviceState ?? (_ => (ConnectionState?)null);
        _scanDemands = scanDemands;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        // PooledConnectionLifetime:连接定期轮换 → MES 域名换 IP/容灾漂移能被感知(长期运行数月不重启的场景必需)
        _http = new HttpClient(httpMessageHandler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };   // 每个上报节点单独计时,避免共享 HttpClient 的全局超时互相影响
        if (!string.IsNullOrWhiteSpace(cfg.AuthBearerToken))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.AuthBearerToken);
        if (!string.IsNullOrWhiteSpace(cfg.LogDbPath))
        {
            // 相对路径落到可写数据根(%ProgramData%\IGW),不再落安装目录
            string dbPath = Path.IsPathRooted(cfg.LogDbPath) || string.IsNullOrEmpty(baseDir)
                ? cfg.LogDbPath : Path.Combine(baseDir, cfg.LogDbPath);
            try { _mesLog = new MesLog(dbPath, 20000, cfg.QueueCapacity); }   // QueueCapacity=内存队列上限(原是死配置,现在真生效)
            catch (Exception ex) { _log.LogWarning("MES 上报日志(SQLite)启动失败,仅用内存日志:{m}", ex.Message); _mesLog = null; }
        }

        foreach (var task in enabledTasks)
        {
            Precompile(task.Payload, MesParameterRouting.UsesAutomaticTaskPayload(task));
            if (task.Flow is not null)
                foreach (var node in task.Flow.Nodes.Where(node => node.Type == "Upload"))
                    Precompile(node.Payload, !MesParameterRouting.HasExplicitTemplate(node.Payload, node.Http));
        }

        // 按需层必须在两条需求路径之外先算好：它决定触发后拉取哪些点，与是否有扫描需求登记无关。
        RegisterPayloadTiers(enabledTasks);
        RegisterConditionTags(enabledTasks);
        if (_scanDemands is null) PinDataDemand(enabledTasks);   // 兼容独立构造/旧测试
        else RegisterScanDemands(enabledTasks);

        foreach (var task in enabledTasks)
        {
            var t = task;
            _stats[t.Id] = BuildStat(t);
            _loops.Add(Task.Run(() => IsInterfaceTask(t) ? InterfaceEventLoopAsync(t) : TaskLoopAsync(t)));
            if (t.Heartbeat.Enabled && !string.IsNullOrWhiteSpace(t.Heartbeat.TagId))
                _loops.Add(Task.Run(() => HeartbeatLoopAsync(t)));
        }
        foreach (var (task, reason) in unsupportedTasks) _stats[task.Id] = BuildUnsupportedStat(task, reason!);
        int n = enabledTasks.Count;
        _log.LogInformation("MES 采集上报已启用:{n} 个任务(默认地址 {url})", n, string.IsNullOrWhiteSpace(cfg.DefaultMesUrl) ? "(无)" : cfg.DefaultMesUrl);
    }

    // 建任务状态:节点元数据(触发?/手工阈值/标题)+ 关联设备集(任务引用的所有点位路径前缀「通道.设备」∪ DeviceRef/GroupRef)
    private void PinDataDemand(IReadOnlyList<MesTask> tasks)
    {
        var knownTagIds = _hub.ListTagIds();
        var selectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) selectors.Add(path.Trim().TrimEnd('.'));
        }
        void AddConditions(IEnumerable<MesCondition> conditions)
        {
            foreach (var condition in conditions.Where(c => c.Source == "Tag")) Add(condition.TagId);
        }
        void AddWriteBack(MesWriteBack writeBack)
        {
            foreach (var item in writeBack.FromResponse) Add(item.TagId);
            foreach (var item in writeBack.Manual)
            {
                Add(item.TagId);
                Add(item.CopyFromTagId);
            }
        }
        void AddPayload(MesPayload payload)
        {
            foreach (var id in ExtraFieldTagIds(payload)) Add(id);
            if (MesPayloadModes.IsDataRanges(payload))
            {
                foreach (var range in payload.DataRanges)
                {
                    Add(range.SourcePath);
                    if (MesArrayLengthPolicy.IsAutomatic(range))
                        foreach (var id in range.ArrayLengthTagIds) Add(id);
                }
                return;
            }

            foreach (var field in payload.Fields) Add(field.TagId);
            foreach (var item in payload.Structs)
            {
                if (IsEnumStruct(item))
                {
                    foreach (var elementPath in EnumStructElements(item, knownTagIds))
                    {
                        // 有子结构体时成员在更深的组里：整个元素组下的点都要采
                        if (item.Structs.Count > 0) { Add(elementPath); continue; }
                        foreach (var member in item.Members)
                            Add(elementPath + "." + member.TagId);
                    }
                }
                else Add(item.BaseGroup);
                Add(item.CountTagId);
            }
        }

        foreach (var task in tasks)
        {
            Add(task.DeviceRef);
            Add(task.GroupRef);
            Add(task.Trigger.TagId);
            Add(task.Heartbeat.Enabled ? task.Heartbeat.TagId : null);
            Add(task.Handshake.MesFlagTagId);
            Add(task.Handshake.PlcFlagTagId);
            AddPayload(task.Payload);
            foreach (var step in task.Steps)
            {
                AddConditions(step.Conditions);
                AddWriteBack(step.WriteBack);
            }
            if (task.Flow is null) continue;
            foreach (var node in task.Flow.Nodes)
            {
                AddConditions(node.Conditions);
                foreach (var branch in node.Branches) AddConditions(branch.Conditions);
                Add(node.SwitchTag);
                AddPayload(node.Payload);
                AddWriteBack(node.WriteBack);
            }
        }

        foreach (var tagId in _hub.ListTagIds())
        {
            if (!selectors.Any(selector =>
                    tagId.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                    tagId.StartsWith(selector + ".", StringComparison.OrdinalIgnoreCase) ||
                    tagId.StartsWith(selector + "[", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (_demandTags.Add(tagId)) _hub.Pin(tagId);
        }
    }

    /// <summary>
    /// 算出每个启用分层采集的任务的按需层点位。只有登记在这里的任务才会在触发后拉取；
    /// 没有按需层(全部点都参与判定)或明确关闭分层的任务不登记，行为与旧版完全一致。
    /// </summary>
    private void RegisterPayloadTiers(IReadOnlyList<MesTask> tasks)
    {
        IReadOnlyList<string> sortedIds = _hub is DataHub coreHub
            ? coreHub.SortedTagIds
            : _hub.ListTagIds().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var task in tasks)
        {
            if (!UsesTieredAcquisition(task)) continue;
            if (!IsGatedByCondition(task))
            {
                _log.LogInformation(
                    "MES 任务 {task} 没有在上报前设置判定条件，每个周期都要上报，保持全部点位按 {period}ms 周期轮询",
                    task.Name, Math.Max(1, task.CollectIntervalMs));
                continue;
            }
            var tiers = ResolveTaskTagTiers(task, sortedIds);
            // 上报层为空(如心跳、只写固定值的握手)也按分层登记：只轮询判定点，不回退为整组周期轮询。
            _payloadTierTagIds[task.Id] = tiers.Payload;
            _log.LogInformation(
                "MES 任务 {task} 启用分层采集：常驻轮询 {trigger} 点(周期 {period}ms)，上报 {payload} 点改为触发后按需读取",
                task.Name, tiers.Trigger.Count, Math.Max(1, task.CollectIntervalMs), tiers.Payload.Count);
        }
    }

    /// <summary>采集周期任务里判断条件要用的点(条件、值分流、数组长度等)，每个周期判断前从 PLC 现读一次。</summary>
    private void RegisterConditionTags(IReadOnlyList<MesTask> tasks)
    {
        IReadOnlyList<string> sortedIds = _hub is DataHub coreHub
            ? coreHub.SortedTagIds
            : _hub.ListTagIds().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var task in tasks)
        {
            if (IsInterfaceTask(task) || task.Flow is not { Nodes.Count: > 0 } || !IsGatedByCondition(task)) continue;
            var trigger = ResolveTaskTagTiers(task, sortedIds).Trigger;
            if (trigger.Count > 0) _conditionTagIds[task.Id] = trigger;
        }
    }

    /// <summary>
    /// 条件满足就处理，所以判断必须用这一刻的真实值：常驻轮询的缓存可能还是上一轮 PLC 复位前的旧值，
    /// 会让同一件重复上报；PLC 忙时轮询也可能慢于采集周期。读取失败时照常用缓存值判断(质量异常的点不会满足条件)。
    /// </summary>
    private async Task RefreshConditionTagsAsync(MesTask task)
    {
        if (_readNow is null || !_conditionTagIds.TryGetValue(task.Id, out var tagIds)) return;
        try
        {
            var result = await _readNow(tagIds, _cts.Token);
            if (!result.Completed)
                _log.LogDebug("MES 任务 {task} 判断前现读 {count} 个条件点未全部成功：{msg}", task.Name, tagIds.Count, result.Error);
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _log.LogDebug(ex, "MES 任务 {task} 判断前现读条件点失败，按缓存值判断", task.Name);
        }
        catch (OperationCanceledException) { }
    }

    private void RegisterScanDemands(IReadOnlyList<MesTask> tasks)
    {
        IReadOnlyList<string> sortedIds = _hub is DataHub coreHub
            ? coreHub.SortedTagIds
            : _hub.ListTagIds().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var task in tasks)
        {
            // 分层采集只让判定链路常驻轮询；上报点位在触发满足后按需批量读取(见 PullPayloadTierAsync)。
            var tagIds = _payloadTierTagIds.ContainsKey(task.Id)
                ? ResolveTaskTagTiers(task, sortedIds).Trigger
                : ResolveTaskTagIds(task, sortedIds);
            if (tagIds.Count == 0) continue;
            _scanDemandLeases.Add(_scanDemands!.Acquire(
                $"mes:{task.Id}", tagIds, Math.Max(1, task.CollectIntervalMs),
                ownerName: task.Name, scope: task.GroupRef));
        }
    }

    /// <summary>
    /// 任务点位的两层划分。<see cref="Trigger"/> 是判定本轮是否要执行所必需的点，必须常驻轮询；
    /// <see cref="Payload"/> 只在触发条件满足后才需要取值，分层采集下改为按需批量读取。
    /// </summary>
    private sealed record TaskTagTiers(
        IReadOnlyCollection<string> Trigger,
        IReadOnlyCollection<string> Payload)
    {
        public IReadOnlyCollection<string> All => Trigger.Concat(Payload)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyCollection<string> ResolveTaskTagIds(MesTask task, IReadOnlyList<string> sortedIds) =>
        ResolveTaskTagTiers(task, sortedIds).All;

    private TaskTagTiers ResolveTaskTagTiers(MesTask task, IReadOnlyList<string> sortedIds)
    {
        var triggerResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triggerSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTrigger(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) triggerSelectors.Add(path.Trim().TrimEnd('.'));
        }

        void AddPayloadSelector(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) payloadSelectors.Add(path.Trim().TrimEnd('.'));
        }

        void AddConditions(IEnumerable<MesCondition> conditions)
        {
            foreach (var condition in conditions.Where(c => c.Source == "Tag")) AddTrigger(condition.TagId);
        }

        void AddWriteBack(MesWriteBack writeBack)
        {
            foreach (var item in writeBack.FromResponse) AddPayloadSelector(item.TagId);
            foreach (var item in writeBack.Manual)
            {
                AddPayloadSelector(item.TagId);
                AddPayloadSelector(item.CopyFromTagId);
            }
        }

        void AddPayload(MesPayload payload)
        {
            foreach (var id in ExtraFieldTagIds(payload)) AddPayloadSelector(id);
            if (MesPayloadModes.IsDataRanges(payload))
            {
                foreach (var range in payload.DataRanges)
                {
                    // 数组长度点决定这一轮要读多少元素，必须在按需读取之前就是最新值 → 常驻层。
                    if (MesArrayLengthPolicy.IsAutomatic(range))
                        foreach (var id in range.ArrayLengthTagIds) AddTrigger(id);
                    if (_rangeCatalog is not null && _rangeCatalog.TryGet(range.SourcePath, out var source))
                    {
                        var excludes = new HashSet<string>(range.Excludes ?? new(), StringComparer.OrdinalIgnoreCase);
                        AddCatalogGroup(source, "", excludes, payloadResolved);
                    }
                    else AddPayloadSelector(range.SourcePath);
                }
                return;
            }

            foreach (var field in payload.Fields) AddPayloadSelector(field.TagId);
            foreach (var item in payload.Structs)
            {
                if (IsEnumStruct(item))
                {
                    foreach (var elementPath in EnumStructElements(item, sortedIds))
                    {
                        // 有子结构体时成员在更深的组里：整个元素组下的点都要采
                        if (item.Structs.Count > 0) { AddSelectorMatches(sortedIds, elementPath, payloadResolved); continue; }
                        foreach (var member in item.Members)
                            AddSelectorMatches(sortedIds, elementPath + "." + member.TagId, payloadResolved);
                    }
                }
                else AddPayloadSelector(item.BaseGroup);
                // 结构体数量点同样决定读取范围，放常驻层。
                AddTrigger(item.CountTagId);
            }
        }

        // 只采集流程里实际配置用到的点。任务所属的绑定组(GroupRef)不再整组隐式加入：
        // 报文只由字段、结构体成员和数据范围拼成，现场一个工位组可有上千个点，
        // 整组读取会让 S7-1515 连续忙上数秒，同 PLC 的其他工位回写全部排队。
        AddTrigger(task.Trigger.TagId);
        AddTrigger(task.Heartbeat.Enabled ? task.Heartbeat.TagId : null);
        AddTrigger(task.Handshake.MesFlagTagId);
        AddTrigger(task.Handshake.PlcFlagTagId);
        AddPayload(task.Payload);
        foreach (var step in task.Steps)
        {
            AddConditions(step.Conditions);
            AddWriteBack(step.WriteBack);
        }
        if (task.Flow is not null)
        {
            foreach (var node in task.Flow.Nodes)
            {
                AddConditions(node.Conditions);
                foreach (var branch in node.Branches) AddConditions(branch.Conditions);
                // Switch 决定走哪条分支，属于判定链路 → 常驻层。
                AddTrigger(node.SwitchTag);
                AddPayload(node.Payload);
                AddWriteBack(node.WriteBack);
            }
        }

        foreach (string selector in triggerSelectors) AddSelectorMatches(sortedIds, selector, triggerResolved);
        foreach (string selector in payloadSelectors) AddSelectorMatches(sortedIds, selector, payloadResolved);
        // 同时出现在两层的点以常驻层为准，避免按需读取重复拉取已经在轮询的点。
        payloadResolved.ExceptWith(triggerResolved);
        return new TaskTagTiers(triggerResolved, payloadResolved);
    }

    /// <summary>
    /// 分层采集只对“先判定、满足才上报”的任务有意义。没有判定条件的任务每个周期都要上报，
    /// 改成每周期按需读取不但不减负，还多一层排队，因此保持原来的周期轮询。
    /// 接口事件任务由外部事件驱动，事件到达才上报，同样适合分层。
    /// </summary>
    internal static bool IsGatedByCondition(MesTask task)
    {
        if (IsInterfaceTask(task)) return true;
        if (task.Flow is { Nodes.Count: > 0 } flow)
        {
            var trigger = flow.Nodes.FirstOrDefault(node => node.Type == "Trigger");
            if (trigger is null) return false;
            var first = flow.Edges.Where(edge => edge.From == trigger.Id)
                .Select(edge => flow.Nodes.FirstOrDefault(node => node.Id == edge.To))
                .ToArray();
            // 触发后必须先经过条件节点，且条件的“都不满足”出口没有接到后续节点；
            // 否则不论条件如何，每个周期都会走到上报。
            return first.Length > 0 && first.All(node =>
                node is { Type: "Condition" } &&
                node.Branches.Any(branch => branch.Conditions.Count > 0) &&
                !flow.Edges.Any(edge => edge.From == node.Id && edge.FromPort == "default"));
        }
        return task.Steps.Count > 0 && task.Steps.All(step => step.Conditions.Count > 0);
    }

    /// <summary>未完成的触发重试时复用上次按需拉取结果的时长。</summary>
    internal const int RetryPullReuseMs = 2000;

    /// <summary>该流程节点执行时是否要读取按需层点位的当前值。</summary>
    internal static bool NeedsPayloadValues(MesNode node) => node.Type switch
    {
        "Upload" => true,
        "Writeback" or "ResponseWriteback" => node.WriteBack.Manual.Any(item => !string.IsNullOrWhiteSpace(item.CopyFromTagId)),
        _ => false,
    };

    /// <summary>本任务是否启用分层采集：任务级设置优先，未设置时跟随总开关。</summary>
    private bool UsesTieredAcquisition(MesTask task) =>
        task.TieredAcquisition ?? _cfg.TieredAcquisition;

    /// <summary>
    /// 界面“测试上报”的任务可能尚未运行，上报点位也可能不在周期轮询里；先读一次再拼报文。
    /// 读取失败只影响显示的值，不阻止测试。
    /// </summary>
    private async Task PullForTestAsync(MesTask task, CancellationToken ct)
    {
        if (_readNow is null) return;
        IReadOnlyList<string> sortedIds = _hub is DataHub coreHub
            ? coreHub.SortedTagIds
            : _hub.ListTagIds().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var tagIds = ResolveTaskTagIds(task, sortedIds);
        if (tagIds.Count == 0) return;
        try { await _readNow(tagIds, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "MES 测试上报前读取 {count} 点失败，使用缓存值", tagIds.Count); }
    }

    /// <summary>
    /// 触发条件满足后，立即拉取本任务的按需层点位，使随后的上报/回写读到触发时刻的真实值。
    /// 每轮流程只执行一次。读取失败不中断流程：已读到的点已发布，其余沿用缓存并记录原因，
    /// 由后续节点自身的质量判定决定是否上报。
    /// </summary>
    private async Task PullPayloadTierAsync(MesTask task, Stat st, CancellationToken ct)
    {
        if (_readNow is null) return;
        if (!_payloadTierTagIds.TryGetValue(task.Id, out var tagIds) || tagIds.Count == 0) return;
        if (st.RetryPending && Environment.TickCount64 - Interlocked.Read(ref st.LastPayloadPullTick) < RetryPullReuseMs)
            return;

        long started = Stopwatch.GetTimestamp();
        OnDemandReadResult result;
        try { result = await _readNow(tagIds, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            st.LastError = "按需拉取上报点位失败:" + ex.Message;
            _log.LogWarning(ex, "MES 任务 {task} 按需拉取 {count} 点失败，本轮沿用缓存值",
                task.Name, tagIds.Count);
            return;
        }

        int elapsedMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Exchange(ref st.LastPayloadPullTick, Environment.TickCount64);
        if (!result.Completed)
        {
            st.LastError = "按需拉取上报点位部分失败:" + result.Error;
            _log.LogWarning(
                "MES 任务 {task} 按需拉取上报点位部分失败：请求 {count} 点，取得 {good} 个有效值，耗时 {ms}ms：{msg}",
                task.Name, tagIds.Count, result.GoodCount, elapsedMs, result.Error);
        }
        else if (elapsedMs > Math.Max(200, task.CollectIntervalMs))
            _log.LogInformation(
                "MES 任务 {task} 按需拉取上报点位耗时 {ms}ms：请求 {count} 点，取得 {good} 个有效值",
                task.Name, elapsedMs, tagIds.Count, result.GoodCount);
    }

    private static void AddCatalogGroup(
        MesCatalogGroup group,
        string relativePrefix,
        HashSet<string> excludes,
        HashSet<string> resolved)
    {
        foreach (var tag in group.Tags)
        {
            string relative = relativePrefix + tag.Name;
            if (!excludes.Contains(relative)) resolved.Add(tag.TagId);
        }
        foreach (var child in group.Children)
            AddCatalogGroup(child, relativePrefix + child.Name + ".", excludes, resolved);
    }

    private static void AddSelectorMatches(IReadOnlyList<string> sortedIds, string selector, HashSet<string> resolved)
    {
        int lo = 0, hi = sortedIds.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (StringComparer.OrdinalIgnoreCase.Compare(sortedIds[mid], selector) < 0) lo = mid + 1;
            else hi = mid;
        }

        for (int i = lo; i < sortedIds.Count; i++)
        {
            string id = sortedIds[i];
            if (id.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(selector + ".", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(selector + "[", StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(id);
                continue;
            }
            if (StringComparer.OrdinalIgnoreCase.Compare(id, selector) > 0 &&
                !id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static Stat BuildStat(MesTask t)
    {
        var st = new Stat { Id = t.Id, Name = t.Name, Group = t.GroupRef ?? "" };
        var devs = new HashSet<string>(StringComparer.Ordinal);
        void Dev(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            int a = path.IndexOf('.'); if (a < 0) return;
            int b = path.IndexOf('.', a + 1);
            devs.Add(b < 0 ? path : path[..b]);   // 通道.设备(DeviceRef 本身就是两段,原样收)
        }
        void Conds(List<MesCondition> cs) { foreach (var c in cs.Where(c => c.Source == "Tag")) Dev(c.TagId); }
        void Wb(MesWriteBack w) { foreach (var x in w.FromResponse) Dev(x.TagId); foreach (var x in w.Manual) { Dev(x.TagId); Dev(x.CopyFromTagId); } }
        void Pay(MesPayload p)
        {
            if (MesPayloadModes.IsDataRanges(p))
                foreach (var range in p.DataRanges)
                {
                    Dev(range.SourcePath);
                    if (MesArrayLengthPolicy.IsAutomatic(range))
                        foreach (var id in range.ArrayLengthTagIds) Dev(id);
                }
            else
            {
                foreach (var f in p.Fields) Dev(f.TagId);
                foreach (var s in p.Structs) Dev(s.BaseGroup);
            }
        }
        Dev(t.DeviceRef); Dev(t.GroupRef); Dev(t.Trigger.TagId);
        Pay(t.Payload);
        foreach (var s in t.Steps) { Conds(s.Conditions); Wb(s.WriteBack); }
        if (t.Flow != null)
            foreach (var n in t.Flow.Nodes)
            {
                st.NodeCfg[n.Id] = new NodeMeta(n.Type == "Trigger", n.StuckThresholdMs, string.IsNullOrWhiteSpace(n.Title) ? n.Type : n.Title,
                    n.Type == "Condition");
                Conds(n.Conditions);
                foreach (var br in n.Branches) Conds(br.Conditions);
                Dev(n.SwitchTag);
                Pay(n.Payload);
                Wb(n.WriteBack);
            }
        st.Devices = devs.ToList();
        return st;
    }

    // 含本版本不支持配置的任务：不启动循环、不执行流程，只在运行快照里给出停用原因。
    private Stat BuildUnsupportedStat(MesTask task, string reason)
    {
        var st = BuildStat(task);
        st.Disabled = true;
        st.LastError = reason;
        string triggerId = task.Flow?.Nodes.FirstOrDefault(n => n.Type == "Trigger")?.Id ?? "trigger";
        Mark(st, triggerId, "fail", reason);
        _log.LogWarning("MES 任务 {task}：{reason}", task.Name, reason);
        return st;
    }

    // 未进入后续流程时保持固定采集节拍；一旦进入流程，则从完整流程结束时重新起算下一周期。
    // 固定节拍若因系统繁忙错过若干次，只跳到下一个未来时刻，不突发补跑。
    internal static long NextCollectDue(long previousDue, long completedAt, int intervalMs, bool enteredFlow)
    {
        long interval = Math.Max(1, intervalMs);
        if (enteredFlow) return completedAt + interval;
        long next = previousDue + interval;
        if (next > completedAt) return next;
        long skipped = (completedAt - next) / interval + 1;
        return next + skipped * interval;
    }

    internal static int ResolveHttpTimeoutMs(MesHttp http, int globalTimeoutMs)
    {
        if (http.TimeoutSec > 0) return Math.Clamp(http.TimeoutSec, 1, 600) * 1000;
        return Math.Clamp(globalTimeoutMs <= 0 ? 30000 : globalTimeoutMs, 1000, 600000);
    }

    private sealed class MesResponseTimeoutException(int timeoutMs)
        : TimeoutException($"等待 MES 响应超过 {timeoutMs / 1000} 秒，已取消请求并断开连接")
    {
        public int TimeoutMs { get; } = timeoutMs;
    }

    private async Task<(System.Net.HttpStatusCode Status, string Body)> PostJsonAsync(
        string url, string body, MesHttp options, CancellationToken cancellationToken,
        string stepId = "", string taskId = "", string taskName = "", string nodeName = "")
    {
        int timeoutMs = ResolveHttpTimeoutMs(options, _cfg.HttpTimeoutMs);
        long started = Stopwatch.GetTimestamp();
        using var activity = GatewayTelemetry.StartActivity("igw.interface.mes",
            new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["stepId"] = stepId,
                ["method"] = "POST",
                ["target"] = url
            }, ActivityKind.Client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        string sentBody = _interfaceContext.Value?.FreezeBody(stepId, body) ?? body;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sentBody, Encoding.UTF8, "application/json")
        };
        if (_interfaceContext.Value is { } listenerEvent)
        {
            request.Headers.Add("X-IGW-Message-Id", Uri.EscapeDataString(listenerEvent.Message.Id));
            request.Headers.Add("X-IGW-Step-Id", Uri.EscapeDataString(stepId));
        }
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            int httpStatusCode = (int)response.StatusCode;
            string status = httpStatusCode is >= 200 and < 300 ? "success" : "http_error";
            double durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string? payloadId = await SaveMesExchangeAsync(sentBody, responseBody,
                $"HTTP {httpStatusCode} {response.ReasonPhrase}".TrimEnd()).ConfigureAwait(false);
            if (status != "success") activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("igw.http.status_code", httpStatusCode);
            activity?.SetTag("igw.duration_ms", durationMs);
            LogMesTransport(taskId, taskName, stepId, nodeName, url, status, "completed", httpStatusCode, durationMs,
                payloadId: payloadId, requestBytes: Encoding.UTF8.GetByteCount(sentBody),
                responseBytes: Encoding.UTF8.GetByteCount(responseBody));
            return (response.StatusCode, responseBody);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // 取消并释放当前请求/响应流；未完整读取的 HTTP/1.1 连接不会回到连接池复用。
            activity?.SetStatus(ActivityStatusCode.Error, "timeout");
            string? payloadId = await SaveMesExchangeAsync(sentBody, "", $"等待响应超时（{timeoutMs} ms）").ConfigureAwait(false);
            LogMesTransport(taskId, taskName, stepId, nodeName, url, "timeout", "timeout", 0,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, payloadId: payloadId,
                requestBytes: Encoding.UTF8.GetByteCount(sentBody));
            throw new MesResponseTimeoutException(timeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            string? payloadId = await SaveMesExchangeAsync(sentBody, "", "请求已取消，未收到完整响应").ConfigureAwait(false);
            LogMesTransport(taskId, taskName, stepId, nodeName, url, "cancelled", "cancelled", 0,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, payloadId: payloadId,
                requestBytes: Encoding.UTF8.GetByteCount(sentBody));
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            string? payloadId = await SaveMesExchangeAsync(sentBody, "", $"传输失败：{ex.GetType().Name}").ConfigureAwait(false);
            LogMesTransport(taskId, taskName, stepId, nodeName, url, "transport_error", "failed", 0,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name, payloadId,
                Encoding.UTF8.GetByteCount(sentBody));
            throw;
        }
    }

    private void LogMesTransport(string taskId, string taskName, string stepId, string nodeName, string url, string status,
        string transportStatus, int httpStatusCode, double durationMs, string errorType = "", string? payloadId = null,
        int requestBytes = 0, int responseBytes = 0) =>
        GatewayTelemetry.Log(_log, status == "success" ? LogLevel.Information : LogLevel.Warning,
            "interface", "MES HTTP 请求完成", new Dictionary<string, object?>
            {
                ["eventKind"] = "mes_transport",
                ["module"] = "mes",
                ["feature"] = "http_upload",
                ["taskId"] = taskId,
                ["taskName"] = taskName,
                ["stepId"] = stepId,
                ["nodeName"] = nodeName,
                ["interfaceName"] = nodeName,
                ["direction"] = "outbound",
                ["protocol"] = "http",
                ["operation"] = "POST",
                ["method"] = "POST",
                ["target"] = url,
                ["httpStatusCode"] = httpStatusCode,
                ["transportStatus"] = transportStatus,
                ["status"] = status,
                ["durationMs"] = durationMs,
                ["errorType"] = errorType,
                ["payloadId"] = payloadId,
                ["requestBytes"] = requestBytes,
                ["responseBytes"] = responseBytes
            });

    private async Task<string?> SaveMesExchangeAsync(string requestBody, string responseBody, string responseStatus)
    {
        if (_saveLogPayload is null) return null;
        try
        {
            // 只保存发送内容和返回内容；状态码、耗时等已在日志字段里。没有返回正文(超时/断线)时记录原因。
            var content = new StringBuilder(requestBody.Length + responseBody.Length + 64)
                .AppendLine("===== 发送内容 =====")
                .AppendLine(requestBody)
                .AppendLine("===== 返回内容 =====")
                .Append(responseBody.Length > 0 ? responseBody : responseStatus)
                .ToString();
            // 关联报文保存在本机；保存故障不能改变 MES 上报、响应判定或 PLC 回写结果。
            return await _saveLogPayload(content, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _log.LogWarning("MES HTTP 关联报文保存失败，接口结果仍按实际响应处理：{errorType}", error.GetType().Name);
            return null;
        }
    }

    private void LogMesBusiness(string taskId, string taskName, string stepId, string nodeName,
        bool success, string businessStatus,
        int httpStatusCode) =>
        GatewayTelemetry.Log(_log, success ? LogLevel.Information : LogLevel.Warning,
            "interface", "MES 业务判定完成", new Dictionary<string, object?>
            {
                ["eventKind"] = "mes_business",
                ["module"] = "mes",
                ["feature"] = "business_result",
                ["taskId"] = taskId,
                ["taskName"] = taskName,
                ["stepId"] = stepId,
                ["nodeName"] = nodeName,
                ["interfaceName"] = nodeName,
                ["direction"] = "outbound",
                ["protocol"] = "http",
                ["operation"] = "evaluate_response",
                ["httpStatusCode"] = httpStatusCode,
                ["businessStatus"] = businessStatus,
                ["status"] = success ? "success" : "failure"
            });

    private async Task<bool> WaitCollectDueAsync(long due)
    {
        long delay = due - Environment.TickCount64;
        if (delay <= 0) return true;
        try { await Task.Delay(TimeSpan.FromMilliseconds(delay), _cts.Token); return true; }
        catch { return false; }
    }

    /// <summary>任务日志的作用域字段：日志中心按任务筛选、工程配置「查看采集上报日志」靠它们。</summary>
    private static Dictionary<string, object?> TaskLogScope(MesTask task) => new()
    {
        ["igw_module"] = "mes",
        ["igw_feature"] = "flow_engine",
        ["igw_task_id"] = task.Id,
        ["igw_task_name"] = task.Name,
        ["igw_scope"] = task.GroupRef ?? ""
    };

    /// <summary>写出一段时间内成功回写的汇总。在定时器线程上写，作用域要自己带上任务字段。</summary>
    private void WriteWritebackSummary(RepeatedSuccessSummary summary)
    {
        if (summary.Context.GetValueOrDefault("task") is not MesTask task) return;
        using var scope = _log.BeginScope(TaskLogScope(task));
        _log.LogInformation("MES 回写节点汇总：任务={task}，节点={node}，{count} 次成功，平均耗时 {avg}ms，最长 {max}ms",
            task.Name, summary.Context.GetValueOrDefault("node"), summary.Count,
            Math.Round(summary.AverageMs, 1), Math.Round(summary.MaxMs, 1));
    }

    private async Task TaskLoopAsync(MesTask task)
    {
        using var taskLogScope = _log.BeginScope(TaskLogScope(task));
        int interval = task.CollectIntervalMs <= 0 ? 1000 : task.CollectIntervalMs;
        var st = _stats[task.Id];
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 任务级变量(捕获响应,跨步/回写/模板复用)
        var condPrev = new Dictionary<MesCondition, string>();   // 「变化」条件的基准值(锁存:整组触发消费时才更新,变化不丢)
        bool flowMode = task.Flow is { Nodes.Count: > 0 };   // 有流程图 → 按图执行,否则线性步骤
        Dictionary<(string, string), MesEdge>? edgeMap = null;   // (From,FromPort)→边;启动时建一次,替代每 hop 线扫
        Dictionary<string, MesNode>? nodeMap = null;
        if (flowMode)
        {
            edgeMap = new(); foreach (var ed in task.Flow!.Edges) edgeMap.TryAdd((ed.From, ed.FromPort), ed);
            nodeMap = new(); foreach (var nd in task.Flow!.Nodes) nodeMap.TryAdd(nd.Id, nd);
        }
        long nextCollectDue = Environment.TickCount64 + interval;
        while (!_cts.IsCancellationRequested)
        {
            if (!await WaitCollectDueAsync(nextCollectDue)) break;
            if (flowMode)
            {
                var trig = task.Flow!.Nodes.FirstOrDefault(n => n.Type == "Trigger");
                if (trig == null) { nextCollectDue = NextCollectDue(nextCollectDue, Environment.TickCount64, interval, false); continue; }
                // 采集周期仅负责节拍，不再承载触发条件。旧工程即使仍序列化了 Trigger.Conditions 也忽略；
                // 所有 PLC 条件判断统一放在后续 Condition 节点，避免入口条件与画布条件重复、难以排查。
                MarkFired(st, trig.Id, "采集周期到达");
                await RefreshConditionTagsAsync(task);
                var run = await SafeRunFlow(task, trig, "out", st, vars, condPrev, edgeMap!, nodeMap!);
                nextCollectDue = NextCollectDue(nextCollectDue, Environment.TickCount64, interval, run.EnteredFlow);
                if (run.EnteredFlow) Mark(st, trig.Id, "wait", run.ConsumeTrigger ? "流程完成,等待下一采集周期" : "流程未完成,等待下一周期重试");
                else Mark(st, trig.Id, "wait", "流程条件未满足,等待下一采集周期");
                continue;
            }
            _flowValues.Value = new FlowValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            bool ranStep = false;
            bool payloadPulled = false;   // 分层采集：本周期第一次执行步骤前拉取一次上报点位
            for (int i = 0; i < task.Steps.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;
                var step = task.Steps[i];
                if (step.Conditions.Count == 0)
                {
                    ranStep = true;
                    if (!payloadPulled) { payloadPulled = true; await PullPayloadTierAsync(task, st, _cts.Token); }
                    await SafeRun(task, step, st, vars);   // 无条件=定时,每轮发(失败下轮再发)
                    continue;
                }
                // 满足条件就处理：每个采集周期(上一轮处理完后再等一个周期)只要条件满足就执行一次
                if (!step.Conditions.All(c => EvalCond(c, condPrev))) continue;
                ranStep = true;
                if (!payloadPulled) { payloadPulled = true; await PullPayloadTierAsync(task, st, _cts.Token); }
                bool uploaded = await SafeRun(task, step, st, vars);
                if (uploaded) ConsumeChanged(step.Conditions, condPrev);   // 仅上报成功才消费「变化」基准;失败留着下轮重发(不丢数据)
            }
            nextCollectDue = NextCollectDue(nextCollectDue, Environment.TickCount64, interval, ranStep);
        }
    }

    private async Task<bool> SafeRun(MesTask task, MesStep step, Stat st, Dictionary<string, string> vars)
    {
        try { return await RunStepAsync(task, step, st, vars); }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
            _lastError = ex.Message; st.LastError = ex.Message;
            _mesLog?.Add(task.Name, step.Name, false, 0, "异常:" + ex.Message);   // 连接被拒/超时等也入库
            _log.LogWarning(ex, "MES 步骤异常 {t}/{s}", task.Name, step.Name);
            return false;
        }
    }

    private readonly record struct FlowRunResult(bool ConsumeTrigger, bool EnteredFlow);

    private async Task<FlowRunResult> SafeRunFlow(MesTask task, MesNode start, string startPort, Stat st, Dictionary<string, string> vars,
        Dictionary<MesCondition, string> condPrev, Dictionary<(string, string), MesEdge> edgeMap, Dictionary<string, MesNode> nodeMap)
    {
        _flowValues.Value = new FlowValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var attemptPrevious = new Dictionary<MesCondition, string>(condPrev);
        try
        {
            var result = await RunFlowAsync(task, task.Flow!, start, startPort, st, vars, attemptPrevious, edgeMap, nodeMap);
            st.RetryPending = !result.ConsumeTrigger && result.EnteredFlow;
            if (result.ConsumeTrigger)
            {
                condPrev.Clear(); foreach (var pair in attemptPrevious) condPrev[pair.Key] = pair.Value;
            }
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
            _lastError = ex.Message; st.LastError = ex.Message;
            _mesLog?.Add(task.Name, "flow", false, 0, "异常:" + ex.Message);
            _log.LogWarning(ex, "MES 流程异常 {t}", task.Name);
            st.RetryPending = true;
            return new FlowRunResult(false, true);
        }
        finally { _flowValues.Value = null; }
    }

    // 按图执行:从 Trigger 沿连线走。Upload=拼报文+POST+解析+捕获;Branch=按上次成功/失败分流(success/fail 端口);
    // Writeback=分支回写;ResponseWriteback=上报成功判定通过后回写;Delay=延时。
    // ConsumeTrigger 表示是否消费触发边沿;EnteredFlow 区分“仅检查条件”和“已进入后续流程”。
    private async Task<FlowRunResult> RunFlowAsync(MesTask task, MesFlow flow, MesNode start, string startPort, Stat st, Dictionary<string, string> vars,
        Dictionary<MesCondition, string> condPrev, Dictionary<(string, string), MesEdge> edgeMap, Dictionary<string, MesNode> nodeMap)
    {
        string now = DateTime.Now.ToString("HH:mm:ss"); st.LastAt = now;
        string? curId = start.Id; string port = startPort;
        bool lastSuccess = true;
        bool calledInterface = false;   // 本轮是否调过 MES 接口：没调过的回写(心跳)成功时按分钟汇总日志
        bool enteredFlow = false;
        bool payloadPulled = false;   // 分层采集：本轮是否已按需拉取过上报点位(每轮只拉一次)
        var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement? lastResponse = _interfaceContext.Value?.Mapped;
        if (lastResponse.HasValue) FlattenResponse(lastResponse.Value, "", dic, overwrite: true);
        MesPayload? lastUploadPayload = null;
        CompiledMesPayload? lastUploadCompiled = null;
        IReadOnlyList<MesParameterRouting.Route>? lastUploadRoutes = null;
        MesHttp? lastUploadHttp = null;
        // 「固定值」节点：值累积到这里，后面的上报合并进 JSON 顶层；最近的数据来源是它时，回写按它写回 PLC
        var pendingFixed = new List<MesFixedValue>();
        MesNode? lastFixedNode = null;
        int guard = 0;
        var executed = new HashSet<string>();   // 每节点一次触发内最多跑一次
        while (curId != null && guard++ < 200)
        {
            if (!edgeMap.TryGetValue((curId, port), out var edge)) break;   // 走到尽头(字典查找,替代每 hop 线扫)
            if (!nodeMap.TryGetValue(edge.To, out var node)) break;
            if (!executed.Add(node.Id)) break;                         // 已在本次触发跑过 → 停(防 A→…→A 回环重复POST)
            if (node.Type != "Condition")
            {
                // 触发边沿和全部条件都已通过。第一个真正要用 PLC 数据的节点(上报、设备指令、复制点回写)执行前，
                // 整批拉取按需层点位，让它用的是触发时刻的真实值。只写固定值的回写分支(如握手确认、心跳)不需要读取，
                // 否则每次握手确认都要把整个上报范围重读一遍，S7-1515 上同 PLC 的其他工位全部被拖慢。
                // 用独立标志：enteredFlow 在 Condition 命中分支时就已置位，不能代表"尚未拉取"。
                if (!payloadPulled && NeedsPayloadValues(node))
                {
                    payloadPulled = true;
                    await PullPayloadTierAsync(task, st, _cts.Token);
                }
                enteredFlow = true;
                Mark(st, start.Id, "run", "本轮处理中");              // 真正进入上报/判定/回写后，采集节点才代表整轮执行中
                Mark(st, node.Id, "run");                              // 执行节点先标运行中(异常卡住时画布能看到停在哪)
            }
            port = "out";
            switch (node.Type)
            {
                case "Upload":
                {
                    calledInterface = true;
                    // “响应直写”只允许使用紧邻的这次上报结果；即使 HTTP 2xx 返回的不是 JSON，
                    // 也不能沿用同一流程中更早上报节点留下的响应和字段映射。
                    lastResponse = null;
                    _flowValues.Value!.Response = null;
                    lastUploadPayload = null;
                    lastUploadCompiled = null;
                    lastUploadRoutes = null;
                    lastUploadHttp = null;
                    lastFixedNode = null;
                    string url = string.IsNullOrWhiteSpace(node.Http.Url) ? _cfg.DefaultMesUrl : node.Http.Url;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        st.LastError = "无上报地址";
                        Mark(st, node.Id, "fail", "无上报地址(工程默认地址也为空)");
                        MarkDownstreamSkipped(st, node.Id, "未执行：上报节点无地址", edgeMap, nodeMap);
                        return new FlowRunResult(false, true);
                    }
                    BuiltBody built;
                    try
                    {
                        built = BuildHttpBody(node.Payload, node.Http, vars);
                        if (pendingFixed.Count > 0)
                        {
                            if (!MesParameterRouting.HasExplicitTemplate(node.Payload, node.Http))
                                MesFieldKeyModes.ValidateReservedRootNames(node.Payload, pendingFixed.Select(item => item.Name));
                            built = built with { Body = MergeFixedValues(built.Body, pendingFixed) };
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
                    {
                        Mark(st, node.Id, "fail", "上报数据生成失败：" + ex.Message);
                        MarkDownstreamSkipped(st, node.Id, "未执行：上报数据无效", edgeMap, nodeMap);
                        throw;
                    }
                    string body = built.Body;
                    body = _interfaceContext.Value?.FreezeBody(node.Id, body) ?? body;
                    string respText;
                    System.Net.HttpStatusCode status;
                    try
                    {
                        string nodeName = string.IsNullOrWhiteSpace(node.Title) ? node.Type : node.Title;
                        (status, respText) = await PostJsonAsync(
                            url, body, node.Http, _cts.Token, node.Id, task.Id, task.Name, nodeName);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
                    {
                        // 网络层失败(超时/连不上/域名解析):归因 StatusCode=0 + 异常摘要;异常继续上抛走 SafeRunFlow 统计
                        Mark(st, node.Id, "fail", "网络失败:" + ex.Message,
                            new MesHttpFailDetail(0, Trunc512(ex.Message), NextAttempt(st, node.Id), url));
                        MarkDownstreamSkipped(st, node.Id, "未执行：上报网络失败", edgeMap, nodeMap);
                        throw;
                    }
                    if ((int)status is < 200 or >= 300)   // 传输失败 → 不执行响应直写,不消费边沿,下轮重发
                    {
                        Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
                        _lastError = st.LastError = $"HTTP {(int)status}";
                        Push($"{now} {task.Name}/{Title(node)} ✗ HTTP {(int)status}");
                        _mesLog?.Add(task.Name, Title(node), false, (int)status, $"HTTP {(int)status}");
                        Mark(st, node.Id, "fail", $"HTTP {(int)status}(传输失败,下轮重发)",
                            new MesHttpFailDetail((int)status, Trunc512(respText), NextAttempt(st, node.Id), url));
                        MarkDownstreamSkipped(st, node.Id, "未执行：上报传输失败", edgeMap, nodeMap);
                        return new FlowRunResult(false, true);
                    }
                    respText = MapHttpResponse(respText, node.Http);
                    var (ok, d) = ParseResponse(respText, new MesStep { Http = node.Http });
                    LogMesBusiness(task.Id, task.Name, node.Id,
                        string.IsNullOrWhiteSpace(node.Title) ? node.Type : node.Title, ok,
                        d.TryGetValue(node.Http.SuccessKey, out string? nodeBusiness) ? nodeBusiness : ok.ToString().ToLowerInvariant(),
                        (int)status);
                    lastSuccess = ok; dic = d;
                    int businessStatus = (int)status;
                    string businessError = "";
                    if (TryParseResponseJson(respText, out var responseJson))
                    {
                        lastResponse = responseJson;
                        _flowValues.Value!.Response = responseJson;
                        lastUploadPayload = node.Payload;
                        lastUploadCompiled = built.Compiled;
                        lastUploadRoutes = built.ParameterRoutes;
                        lastUploadHttp = node.Http;
                        (businessStatus, businessError) = BusinessFailureOf(responseJson, (int)status);
                    }
                    foreach (var c in node.Captures)   // 捕获响应字段成变量
                        if (!string.IsNullOrWhiteSpace(c.VarName) && d.TryGetValue(c.ResponseField, out var cv))
                        { vars[c.VarName] = cv; _flowValues.Value!.Variables[c.VarName] = cv; }
                    if (ok) { Interlocked.Increment(ref _ok); Interlocked.Increment(ref st.Ok); }
                    else
                    {
                        Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
                        _lastError = st.LastError = string.IsNullOrWhiteSpace(businessError) ? "接口返回非成功" : businessError;
                    }
                    Push($"{now} {task.Name}/{Title(node)} {(ok ? "✓" : "✗判失败")}");
                    _mesLog?.Add(task.Name, Title(node), ok, 200, ok ? "OK" : "返回非成功:" + Trunc(respText));
                    var quality = ok && built.Quality?.Partial == true
                        ? new MesDataQualityDetail(built.Quality.OfflineSources, built.Quality.BadTags)
                        : null;
                    string info = ok && quality != null
                        ? $"部分数据:离线范围 {quality.OfflineSources.Count}，异常点位 {quality.BadTags.Count}"
                        : ok ? "" : "接口返回失败:" + (string.IsNullOrWhiteSpace(businessError) ? Trunc(respText) : Trunc(businessError));
                    Mark(st, node.Id, ok ? "ok" : "fail", info,
                        ok ? quality : new MesHttpFailDetail(businessStatus, Trunc512(respText), NextAttempt(st, node.Id), url));
                    // 上报不再拦路：不管业务判定成败都继续走后续节点，由「判成败」节点显式分流。
                    // 传输失败(非 2xx、超时、网络错误)仍在上面就已经返回并等下轮重试，
                    // 这里只处理“报文收到了、业务结果是 NG”的情形。
                    break;
                }
                case "Branch":   // 判成败:多条件(JudgeConds)优先 → 单条件(JudgeField)→ 用上报的成功判定
                {
                    bool One(MesJudgeCond c) => dic.TryGetValue(c.Field, out var v)
                        && CompareOp(v, string.IsNullOrWhiteSpace(c.Op) ? "Eq" : c.Op, c.Value);
                    bool ok; string how = "";
                    if (node.JudgeConds is { Count: > 0 })
                    {
                        bool any = string.Equals(node.JudgeCombine, "Any", StringComparison.OrdinalIgnoreCase);
                        ok = any ? node.JudgeConds.Any(One) : node.JudgeConds.All(One);
                        how = $"({(any ? "任一" : "全部")}满足 {node.JudgeConds.Count} 条)";
                    }
                    else if (!string.IsNullOrWhiteSpace(node.JudgeField))
                        ok = dic.TryGetValue(node.JudgeField, out var jv) && CompareOp(jv, string.IsNullOrWhiteSpace(node.JudgeOp) ? "Eq" : node.JudgeOp, node.JudgeValue);
                    else
                        ok = lastSuccess;
                    port = ok ? "success" : "fail";
                    Mark(st, node.Id, "ok", $"判{(ok ? "成功" : "失败")}{how} → 走 {port}");
                    break;
                }
                case "Condition":   // 多分支:第一个"全部条件满足"的分支走 b{i};都不满足走 default
                {
                    int b = node.Branches.FindIndex(br => br.Conditions.Count > 0 && br.Conditions.All(c => EvalCond(c, condPrev)));
                    if (b < 0 && node.Branches.SelectMany(branch => branch.Conditions).Any(condition =>
                        condition.Source == "Tag"
                            ? !MesArrayConditionEvaluator.TrySelect(_hub, condition, out _) && !TryResolveValue("Tag", condition.TagId, out _, out _)
                            : !TryResolveValue(condition.Source, condition.Path, out _, out _)))
                    {
                        Mark(st, node.Id, "wait", "条件来源缺失或质量无效，不能进入默认动作分支", BranchWaitDetail(node.Branches, condPrev));
                        return new FlowRunResult(!IsEventExecution, enteredFlow);
                    }
                    // 满足条件就处理：只要本周期条件满足就走对应分支，不再要求先复位(上升沿)。
                    // 上一轮处理完后要再等一个完整采集周期才会重新判断，PLC 有这段时间复位请求信号。
                    string np = b >= 0 ? $"b{b}" : "default";
                    if (b < 0 && !edgeMap.ContainsKey((node.Id, "default")))
                    {   // 都不满足且「默认」口没接线 = 这个节点就是在等分支条件 → 标 wait + 全分支逐条件归因(不同分支=不同目标值的等待场景)
                        Mark(st, node.Id, "wait", $"等待分支条件({node.Branches.Count} 路都未满足)", BranchWaitDetail(node.Branches, condPrev));
                        return new FlowRunResult(true, enteredFlow);
                    }
                    enteredFlow = true;
                    if (b >= 0) ConsumeChanged(node.Branches[b].Conditions, condPrev);   // 命中即消费该分支「变化」条件的基准
                    Mark(st, node.Id, "ok", b >= 0 ? $"命中分支{b + 1} → 走 b{b}" : "都不满足 → 走 default");
                    port = np;
                    break;
                }
                case "Switch":   // 读一个点,按值走对应端口;都不命中走 default
                {
                    string cur = ReadTagStr(node.SwitchTag);
                    int idx = node.Cases.FindIndex(v => v == cur);
                    port = idx >= 0 ? $"case{idx}" : "default";
                    Mark(st, node.Id, "ok", $"值={cur} → 走 {port}");
                    break;
                }
                case "Writeback":
                case "ResponseWriteback":
                {
                    long writebackStarted = Stopwatch.GetTimestamp();
                    var wfails = new List<MesWriteFailItem>();
                    int autoMatched = 0, autoChanged = 0;
                    bool autoOk = true;
                    if (node.Type == "ResponseWriteback" && lastResponse.HasValue && lastUploadPayload is not null)
                    {
                        CompiledMesPayload? compiled = MesPayloadModes.IsDataRanges(lastUploadPayload)
                            ? lastUploadCompiled ?? _compiledRanges.GetOrAdd(lastUploadPayload, CompileRanges)
                            : null;
                        var structureTags = compiled is null && lastUploadPayload.Structs.Count > 0
                            ? _hub.ListTagIds() : Array.Empty<string>();
                        var automatic = MesResponseWritePlanner.Plan(lastUploadPayload, lastResponse.Value,
                            lastUploadHttp?.ParameterDicName, structureTags, compiled, lastUploadHttp,
                            tagId => _hub.TryGetDef(tagId, out var definition) &&
                                     definition.Access != TagAccess.Read && !definition.IsArray &&
                                     definition.DataType != IgwDataType.Struct, lastUploadRoutes);
                        // 显式配置是覆盖项：同一目标不先自动写一次再立即被手工值覆盖。
                        var overrides = new HashSet<string>(node.WriteBack.FromResponse.Select(item => item.TagId)
                            .Concat(node.WriteBack.Manual.Select(item => item.TagId)), StringComparer.OrdinalIgnoreCase);
                        automatic = automatic.Where(item => !overrides.Contains(item.TagId)).ToArray();
                        // 事件执行失败会保留消息重试：接口回显的只读字段不尝试反写，否则消息永远无法提交。
                        // 显式响应映射仍由目标 PLC 驱动校验权限，不能静默忽略错误配置。
                        if (IsEventExecution)
                            automatic = automatic.Where(item => !_hub.TryGetDef(item.TagId, out var definition) ||
                                definition.Access != TagAccess.Read).ToArray();
                        var autoResult = await WriteAutomaticResponseAsync(task, node, automatic, wfails);
                        autoMatched = autoResult.Matched;
                        autoChanged = autoResult.Changed;
                        autoOk = autoResult.Success;
                    }
                    else if (node.Type == "ResponseWriteback" && lastFixedNode is not null)
                    {
                        // 前面是「固定值」节点(没有调接口)：把填了写回点位的固定值写回 PLC，自定义项照样覆盖
                        var overrides = new HashSet<string>(node.WriteBack.FromResponse.Select(item => item.TagId)
                            .Concat(node.WriteBack.Manual.Select(item => item.TagId)), StringComparer.OrdinalIgnoreCase);
                        var automatic = lastFixedNode.FixedValues
                            .Where(item => !string.IsNullOrWhiteSpace(item.TagId) && !overrides.Contains(item.TagId!))
                            .Select(item => new MesResponseWritePlanItem(item.TagId!.Trim(), MesFixedValueTypes.ToWriteText(item)))
                            .ToArray();
                        var autoResult = await WriteAutomaticResponseAsync(task, node, automatic, wfails);
                        autoMatched = autoResult.Matched;
                        autoChanged = autoResult.Changed;
                        autoOk = autoResult.Success;
                    }
                    bool explicitOk = (autoOk || !IsEventExecution) && await WriteBackAsync(task,
                        new MesStep { Name = Title(node), WriteBack = node.WriteBack }, dic, vars, wfails);
                    bool wok = autoOk && explicitOk;
                    string okInfo = node.Type == "ResponseWriteback"
                        ? $"{(lastFixedNode is not null && lastUploadPayload is null ? "固定值" : "接口响应")}已回写：自动匹配 {autoMatched} 点，实际更新 {autoChanged} 点，自定义 {node.WriteBack.FromResponse.Count + node.WriteBack.Manual.Count} 项"
                        : "";
                    Mark(st, node.Id, wok ? "ok" : "fail",
                        wok ? okInfo : $"回写失败 {wfails.Count} 点:{string.Join("、", wfails.Take(3).Select(f => LastSeg(f.TagId)))}",
                        wok ? null : new MesWriteFailDetail(wfails));
                    long writebackMs = (long)Stopwatch.GetElapsedTime(writebackStarted).TotalMilliseconds;
                    string writebackKey = task.Id + "/" + node.Id;
                    bool logWriteback = true;
                    if (wok && !calledInterface)
                        logWriteback = _writebackLog.Success(writebackKey, writebackMs,
                            new Dictionary<string, object?> { ["task"] = task, ["node"] = Title(node) });
                    else
                        _writebackLog.Interrupt(writebackKey);   // 失败照常逐条记：先写出攒着的汇总，时间线不乱
                    if (logWriteback)
                        _log.LogInformation(
                            "MES 回写节点完成：任务={task}，节点={node}，结果={result}，失败 {failed} 点，耗时 {ms}ms",
                            task.Name, Title(node), wok ? "成功" : "失败", wfails.Count, writebackMs);
                    if (!wok && IsEventExecution) return new FlowRunResult(false, true);
                    break;
                }
                case "FixedData":   // 固定值：不调接口，拼成 JSON；后面有上报就一起发，没有就当作接口返回给判成败/回写
                {
                    System.Text.Json.Nodes.JsonObject json;
                    try { json = FixedValuesJson(node.FixedValues); }
                    catch (InvalidDataException ex)
                    {
                        Mark(st, node.Id, "fail", ex.Message);
                        MarkDownstreamSkipped(st, node.Id, "未执行：固定值无效", edgeMap, nodeMap);
                        return new FlowRunResult(false, true);
                    }
                    foreach (var item in node.FixedValues)
                    {
                        pendingFixed.RemoveAll(existing => string.Equals(existing.Name.Trim(), item.Name.Trim(), StringComparison.Ordinal));
                        pendingFixed.Add(item);
                    }
                    using (var document = JsonDocument.Parse(json.ToJsonString()))
                    {
                        lastResponse = document.RootElement.Clone();
                        _flowValues.Value!.Response = lastResponse;
                    }
                    dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    FlattenResponse(lastResponse.Value, "", dic, overwrite: true);
                    lastSuccess = true;
                    lastUploadPayload = null;
                    lastUploadCompiled = null;
                    lastUploadRoutes = null;
                    lastUploadHttp = null;
                    lastFixedNode = node;
                    Mark(st, node.Id, "ok", $"{node.FixedValues.Count} 个固定值：{Trunc(json.ToJsonString())}");
                    break;
                }
                case "Delay":
                    if (node.DelayMs > 0) { try { await Task.Delay(node.DelayMs, _cts.Token); } catch { Mark(st, node.Id, "fail", "延时被停机/重载中断"); return new FlowRunResult(false, true); } }   // 被取消 → 不算消费,下轮重来
                    Mark(st, node.Id, "ok", $"{node.DelayMs}ms");
                    break;
                default:
                    // 未知或已下线的节点类型不能当作成功跳过，否则后续上报/回写会在缺少前置动作时照常执行。
                    Mark(st, node.Id, "fail", "不支持的节点类型：" + node.Type);
                    MarkDownstreamSkipped(st, node.Id, "未执行：前置节点类型不受支持", edgeMap, nodeMap);
                    return new FlowRunResult(false, true);
            }
            curId = node.Id;
        }
        return new FlowRunResult(true, enteredFlow);   // 跑到底(上报都 2xx)= 消费边沿
    }

    // 上报节点提前结束时，把以前执行过的后续节点恢复为“本轮未执行”，避免旧的响应直写错误继续挂红，
    // 造成“接口错误发生在响应直写”的错误归因。真正的接口错误只保留在 Upload 节点。
    private void MarkDownstreamSkipped(
        Stat st,
        string uploadNodeId,
        string reason,
        Dictionary<(string, string), MesEdge> edgeMap,
        Dictionary<string, MesNode> nodeMap)
    {
        var pending = new Queue<string>(edgeMap.Values
            .Where(edge => string.Equals(edge.From, uploadNodeId, StringComparison.Ordinal))
            .Select(edge => edge.To));
        var visited = new HashSet<string>(StringComparer.Ordinal) { uploadNodeId };
        while (pending.TryDequeue(out string? nodeId))
        {
            if (!visited.Add(nodeId) || !nodeMap.ContainsKey(nodeId)) continue;
            Mark(st, nodeId, "idle", reason);
            if (st.Nodes.TryGetValue(nodeId, out var skipped)) Interlocked.Exchange(ref skipped.FailStreak, 0);
            foreach (var edge in edgeMap.Values)
                if (string.Equals(edge.From, nodeId, StringComparison.Ordinal)) pending.Enqueue(edge.To);
        }
    }

    private static string Title(MesNode n) => string.IsNullOrWhiteSpace(n.Title) ? n.Type : n.Title;

    private string ReadTagStr(string tagId)
        => !string.IsNullOrWhiteSpace(tagId) && _hub.TryGet(tagId, out var dv) && dv.Value != null
            ? Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? "" : "";

    private bool EvalCond(MesCondition c, Dictionary<MesCondition, string> prev)
    {
        if (c.Source != "Tag")
        {
            if (!TryResolveValue(c.Source, c.Path, out var value, out _)) return false;
            if (c.Op != "Changed") return EvalScalar(c, value);
            string current = ScalarText(value!);
            if (!prev.TryGetValue(c, out var baseline)) { prev[c] = current; return false; }
            return current != baseline;
        }
        if (string.IsNullOrWhiteSpace(c.TagId)) return false;
        if (MesArrayConditionEvaluator.TrySelect(_hub, c, out var selected))
        {
            if (string.Equals(c.Op, "Changed", StringComparison.Ordinal))
            {
                if (selected.GoodCount != selected.Count) return false;
                string current = MesArrayConditionEvaluator.Fingerprint(selected);
                if (!prev.TryGetValue(c, out var baseline)) { prev[c] = current; return false; }
                return !string.Equals(current, baseline, StringComparison.Ordinal);
            }
            return MesArrayConditionEvaluator.Evaluate(selected, c.ArrayQuantifier,
                value => EvalScalar(c, value));
        }
        if (!TryResolveValue("Tag", c.TagId, out var scalar, out _)) return false;
        var dv = new DataValue(scalar, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow);
        if (string.Equals(c.Op, "Changed", StringComparison.Ordinal))
        {
            // 「变化」:与上次消费的基准不同即满足(锁存:整组条件触发消费时才更新基准,
            // 变化发生时其他条件未就绪也不丢——等就绪后仍会触发)。首见只记基准不触发(启动不误发)。
            string cur = Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? "";
            if (!prev.TryGetValue(c, out var last)) { prev[c] = cur; return false; }
            return !string.Equals(cur, last, StringComparison.Ordinal);
        }
        return EvalScalar(c, dv.Value);
    }

    private bool EvalScalar(MesCondition c, object? value)
    {
        if (value is null) return false;
        if (value is bool boolean && bool.TryParse(c.Value, out var expectedBoolean) && c.Op is "Eq" or "Ne" or "" or null)
            return c.Op == "Ne" ? boolean != expectedBoolean : boolean == expectedBoolean;
        if (string.Equals(c.Op, "In", StringComparison.Ordinal)) return MatchIn(value, c.Value);
        // 数值快路径:boxed 原值直接比数(免每任务每周期 Convert.ToString 分配);
        // 仅限已知数值操作符——其他(Regex/未来新增)一律走原字符串路径,语义不漂移
        if (c.Op is null or "" or "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le" &&
            TryNum(value, out double a) &&
            double.TryParse(c.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double b))
        {
            return c.Op switch { "Ne" => a != b, "Gt" => a > b, "Ge" => a >= b, "Lt" => a < b, "Le" => a <= b, _ => a == b };
        }
        return CompareOp(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", c.Op ?? "Eq", c.Value);
    }

    /// <summary>「∈任一」:期望值为逗号分隔列表(中英文逗号均可),当前值命中任一即满足;数值按数值比(3.0=3),否则按字符串。</summary>
    public static bool MatchIn(object? raw, string list)
    {
        if (raw is null || string.IsNullOrWhiteSpace(list)) return false;
        string cur = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        bool isNum = TryNum(raw, out double a);
        foreach (var part in list.Split(',', '，'))
        {
            var v = part.Trim();
            if (v.Length == 0) continue;
            if (isNum && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double b)) { if (a == b) return true; }
            else if (string.Equals(cur, v, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // 整组条件触发成功后,更新其中「变化」条件的基准值(锁存消费)
    private void ConsumeChanged(List<MesCondition> conds, Dictionary<MesCondition, string> prev)
    {
        foreach (var c in conds)
        {
            if (!string.Equals(c.Op, "Changed", StringComparison.Ordinal)) continue;
            if (c.Source != "Tag")
            {
                if (TryResolveValue(c.Source, c.Path, out var value, out _)) prev[c] = ScalarText(value!);
                continue;
            }
            if (MesArrayConditionEvaluator.TrySelect(_hub, c, out var selected))
                prev[c] = MesArrayConditionEvaluator.Fingerprint(selected);
            else if (_hub.TryGet(c.TagId, out var dv) && dv.Value is not null)
                prev[c] = Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    // 逐条件归因(结构化"期望 vs 实际"):实际值在求值现场取缓存,不额外读设备;Bad 质量标明而不展示陈旧值
    private MesWaitDetail WaitDetailOf(List<MesCondition> conds, Dictionary<MesCondition, string> prev)
    {
        var rows = new List<MesCondCheck>(conds.Count);
        foreach (var c in conds) rows.Add(CheckOf(c, prev, null));
        return new MesWaitDetail(rows);
    }

    // 条件节点全分支等待:每行带分支标签(不同分支=不同目标值的等待场景,全部展开给用户看差在哪)
    private MesWaitDetail BranchWaitDetail(List<MesCondBranch> branches, Dictionary<MesCondition, string> prev)
    {
        var rows = new List<MesCondCheck>();
        for (int i = 0; i < branches.Count; i++)
            foreach (var c in branches[i].Conditions)
                rows.Add(CheckOf(c, prev, string.IsNullOrWhiteSpace(branches[i].Label) ? $"分支{i + 1}" : branches[i].Label));
        return new MesWaitDetail(rows);
    }

    private MesCondCheck CheckOf(MesCondition c, Dictionary<MesCondition, string> prev, string? branch)
    {
        if (c.Source != "Tag")
        {
            bool resolved = TryResolveValue(c.Source, c.Path, out var value, out var error);
            return new MesCondCheck(c.Source + ":" + c.Path, OpSym(c.Op), c.Value,
                resolved ? ScalarText(value!) : error, EvalCond(c, prev), !resolved, branch);
        }
        if (MesArrayConditionEvaluator.TrySelect(_hub, c, out var selected))
        {
            string expected = string.Equals(c.Op, "Changed", StringComparison.Ordinal)
                ? "(数组内容发生变化)"
                : $"{c.ArrayQuantifier}:{c.Value}";
            return new MesCondCheck(c.TagId, OpSym(c.Op), expected,
                MesArrayConditionEvaluator.Describe(selected), EvalCond(c, prev),
                selected.BadCount == selected.Count, branch);
        }
        bool has = _hub.TryGet(c.TagId, out var dv);
        bool bad = !has || !dv.IsGood;
        string actual = !has ? "(点不存在)"
            : !dv.IsGood ? $"(质量 {dv.Quality.Class})"
            : dv.Value is null ? "(无值)"
            : Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? "";
        string expect = string.Equals(c.Op, "Changed", StringComparison.Ordinal) ? "(值发生变化)" : c.Value;
        return new MesCondCheck(c.TagId, OpSym(c.Op), expect, actual, EvalCond(c, prev), bad, branch);
    }

    // 触发条件未满足时,给出"卡在哪个条件"的人话:每个未满足条件显示 点名=当前值(需 op 期望值)
    private string CondInfo(List<MesCondition> conds, Dictionary<MesCondition, string> prev)
    {
        var sb = new StringBuilder("等待条件:");
        foreach (var c in conds)
        {
            if (EvalCond(c, prev)) continue;
            if (c.Source != "Tag")
            {
                TryResolveValue(c.Source, c.Path, out var value, out var error);
                sb.Append($" {c.Source}:{c.Path}={value ?? error}(需{OpSym(c.Op)}{c.Value})");
                continue;
            }
            string cur = MesArrayConditionEvaluator.TrySelect(_hub, c, out var selected)
                ? MesArrayConditionEvaluator.Describe(selected)
                : _hub.TryGet(c.TagId, out var dv) && dv.Value != null
                ? Convert.ToString(dv.Value, CultureInfo.InvariantCulture) ?? "空" : "无值";
            string tag = c.TagId; int i = tag.LastIndexOf('.'); if (i >= 0) tag = tag[(i + 1)..];
            sb.Append(string.Equals(c.Op, "Changed", StringComparison.Ordinal)
                ? $" {tag}={cur}(等变化)"
                : $" {tag}={cur}(需{OpSym(c.Op)}{c.Value})");
        }
        return sb.ToString();
    }
    private static string OpSym(string op) => op switch
    { "Ne" => "≠", "Gt" => ">", "Ge" => "≥", "Lt" => "<", "Le" => "≤", "Regex" => "~", "In" => "∈", "Changed" => "变化", _ => "=" };

    private static bool TryNum(object v, out double d)
    {
        switch (v)
        {
            case double x: d = x; return true;
            case float x: d = x; return true;
            case int x: d = x; return true;
            case short x: d = x; return true;
            case ushort x: d = x; return true;
            case uint x: d = x; return true;
            case long x: d = x; return true;
            case ulong x: d = x; return true;
            case byte x: d = x; return true;
            case sbyte x: d = x; return true;
            default: d = 0; return false;
        }
    }

    // 通用比较:当前值串 op 期望值串(点条件、判成败判据共用)
    private static bool CompareOp(string cur, string op, string val)
    {
        switch (op)
        {
            case "Ne": return cur != val;
            case "Regex": try { return Regex.IsMatch(cur, val); } catch { return false; }
            case "Gt": case "Ge": case "Lt": case "Le":
                if (double.TryParse(cur, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
                    return op switch { "Gt" => a > b, "Ge" => a >= b, "Lt" => a < b, _ => a <= b };
                return false;
            default: return cur == val;   // Eq
        }
    }

    // 返回:接口是否已返回(HTTP 2xx)。2xx→捕获+回写+消费边沿(不论业务判成/败);仅传输失败(非2xx/超时/连不上)返回 false,留边沿下轮重发。
    private async Task<bool> RunStepAsync(MesTask task, MesStep step, Stat st, Dictionary<string, string> vars)
    {
        if (_flowValues.Value is { } execution) execution.Response = null;
        if (step.DelayMsBefore > 0) { try { await Task.Delay(step.DelayMsBefore, _cts.Token); } catch { return false; } }
        string url = string.IsNullOrWhiteSpace(step.Http.Url) ? _cfg.DefaultMesUrl : step.Http.Url;
        if (string.IsNullOrWhiteSpace(url)) { st.LastError = "无上报地址"; return false; }

        string body = BuildBody(task, step, vars);
        body = _interfaceContext.Value?.FreezeBody("step:" + task.Steps.IndexOf(step), body) ?? body;
        string stepId = "step:" + task.Steps.IndexOf(step);
        var (status, respText) = await PostJsonAsync(
            url, body, step.Http, _cts.Token, stepId, task.Id, task.Name, step.Name);
        string now = DateTime.Now.ToString("HH:mm:ss"); st.LastAt = now;

        if ((int)status is < 200 or >= 300)
        {
            Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
            _lastError = st.LastError = $"HTTP {(int)status}";
            Push($"{now} {task.Name}/{step.Name} ✗ HTTP {(int)status}");
            _mesLog?.Add(task.Name, step.Name, false, (int)status, $"HTTP {(int)status}");
            return false;
        }
        respText = MapHttpResponse(respText, step.Http);
        var (success, dic) = ParseResponse(respText, step);
        LogMesBusiness(task.Id, task.Name, stepId, step.Name, success,
            dic.TryGetValue(step.Http.SuccessKey, out string? stepBusiness) ? stepBusiness : success.ToString().ToLowerInvariant(),
            (int)status);
        if (_flowValues.Value is { } current) current.Response = TryParseResponseJson(respText, out var parsed) ? parsed : null;
        // 接口已返回(HTTP 2xx)= MES 已明确处理:无论业务判成/判败,都执行 捕获 + 回写(用户要求),并消费触发边沿(不无限重发)。
        // 只有「传输失败」(上面的非 2xx / 超时 / 连不上抛异常)才不回写、留边沿下轮重发。
        foreach (var c in step.Captures)   // 捕获响应字段成变量(供本任务后续步骤的模板/回写复用)
            if (!string.IsNullOrWhiteSpace(c.VarName) && dic.TryGetValue(c.ResponseField, out var cv))
            { vars[c.VarName] = cv; if (_flowValues.Value is { } currentValues) currentValues.Variables[c.VarName] = cv; }
        bool wb = await WriteBackAsync(task, step, dic, vars);
        if (success)
        {
            Interlocked.Increment(ref _ok); Interlocked.Increment(ref st.Ok);
            Push($"{now} {task.Name}/{step.Name} ✓");
            if (!wb) st.LastError = "回写未完成(上报已成功)";
            _mesLog?.Add(task.Name, step.Name, true, 200, wb ? "OK" : "上报成功,回写未完成");
        }
        else   // 2xx 但业务判失败:仍回写(已做),计为失败供监控,消费边沿不重发
        {
            Interlocked.Increment(ref _err); Interlocked.Increment(ref st.Err);
            _lastError = st.LastError = "返回非成功(已回写)";
            Push($"{now} {task.Name}/{step.Name} ✗判失败(已回写) {Trunc(respText)}");
            _mesLog?.Add(task.Name, step.Name, false, 200, "返回非成功(已回写):" + Trunc(respText));
        }
        return !IsEventExecution || wb;
    }

    // 测试上报:发一次、拿响应、解析出「可用响应字段名」;不回写、不改统计、不消费边沿(供配置界面「测试上报」用)
    public async Task<object> TestRunAsync(MesTask task, CancellationToken ct)
    {
        await PullForTestAsync(task, ct);
        var step = task.Steps.Count > 0 ? task.Steps[0] : new MesStep();
        string url = string.IsNullOrWhiteSpace(step.Http.Url) ? _cfg.DefaultMesUrl : step.Http.Url;
        string body = BuildBody(task, step, new Dictionary<string, string>());
        if (string.IsNullOrWhiteSpace(url))
            return new { ok = false, url = "", request = body, status = 0, response = "(未填上报地址,运行时用工程默认)", fields = Array.Empty<string>() };
        try
        {
            string stepId = "step:" + Math.Max(0, task.Steps.IndexOf(step));
            var (status, respText) = await PostJsonAsync(
                url, body, step.Http, ct, stepId, task.Id, task.Name, step.Name);
            respText = MapHttpResponse(respText, step.Http);
            var (success, dic) = ParseResponse(respText, step);
            LogMesBusiness(task.Id, task.Name, stepId, step.Name, success,
                dic.TryGetValue(step.Http.SuccessKey, out string? business) ? business : success.ToString().ToLowerInvariant(),
                (int)status);
            return new { ok = (int)status is >= 200 and < 300 && success, url, request = body, status = (int)status, response = respText, fields = dic.Keys.ToArray() };
        }
        catch (Exception ex)
        {
            return new { ok = false, url, request = body, status = 0, response = "异常:" + ex.Message, fields = Array.Empty<string>() };
        }
    }

    // ---- 拼报文:范围模式整块生成;字段映射优先模板,自动模式由参数码/结构体开关决定字典路由 ----
    private string BuildBody(MesTask task, MesStep step, Dictionary<string, string> vars)
        => BuildHttpBody(task.Payload, step.Http, vars).Body;

    private string BuildBody(MesPayload payload, string template, Dictionary<string, string> vars)
        => BuildBodyResult(payload, template, vars).Body;

    private sealed record BuiltBody(string Body, MesPayloadBuildResult? Quality, CompiledMesPayload? Compiled = null,
        IReadOnlyList<MesParameterRouting.Route>? ParameterRoutes = null);

    private BuiltBody BuildBodyResult(MesPayload payload, string template, Dictionary<string, string> vars, bool automatic = true)
    {
        automatic &= string.IsNullOrWhiteSpace(template);
        MesFieldKeyModes.Validate(payload, StructElementPaths, automatic);
        if (MesPayloadModes.IsDataRanges(payload))
        {
            var compiled = payload.DataRanges.Any(MesArrayLengthPolicy.IsAutomatic)
                ? MesRangePayloadCompiler.Compile(payload, _rangeCatalog ?? throw new InvalidDataException("缺少上报数据目录"), ReadArrayLength)
                : _compiledRanges.GetOrAdd(payload, CompileRanges);
            var result = MesRangeJsonWriter.Build(compiled, payload.Append, _hub, _deviceState, payload.Number, payload.FixedValues);
            return new BuiltBody(result.Json, result, compiled);
        }

        var badTags = new HashSet<string>(StringComparer.Ordinal);
        // 数值统一处理：无穷大/无穷小改成 ±99999999、NaN 改成 null，小数按节点设置截取(数组点逐个元素处理)
        var wires = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);   // 本次已展开的 UDT 数组
        object? At(string path, double scale)
        {
            object? value = ReadPayloadPath(path, scale, badTags, wires);
            return MesNumberPolicy.Apply(value is ArrayRuntimeValue array ? MesArrayProjection.ToWireValue(array) : value, payload.Number);
        }
        BuiltBody Done(string body, IReadOnlyList<MesParameterRouting.Route>? routes = null) => new(body,
            new MesPayloadBuildResult(body, Array.Empty<string>(), badTags.ToArray(),
                payload.Fields.Count + payload.Structs.Sum(item => item.Members.Count)), ParameterRoutes: routes);

        if (!string.IsNullOrWhiteSpace(template))
        {
            string t = template;
            foreach (var f in payload.Fields)
            {
                string raw = RawVal(At(f.TagId, f.Scale));
                string key = string.IsNullOrWhiteSpace(f.FieldName) ? LastSeg(f.TagId) : f.FieldName;
                t = t.Replace("{" + key + "}", raw).Replace("{" + f.TagId + "}", raw);
            }
            foreach (var kv in payload.Append) t = t.Replace("{" + kv.Key + "}", JsonEsc(kv.Value));
            t = ReplaceFixedValues(t, payload.FixedValues);
            foreach (var kv in vars) t = t.Replace("{" + kv.Key + "}", JsonEsc(kv.Value));   // 变量复用
            return Done(t);
        }

        var sb = new StringBuilder("{");
        var dictionaryRoutes = automatic ? MesParameterRouting.Routes(payload, StructElementPaths).ToList() : [];
        bool first = true;
        foreach (var f in payload.Fields)
        {
            if (IsCoveredByStruct(f.TagId, payload.Structs)) continue;   // 被结构体覆盖的点不再作为普通字段重复输出
            if (automatic && !string.IsNullOrWhiteSpace(f.ParameterCode)) continue;
            if (!first) sb.Append(','); first = false;
            string key = MesFieldKeyModes.Resolve(payload, f);
            sb.Append('"').Append(JsonEsc(key)).Append("\":").Append(ToJson(At(f.TagId, f.Scale)));
        }
        foreach (var st in payload.Structs)   // 结构体按层拼：子结构体套在上一层元素里(见 MesUploadEngine.Structs.cs)
        {
            if (automatic && st.AsDictionary) continue;
            if (string.IsNullOrWhiteSpace(st.AliasName) || st.Members.Count == 0 && st.Structs.Count == 0) continue;
            if (!first) sb.Append(','); first = false;
            sb.Append('"').Append(JsonEsc(st.AliasName)).Append("\":");
            AppendStructValue(sb, st, st.BaseGroup, At, automatic);
        }
        if (automatic)
        {
            MesParameterRouting.Validate(dictionaryRoutes);
            if (dictionaryRoutes.Count > 0)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append('"').Append(MesFieldKeyModes.ParameterDictionaryName).Append("\":{");
                bool firstRoute = true;
                foreach (var route in dictionaryRoutes.DistinctBy(route => route.Key))
                {
                    if (!firstRoute) sb.Append(','); firstRoute = false;
                    sb.Append('"').Append(JsonEsc(route.Key)).Append("\":").Append(ToJson(At(route.TagId, route.Scale)));
                }
                sb.Append('}');
            }
        }
        foreach (var kv in payload.Append)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append('"').Append(JsonEsc(kv.Key)).Append("\":\"").Append(JsonEsc(kv.Value)).Append('"');
        }
        AppendFixedValues(sb, ref first, payload.FixedValues);   // 有类型的固定值仍放在根层
        sb.Append('}');
        return Done(sb.ToString(), dictionaryRoutes);
    }

    private void Precompile(MesPayload payload, bool automatic = true)
    {
        MesFieldKeyModes.Validate(payload, StructElementPaths, automatic);
        MesExtraFieldValidation.Validate(payload, automatic);
        if (MesPayloadModes.IsDataRanges(payload))
            _compiledRanges.TryAdd(payload, CompileRanges(payload));
    }

    private int ReadArrayLength(string id)
    {
        string device = string.Join('.', id.Split('.').Take(2));
        if (_deviceState(device) != ConnectionState.Connected || !_hub.TryGet(id, out var value) || !value.IsGood)
            throw new InvalidDataException($"数组长度点位无法读取或质量异常：{id}");
        return value.Value switch
        {
            byte v => v, sbyte v when v >= 0 => v, short v when v >= 0 => v, ushort v => v,
            int v when v >= 0 => v, uint v when v <= int.MaxValue => (int)v,
            long v when v is >= 0 and <= int.MaxValue => (int)v,
            ulong v when v <= int.MaxValue => (int)v,
            _ => throw new InvalidDataException($"数组长度点位 {id} 的值必须是 0..{int.MaxValue} 的整数，实际值：{value.Value ?? "null"}"),
        };
    }

    private CompiledMesPayload CompileRanges(MesPayload payload)
    {
        if (_rangeCatalog is null)
            throw new InvalidDataException("MES 数据范围模式缺少工程数据目录,请重新保存并应用工程。");
        return MesRangePayloadCompiler.Compile(payload, _rangeCatalog);
    }

    // 点是否被某结构体覆盖(数组 base[i].x 或对象 base.x),覆盖则不在平铺里重复出现
    private static bool IsCoveredByStruct(string path, List<MesStruct> structs)
    {
        foreach (var st in structs)
        {
            if (string.IsNullOrWhiteSpace(st.BaseGroup)) continue;
            if (string.Equals(path, st.BaseGroup, StringComparison.Ordinal) ||   // UDT 数组点位本身已按结构体上报
                path.StartsWith(st.BaseGroup + ".", StringComparison.Ordinal) ||
                path.StartsWith(st.BaseGroup + "[", StringComparison.Ordinal) ||
                path.StartsWith(st.BaseGroup + "_", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private object? CurAt(string path, double scale, ISet<string>? badTags = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        object? v = MesPayloadValuePolicy.Read(_hub, path, out bool bad);
        if (bad) badTags?.Add(path);
        if (v is null) return null;
        if (scale != 1.0 && scale != 0 && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d * scale;
        return v;
    }

    // Enum 结构体元素枚举:兼容前缀[下标]与西门子 DB 导入器生成的前缀_下标，
    // 扫描直接挂成员点的叶子组，按数值下标+路径稳定排序，返回各元素组完整路径。
    private static bool IsEnumStruct(MesStruct item) =>
        string.Equals(item.Kind, "Array", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.CountMode, "Enum", StringComparison.OrdinalIgnoreCase);

    // 元素组不一定直接挂成员点：UDT 数组展开的 名字[下标] 组里先是子结构体组(如 GlueData)，
    // 点位在更深一层。沿点位路径往上找到名字是 前缀[下标](或 基准组.数字)的那一层就是元素组。
    internal static List<string> EnumStructElements(MesStruct item, IEnumerable<string> tagIds)
    {
        string baseGroup = item.BaseGroup;
        if (string.IsNullOrWhiteSpace(baseGroup)) return new();
        int dot = baseGroup.LastIndexOf('.');
        string prefixName = dot >= 0 ? baseGroup[(dot + 1)..] : baseGroup;   // OCVStruct
        string scope = dot >= 0 ? baseGroup[..(dot + 1)] : "";               // …BatteryOCVData.
        var keyOf = new Dictionary<string, long>(StringComparer.Ordinal);    // 元素组路径 → 排序键
        foreach (var tag in tagIds)
        {
            if (scope.Length > 0 && !tag.StartsWith(scope, StringComparison.Ordinal)) continue;
            string numericPrefix = baseGroup + ".";
            // 从直接挂该点的组开始逐层往上，找到的第一层元素组即为所属元素
            for (int md = tag.LastIndexOf('.'); md > 0 && md >= scope.Length; md = tag.LastIndexOf('.', md - 1))
            {
                string parent = tag[..md];
                if (keyOf.ContainsKey(parent)) break;
                if (parent.StartsWith(numericPrefix, StringComparison.Ordinal))
                {
                    string remainder = parent[numericPrefix.Length..];
                    if (!remainder.Contains('.') && int.TryParse(remainder, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int numericIndex))
                    {
                        keyOf[parent] = numericIndex;
                        break;
                    }
                }
                int gd = parent.LastIndexOf('.');
                string groupName = gd >= 0 ? parent[(gd + 1)..] : parent;    // OCVStruct[01]
                // 元素组必须与基准数组同父；内层同名数组不能成为外层元素。
                if (gd + 1 == scope.Length &&
                    MesIndexedGroupName.TryParse(groupName, out var candidateBase, out var index) &&
                    string.Equals(candidateBase, prefixName, StringComparison.Ordinal))
                {
                    keyOf[parent] = index;
                    break;
                }
            }
        }
        int start = item.StartIndex;
        int count = Math.Max(0, item.FixedCount);
        long endExclusive = (long)start + count;
        return keyOf.Keys
            .Where(path => keyOf[path] >= start && keyOf[path] < endExclusive)
            .OrderBy(path => keyOf[path])
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string RawVal(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonEsc(s),
        System.Collections.IList or IDictionary<string, object?> => JsonSerializer.Serialize(v),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string ToJson(object? v) => v switch
    {
        null => "null",
        // 原生数组点(含结构体数组)：输出 JSON 数组，而不是类型名
        ArrayRuntimeValue array => JsonSerializer.Serialize(MesArrayProjection.ToWireValue(array)),
        bool b => b ? "true" : "false",
        float or double or decimal or int or long or short or byte or uint or ulong or ushort or sbyte => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "0",
        // 数组点经数值处理后是 object?[] / 字典
        System.Collections.IList or IDictionary<string, object?> => JsonSerializer.Serialize(v),
        _ => "\"" + JsonEsc(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "") + "\"",
    };

    private static (bool ok, Dictionary<string, string> dic) ParseResponse(string text, MesStep step)
    {
        var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool ok = true;
            if (!string.IsNullOrEmpty(step.Http.SuccessKey))
            {
                if (TryResolveResponseField(root, step.Http.SuccessKey, out var sk) ||
                    TryResolveResponseField(root, "value." + step.Http.SuccessKey, out sk))
                    ok = string.Equals(ElemStr(sk), step.Http.SuccessValue, StringComparison.OrdinalIgnoreCase);
                else if (string.Equals(step.Http.SuccessKey, "code", StringComparison.OrdinalIgnoreCase) &&
                         (TryResolveResponseField(root, "value.isSuccess", out var wrappedSuccess) ||
                          TryResolveResponseField(root, "isSuccess", out wrappedSuccess)))
                    ok = wrappedSuccess.ValueKind == JsonValueKind.True ||
                         string.Equals(ElemStr(wrappedSuccess), "true", StringComparison.OrdinalIgnoreCase);
                else if (string.Equals(step.Http.SuccessKey, "code", StringComparison.OrdinalIgnoreCase) &&
                         (TryResolveResponseField(root, "value.httpStatusCode", out var wrappedStatus) ||
                          TryResolveResponseField(root, "httpStatusCode", out wrappedStatus)))
                    ok = int.TryParse(ElemStr(wrappedStatus), out int code) && code is >= 200 and < 300;
                else
                    ok = false;
            }
            // 成功与否只看用户在「响应与成功判定」里配的 SuccessKey/SuccessValue。
            // isSuccess 不再做隐式的二次判定：它只是一个普通响应字段，要拿它分流就把它填进
            // 成功判定，或者用「判成败」节点显式判。
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject()) dic[p.Name] = ElemStr(p.Value);
                if (!string.IsNullOrEmpty(step.Http.ParameterDicName) &&
                    MesResponseWritePlanner.TryGetProperty(root, step.Http.ParameterDicName, out var pd) && pd.ValueKind == JsonValueKind.Object)
                    FlattenResponse(pd, "", dic, overwrite: true);
                if (MesResponseWritePlanner.TryGetProperty(root, "backResult", out var backResult) && backResult.ValueKind == JsonValueKind.Object)
                    FlattenResponse(backResult, "", dic, overwrite: false);
                // ASP.NET ObjectResult 包装：显式捕获/手工响应回写也应继续使用 mesFlag、structures[0].isNg
                // 这类业务字段名，而不要求用户改成 value.backResult.mesFlag。
                if (MesResponseWritePlanner.TryGetProperty(root, "value", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                {
                    if (!string.IsNullOrEmpty(step.Http.ParameterDicName) &&
                        MesResponseWritePlanner.TryGetProperty(wrapped, step.Http.ParameterDicName, out var wrappedDic) &&
                        wrappedDic.ValueKind == JsonValueKind.Object)
                        FlattenResponse(wrappedDic, "", dic, overwrite: true);
                    if (MesResponseWritePlanner.TryGetProperty(wrapped, "backResult", out var wrappedBackResult) &&
                        wrappedBackResult.ValueKind == JsonValueKind.Object)
                        FlattenResponse(wrappedBackResult, "", dic, overwrite: false);
                    FlattenResponse(wrapped, "", dic, overwrite: false);
                }
                FlattenResponse(root, "", dic, overwrite: false);
            }
            return (ok, dic);
        }
        catch { return (false, dic); }
    }

    // ASP.NET ObjectResult 常见响应会再包一层 value：HTTP 传输本身是 200，真实业务状态和错误在
    // value.httpStatusCode / value.errorMessage。画布归因必须显示真实业务状态，而不是误写成 HTTP 200。
    private static (int StatusCode, string Error) BusinessFailureOf(JsonElement root, int transportStatus)
    {
        int status = transportStatus;
        foreach (string path in new[] { "value.httpStatusCode", "httpStatusCode" })
            if (TryResolveResponseField(root, path, out var statusElement) &&
                int.TryParse(ElemStr(statusElement), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            { status = parsed; break; }

        foreach (string path in new[] { "value.errorMessage", "errorMessage", "value.exceptionMessage", "exceptionMessage" })
            if (TryResolveResponseField(root, path, out var errorElement))
            {
                string error = ElemStr(errorElement);
                if (!string.IsNullOrWhiteSpace(error)) return (status, error);
            }
        return (status, "");
    }

    private static bool TryParseResponseJson(string text, out JsonElement response)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            response = document.RootElement.Clone();
            return true;
        }
        catch
        {
            response = default;
            return false;
        }
    }

    // 响应字段支持大小写不敏感的 a.b[0].c 路径；旧配置的平级字段仍保持兼容。
    private static bool TryResolveResponseField(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = segment;
            int bracket = segment.IndexOf('[');
            if (bracket >= 0) name = segment[..bracket];
            if (name.Length > 0 && !MesResponseWritePlanner.TryGetProperty(value, name, out value)) return false;
            while (bracket >= 0)
            {
                int close = segment.IndexOf(']', bracket + 1);
                if (close < 0 || value.ValueKind != JsonValueKind.Array ||
                    !int.TryParse(segment[(bracket + 1)..close], out int index) || index < 0 || index >= value.GetArrayLength()) return false;
                value = value[index];
                bracket = segment.IndexOf('[', close + 1);
            }
        }
        return true;
    }

    private static void FlattenResponse(JsonElement value, string path, Dictionary<string, string> dic, bool overwrite)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    FlattenResponse(property.Value, string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name, dic, overwrite);
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (var element in value.EnumerateArray())
                    FlattenResponse(element, $"{path}[{index++}]", dic, overwrite);
                break;
            default:
                if (string.IsNullOrEmpty(path)) return;
                if (overwrite) dic[path] = ElemStr(value);
                else dic.TryAdd(path, ElemStr(value));
                break;
        }
    }

    private static string ElemStr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.GetRawText(),
    };

    private readonly record struct AutomaticWriteResult(bool Success, int Matched, int Changed);

    // 自动响应回写先批量下发，再统一轮询确认，避免结构体几十个成员逐点等待扫描周期。
    private async Task<AutomaticWriteResult> WriteAutomaticResponseAsync(
        MesTask task,
        MesNode node,
        IReadOnlyList<MesResponseWritePlanItem> plan,
        List<MesWriteFailItem> fails)
    {
        // Service 的 ErrorInfo 可能包含远端调用栈，长度远超 PLC String[n]。
        // 写入前按目标点声明长度裁剪；否则整批里只有固定 MESFlag 能写成功，结构体字符串会被驱动拒绝。
        var normalized = plan.Select(item => new MesResponseWritePlanItem(
            item.TagId, NormalizeWriteValue(item.TagId, item.Value))).ToArray();
        int matched = normalized.Length;
        var pending = normalized.Where(item => !TryReadCurrentValue(item.TagId, out var current) || current is null ||
                                         !ValuesEquivalent(current, item.Value)).ToList();
        int changed = pending.Count;
        if (pending.Count == 0) return new AutomaticWriteResult(true, matched, changed);

        // 运行引擎是唯一写入确认层：它会按真实权限写入、直接回读，并只对不一致字段重写最多 10 次。
        // MES 层不再额外等待扫描缓存或重复整批写，避免一次业务回写平白增加数秒延迟。
        // 这批字段互不依赖，一起交给引擎，由它合成少数几个协议请求并统一回读核对。原先逐个 await，
        // 现场 79 条 × 6 个字段的参数表是 474 次串行往返，一次响应直写 3～4 秒。
        // 显式配置的写入(握手位等)仍在本方法返回后才按顺序执行。
        var outcomes = await Task.WhenAll(pending.Select(async item =>
        {
            try
            {
                StatusCode status = await _writeBack(item.TagId, item.Value, WritePolicy.Default, _cts.Token);
                return (Item: item, Status: status, Error: (Exception?)null);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return (Item: item, Status: StatusCode.BadNotConnected, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "MES 自动响应回写下发失败 {tag}", item.TagId);
                return (Item: item, Status: StatusCode.BadCommFailure, Error: (Exception?)ex);
            }
        }));
        if (_cts.IsCancellationRequested) return new AutomaticWriteResult(false, matched, changed);
        var failed = outcomes.Where(outcome => !outcome.Status.IsGood).ToList();

        foreach (var failure in failed)
        {
            MesWriteFailItem detail = CreateWriteFailure(
                failure.Item.TagId, failure.Item.Value, failure.Status, failure.Error);
            fails.Add(detail);
            LogWriteFailure(task.Name, Title(node), detail, automatic: true);
        }
        return new AutomaticWriteResult(failed.Count == 0, matched, changed);
    }

    private string NormalizeWriteValue(string tagId, string value)
    {
        if (!_hub.TryGetDef(tagId, out var def))
        {
            if (!TryResolveArrayAliasDefinition(tagId, out def)) return value;
        }
        // 数值/布尔值保持响应字符串，统一交给 Engine.WriteAsync 按真实 TagDef 强类型转换；
        // MES 层只负责 String/WString 的 PLC 声明长度裁剪，避免两套类型转换规则漂移。
        if (def.DataType is not (IgwDataType.String or IgwDataType.WString)) return value;
        return Convert.ToString(PlcWriteValueNormalizer.Normalize(def, value), CultureInfo.InvariantCulture) ?? value;
    }

    private bool TryResolveArrayAliasDefinition(string tagId, out TagDef definition)
    {
        definition = null!;
        if (!ArrayAliasResolver.TryParse(tagId, out var alias) ||
            !_hub.TryGetDef(alias.ParentTagId, out var parent) || parent.Array is null)
            return false;

        IgwDataType dataType = parent.Array.ElementType;
        int? stringLength = parent.Array.StringLength;
        var schema = parent.Array.Struct;
        foreach (string segment in alias.MemberPath)
        {
            var field = schema?.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.JsonName, segment, StringComparison.OrdinalIgnoreCase));
            if (field is null || field.Array is not null) return false;
            dataType = field.DataType;
            stringLength = field.StringLength;
            schema = field.Struct;
        }
        definition = new TagDef
        {
            Name = tagId,
            DataType = dataType,
            StringLength = stringLength,
            TextEncoding = parent.TextEncoding,
            Access = TagAccess.ReadWrite,
        };
        return true;
    }

    private bool TryReadCurrentValue(string tagId, out object? value)
    {
        if (_hub.TryGet(tagId, out var direct))
        {
            value = direct.Value;
            return true;
        }
        value = null;
        if (!ArrayAliasResolver.TryParse(tagId, out var alias) ||
            !_hub.TryGet(alias.ParentTagId, out var parent) || parent.Value is not ArrayRuntimeValue current ||
            alias.Indices.Count != current.Shape.Rank)
            return false;
        try
        {
            int offset = checked((int)current.Shape.GetLinearOffset(alias.Indices));
            foreach (string segment in alias.MemberPath)
            {
                if (current is not StructArrayValue structure) return false;
                var field = structure.Schema.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.JsonName, segment, StringComparison.OrdinalIgnoreCase));
                if (field is null || field.Array is not null ||
                    !structure.Members.TryGetValue(field.Name, out current!)) return false;
            }
            value = current.GetBoxedValue(offset);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValuesEquivalent(object current, string expected)
    {
        string actual = Convert.ToString(current, CultureInfo.InvariantCulture) ?? "";
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
        return decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal left) &&
               decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal right) && left == right;
    }

    // 回写:响应字段→点 + 固定/复制写,可读回确认+重试。返回是否全部写成功;fails 非空时收集失败明细(流程节点归因用)。
    private async Task<bool> WriteBackAsync(MesTask task, MesStep step, Dictionary<string, string> dic, Dictionary<string, string> vars,
        List<MesWriteFailItem>? fails = null)
    {
        bool confirm = step.WriteBack.Confirm && task.ConfirmRewrite;
        int retries = Math.Max(0, step.WriteBack.Retries);
        bool all = true;
        bool required = IsEventExecution;

        void MissingSource(string tagId, string source)
        {
            all = false;
            string error = $"MES 必需回写数据缺失或质量无效: {source}，未下发到 {tagId}";
            var status = StatusCode.BadDecodeError;
            var detail = new MesWriteFailItem(tagId, "", "", 0, status.Code, status.SymbolicName, "取值", error);
            fails?.Add(detail);
            _lastError = error;
            if (_stats.TryGetValue(task.Id, out var st)) st.LastError = error;
            LogWriteFailure(task.Name, step.Name, detail, automatic: false);
        }

        foreach (var w in step.WriteBack.FromResponse)
        {
            if (string.IsNullOrWhiteSpace(w.TagId)) continue;
            if (!dic.TryGetValue(w.ResponseField, out var val))
            {
                if (required) { MissingSource(w.TagId, "响应字段 " + w.ResponseField); return false; }
                continue;
            }
            if (!await WriteOneAsync(w.TagId, val, confirm, retries, task, step, fails))
            {
                if (required) return false;
                all = false;
            }
        }
        foreach (var m in step.WriteBack.Manual)
        {
            if (string.IsNullOrWhiteSpace(m.TagId)) continue;
            string? val = m.Value;
            if (!string.IsNullOrWhiteSpace(m.CopyFromTagId))
            {
                if (_hub.TryGet(m.CopyFromTagId, out var src) && src.Value is not null && (!required || src.IsGood))
                    val = Convert.ToString(src.Value, CultureInfo.InvariantCulture);
                else if (required)
                {
                    MissingSource(m.TagId, "复制点 " + m.CopyFromTagId);
                    return false;
                }
            }
            if (val is null)
            {
                if (required) { MissingSource(m.TagId, "固定值"); return false; }
                continue;
            }
            foreach (var kv in vars) val = val.Replace("{" + kv.Key + "}", kv.Value);   // 手动值支持 {变量名}
            if (!await WriteOneAsync(m.TagId, val, confirm, retries, task, step, fails))
            {
                if (required) return false;
                all = false;
            }
        }
        return all;
    }

    private async Task<bool> WriteOneAsync(string tagId, string val, bool confirm, int retries, MesTask task, MesStep step, List<MesWriteFailItem>? fails = null)
    {
        val = NormalizeWriteValue(tagId, val);
        bool hasDefinition = _hub.TryGetDef(tagId, out var definition);
        string targetType = hasDefinition ? definition.DataType.ToString() : "未知";
        string valueForLog = hasDefinition && definition.DataType is IgwDataType.String or IgwDataType.WString
            ? $"(内容省略，UTF16长度={val.Length})"
            : Trunc512(val);
        // 逐点明细只在调试级别记录：大流程每轮要写几十个点，信息级会让日志量暴涨(失败仍逐点告警)。
        _log.LogDebug(
            "MES 回写准备下发：任务={task}，节点={step}，Tag={tag}，目标类型={type}，写入值={value}，确认={confirm}，配置重试={retries}",
            task.Name, step.Name, tagId, targetType, valueForLog, confirm, retries);
        StatusCode status;
        Exception? error = null;
        try
        {
            status = await _writeBack(tagId, val, new WritePolicy(confirm, retries), _cts.Token);
            if (status.IsGood)
            {
                string actual = TryReadCurrentValue(tagId, out var current) && current is not null
                    ? Convert.ToString(current, CultureInfo.InvariantCulture) ?? ""
                    : "";
                _log.LogDebug(
                    "MES 回写完成：任务={task}，节点={step}，Tag={tag}，状态={status}，期望={expected}，当前缓存/回读={actual}",
                    task.Name, step.Name, tagId, status, valueForLog,
                    hasDefinition && definition.DataType is IgwDataType.String or IgwDataType.WString
                        ? $"(内容省略，UTF16长度={actual.Length})"
                        : string.IsNullOrEmpty(actual) ? "(未读到)" : Trunc512(actual));
                return true;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            status = StatusCode.BadCommFailure;
            error = ex;
            _log.LogDebug(ex, "MES 回写下发失败 {tag}", tagId);
        }

        MesWriteFailItem detail = CreateWriteFailure(tagId, val, status, error);
        LogWriteFailure(task.Name, step.Name, detail, automatic: false);
        fails?.Add(detail);
        return false;
    }

    private MesWriteFailItem CreateWriteFailure(
        string tagId,
        string expected,
        StatusCode status,
        Exception? exception)
    {
        string actual = TryReadCurrentValue(tagId, out var current) && current is not null
            ? Convert.ToString(current, CultureInfo.InvariantCulture) ?? ""
            : "";
        string stage = WriteFailureStage(status);
        int attempts = WriteFailureAttempts(tagId, status);
        string error = exception?.Message ?? status.Code switch
        {
            0x80B80000 => "协议写入已返回成功，但重写 10 次后回读仍与期望值不一致",
            0x803A0000 => "点位不可回读，无法执行强制写后验证",
            0x803B0000 => "点位声明或远端服务器拒绝写入",
            0x801F0000 => "远端 OPC UA 用户没有该节点的写权限",
            0x80740000 => "写入值的 OPC UA Variant 类型与目标节点数据类型不一致",
            0x80070000 => "MES 响应值无法转换为目标 PLC 点位的声明类型",
            _ => $"驱动返回 {status}",
        };
        return new MesWriteFailItem(tagId, Trunc512(expected), Trunc512(actual), attempts,
            status.Code, status.SymbolicName, stage, Trunc512(error));
    }

    private void LogWriteFailure(string taskName, string stepName, MesWriteFailItem detail, bool automatic)
    {
        _log.LogWarning(
            "MES {kind}回写失败 {task}/{step} {tag} 状态={status} 期望={expected} 回读={actual} 阶段={stage} 尝试={attempts} 错误={error}",
            automatic ? "自动响应" : "", taskName, stepName, detail.TagId,
            $"{detail.StatusName}(0x{detail.StatusCode:X8})", detail.Written,
            string.IsNullOrEmpty(detail.ReadBack) ? "(未读到)" : detail.ReadBack,
            detail.Stage, detail.Attempts, detail.Error);
    }

    private static string WriteFailureStage(StatusCode status) => status.Code switch
    {
        0x808A0000 or 0x800A0000 or 0x80050000 => "连接/通信",
        0x80100000 => "写入队列",
        0x802C0000 or 0x80B90000 => "并发写入",
        0x803A0000 => "回读权限检查",
        0x803B0000 or 0x801F0000 => "写权限检查",
        0x80B80000 => "写后回读验证",
        0x80070000 => "值转换",
        _ => "协议写入",
    };

    private int WriteFailureAttempts(string tagId, StatusCode status)
    {
        if (_hub.TryGetDef(tagId, out var def) &&
            ((status == StatusCode.BadNotWritable && def.Access == TagAccess.Read) ||
             (status == StatusCode.BadNotReadable && def.Access == TagAccess.Write)))
            return 0;
        return status.Code switch
        {
            0x80B80000 => 11,
            0x808A0000 or 0x80100000 or 0x802C0000 or 0x80B90000 => 0,
            _ => 1,
        };
    }

    private async Task HeartbeatLoopAsync(MesTask task)
    {
        var hb = task.Heartbeat;
        var successLog = new HeartbeatSuccessLogWindow();
        void LogSummary(HeartbeatSuccessSummary? summary)
        {
            if (summary is null) return;
            _log.LogInformation(
                "MES 系统心跳{phase}：任务={task}，Tag={tag}，成功回调次数={count}，汇总窗口={elapsed}ms（仅心跳写入回调，非 MES 业务交易数）",
                summary.First ? "首次成功" : summary.Recovered ? "恢复" : "成功汇总",
                task.Name, hb.TagId, summary.Count, summary.ElapsedMilliseconds);
        }
        int v = 0, max = hb.MaxLimit <= 0 ? 1000 : hb.MaxLimit, interval = hb.IntervalMs <= 0 ? 1000 : hb.IntervalMs;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(interval, _cts.Token); } catch { break; }
                try
                {
                    var status = await _writeBack(hb.TagId, v, WritePolicy.Default, _cts.Token);
                    if (_cts.IsCancellationRequested) break;
                    if (status.IsGood) LogSummary(successLog.Success(Environment.TickCount64));
                    else
                    {
                        LogSummary(successLog.Failure(Environment.TickCount64));
                        _log.LogWarning("MES 系统心跳失败：任务={task}，Tag={tag}，状态={status}", task.Name, hb.TagId, status);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested) break;
                    LogSummary(successLog.Failure(Environment.TickCount64));
                    _log.LogWarning("MES 系统心跳失败：任务={task}，Tag={tag}，异常类型={exceptionType}",
                        task.Name, hb.TagId, ex.GetType().Name);
                }
                v = v >= max ? 0 : v + 1;
            }
        }
        finally { LogSummary(successLog.Drain(Environment.TickCount64)); }
    }

    private void Push(string line) { Recent.Enqueue(line); while (Recent.Count > 100 && Recent.TryDequeue(out _)) { } }
    private static string LastSeg(string path) { int i = path.LastIndexOf('.'); return i >= 0 ? path[(i + 1)..] : path; }
    private static string Trunc(string s) => s.Length > 200 ? s[..200] + "…" : s;
    private static string Trunc512(string s) => s.Length > 512 ? s[..512] + "…" : s;   // 归因载荷:响应片段截断,防快照膨胀
    private static string JsonEsc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var allLoops = Task.WhenAll(_loops);
        bool loopsStopped = false;
        try
        {
            await allLoops.WaitAsync(_shutdownTimeout);
            loopsStopped = true;
        }
        catch (TimeoutException)
        {
            // 回写委托/第三方 I/O 即使错误地忽略取消，也不能无限卡住“保存并应用”。
            _log.LogError("MES 停止超过 {ms}ms，已取消旧任务并继续热重载", (long)_shutdownTimeout.TotalMilliseconds);
        }
        catch { loopsStopped = true; }
        _writebackLog.Dispose();   // 停止前补记攒着的回写汇总
        try
        {
            if (_mesLog != null)
            {
                try { await _mesLog.DisposeAsync().AsTask().WaitAsync(_shutdownTimeout); }
                catch (TimeoutException)
                {
                    _log.LogError("MES 日志停止超过 {ms}ms，继续热重载", (long)_shutdownTimeout.TotalMilliseconds);
                }
            }
        }
        finally
        {
            foreach (var lease in _scanDemandLeases) lease.Dispose();
            _scanDemandLeases.Clear();
            foreach (var tagId in _demandTags) _hub.Unpin(tagId);
            _demandTags.Clear();
            _http.Dispose();
            if (loopsStopped) { _interfaceInbox?.Dispose(); _cts.Dispose(); }
            else
            {
                // 忽略取消的旧委托若稍后返回，循环会看到已取消并退出；届时再释放 CTS。
                _ = allLoops.ContinueWith(_ => { _interfaceInbox?.Dispose(); _cts.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
