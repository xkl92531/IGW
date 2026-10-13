using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

public sealed record AcquisitionDiagnosticAddress(string TagId, string? Address);
public sealed record AcquisitionDiagnosticDevice(ScanTimingDeviceSummary Summary,
    IReadOnlyList<ScanTimingRecord> Records, IReadOnlyList<AcquisitionDiagnosticAddress> Addresses);
public sealed record AcquisitionDiagnosticsSnapshot(DateTimeOffset CapturedAt, string? Build,
    string ProjectName, int ProjectSchemaVersion, IReadOnlyList<AcquisitionDiagnosticDevice> Devices,
    IReadOnlyList<NetworkProbeSample> Network, ConnectionIncidentSnapshot Connections, bool Truncated);

/// <summary>分层采集按需拉取的结果：读到的点数、Good 点数，以及首个失败原因(全部成功为 null)。</summary>
public sealed record OnDemandReadResult(int GoodCount, int SampleCount, string? Error)
{
    public bool Completed => Error is null;
}

public sealed record DeviceStatus(
    string Name,
    ConnectionState State,
    int TagCount,
    int ActiveTags,
    bool DemandActive,
    DeviceStats Stats,
    DeviceConnectionDetail? Detail = null,
    ConnectionIncident? Diagnostic = null);

/// <summary>设备诊断统计(对标 KEP 设备诊断计数)。</summary>
public sealed record DeviceStats(long ReadsOk, long ReadsErr, long WritesOk, long WritesErr, int LastScanMs, string LastError, double SuccessRate)
{
    public int ConfiguredMinPeriodMs { get; init; }
    public double ActualAveragePeriodMs { get; init; }
    public int ActualP95PeriodMs { get; init; }
    public int ActualP99PeriodMs { get; init; }
    public long OverduePeriods { get; init; }
    public long ConsecutiveOverruns { get; init; }
    public int PlannedBlocks { get; init; }
    public double SamplesPerSecond { get; init; }
    public bool PeriodHealthy { get; init; } = true;
    public int ArrayTags { get; init; }
    public long ArrayElements { get; init; }
    public long ArrayGood { get; init; }
    public long ArrayUncertain { get; init; }
    public long ArrayBad { get; init; }
    public long ArrayBytes { get; init; }
    public int ArrayBlocks { get; init; }
    public string ArrayFailedRanges { get; init; } = "";
    public int WriteQueueP95Ms { get; init; }
    public int WriteResponseP95Ms { get; init; }
    public int WriteVerificationP95Ms { get; init; }
    public int PeakWriteQueueDepth { get; init; }
    public long SameSessionRetries { get; init; }
    public long BatchDownshifts { get; init; }
}

/// <summary>每设备一个采集循环(独立 Task + 独立连接):连接→重连(指数退避)→分组轮询→死区→写缓存。</summary>
public sealed class DeviceLoop
{
    private static readonly ArrayDriverCapabilities UnknownArrayCapabilities =
        new(false, false, false, false, false, false, int.MaxValue, int.MaxValue);
    private readonly IDeviceDriver _primary;
    private readonly IDeviceDriver? _secondary;
    private IDeviceDriver _active;
    private readonly IReadOnlyList<TagBinding> _bindings;
    private readonly IReadOnlyDictionary<string, TagBinding> _bindingsByTagId;
    private readonly DataHub _hub;
    private readonly ScanDemandRegistry _scanDemands;
    private readonly IMonotonicClock _clock;
    private readonly InvalidNodeReadPolicy _invalidNodeReads;
    /// <summary>点位级故障：哪个点、为什么坏、该怎么改，供监控中心逐点展示。</summary>
    private TagFaultTracker _tagFaults = null!;
    private readonly ScanDeadlineQueue _deadlines = new();
    private readonly DeviceTimingMetrics _timing = new();
    private volatile IReadOnlyList<CompiledScanBlock> _plan = Array.Empty<CompiledScanBlock>();
    private long _planVersion = long.MinValue;
    private long _driverCapabilityVersion = long.MinValue;
    private bool _planInitialized;
    private long _lastActivePlanRefresh;
    private long _hubDemandVersion = long.MinValue;
    private bool _hubDemandActive;
    private RealtimeSubscriptions? _realtimeSubscription;
    private IReadOnlyDictionary<string, int> _requestedSubscriptionPeriods =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private HashSet<string> _subscribedTagIds = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int> _subscribedRequestedPeriods =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _subscriptionFallbackTagIds = new(StringComparer.Ordinal);
    private long _subscriptionFallbackVersion;
    private long _planSubscriptionFallbackVersion = long.MinValue;
    private long _subscriptionDemandVersion = long.MinValue;
    private long _subscriptionScanDemandVersion = long.MinValue;
    private long _lastSubscriptionRefreshMs;
    private long _nextSubscriptionRetryMs;
    private readonly Dictionary<string, long> _transientSubscriptionRetentionUntil = new(StringComparer.Ordinal);
    private HashSet<string> _subscribedExplicitTagIds = new(StringComparer.Ordinal);
    private readonly int _transientSubscriptionRetentionMs;
    /// <summary>安静期内最多保留多少个瞬态订阅点，防止大工程下订阅集合只增不减。</summary>
    private const int MaxRetainedTransientSubscriptionTags = 4096;
    private HighResolutionWaiter? _highResolutionWaiter;
    private readonly ILogger _log;
    private readonly string _name;
    private string? _lastTelemetryState;
    private string? _lastTelemetryQuality;
    private readonly bool _zeroNonNormalFloat;
    private readonly bool _demoteEnabled;
    private readonly int _demoteAfter;
    private readonly int _demotePeriodMs;
    private readonly bool _returnToPrimary;
    private readonly int _monitorIntervalMs;
    private readonly ConnectionIncidentRegistry? _incidents;
    private readonly string _channelName;
    private readonly string _protocol;
    private readonly Action<string>? _requestChannelHardReconnect;
    private readonly string? _configuredRemoteAddress;
    private readonly string? _localAddress;
    private readonly string _deviceFingerprint;
    private readonly string? _secondaryDiagnosticEndpoint;
    private long _primarySessionGeneration, _secondarySessionGeneration;
    // Defaults of the factories registered in Runtime.Program; resolving them is entirely DNS/I/O-free.
    // Mitsubishi uses IoTMitsubishiDriverFactory (6000). Do not use NetworkBindingPlan's legacy 5000
    // default here: it describes the alternative MC factory, not the current direct connection factory.
    private static int? DiagnosticDefaultEndpointPort(string driverId) => driverId.ToLowerInvariant() switch
    {
        "opcua.client" => 4840,
        "siemens.s7" => 102,
        "modbus.tcp" => 502,
        "omron.fins" => 9600,
        "omron.cip" or "ab.eip" => 44818,
        "mitsubishi.mc" => 6000,
        "mqtt.client" => 1883,
        _ => null
    };
    internal string? DiagnosticEndpointIdentity
    {
        get
        {
            string? endpoint = _onSecondary ? _secondaryDiagnosticEndpoint : _configuredRemoteAddress;
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint == "(未配置)") return null;
            if (Uri.TryCreate(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "tcp://" + endpoint,
                UriKind.Absolute, out var uri))
                return _protocol + "|" + uri.IdnHost.ToLowerInvariant() + "|" +
                    (uri.Port < 0 ? DiagnosticDefaultEndpointPort(_protocol) ?? uri.Port : uri.Port) + "|" + uri.AbsolutePath.TrimEnd('/');
            return null;
        }
    }
    internal bool DiagnosticSessionConnected => _active.State == ConnectionState.Connected;
    private string DiagnosticSessionFingerprint => _deviceFingerprint + (_onSecondary ? "-s" : "-p");
    internal string DiagnosticPhysicalSessionFingerprint =>
        (_active as SharedOpcUaDriverLease)?.PhysicalConnectionFingerprint ?? DiagnosticSessionFingerprint;
    private long? DiagnosticSessionGeneration => SessionGenerationOf(_active);
    private long? SessionGenerationOf(IDeviceDriver driver)
    {
        if (driver is SharedOpcUaDriverLease lease) return lease.DiagnosticGeneration;
        long generation = ReferenceEquals(driver, _primary)
            ? Interlocked.Read(ref _primarySessionGeneration) : Interlocked.Read(ref _secondarySessionGeneration);
        return generation > 0 ? generation : null;
    }
    private async Task ConnectTrackedAsync(IDeviceDriver driver, CancellationToken ct)
    {
        await driver.ConnectAsync(ct);
        if (ReferenceEquals(driver, _primary)) Interlocked.Increment(ref _primarySessionGeneration);
        else Interlocked.Increment(ref _secondarySessionGeneration);
    }
    private readonly Action? _runtimeStateChanged;
    private readonly Func<DateTimeOffset, NetworkProbeWindow?>? _networkWindow;
    private readonly Func<IDeviceDriver, Func<DateTimeOffset, NetworkProbeWindow?>?>? _captureNetworkWindow;
    private string _lastRuntimeSignature = "";
    private long _lastRuntimeNotificationMs;
    private long _nextReadSuccessDiagnosticMs;
    private long _nextWriteSuccessDiagnosticMs;
    private bool _onSecondary;
    private long _nextProbe;
    private int _fails;
    private long _demotedUntil;

    // 写优化(对齐 KEP):按优化方法合并并受队列容量约束；串行协议由采集线程处理，
    // 支持多在途请求的协议由高优先级写泵处理。
    private readonly ConcurrentQueue<PendingWrite> _writes = new();
    private readonly ConcurrentQueue<PendingArrayWrite> _arrayWrites = new();
    /// <summary>串行协议的按需读取队列，由设备循环在报文边界串行执行。</summary>
    private readonly ConcurrentQueue<PendingOnDemandRead> _onDemandReads = new();
    private readonly ConcurrentDictionary<string, long> _writeGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastWriteEpochByTag = new(StringComparer.Ordinal);
    private long _writeEpoch;
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> _manualReconnects = new();
    private readonly SemaphoreSlim _writeSignal = new(0);
    private readonly SemaphoreSlim _concurrentWriteSignal = new(0);
    private TaskCompletionSource<bool> _writeArrivalSignal = NewWriteArrivalSignal();
    private int _writeWakePending;
    private int _priorityProtocolOperationDepth;
    private int _concurrentWriteWakePending;
    private int _pendingWriteCount;
    private readonly int _maxPendingWrites;
    private int _demandWakePending;
    private readonly SemaphoreSlim _browseProbeGate = new(1, 1);
    private readonly string _writeOpt;     // all=全写 / nonbool-latest=仅非布尔取最新 / latest=全取最新
    private readonly int _dutyCycle;       // 旧工程兼容字段；真实批量由驱动会话能力决定
    private volatile bool _acceptWrites = true;
    private int _started;

    // active-tag(KEP「遵循客户端」):没人引用的点不去轮设备
    private readonly string _scanMode;     // all=全轮 / active=仅活动标记
    private readonly int _activeWindowMs;  // active:客户端多久不引用则停轮该点
    private readonly int _idlePollMs;
    private int _explicitDemand;

    private enum CircuitState { Closed, Open, HalfOpen }
    private volatile CircuitState _circuit;
    private readonly int _circuitFailureThreshold;
    private readonly int _circuitOpenMs;
    private int _communicationFailures;
    private long _circuitOpenUntil;

    // 发布热路径:tagId → (Hub句柄, 点定义)。绑定期解析一次,Publish 每样本只查本地小字典一次(原来查 Hub 全局字典 3 次)
    private readonly Dictionary<string, (object h, TagDef def)> _pub = new(StringComparer.Ordinal);
    private readonly object _arrayHealthGate = new();
    private readonly Dictionary<string, ArrayTagHealth> _arrayHealth = new(StringComparer.Ordinal);
    private readonly int _totalTags;
    private volatile int _activeTags;      // active 模式的活动点数缓存(每 ~2s 重算;供状态轮询,避免每次 REST O(全部点) LINQ)

    // 诊断统计(对标 KEP 设备诊断):读/写成功失败计数、最近扫描耗时、最近错误
    private long _readOk, _readErr, _writeOk, _writeErr;
    private int _lastScanMs;
    private readonly ScanTimingHistory _scanHistory = new();
    private readonly int _configuredMinScanMs;
    private readonly int _configuredMaxScanMs;
    private string _lastError = "";
    // 某些 PLC/驱动在连接仍存活时会返回整批 Bad（例如单个 DB 地址不兼容）。
    // 数据质量失败不能用来推断 TCP 会话已经断开；只有驱动抛出真实 I/O 异常时才重连。
    private int _consecutiveNoGoodReads;
    public DeviceStats Stats
    {
        get
        {
            var timing = _timing.Snapshot();
            var plan = _plan;
            ArrayTagHealth[] arrayHealth;
            lock (_arrayHealthGate) arrayHealth = _arrayHealth.Values.ToArray();
            return new DeviceStats(_readOk, _readErr, _writeOk, _writeErr, _lastScanMs, _lastError,
                (_readOk + _readErr) == 0 ? 0 : Math.Round(100.0 * _readOk / (_readOk + _readErr), 1))
            {
                ConfiguredMinPeriodMs = plan.Count == 0 ? 0 : plan.Min(block => block.PeriodMs),
                ActualAveragePeriodMs = timing.AveragePeriodMs,
                ActualP95PeriodMs = timing.P95PeriodMs,
                ActualP99PeriodMs = timing.P99PeriodMs,
                OverduePeriods = timing.OverduePeriods,
                ConsecutiveOverruns = timing.ConsecutiveOverruns,
                PlannedBlocks = plan.Count,
                SamplesPerSecond = timing.SamplesPerSecond,
                PeriodHealthy = !timing.RecentOverrun,
                ArrayTags = arrayHealth.Length,
                ArrayElements = arrayHealth.Sum(item => item.Total),
                ArrayGood = arrayHealth.Sum(item => item.Good),
                ArrayUncertain = arrayHealth.Sum(item => item.Uncertain),
                ArrayBad = arrayHealth.Sum(item => item.Bad),
                ArrayBytes = arrayHealth.Sum(item => item.EstimatedBytes),
                ArrayBlocks = arrayHealth.Sum(item => item.Blocks),
                ArrayFailedRanges = string.Join("；", arrayHealth
                    .Where(item => item.FailedRanges.Count > 0)
                    .Take(16)
                    .Select(item => $"{item.TagId}: {string.Join('、', item.FailedRanges)}")),
                WriteQueueP95Ms = timing.WriteQueueP95Ms,
                WriteResponseP95Ms = timing.WriteResponseP95Ms,
                WriteVerificationP95Ms = timing.WriteVerificationP95Ms,
                PeakWriteQueueDepth = timing.PeakWriteQueueDepth,
                SameSessionRetries = timing.SameSessionRetries,
                BatchDownshifts = timing.BatchDownshifts,
            };
        }
    }

    /// <summary>当前正在轮询的标记数(活动标记):未连接=0;all 模式=全部点;active 模式=被引用/订阅的点(缓存值,~2s 刷新)。</summary>
    public int ActiveTagCount => _active.State != ConnectionState.Connected ? 0 : _activeTags;
    public ConnectionState State => _active.State;
    public bool DemandActive => HasDemand();
    public IReadOnlyList<ScanTimingRecord> ScanHistory(int limit, string? tag) => _scanHistory.Snapshot(limit, tag);

    internal string? DiagnosticAddress(string tagId) =>
        _bindingsByTagId.TryGetValue(tagId, out var binding) ? binding.Def.Address : null;
    internal IReadOnlyList<TagScanTiming> TagScanTimings(IReadOnlySet<string> requested)
    {
        var owned = requested.Where(_bindingsByTagId.ContainsKey).ToHashSet(StringComparer.Ordinal);
        if (owned.Count == 0) return [];
        var latest = _scanHistory.LatestForTags(owned);
        var polling = new HashSet<string>(StringComparer.Ordinal);
        bool active = DemandActive && State == ConnectionState.Connected;
        var subscribed = _subscribedTagIds;
        if (active)
            foreach (var block in _plan)
                foreach (var tag in block.Tags)
                    if (owned.Contains(tag.TagId)) polling.Add(tag.TagId);
        return owned.Select(id =>
        {
            string mode = !active ? "standby" : polling.Contains(id) ? "polling" :
                subscribed.Contains(id) ? "subscription" : "standby";
            return new TagScanTiming(id, _name, mode,
                mode != "subscription" && latest.TryGetValue(id, out var record) ? TagScanTimingBatch.From(record) : null);
        }).ToArray();
    }
    internal IEnumerable<SharedOpcUaDriverLease> SharedOpcUaConnections =>
        new[] { _primary, _secondary }.OfType<SharedOpcUaDriverLease>();

    public ScanTimingDeviceSummary ScanTimingSummary()
    {
        var records = _scanHistory.Snapshot(ScanTimingHistory.Capacity, null);
        var latest = records.FirstOrDefault();
        var network = latest?.Network;
        var plan = _plan;
        var subscribed = _subscribedTagIds;
        bool active = DemandActive && State == ConnectionState.Connected;
        int pollingTags = active ? plan.Sum(block => block.Tags.Count) : 0;
        int subscribedTags = active ? subscribed.Count : 0;
        // In mixed mode compare the measured polling intervals with polling demand periods only.
        var periods = plan.Count > 0 ? plan.Select(block => block.PeriodMs).ToArray() :
            subscribed.Select(id => _bindingsByTagId.TryGetValue(id, out var binding)
                ? Math.Max(1, binding.Def.ScanRateMs) : 0).Where(period => period > 0).ToArray();
        var timing = _timing.Snapshot();
        return new ScanTimingDeviceSummary(_name,
            !active || pollingTags + subscribedTags == 0 ? "standby" : pollingTags == 0 ? "subscription" : subscribedTags == 0 ? "polling" : "mixed",
            periods.Length == 0 ? _configuredMinScanMs : periods.Min(), periods.Length == 0 ? _configuredMaxScanMs : periods.Max(),
            pollingTags, subscribedTags, active ? plan.Count : 0, latest?.TotalMs ?? 0,
            Math.Round(timing.AveragePeriodMs, 2), timing.P95PeriodMs, timing.OverduePeriods,
            Math.Round(timing.SamplesPerSecond, 1), latest?.StartedAt,
            latest?.DriverMs, latest?.LocalMs, latest?.TagCount,
            ScanTimingHistory.Percentile(records.Select(record => record.Protocol?.QueueMs)),
            ScanTimingHistory.Percentile(records.Where(record => record.Protocol is { Requests: > 0 }).Select(record => (double?)record.Protocol!.RequestMaxMs)),
            records.Where(record => record.Protocol is { Requests: > 0 }).Select(record => (double?)record.Protocol!.RequestMaxMs).Max(),
            ScanTimingHistory.Percentile(records.Select(record => (double?)record.LocalMs)),
            // Use one immutable window; aggregating overlapping batch windows double-counts echo samples.
            network?.P95RttMs, network?.LossPercent,
            latest?.Attribution?.Label, latest?.Attribution?.Confidence,
            LogicalBatchCount: active ? plan.Count : 0, LatestProtocolRequestCount: latest?.LatestProtocolRequestCount,
            ProtocolRequestSamples: latest?.ProtocolRequestSamples, OmittedProtocolRequestSamples: latest?.OmittedProtocolRequestSamples,
            DeviceSessionFingerprint: DiagnosticSessionFingerprint, SessionGeneration: DiagnosticSessionGeneration);
    }
    private DriverCapabilitySnapshot ActiveCapabilities =>
        (_active as IDriverCapabilityProvider)?.Capabilities ?? DriverCapabilitySnapshot.FromLimits(_active.Limits);
    private bool SupportsConcurrentWritePipeline => ActiveCapabilities.MaxInFlight > 1;
    internal void ResetTimingMetrics() => _timing.Reset();

    internal sealed class PendingWrite : IDisposable
    {
        private readonly Action _releaseSlot;
        private CancellationTokenRegistration _registration;
        private int _state; // 0=排队，1=已下发，2=已结束
        private int _slotReleased;

        public PendingWrite(
            WriteRequest req,
            TaskCompletionSource<StatusCode> tcs,
            long enqueuedMs,
            long generation,
            CancellationToken cancellationToken,
            Action releaseSlot)
        {
            Req = req;
            Tcs = tcs;
            EnqueuedMs = enqueuedMs;
            Generation = generation;
            ExpectedValue = req.Value;
            _releaseSlot = releaseSlot;
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(static state =>
                    ((PendingWrite)state!).CancelBeforeDispatch(), this);
                if (Volatile.Read(ref _state) == 2) _registration.Dispose();
            }
        }

        public WriteRequest Req { get; }
        public TaskCompletionSource<StatusCode> Tcs { get; }
        public long EnqueuedMs { get; set; }
        public long Generation { get; }
        public long DispatchGenerationCeiling { get; private set; }
        public bool IsTerminal => Volatile.Read(ref _state) == 2;
        public int Attempts { get; set; }
        public object ExpectedValue { get; set; }
        public long RetryNotBeforeMs { get; set; }

        public bool TryBeginDispatch(long latestGeneration)
        {
            // A yielded retry is still dispatched: caller cancellation cannot undo a PLC write,
            // and its original generation ceiling must survive subsequent queue passes.
            if (Volatile.Read(ref _state) == 1) return true;
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            DispatchGenerationCeiling = latestGeneration;
            return true;
        }

        public void Complete(StatusCode status)
        {
            int previous = Interlocked.Exchange(ref _state, 2);
            if (previous == 2) return;
            _registration.Dispose();
            ReleaseSlotOnce();
            Tcs.TrySetResult(status);
        }

        private void CancelBeforeDispatch()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
            ReleaseSlotOnce();
            Tcs.TrySetResult(StatusCode.BadRequestCancelled);
        }

        private void ReleaseSlotOnce()
        {
            if (Interlocked.Exchange(ref _slotReleased, 1) == 0) _releaseSlot();
        }

        public void Dispose() => _registration.Dispose();
    }

    internal sealed class PendingArrayWrite : IDisposable
    {
        private readonly Action _releaseSlot;
        private CancellationTokenRegistration _registration;
        private int _state;
        private int _slotReleased;

        public PendingArrayWrite(
            ArrayWriteRequest req,
            TaskCompletionSource<ArrayWriteResult> tcs,
            long generation,
            CancellationToken cancellationToken,
            Action releaseSlot)
        {
            Req = req;
            RetryRequests = [req];
            Tcs = tcs;
            Generation = generation;
            _releaseSlot = releaseSlot;
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(static state =>
                    ((PendingArrayWrite)state!).CancelBeforeDispatch(), this);
                if (Volatile.Read(ref _state) == 2) _registration.Dispose();
            }
        }

        public ArrayWriteRequest Req { get; }
        public IReadOnlyList<ArrayWriteRequest> RetryRequests { get; set; }
        public int Attempts { get; set; }
        public TaskCompletionSource<ArrayWriteResult> Tcs { get; }
        public long Generation { get; }
        public long DispatchGenerationCeiling { get; private set; }
        public bool IsTerminal => Volatile.Read(ref _state) == 2;

        public bool TryBeginDispatch(long latestGeneration)
        {
            if (Volatile.Read(ref _state) == 1) return true;
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            DispatchGenerationCeiling = latestGeneration;
            return true;
        }

        public void Complete(ArrayWriteResult result)
        {
            int previous = Interlocked.Exchange(ref _state, 2);
            if (previous == 2) return;
            _registration.Dispose();
            ReleaseSlotOnce();
            Tcs.TrySetResult(result);
        }

        private void CancelBeforeDispatch()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
            ReleaseSlotOnce();
            Tcs.TrySetResult(ArrayWriteResult.Failed(StatusCode.BadRequestCancelled, "调用方已在下发前取消写入"));
        }

        private void ReleaseSlotOnce()
        {
            if (Interlocked.Exchange(ref _slotReleased, 1) == 0) _releaseSlot();
        }

        public void Dispose() => _registration.Dispose();
    }

    public Task Completion { get; private set; } = Task.CompletedTask;

    public IDisposable AcquireDemand()
    {
        Interlocked.Increment(ref _explicitDemand);
        _writeSignal.Release();   // 立即唤醒空闲等待,不必等下一次轮询
        return new DemandLease(this);
    }

    /// <summary>该点当前是否被持续采集(轮询或订阅)：是则缓存值就是实时值，读取接口可以直接返回。</summary>
    internal bool IsAcquiring(string tagId) =>
        (_scanMode == "all" && _hub.HasDeviceDemand(_name, _activeWindowMs)) ||
        _hub.IsActive(tagId, _activeWindowMs) ||
        _scanDemands.IsDemanded(tagId);

    internal void WakeForDemand()
    {
        if (Interlocked.Exchange(ref _demandWakePending, 1) == 0)
            _writeSignal.Release();
    }

    public async Task WaitUntilConnectedAsync(CancellationToken ct)
    {
        while (_active.State != ConnectionState.Connected || _circuit != CircuitState.Closed)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }

    public async Task<BrowseResult> BrowseAsync(CancellationToken ct)
    {
        using var demand = AcquireDemand();
        await _browseProbeGate.WaitAsync(ct);
        try
        {
            while (_active.State != ConnectionState.Connected ||
                   (_circuit != CircuitState.Closed && !(_circuit == CircuitState.HalfOpen && _bindings.Count == 0)))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(25, ct);
            }

            if (_active is not IBrowsableDriver browser)
                throw new NotSupportedException($"驱动 {_active.DriverId} 不支持浏览");
            bool halfOpenProbe = _circuit == CircuitState.HalfOpen;
            try
            {
                var result = await browser.BrowseAsync(ct);
                if (halfOpenProbe) RecordCommunicationSuccess();
                return result;
            }
            catch
            {
                if (halfOpenProbe)
                {
                    try { await _active.DisconnectAsync(); } catch { }
                    RecordCommunicationFailure("半开地址浏览失败");
                }
                throw;
            }
        }
        finally
        {
            _browseProbeGate.Release();
        }
    }

    private void ReleaseDemand()
    {
        while (true)
        {
            int before = Volatile.Read(ref _explicitDemand);
            if (before <= 0) return;
            if (Interlocked.CompareExchange(ref _explicitDemand, before - 1, before) == before) return;
        }
    }

    private sealed class DemandLease(DeviceLoop owner) : IDisposable
    {
        private DeviceLoop? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseDemand();
    }

    public DeviceLoop(IDeviceDriver driver, IReadOnlyList<TagBinding> bindings, DataHub hub, ILogger log, string name,
        bool zeroNonNormalFloat = false, bool demoteEnabled = false, int demoteAfter = 3, int demotePeriodMs = 10000,
        IDeviceDriver? secondary = null, int monitorIntervalSec = 300, bool returnToPrimary = true,
        string writeOpt = "nonbool-latest", int dutyCycle = 10, string scanMode = "active", int activeWindowMs = 30000,
        int idlePollMs = 250, int circuitFailureThreshold = 3, int circuitOpenMs = 5_000,
        ScanDemandRegistry? scanDemands = null, IMonotonicClock? clock = null,
        ConnectionIncidentRegistry? incidents = null, string? channelName = null, string? protocol = null,
        Action<string>? requestChannelHardReconnect = null, int maxPendingWrites = 100_000,
        string? configuredRemoteAddress = null, string? localAddress = null,
        Action? runtimeStateChanged = null, Func<DateTimeOffset, NetworkProbeWindow?>? networkWindow = null,
        Func<IDeviceDriver, Func<DateTimeOffset, NetworkProbeWindow?>?>? captureNetworkWindow = null,
        string? deviceIdentity = null, string? secondaryDiagnosticEndpoint = null)
    {
        _primary = driver; _active = driver; _secondary = secondary;
        _deviceFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(deviceIdentity) ? name : deviceIdentity)))[..16].ToLowerInvariant();
        _secondaryDiagnosticEndpoint = secondaryDiagnosticEndpoint;
        _hub = hub; _log = log; _name = name; _zeroNonNormalFloat = zeroNonNormalFloat;
        _demoteEnabled = demoteEnabled; _demoteAfter = demoteAfter <= 0 ? 3 : demoteAfter; _demotePeriodMs = demotePeriodMs <= 0 ? 10000 : demotePeriodMs;
        _returnToPrimary = returnToPrimary; _monitorIntervalMs = (monitorIntervalSec <= 0 ? 300 : monitorIntervalSec) * 1000;
        _writeOpt = writeOpt; _dutyCycle = Math.Clamp(dutyCycle, 1, 10);
        _scanMode = scanMode; _activeWindowMs = activeWindowMs <= 0 ? 30000 : activeWindowMs;
        _transientSubscriptionRetentionMs = (int)Math.Clamp((long)_activeWindowMs * 10, 300_000, 900_000);
        _idlePollMs = Math.Clamp(idlePollMs, 10, 5000);
        _circuitFailureThreshold = Math.Max(1, circuitFailureThreshold);
        _circuitOpenMs = Math.Max(50, circuitOpenMs);
        _maxPendingWrites = Math.Clamp(maxPendingWrites, 1, 1_000_000);
        _bindings = bindings;
        _configuredMinScanMs = bindings.Count == 0 ? 0 : bindings.Min(binding => Math.Max(1, binding.Def.ScanRateMs));
        _configuredMaxScanMs = bindings.Count == 0 ? 0 : bindings.Max(binding => Math.Max(1, binding.Def.ScanRateMs));
        _bindingsByTagId = bindings
            .GroupBy(binding => binding.TagId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _invalidNodeReads = new InvalidNodeReadPolicy(_bindingsByTagId);
        _scanDemands = scanDemands ?? new ScanDemandRegistry(hub);
        _clock = clock ?? SystemMonotonicClock.Instance;
        _incidents = incidents;
        _channelName = string.IsNullOrWhiteSpace(channelName)
            ? (name.Contains('.') ? name[..name.IndexOf('.')] : name)
            : channelName;
        _protocol = string.IsNullOrWhiteSpace(protocol) ? driver.DriverId : protocol;
        _tagFaults = new TagFaultTracker(_channelName, name, driver.DriverId);
        _requestChannelHardReconnect = requestChannelHardReconnect;
        _configuredRemoteAddress = configuredRemoteAddress;
        _localAddress = localAddress;
        _runtimeStateChanged = runtimeStateChanged;
        _networkWindow = networkWindow;
        _captureNetworkWindow = captureNetworkWindow;
        AttachPriorityWorkProbe(_primary);
        if (_secondary != null) AttachPriorityWorkProbe(_secondary);
        foreach (var b in bindings)   // 绑定期解析 Hub 句柄:发布热路径零全局字典查找
        {
            var h = hub.GetHandle(b.TagId);
            if (h != null) _pub[b.TagId] = (h, b.Def);
        }
        _totalTags = bindings.Count;
        _activeTags = 0;
    }

    private void AttachPriorityWorkProbe(IDeviceDriver driver)
    {
        if (driver is IDriverPriorityWorkAware aware)
            aware.SetPriorityWorkProbe(() =>
                Volatile.Read(ref _priorityProtocolOperationDepth) == 0 &&
                (!_writes.IsEmpty || !_arrayWrites.IsEmpty || !_onDemandReads.IsEmpty));
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("设备循环已经启动。");
        Completion = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Either loop can complete several awaits synchronously (including the short
            // scan wait). Schedule both independently so one cannot prevent the other starting.
            Task collector = Task.Run(() => RunAsync(linked.Token));
            Task writer = Task.Run(() => ConcurrentWritePumpAsync(linked.Token));
            await Task.WhenAny(collector, writer);
            linked.Cancel();
            SignalConcurrentWriteWork();
            await Task.WhenAll(collector, writer);
        });
    }

    /// <summary>
    /// 在设备自己的串行通信循环中执行一次立即重连。复用现有驱动与网卡绑定，
    /// 不销毁点位句柄、MES 扫描需求或本地 TCP 转发器。
    /// </summary>
    public async Task<bool> ReconnectNowAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _started) == 0 || Completion.IsCompleted) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manualReconnects.Enqueue(completion);
        _writeSignal.Release();
        // 循环可能在排队后被热重载停掉(不再处理队列)，此时按“未连上”返回，不能永远等下去。
        var finished = await Task.WhenAny(completion.Task, Completion).WaitAsync(ct);
        return ReferenceEquals(finished, completion.Task) && await completion.Task;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        RecordTelemetryState("disconnected", "设备采集循环已启动", LogLevel.Information);
        try
        {
        var backoff = 1.0; // 秒
        while (!ct.IsCancellationRequested)
        {
            var reconnectRequests = DrainManualReconnectRequests();
            bool manualReconnect = reconnectRequests.Count > 0;
            if (manualReconnect)
            {
                await CloseRealtimeSubscriptionAsync();
                await ResetTransportAsync(_active, ct);
                _circuit = CircuitState.Closed;
                _communicationFailures = 0;
                Volatile.Write(ref _circuitOpenUntil, 0);
                _fails = 0;
                _demotedUntil = 0;
                _consecutiveNoGoodReads = 0;
                _planInitialized = false;
                backoff = 1.0;
                MarkAllBad(StatusCode.BadNotConnected);
                _log.LogInformation("设备 {dev} 收到手动重连请求，复用当前连接实例立即重试", _name);
            }

            bool hasDemand = manualReconnect || HasDemand();
            // PLC 会话和业务扫描必须解耦：设备启用后始终建立连接，使在线/离线反映真实通信状态；
            // MES、QC、OPC UA 等消费者只决定是否读取点位。无需求时保持会话但不建立扫描计划。

            if (_circuit == CircuitState.Open)
            {
                long remaining = Volatile.Read(ref _circuitOpenUntil) - _clock.NowMilliseconds;
                if (remaining > 0)
                {
                    MarkAllBad(StatusCode.BadNotConnected);
                    FailPendingWrites(StatusCode.BadNotConnected);
                    if (!await WaitWorkOrDelay((int)Math.Clamp(remaining, 1, 1000), ct)) break;
                    continue;
                }
                _circuit = CircuitState.HalfOpen;
                _planInitialized = false;   // 半开探测必须立即执行当前最小读取块,不能等待旧截止时间
                _log.LogInformation("设备 {dev} 熔断到期,开始一次半开探测", _name);
            }

            // 自动降级:降级期内停止采集,只等待(对齐 KEP「故障时降级」)
            if (_demoteEnabled && Environment.TickCount64 < _demotedUntil)
            {
                MarkAllBad(StatusCode.BadNotConnected);
                FailPendingWrites(StatusCode.BadNotConnected);   // 降级期内写直接失败,别让客户端卡住
                if (!await DelaySafe(0.5, ct)) break;
                continue;
            }

            // 冗余回切:在备用路径时,定期探测主路径,恢复则切回(对齐 KEP「尽快返回至主要设备」)
            if (_onSecondary && _returnToPrimary && Environment.TickCount64 >= _nextProbe)
            {
                _nextProbe = Environment.TickCount64 + _monitorIntervalMs;
                if (await ProbePrimaryAsync(ct))
                {
                    try { await _secondary!.DisconnectAsync(); } catch { }
                    _active = _primary; _onSecondary = false; _fails = 0;
                    _log.LogInformation("设备 {dev} 主路径已恢复 → 切回主设备", _name);
                }
            }

            if (_active.State != ConnectionState.Connected)
            {
                try
                {
                    await CloseRealtimeSubscriptionAsync();
                    await ConnectTrackedAsync(_active, ct);
                    ApplyDriverDiagnostic(null, ConnectionState.Connected, retrying: false, nextRetryMs: 0);
                    // 注意:退避不在这里复位——"TCP 连上但读报错"的半死设备若在此复位,就变成零退避风暴;读成功才复位(见下)
                    _log.LogInformation("设备 {dev} 已连接{path}", _name, _onSecondary ? "(备用)" : "");
                    RecordTelemetryState("online", "设备连接已建立", LogLevel.Information);
                    if (_active is IRemoteEndpointDiagnostics endpointDiagnostics)
                    {
                        var endpoint = endpointDiagnostics.RemoteEndpoint();
                        _log.LogInformation(
                            "设备 {dev} OPC UA Client 端点核对：配置/转发={configured}；服务器发现={discovered}；SDK 会话={session}",
                            _name,
                            endpoint.ConfiguredEndpoint,
                            endpoint.DiscoveredEndpoint ?? "(未返回)",
                            endpoint.SessionEndpoint ?? "(未建立)");
                    }
                }
                catch (DriverReadYieldException)
                {
                    // 串行协议在一个完整报文结束后主动让行。缓存、质量、失败计数和连接状态均保持不变，
                    // 当前循环随后立即处理已经排队的写入。
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    CompleteManualReconnectRequests(reconnectRequests, false);
                    int retryMs = (int)Math.Clamp(backoff * (0.7 + Random.Shared.NextDouble() * 0.6) * 1000, 1, 30_000);
                    ReportDiagnostic(ex, DriverDetail(), ConnectionState.Faulted, retrying: true, retryMs);
                    await ResetTransportAsync(_active, ct);
                    MarkAllBad(StatusCode.BadNotConnected);
                    FailPendingOnDemandReads("设备连接失败");
                    // 单次连接超时不丢弃排队写：调用方取消会在真正下发前移除，
                    // 连续失败进入熔断后再返回 BadNotConnected。
                    if (await HandleFailureAsync(ct)) continue; // 已切换备用 / 已降级
                    if (RecordCommunicationFailure(ex.Message)) continue;
                    _log.LogWarning("设备 {dev} 连接失败:{msg};{s}s 后重试", _name, ex.Message, backoff);
                    RecordTelemetryState("faulted", ex.Message, LogLevel.Warning);
                    if (!await WaitWorkOrDelay(retryMs, ct)) break;   // 手动重连/写请求可立即唤醒退避；±30% 抖动避免全场锁步
                    backoff = Math.Min(5, backoff * 2);   // 现场网络恢复后最长 5 秒再次尝试，不再累计到 30 秒
                    continue;
                }
            }
            CompleteManualReconnectRequests(reconnectRequests, true);
            if (SupportsConcurrentWritePipeline && (!_writes.IsEmpty || !_arrayWrites.IsEmpty))
                SignalConcurrentWriteWork();

            // 空闲设备只保持协议会话；不建扫描计划、不读取点位，也不要求存在 MES 流程。
            if (!hasDemand)
            {
                _activeTags = 0;
                if (_realtimeSubscription is not null) await CloseRealtimeSubscriptionAsync();
                if (!await WaitWorkOrDelay(_idlePollMs, ct)) break;
                continue;
            }

            long now = _clock.NowMilliseconds;
            await ReconcileRealtimeSubscriptionAsync(now, ct);
            RebuildPlanIfNeeded(now);
            if (_invalidNodeReads.ApplyVerifiedRecoveries() is { Count: > 0 } recoveredNodes)
            {
                // Only the scan loop touches deadlines. Restore existing active blocks to their
                // configured period; a successful write never creates demand for an inactive tag.
                _deadlines.Clear();
                foreach (var planned in _plan)
                    _deadlines.Add(planned, planned.Tags.Any(tag => recoveredNodes.Contains(tag.TagId))
                        ? Math.Min(planned.DueMs, now + planned.PeriodMs) : planned.DueMs);
            }
            bool connectionLost = false;
            bool didGoodRead = false;
            bool hadReadFailure = false;

            // active 模式没有活动点但半开需要通信探测时,使用一个真实绑定完成最小读取;
            // 零点位设备则继续由 BrowseAsync 自身完成半开探测。
            if (_circuit == CircuitState.HalfOpen && _plan.Count == 0 && _bindings.Count > 0)
            {
                long t0 = _clock.NowMilliseconds;
                try
                {
                    long writeEpochAtReadStart = Volatile.Read(ref _writeEpoch);
                    var samples = await _active.ReadAsync(new[] { _bindings[0] }, ct);
                    Publish(samples, writeEpochAtReadStart: writeEpochAtReadStart);
                    var arrayHealth = RecordArrayHealth(samples);
                    if (!HasUsableSample(samples))
                        throw new InvalidDataException("读取未返回任何有效数据");
                    if (TryBuildPartialReadMessage(samples, arrayHealth, _invalidNodeReads.IsIsolated, out string partialMessage))
                        ReportDiagnostic(null, new DeviceConnectionDetail("PartialOnline",
                                $"{partialMessage}；失败点位={DescribeReadBindings([_bindings[0]], samples, StatusCode.BadCommFailure, onlyBad: true)}"),
                            ConnectionState.Connected, retrying: false, nextRetryMs: 0);
                    else
                        ApplyDriverDiagnostic(null, ConnectionState.Connected, retrying: false, nextRetryMs: 0);
                    _consecutiveNoGoodReads = 0;
                    _readOk++;
                    didGoodRead = true;
                    _lastScanMs = (int)(_clock.NowMilliseconds - t0);
                    NotifyRuntimeState();
                }
                catch (DriverReadYieldException)
                {
                    // 半开探测也只是在完整协议报文后让行；连接状态与失败计数保持不变，
                    // 下一轮会重新执行探测，当前轮优先下发已经排队的写入。
                    _planInitialized = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _readErr++;
                    var failure = DriverOperationFailure.FromException(ex, _active.State);
                    MarkBindingsBad(new[] { _bindings[0] }, failure.Status);
                    _planInitialized = false;
                    if (!failure.ConnectionLost)
                    {
                        // 半开读取超时、容量限制或单点数据错误不代表会话失效。
                        // 保留已经恢复的连接，只延后下一次半开数据探测。
                        _circuit = CircuitState.Open;
                        Volatile.Write(ref _circuitOpenUntil, _clock.NowMilliseconds + _circuitOpenMs);
                        if (failure.Retry == DriverRetryDisposition.ReduceBatchAndRetry)
                            _timing.RecordBatchDownshift();
                        else
                            _timing.RecordSameSessionRetry();
                        hadReadFailure = true;
                        ReportDiagnostic(ex,
                            new DeviceConnectionDetail("PartialOnline", "半开数据探测失败，会话仍在线"),
                            ConnectionState.Connected, retrying: true, _circuitOpenMs);
                        _log.LogWarning(
                            "设备 {dev} 半开数据探测失败但会话仍在线，范围={scope}，动作={retry}，{delay}ms 后复探:{msg}",
                            _name, failure.Scope, failure.Retry, _circuitOpenMs, failure.Message);
                    }
                    else
                    {
                        ReportDiagnostic(ex, DriverDetail(), ConnectionState.Faulted, retrying: true, _circuitOpenMs);
                        await ResetTransportAsync(_active, ct);
                        _consecutiveNoGoodReads = 0;
                        connectionLost = true;
                    }
                }
            }
            else
            {
                // 单次最多处理有限读取块,让写队列、取消和其他设备调度获得运行机会。
                int readsThisPass = 0;
                // Concurrent protocols have a separate writer and bounded shared admission.
                // Queued writes must not stop the collector; serial protocols still yield here.
                while (readsThisPass < 64 &&
                       (SupportsConcurrentWritePipeline ||
                        (_writes.IsEmpty && _arrayWrites.IsEmpty && _onDemandReads.IsEmpty)) &&
                       _deadlines.TryTakeDue(now, out var block))
                {
                    if (ct.IsCancellationRequested) break;
                    readsThisPass++;
                    var readTags = _invalidNodeReads.SelectDue(block.Tags, now, out int nextProbeMs);
                    if (readTags.Count == 0)
                    {
                        // No synthetic samples: retained bad values keep their real timestamps.
                        // A future deadline leaves the normal write/cancellation wakeup path active.
                        _deadlines.CompleteAfter(block, now, nextProbeMs);
                        continue;
                    }
                    long t0 = _clock.NowMilliseconds;
                    var scanStartedAt = DateTimeOffset.UtcNow;
                    using var scanActivity = GatewayTelemetry.StartActivity("igw.collection.scan", new Dictionary<string, object?>
                    {
                        ["channel"] = _channelName,
                        ["device"] = _name,
                        ["database"] = block.Area,
                        ["blockId"] = block.Id,
                        ["configuredPeriodMs"] = block.PeriodMs,
                        ["protocol"] = _protocol
                    });
                    long? readCompleted = null;
                    long? scanCompleted = null;
                    IReadOnlyList<TagSample>? scanSamples = null;
                    string? scanError = null;
                    StatusCode scanMissingStatus = StatusCode.BadCommFailure;   // 没返回样本的点在点位值里记的状态
                    long scanMissedPeriods = 0;
                    bool retainTiming = true;
                    var scanDriver = _active;
                    var scanNetworkWindow = _networkWindow;
                    try { if (_captureNetworkWindow is not null) scanNetworkWindow = _captureNetworkWindow(scanDriver); }
                    catch (Exception) { scanNetworkWindow = null; }
                    using var protocolTiming = ProtocolOperationTiming.Begin();
                    var sessionFingerprint = DiagnosticSessionFingerprint;
                    var sessionGeneration = SessionGenerationOf(scanDriver);
                    long sameSessionRetries = 0, batchDownshifts = 0;
                    ScanTimingRecord? finalRecord = null;
                    ScanTimingRecord FinalizeScan()
                    {
                        if (finalRecord is not null) return finalRecord;
                        long end = scanCompleted ?? _clock.NowMilliseconds;
                        int bad = scanSamples?.Count(sample => !sample.Value.IsGood ||
                            sample.Value.Value is ArrayRuntimeValue array && !array.OverallQuality.IsGood) ?? 0;
                        NetworkProbeWindow? network = null;
                        // Callback is a bounded in-memory snapshot only. Echo and DNS never run here.
                        try { network = scanNetworkWindow?.Invoke(scanStartedAt.AddMilliseconds(ScanTimingHistory.Milliseconds(end - t0))); }
                        catch (Exception) { /* Diagnostics cannot fail acquisition. */ }
                        var protocolSnapshot = protocolTiming.Snapshot();
                        // No observed raw boundary means unknown, including an entirely uninstrumented driver.
                        if (protocolSnapshot.Requests == 0 && protocolSnapshot.OpaqueOperations == 0) protocolSnapshot = null;
                        return finalRecord = _scanHistory.Record(block, scanStartedAt, t0, readCompleted ?? end, end,
                            scanSamples?.Count ?? 0, bad, scanError, scanMissedPeriods, protocolSnapshot, readTags,
                            network, sameSessionRetries, batchDownshifts, sessionFingerprint, sessionGeneration);
                    }
                    try
                    {
                        long writeEpochAtReadStart = Volatile.Read(ref _writeEpoch);
                        var samples = await scanDriver.ReadAsync(readTags, ct);
                        long driverCompleted = _clock.NowMilliseconds;
                        HashSet<string>? superseded = null;
                        if (Volatile.Read(ref _writeEpoch) != writeEpochAtReadStart)
                            foreach (var tag in readTags)
                                if (IsReadSuperseded(tag.TagId, writeEpochAtReadStart))
                                    (superseded ??= new(StringComparer.Ordinal)).Add(tag.TagId);
                        _invalidNodeReads.Record(readTags, samples, driverCompleted, superseded);
                        RecordTagFaults(readTags, samples);
                        readCompleted = driverCompleted;
                        scanSamples = samples;
                        Publish(samples, block, writeEpochAtReadStart);
                        var arrayHealth = RecordArrayHealth(samples);
                        bool driverHasIncident = ReportDriverDiagnosticIfPresent(
                            ConnectionState.Connected, retrying: false, nextRetryMs: 0);
                        long completed = _clock.NowMilliseconds;
                        scanCompleted = completed;
                        _lastScanMs = (int)(completed - t0);
                        NotifyRuntimeState();
                        now = completed;
                        if (!HasUsableSample(samples))
                        {
                            int failures = Math.Min(5, ++block.ConsecutiveFailures);
                            // 会话仍在线时只对失败块做快速退避；100/200/400/...ms 可在网络或 DB
                            // 恢复后尽快重新读到，同时上限 2s 避免无效地址形成请求风暴。
                            int retryDelayMs = Math.Max(block.PeriodMs, 100 * (1 << (failures - 1)));
                            retryDelayMs = Math.Min(2_000, retryDelayMs);
                            if (_invalidNodeReads.SelectDue(block.Tags, completed, out int isolatedRetryMs).Count == 0)
                                retryDelayMs = isolatedRetryMs;
                            ScanCompletion failureCompletion = _deadlines.CompleteAfter(block, completed, retryDelayMs);
                            scanMissedPeriods = failureCompletion.MissedPeriods;
                            hadReadFailure = true;
                            _readErr++;
                            if (!driverHasIncident)
                                ReportDiagnostic(null,
                                    new DeviceConnectionDetail("PartialOnline",
                                        $"读取未返回任何有效数据；失败点位={DescribeReadBindings(readTags, samples, StatusCode.BadCommFailure)}"),
                                    ConnectionState.Connected, retrying: true, retryDelayMs);
                            // Returned qualities (notably BadNodeIdUnknown) are authoritative.
                            // Fill only missing responses and never overwrite quarantined points.
                            var returnedIds = samples.Select(sample => sample.TagId).ToHashSet(StringComparer.Ordinal);
                            MarkBindingsBad(readTags.Where(tag => !returnedIds.Contains(tag.TagId)).ToArray(), StatusCode.BadCommFailure);
                            int consecutive = ++_consecutiveNoGoodReads;
                            RecordTelemetryQuality("bad", "采集批次没有可用样本", LogLevel.Warning,
                                readTags, samples, StatusCode.BadCommFailure);
                            if (block.FailureLog.TryEmit(samples, "全部无效", completed, out int allBadSuppressed))
                                LogBatchDiagnostic(LogLevel.Warning,
                                "设备 {dev} 读取批次异常：{endpoint}；区域={area}；点数={count}；Good=0；Bad={bad}；耗时={elapsed}ms；{protocolTiming}；连续失败={n}；{delay}ms 后单独复探；合并重复={suppressed}；明细={details}",
                                _name, IoEndpointSummary(), block.Area, readTags.Count, readTags.Count,
                                _lastScanMs, DescribeScanDiagnostics(FinalizeScan()),
                                consecutive, retryDelayMs, allBadSuppressed,
                                DescribeReadBindings(readTags, samples, StatusCode.BadCommFailure));
                            continue;
                        }

                        block.ConsecutiveFailures = 0;
                        ScanCompletion completion = _deadlines.Complete(block, completed);
                        scanMissedPeriods = completion.MissedPeriods;

                        bool partialRead = TryBuildPartialReadMessage(
                            samples, arrayHealth, _invalidNodeReads.IsIsolated, out string partialMessage);
                        // 隔离点自身不再让设备进入「部分在线」：它们已在点位异常里逐点记录(原因+处置)，
                        // 每 30 秒复探一次；真有其它点位异常时，才把隔离点数一并带上供对照。
                        if (partialRead && _invalidNodeReads.IsolatedCount > 0)
                            partialMessage = $"{partialMessage}；另有 BadNodeIdUnknown 隔离 {_invalidNodeReads.IsolatedCount} 点，30秒定期复查";
                        if (partialRead)
                        {
                            RecordTelemetryQuality("bad", partialMessage, LogLevel.Warning,
                                readTags, samples, StatusCode.BadCommFailure);
                            hadReadFailure = true;
                            _readErr++;
                            if (!driverHasIncident)
                                ReportDiagnostic(null,
                                    new DeviceConnectionDetail("PartialOnline",
                                        $"{partialMessage}；失败点位={DescribeReadBindings(readTags, samples, StatusCode.BadCommFailure, onlyBad: true)}"),
                                    ConnectionState.Connected, retrying: false, block.PeriodMs);
                            if (block.FailureLog.TryEmit(samples, partialMessage, completed, out int partialSuppressed))
                                LogBatchDiagnostic(LogLevel.Warning,
                                "设备 {dev} 读取批次异常：{endpoint}；区域={area}；实际请求点数={count}；Good={good}；Bad={bad}；设备隔离点数={isolated}；耗时={elapsed}ms；{protocolTiming}；合并重复={suppressed}；明细={details}",
                                _name, IoEndpointSummary(), block.Area, readTags.Count,
                                samples.Count(sample => sample.Value.Quality.IsGood),
                                samples.Count(sample => !sample.Value.Quality.IsGood), _invalidNodeReads.IsolatedCount, _lastScanMs,
                                DescribeScanDiagnostics(FinalizeScan()), partialSuppressed,
                                DescribeReadBindings(readTags, samples, StatusCode.BadCommFailure, onlyBad: true));
                        }
                        else
                        {
                            RecordTelemetryQuality("good", "采集质量正常", LogLevel.Information,
                                readTags, samples, StatusCode.BadCommFailure);
                            if (block.FailureLog.Recover(out int suppressed))
                                LogBatchDiagnostic(LogLevel.Information, "设备 {dev} 读取质量恢复：区域={area}；点数={count}；合并重复={suppressed}",
                                    _name, block.Area, block.Tags.Count, suppressed);
                        }

                        if (!partialRead && completed >= Volatile.Read(ref _nextReadSuccessDiagnosticMs))
                        {
                            Volatile.Write(ref _nextReadSuccessDiagnosticMs, completed + 30_000);
                            LogBatchDiagnostic(LogLevel.Information,
                                "设备 {dev} 读取批次完成：{endpoint}；区域={area}；点数={count}；Good={good}；Bad={bad}；耗时={elapsed}ms；驱动读取={driverMs}ms；本地处理={localMs}ms；{protocolTiming}；目标周期={period}ms；错过周期={missed}；明细={details}",
                                _name, IoEndpointSummary(), block.Area, readTags.Count,
                                samples.Count(sample => sample.Value.Quality.IsGood),
                                samples.Count(sample => !sample.Value.Quality.IsGood), _lastScanMs,
                                driverCompleted - t0, completed - driverCompleted,
                                DescribeScanDiagnostics(FinalizeScan()), block.PeriodMs, completion.MissedPeriods,
                                DescribeReadBindings(readTags, samples, StatusCode.BadCommFailure));
                        }

                        _consecutiveNoGoodReads = 0;
                        _readOk++;
                        didGoodRead = true;
                        if (_circuit == CircuitState.HalfOpen) break;   // 半开只允许一个有效读取探测
                    }
                    catch (DriverReadYieldException)
                    {
                        retainTiming = false;
                        _deadlines.CompleteAfter(block, _clock.NowMilliseconds, 1);
                        now = _clock.NowMilliseconds;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { retainTiming = false; break; }
                    catch (Exception ex)
                    {
                        scanCompleted = _clock.NowMilliseconds;
                        readCompleted ??= scanCompleted;
                        scanError = ex.Message;
                        scanMissedPeriods = Math.Max(0, readCompleted.Value - block.DueMs) / block.PeriodMs;
                        _readErr++;
                        var failure = DriverOperationFailure.FromException(ex, _active.State);
                        scanMissingStatus = failure.Status;
                        RecordTelemetryQuality("bad", ex.Message, LogLevel.Warning, readTags, [], failure.Status);
                        _invalidNodeReads.BreakSequence(readTags);
                        MarkBindingsBad(readTags, failure.Status);
                        if (!failure.ConnectionLost)
                        {
                            int retryDelayMs = failure.Retry == DriverRetryDisposition.ReduceBatchAndRetry
                                ? 1
                                : Math.Clamp(block.PeriodMs, 1, 5_000);
                            _deadlines.CompleteAfter(block, _clock.NowMilliseconds, retryDelayMs);
                            hadReadFailure = true;
                            if (failure.Retry == DriverRetryDisposition.ReduceBatchAndRetry)
                            {
                                _planInitialized = false;
                                _timing.RecordBatchDownshift();
                                batchDownshifts++;
                            }
                            else
                            {
                                _timing.RecordSameSessionRetry();
                                sameSessionRetries++;
                            }
                            ReportDiagnostic(ex,
                                new DeviceConnectionDetail("PartialOnline",
                                    $"读取请求失败，会话仍在线；失败点位={DescribeReadBindings(readTags, [], failure.Status)}"),
                                ConnectionState.Connected, retrying: true, retryDelayMs);
                            LogBatchDiagnostic(LogLevel.Warning,
                                "设备 {dev} 读取批次异常但会话仍在线：{endpoint}；耗时={elapsed}ms；{protocolTiming}；范围={scope}；动作={retry}；{delay}ms 后复探；错误={msg}；明细={details}",
                                _name, IoEndpointSummary(), ScanTimingHistory.Milliseconds(scanCompleted.Value - t0),
                                DescribeScanDiagnostics(FinalizeScan()), failure.Scope, failure.Retry, retryDelayMs, failure.Message,
                                DescribeReadBindings(readTags, [], failure.Status));
                            // 把执行权交给排队写入；下次循环再读取该块。
                            break;
                        }

                        ReportDiagnostic(ex, DriverDetail(), ConnectionState.Faulted, retrying: true, _circuitOpenMs);
                        LogBatchDiagnostic(LogLevel.Warning,
                            "设备 {dev} 读取批次异常并确认连接失效：{endpoint}；耗时={elapsed}ms；{protocolTiming}；错误={msg}；明细={details}",
                            _name, IoEndpointSummary(), ScanTimingHistory.Milliseconds(scanCompleted.Value - t0),
                            DescribeScanDiagnostics(FinalizeScan()), failure.Message,
                            DescribeReadBindings(readTags, [], failure.Status));
                        await ResetTransportAsync(_active, ct);
                        _consecutiveNoGoodReads = 0;
                        connectionLost = true;
                        _planInitialized = false;
                        break;
                    }
                    finally
                    {
                        if (retainTiming)
                        {
                            // End at the read/publish boundary, excluding reconnect disposal and failure logging.
                            long end = scanCompleted ?? _clock.NowMilliseconds;
                            _lastScanMs = ScanTimingHistory.Milliseconds(end - t0);
                            _timing.RecordCompletion(block, end, scanSamples?.Count ?? 0, scanMissedPeriods);
                            ScanTimingRecord record = FinalizeScan();
                            var telemetryFields = new Dictionary<string, object?>
                            {
                                ["module"] = "acquisition",
                                ["feature"] = "plc_scan",
                                ["eventKind"] = "collection_batch",
                                ["operation"] = "read",
                                ["channel"] = _channelName,
                                ["device"] = _name,
                                ["database"] = block.Area,
                                ["blockId"] = record.BlockId,
                                ["protocol"] = _protocol,
                                ["status"] = record.Status,
                                ["durationMs"] = record.TotalMs,
                                ["driverMs"] = record.DriverMs,
                                ["localMs"] = record.LocalMs,
                                ["configuredPeriodMs"] = record.ConfiguredPeriodMs,
                                ["actualIntervalMs"] = record.ActualIntervalMs,
                                ["tagCount"] = record.TagCount,
                                ["sampleCount"] = record.SampleCount,
                                ["badCount"] = record.BadCount,
                                ["missedPeriods"] = record.MissedPeriods,
                                // 本批点位值：只引用样本，写日志时才序列化；异常批次只列坏点
                                [TagValuesLogField.FieldName] = new TagValuesLogField(
                                    readTags, scanSamples, scanMissingStatus, badOnly: record.Status != "good")
                            };
                            AddCollectionOwnerFields(telemetryFields, block);
                            // 成功批次按块每 30 秒汇总一条，异常批次逐条立即记(见 CollectionLogSummarizer)
                            (_collectionLog ??= new CollectionLogSummarizer(_log, CollectionLogWindow))
                                .Completed(record.BlockId, record.Status == "good", telemetryFields, _clock.NowMilliseconds);
                            scanActivity?.SetStatus(record.Status == "good" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
                        }
                    }
                }
            }

            if (connectionLost)
            {
                FailPendingWrites(StatusCode.BadCommFailure);
                if (await HandleFailureAsync(ct)) continue;
                RecordCommunicationFailure(_lastError);
                continue;
            }
            if (didGoodRead)
            {
                _fails = 0; backoff = 1.0;
                RecordCommunicationSuccess();   // 只有有效读取成功才关闭熔断并清零失败计数
                if (!hadReadFailure)
                    ApplyDriverDiagnostic(null, ConnectionState.Connected, retrying: false, nextRetryMs: 0);
            }

            // 串行协议仍在采集线程处理写入；支持多在途请求的协议交给独立高优先级写泵，
            // 避免一个大型读取请求把 QC/MES 写入阻塞 1–2 秒。
            if (SupportsConcurrentWritePipeline)
            {
                if (!_writes.IsEmpty || !_arrayWrites.IsEmpty) SignalConcurrentWriteWork();
            }
            else if (await DrainWritesAsync(ct))
            {
                if (!await HandleFailureAsync(ct)) RecordCommunicationFailure(_lastError);
                continue;
            }
            // 不按协议类型判断：驱动能力在重连后可能变化，已排队的请求必须有人处理。
            if (!_onDemandReads.IsEmpty) await DrainOnDemandReadsAsync(ct);

            long nextDueIn = _deadlines.DelayUntilNext(_clock.NowMilliseconds);
            int sleep = nextDueIn == long.MaxValue ? 100 : (int)Math.Clamp(nextDueIn, 1, 1000);
            if (!await WaitWorkOrDelay(sleep, ct)) break;   // 睡到下个到期组,或被新写请求提前唤醒(写低延迟)
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { /* 正常停机,不让 Completion 变 faulted 连累 StopAsync */ }
        finally
        {
            _acceptWrites = false;
            _collectionLog?.FlushAll();   // 停止前补记攒着的采集批次汇总
            _highResolutionWaiter?.Dispose();
            _highResolutionWaiter = null;
            await CloseRealtimeSubscriptionAsync();
            FailPendingWrites(StatusCode.BadNotConnected);     // 退出时(含异常)排空,挂起的写不悬空
            CompleteManualReconnectRequests(DrainManualReconnectRequests(), false);
            MarkAllBad(StatusCode.BadNotConnected);
            RecordTelemetryState("disconnected", "设备采集循环已停止", LogLevel.Information);
            try { await _primary.DisconnectAsync(); } catch { }
            if (_secondary != null) { try { await _secondary.DisposeAsync(); } catch { } }   // 备用连接也释放,避免句柄泄漏
        }
    }

    private void RecordTelemetryState(string status, string message, LogLevel level)
    {
        if (string.Equals(_lastTelemetryState, status, StringComparison.Ordinal)) return;
        _lastTelemetryState = status;
        GatewayTelemetry.Log(_log, level, "state", message, new Dictionary<string, object?>
        {
            ["channel"] = _channelName,
            ["device"] = _name,
            ["module"] = "acquisition",
            ["feature"] = "device_connection",
            ["eventKind"] = "device_state_change",
            ["operation"] = "connect",
            ["protocol"] = _protocol,
            ["status"] = status
        });
    }

    /// <summary>
    /// 采集质量好↔坏变化时记一条状态日志，带上触发这次变化的批次点位值：变坏时只列坏点(哪个点、什么状态)，
    /// 恢复时列当前值。点位值在状态真的变化时才创建，每批调用不产生额外开销。
    /// </summary>
    private void RecordTelemetryQuality(string status, string message, LogLevel level,
        IReadOnlyList<TagBinding>? tags = null, IReadOnlyList<TagSample>? samples = null, StatusCode missingStatus = default)
    {
        if (string.Equals(_lastTelemetryQuality, status, StringComparison.Ordinal)) return;
        _lastTelemetryQuality = status;
        var fields = new Dictionary<string, object?>
        {
            ["channel"] = _channelName,
            ["device"] = _name,
            ["module"] = "acquisition",
            ["feature"] = "data_quality",
            ["eventKind"] = "quality_change",
            ["operation"] = "read",
            ["protocol"] = _protocol,
            ["status"] = status,
            ["stateKind"] = "quality"
        };
        if (tags is not null)
            fields[TagValuesLogField.FieldName] = new TagValuesLogField(tags, samples, missingStatus, badOnly: status != "good");
        GatewayTelemetry.Log(_log, level, "state", message, fields);
    }

    /// <summary>成功采集批次的日志汇总周期。</summary>
    private static readonly TimeSpan CollectionLogWindow = TimeSpan.FromSeconds(30);
    private CollectionLogSummarizer? _collectionLog;

    private void AddCollectionOwnerFields(
        Dictionary<string, object?> fields,
        CompiledScanBlock block)
    {
        IReadOnlyList<ScanDemandOwner> owners = block.TelemetryOwners;
        if (block.TelemetryOwnerCount == 0)
        {
            fields["collectionSource"] = "configured";
            return;
        }

        IReadOnlyList<string> sources = block.TelemetryOwnerKinds;
        fields["collectionSource"] = sources.Count == 1 ? sources[0] : "shared";
        fields["collectionSources"] = string.Join(", ", sources);
        fields["ownerCount"] = block.TelemetryOwnerCount;
        fields["ownerListTruncated"] = block.TelemetryOwnersTruncated;

        ScanDemandOwner[] tasks = owners.Where(owner => owner.Kind == "mes").ToArray();
        if (tasks.Length == 0) return;
        fields["taskIds"] = string.Join(", ", tasks.Select(task => task.Id));
        fields["taskNames"] = string.Join(", ", tasks.Select(task => task.Name));
        string[] scopes = tasks.Select(task => task.Scope).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scopes.Length > 0) fields["scope"] = string.Join(", ", scopes);
        if (tasks.Length != 1) return;
        fields["taskId"] = tasks[0].Id;
        fields["taskName"] = tasks[0].Name;
    }

    private void RebuildPlanIfNeeded(long nowMs)
    {
        long version = _scanDemands.Version(_name);
        long hubVersion = _hub.DeviceDemandVersion(_name);
        bool hubHasDemand = _hub.HasDeviceDemand(_name, _activeWindowMs);
        long activeRefreshMs = _scanDemands.HasDemand(_name)
            ? Math.Max(30_000, _activeWindowMs)
            : 2_000;
        // all 模式也必须响应“首个客户端开始/停止读值”：MES 需求可能先把计划初始化为一个子集，
        // 随后 QC/REST 访问任一点时应立即扩展为全点计划。逐点版本变化只与 active 模式有关，
        // 避免 all 模式在百万点首次访问期间反复重编译同一份全点计划。
        bool demandStateChanged = hubHasDemand != _hubDemandActive;
        bool activeTagRefresh = _scanMode == "active" &&
            (hubVersion != _hubDemandVersion ||
             (hubHasDemand && nowMs - _lastActivePlanRefresh >= activeRefreshMs));
        long capabilityVersion = ActiveCapabilities.Version;
        long subscriptionFallbackVersion = Interlocked.Read(ref _subscriptionFallbackVersion);
        if (_planInitialized && version == _planVersion && capabilityVersion == _driverCapabilityVersion &&
            subscriptionFallbackVersion == _planSubscriptionFallbackVersion &&
            !demandStateChanged && !activeTagRefresh) return;

        var priorBlocks = new Dictionary<ScanBlockKey, CompiledScanBlock>();
        foreach (var old in _plan) priorBlocks.TryAdd(old.Key, old);

        var snapshot = _scanDemands.Snapshot(_name);
        var pollingSnapshot = _scanMode == "active" && _subscribedTagIds.Count > 0
            ? snapshot with
            {
                EffectivePeriods = snapshot.EffectivePeriods
                    .Where(item => !_subscribedTagIds.Contains(item.TagId) ||
                        _subscriptionFallbackTagIds.ContainsKey(item.TagId))
                    .ToArray()
            }
            : snapshot;
        var next = DeviceScanPlanCompiler.Compile(
            _bindings,
            _hub,
            pollingSnapshot,
            _active.Limits,
            ActiveCapabilities,
            includeAllTags: _scanMode == "all" && hubHasDemand,
            includeActiveTag: _scanMode == "active"
                ? tagId => (!_subscribedTagIds.Contains(tagId) || _subscriptionFallbackTagIds.ContainsKey(tagId)) &&
                    _hub.IsActive(tagId, _activeWindowMs)
                : null);
        AssignCollectionOwners(next, snapshot);

        _deadlines.Clear();
        foreach (var block in next)
        {
            priorBlocks.TryGetValue(block.Key, out var previous);
            if (previous is not null)
            {
                block.FailureLog = previous.FailureLog;
                if (block.Tags.SequenceEqual(previous.Tags))
                {
                    block.LastScanStartedMs = previous.LastScanStartedMs;
                    block.LastCompletedMs = previous.LastCompletedMs;
                    block.TimingTagIds = previous.TimingTagIds;
                }
            }
            long due = _circuit == CircuitState.HalfOpen
                ? nowMs
                : previous is not null && previous.DueMs > nowMs
                    ? previous.DueMs
                    : nowMs;
            _deadlines.Add(block, due);
        }

        _plan = next;
        bool needsHighResolution = next.Any(block => block.PeriodMs < 20);
        if (needsHighResolution && _highResolutionWaiter is null)
            _highResolutionWaiter = new HighResolutionWaiter();
        else if (!needsHighResolution && _highResolutionWaiter is not null)
        {
            _highResolutionWaiter.Dispose();
            _highResolutionWaiter = null;
        }
        int subscriptionOnly = _subscribedTagIds.Count(tagId => !_subscriptionFallbackTagIds.ContainsKey(tagId));
        _activeTags = next.Sum(block => block.Tags.Count) + subscriptionOnly;
        _planVersion = snapshot.Version;
        _driverCapabilityVersion = capabilityVersion;
        _planSubscriptionFallbackVersion = subscriptionFallbackVersion;
        _hubDemandVersion = hubVersion;
        _hubDemandActive = hubHasDemand;
        _planInitialized = true;
        _lastActivePlanRefresh = nowMs;
    }

    private static void AssignCollectionOwners(
        IReadOnlyList<CompiledScanBlock> blocks,
        DeviceScanDemandSnapshot snapshot)
    {
        if (blocks.Count == 0 || snapshot.OwnerRegistrations.Count == 0) return;
        var blocksByTag = new Dictionary<string, CompiledScanBlock>(
            blocks.Sum(block => block.Tags.Count), StringComparer.OrdinalIgnoreCase);
        foreach (CompiledScanBlock block in blocks)
        foreach (TagBinding tag in block.Tags)
            blocksByTag.TryAdd(tag.TagId, block);

        var ownersByBlock = new Dictionary<CompiledScanBlock, CollectionOwnerAccumulator>();
        foreach (ScanDemandOwnerRegistration registration in snapshot.OwnerRegistrations)
        foreach (string tagId in registration.TagIds)
        {
            if (!blocksByTag.TryGetValue(tagId, out CompiledScanBlock? block)) continue;
            if (!ownersByBlock.TryGetValue(block, out CollectionOwnerAccumulator? owners))
                ownersByBlock[block] = owners = new();
            owners.Add(registration.Owner);
        }

        foreach (CompiledScanBlock block in blocks)
            if (ownersByBlock.TryGetValue(block, out CollectionOwnerAccumulator? owners))
            {
                block.TelemetryOwners = owners.Retained;
                block.TelemetryOwnerKinds = owners.Kinds;
                block.TelemetryOwnerCount = owners.Count;
                block.TelemetryOwnersTruncated = owners.Count > owners.Retained.Count;
            }
    }

    private sealed class CollectionOwnerAccumulator
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _kinds = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScanDemandOwner> _retained = new(32);

        public int Count => _keys.Count;
        public IReadOnlyList<ScanDemandOwner> Retained => _retained;
        public IReadOnlyList<string> Kinds => _kinds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

        public void Add(ScanDemandOwner owner)
        {
            if (!_keys.Add(owner.Kind + ":" + owner.Id)) return;
            _kinds.Add(owner.Kind);
            if (_retained.Count < 32) _retained.Add(owner);
        }
    }

    private async Task ReconcileRealtimeSubscriptionAsync(long nowMs, CancellationToken ct)
    {
        if (_scanMode != "active" || _active is not IDriverSubscriptionProvider provider ||
            !ActiveCapabilities.SupportsSubscriptions || _active.State != ConnectionState.Connected)
        {
            if (_realtimeSubscription is not null) await CloseRealtimeSubscriptionAsync();
            return;
        }

        long hubVersion = _hub.DeviceDemandVersion(_name);
        long demandVersion = _scanDemands.Version(_name);
        bool refreshExpired = nowMs - _lastSubscriptionRefreshMs >= 2_000;
        if (_realtimeSubscription is not null && !refreshExpired &&
            hubVersion == _subscriptionDemandVersion && demandVersion == _subscriptionScanDemandVersion)
            return;
        // 退避期间不得被需求变化绕过：订阅刚刚连续失败时，新的 QC/MES 引用只会让重建风暴提前。
        if (_realtimeSubscription is null && nowMs < _nextSubscriptionRetryMs)
            return;

        var explicitPeriods = _scanDemands.Snapshot(_name).EffectivePeriods
            .ToDictionary(item => item.TagId, item => item.PeriodMs, StringComparer.Ordinal);
        var requestedPeriods = new Dictionary<string, int>(StringComparer.Ordinal);
        var explicitIds = explicitPeriods.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var binding in _bindings)
        {
            bool active = _hub.IsActive(binding.TagId, _activeWindowMs);
            if (!explicitPeriods.TryGetValue(binding.TagId, out int explicitPeriod) && !active) continue;
            int defaultPeriod = binding.Def.ScanRateMs <= 0 ? 1000 : binding.Def.ScanRateMs;
            requestedPeriods[binding.TagId] = Math.Max(1,
                explicitPeriods.ContainsKey(binding.TagId)
                    ? active ? Math.Min(defaultPeriod, explicitPeriod) : explicitPeriod
                    : defaultPeriod);
            _transientSubscriptionRetentionUntil.Remove(binding.TagId);
        }

        // 保留最近订阅过的瞬态 QC/REST 点一段安静期，使它们 30 秒活动窗口到期时不会把整组订阅
        // 拆掉重建。以前这里要求存在显式扫描需求，纯 REST/QC 访问的点因此完全不受保护——恰恰
        // 是抖动最严重的一类。显式需求点在其所有者离开后不保留。
        if (_realtimeSubscription is not null)
        {
            int retained = 0;
            foreach (string tagId in _subscribedTagIds)
            {
                if (requestedPeriods.ContainsKey(tagId) || _subscribedExplicitTagIds.Contains(tagId)) continue;
                if (!_bindingsByTagId.ContainsKey(tagId)) continue;
                if (!_transientSubscriptionRetentionUntil.TryGetValue(tagId, out long until))
                {
                    // 大工程下 QC 可能在几分钟内扫过成千上万个不同点位；保留数量必须有界，
                    // 否则订阅集合只增不减，反而把 PLC 的监控项配额吃光。超出上限的点不再保留，
                    // 下次被访问时重新订阅。
                    if (retained >= MaxRetainedTransientSubscriptionTags) continue;
                    until = nowMs + _transientSubscriptionRetentionMs;
                    _transientSubscriptionRetentionUntil[tagId] = until;
                }
                if (nowMs < until)
                {
                    retained++;
                    requestedPeriods[tagId] = _subscribedRequestedPeriods.TryGetValue(tagId, out int period)
                        ? period
                        : Math.Max(1, _bindingsByTagId[tagId].Def.ScanRateMs);
                }
                else
                    _transientSubscriptionRetentionUntil.Remove(tagId);
            }
        }

        var candidates = _bindings
            .Where(binding => requestedPeriods.ContainsKey(binding.TagId))
            .Select(binding => new TagBinding
            {
                TagId = binding.TagId,
                Def = binding.Def,
                Address = binding.Address,
                RequestedSamplingIntervalMs = requestedPeriods[binding.TagId],
            })
            .ToArray();
        var candidateIds = candidates.Select(binding => binding.TagId)
            .ToHashSet(StringComparer.Ordinal);
        var candidatePeriods = candidates.ToDictionary(
            binding => binding.TagId,
            binding => binding.RequestedSamplingIntervalMs ??
                (binding.Def.ScanRateMs <= 0 ? 1000 : binding.Def.ScanRateMs),
            StringComparer.Ordinal);

        _subscriptionDemandVersion = hubVersion;
        _subscriptionScanDemandVersion = demandVersion;
        _lastSubscriptionRefreshMs = nowMs;
        bool retryRejected = _realtimeSubscription is not null &&
            _requestedSubscriptionPeriods.Count == candidatePeriods.Count &&
            candidatePeriods.All(item => _requestedSubscriptionPeriods.TryGetValue(item.Key, out int period) &&
                period == item.Value);
        if (retryRejected)
        {
            _subscribedExplicitTagIds = explicitIds.Where(_subscribedTagIds.Contains).ToHashSet(StringComparer.Ordinal);
            if (_subscribedTagIds.SetEquals(candidateIds) || nowMs < _nextSubscriptionRetryMs) return;
            candidates = candidates.Where(binding => !_subscribedTagIds.Contains(binding.TagId)).ToArray();
        }
        else if (await TryRemoveSubscribedPointsAsync(candidatePeriods, candidateIds, explicitIds, ct))
            return;
        else
            await CloseRealtimeSubscriptionAsync();
        _planInitialized = false;
        if (candidates.Length == 0) return;
        // 请求被中断后，在同一会话内做一次有界优先重试，期间不让慢速兜底轮询抢占设备循环。
        // 再次中断则交回轮询并退避，避免优先重试无限抢占而饿死采集。
        for (int attempt = 0; attempt < 2; attempt++)
        {
        try
        {
            var notificationGoodIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var initialFrameReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int fallbackInitialized = 0;
            var lease = await provider.SubscribeAsync(candidates, samples =>
            {
                try
                {
                    Publish(samples);
                    bool changed = false;
                    foreach (var sample in samples)
                    {
                        if (!sample.Value.IsGood) continue;
                        notificationGoodIds.TryAdd(sample.TagId, 0);
                        changed |= _subscriptionFallbackTagIds.TryRemove(sample.TagId, out _);
                    }
                    if (changed) Interlocked.Increment(ref _subscriptionFallbackVersion);
                    if (changed && Volatile.Read(ref fallbackInitialized) != 0 && _subscriptionFallbackTagIds.IsEmpty)
                        initialFrameReady.TrySetResult();
                }
                catch (Exception ex) { _log.LogDebug(ex, "设备 {dev} 实时订阅发布失败", _name); }
            }, ct);
            var acceptedIds = lease.TagIds.ToHashSet(StringComparer.Ordinal);
            _realtimeSubscription ??= new RealtimeSubscriptions();
            // 全部点位被拒绝的租约没有任何监控项，但仍占用 PLC 侧订阅资源（S7-1500 的 OPC UA
            // 服务器订阅数有限），立即释放；被拒点继续走轮询，重建由退避控制，不会形成抖动。
            if (acceptedIds.Count > 0) _realtimeSubscription.Add(lease);
            else { try { await lease.DisposeAsync(); } catch { } }
            _requestedSubscriptionPeriods = candidatePeriods;
            _subscribedTagIds = _subscribedTagIds.Concat(acceptedIds).ToHashSet(StringComparer.Ordinal);
            _subscribedRequestedPeriods = candidatePeriods
                .Where(item => _subscribedTagIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            _subscribedExplicitTagIds = explicitIds.Where(_subscribedTagIds.Contains).ToHashSet(StringComparer.Ordinal);
            foreach (string tagId in acceptedIds)
                if (!notificationGoodIds.ContainsKey(tagId)) _subscriptionFallbackTagIds.TryAdd(tagId, 0);
            Volatile.Write(ref fallbackInitialized, 1);
            if (_subscriptionFallbackTagIds.IsEmpty) initialFrameReady.TrySetResult();
            Interlocked.Increment(ref _subscriptionFallbackVersion);
            _nextSubscriptionRetryMs = _subscribedTagIds.SetEquals(candidateIds) ? 0 : nowMs + 30_000;
            _planInitialized = false;
            _log.LogInformation("设备 {dev} 已建立原生变化订阅：{count} 点；周期业务读取 DataHub 最新快照，仅首值失败点继续批量补读",
                _name, _subscribedTagIds.Count);
            if (!initialFrameReady.Task.IsCompleted)
            {
                int publishingPeriod = candidates.Length == 0 ? 100 : candidates.Min(binding =>
                    binding.RequestedSamplingIntervalMs ?? (binding.Def.ScanRateMs <= 0 ? 1000 : binding.Def.ScanRateMs));
                int graceMs = Math.Clamp(publishingPeriod * 2, 100, 500);
                try { await initialFrameReady.Task.WaitAsync(TimeSpan.FromMilliseconds(graceMs), ct); }
                catch (TimeoutException) { }
            }
            int fallback = await PrimeRealtimeSubscriptionAsync(ct);
            if (fallback > 0)
            {
                _planInitialized = false;
                _log.LogWarning("设备 {dev} 原生订阅首帧仍有 {count} 点未取得 Good 数据；订阅保持活动，仅这些点同时使用周期批量读取兜底",
                    _name, fallback);
            }
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (retryRejected)
        {
            // A failed incremental retry must not invalidate healthy leases or their polling exclusion.
            _nextSubscriptionRetryMs = _clock.NowMilliseconds + 30_000;
            _log.LogWarning("设备 {dev} 部分点订阅重试失败，正常订阅保持活动，失败点继续轮询，30 秒后复试：{msg}",
                _name, ex.Message);
            return;
        }
        catch (Exception ex) when (attempt == 0 && IsBadRequestInterrupted(ex))
        {
            _planInitialized = false;
            _log.LogInformation("设备 {dev} 原生订阅请求被中断，暂停补读，100ms 后在当前会话优先重试一次：{msg}",
                _name, ex.Message);
            await Task.Delay(100, ct);
        }
        catch (Exception ex)
        {
            _subscribedTagIds = new HashSet<string>(StringComparer.Ordinal);
            _subscribedRequestedPeriods = new Dictionary<string, int>(StringComparer.Ordinal);
            _subscribedExplicitTagIds = new HashSet<string>(StringComparer.Ordinal);
            // 退避从失败真正完成的时刻起算，而不是进入本次检查时的时间戳，
            // 否则一次耗时很久的失败会让下一次重试立刻到期。
            _nextSubscriptionRetryMs = _clock.NowMilliseconds + 5_000;
            _planInitialized = false;
            _log.LogWarning("设备 {dev} 原生订阅建立失败，自动回退批量读取，5 秒后复试：{msg}",
                _name, ex.Message);
            return;
        }
        }
    }

    private async Task CloseRealtimeSubscriptionAsync()
    {
        var subscription = Interlocked.Exchange(ref _realtimeSubscription, null);
        _requestedSubscriptionPeriods = new Dictionary<string, int>(StringComparer.Ordinal);
        _subscribedTagIds = new HashSet<string>(StringComparer.Ordinal);
        _subscribedRequestedPeriods = new Dictionary<string, int>(StringComparer.Ordinal);
        _subscribedExplicitTagIds = new HashSet<string>(StringComparer.Ordinal);
        _transientSubscriptionRetentionUntil.Clear();
        if (!_subscriptionFallbackTagIds.IsEmpty)
        {
            _subscriptionFallbackTagIds.Clear();
            Interlocked.Increment(ref _subscriptionFallbackVersion);
        }
        if (subscription is not null)
        {
            try { await subscription.DisposeAsync(); } catch { }
        }
        _planInitialized = false;
    }

    /// <summary>
    /// 需求只是"少了几个点"、其余点采样周期不变时，直接摘除这些监控项，不拆整组重建。
    /// 几十万点会被切成几十个订阅，一次整组重建就是几十轮删除加创建的协议往返，期间该设备
    /// 完全不出数据。返回 true 表示已按增量处理完毕，调用方无需再重建。
    /// </summary>
    private async Task<bool> TryRemoveSubscribedPointsAsync(
        Dictionary<string, int> candidatePeriods,
        HashSet<string> candidateIds,
        HashSet<string> explicitIds,
        CancellationToken ct)
    {
        if (_realtimeSubscription is not { } subscription || !subscription.SupportsIncrementalRemoval) return false;
        if (_subscribedTagIds.Count == 0) return false;
        // 只处理纯减少：有新增点或任何点的采样周期变化，仍然走原来的重建路径。
        if (!candidateIds.IsProperSubsetOf(_subscribedTagIds)) return false;
        foreach (var item in candidatePeriods)
            if (!_subscribedRequestedPeriods.TryGetValue(item.Key, out int period) || period != item.Value)
                return false;

        var removed = _subscribedTagIds.Where(tagId => !candidateIds.Contains(tagId)).ToArray();
        if (removed.Length == 0) return false;

        try { await subscription.RemoveAsync(removed, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 摘除失败不能让剩下的点跟着掉线：交回重建路径按原有退避处理。
            _log.LogWarning("设备 {dev} 增量摘除 {count} 个订阅点失败，回退整组重建：{msg}",
                _name, removed.Length, ex.Message);
            return false;
        }

        var dropped = removed.ToHashSet(StringComparer.Ordinal);
        foreach (string tagId in removed) _subscriptionFallbackTagIds.TryRemove(tagId, out _);
        _subscribedTagIds = _subscribedTagIds.Where(tagId => !dropped.Contains(tagId))
            .ToHashSet(StringComparer.Ordinal);
        _subscribedRequestedPeriods = _subscribedRequestedPeriods.Where(item => !dropped.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        _requestedSubscriptionPeriods = _requestedSubscriptionPeriods.Where(item => !dropped.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        _subscribedExplicitTagIds = explicitIds.Where(_subscribedTagIds.Contains).ToHashSet(StringComparer.Ordinal);
        Interlocked.Increment(ref _subscriptionFallbackVersion);
        // 摘除的点回到周期轮询计划，需要重编。
        _planInitialized = false;
        _log.LogInformation("设备 {dev} 增量摘除 {count} 个订阅点，剩余 {rest} 点保持推送，未重建订阅",
            _name, removed.Length, _subscribedTagIds.Count);
        return true;
    }

    private sealed class RealtimeSubscriptions : IAsyncDisposable
    {
        private readonly List<IDriverSubscription> _leases = new();
        public void Add(IDriverSubscription lease) => _leases.Add(lease);

        /// <summary>每条租约都支持增量摘除时才算成功；任一条不支持就交回调用方整组重建。</summary>
        public bool SupportsIncrementalRemoval =>
            _leases.Count > 0 && _leases.All(lease => lease is IIncrementalDriverSubscription);

        public async Task<int> RemoveAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct)
        {
            int removed = 0;
            foreach (var lease in _leases.OfType<IIncrementalDriverSubscription>())
                removed += await lease.RemoveAsync(tagIds, ct).ConfigureAwait(false);
            // 监控项被摘光的租约留着也没有用处，顺手释放。
            var empty = _leases.Where(lease => lease.TagIds.Count == 0).ToArray();
            foreach (var lease in empty)
            {
                _leases.Remove(lease);
                try { await lease.DisposeAsync(); } catch { }
            }
            return removed;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var lease in _leases)
                try { await lease.DisposeAsync(); } catch { }
            _leases.Clear();
        }
    }

    private static bool IsBadRequestInterrupted(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is DriverOperationException operation &&
                operation.Status == StatusCode.BadRequestInterrupted)
                return true;
        return false;
    }

    /// <summary>
    /// 原生订阅只保证后续变化通知，不保证所有 OPC UA/MQTT 服务器都会主动发送首值。
    /// 对新订阅做一次受驱动能力上限约束的批量读取来填充 DataHub；只要存在首读失败点，
    /// 仅失败点使用周期批量读取兜底，保持正常点订阅，不把局部首读失败升级成设备掉线。
    /// </summary>
    private async Task<int> PrimeRealtimeSubscriptionAsync(CancellationToken ct)
    {
        var pending = _bindings
            .Where(binding => _subscribedTagIds.Contains(binding.TagId) &&
                _subscriptionFallbackTagIds.ContainsKey(binding.TagId))
            .ToArray();
        if (pending.Length == 0) return 0;

        var capabilities = ActiveCapabilities;
        int maxItems = capabilities.SupportsBatchRead
            ? Math.Max(1, capabilities.MaxTagsPerRead)
            : 1;
        var batches = TransferBatchPlanner.Partition(
            pending,
            maxItems,
            capabilities.MaxReadBytes,
            static binding => TransferBatchPlanner.EstimateBytes(binding.Def, binding.Address));

        foreach (var batch in batches)
        {
            // 前面批次读取期间，订阅通知可能已经送来了这些点的首值；已经拿到的不再重复读。
            var stillMissing = batch.Where(binding => _subscriptionFallbackTagIds.ContainsKey(binding.TagId)).ToArray();
            if (stillMissing.Length == 0) continue;
            try
            {
                // 补读期间本设备的采集循环处于等待状态，走优先通道尽快完成；
                // 同时带上写入代次，读取期间发生的写入不会被这次读到的旧值覆盖。
                long writeEpochAtReadStart = Volatile.Read(ref _writeEpoch);
                IReadOnlyList<TagSample> samples = await ReadForWriteVerificationAsync(stillMissing, ct);
                Publish(samples, writeEpochAtReadStart: writeEpochAtReadStart);
                foreach (var sample in samples)
                    if (sample.Value.IsGood && _subscribedTagIds.Contains(sample.TagId))
                    {
                        if (_subscriptionFallbackTagIds.TryRemove(sample.TagId, out _))
                            Interlocked.Increment(ref _subscriptionFallbackVersion);
                    }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("设备 {dev} 原生订阅首帧批量读取失败：{msg}", _name, ex.Message);
            }
        }

        int fallback = _subscriptionFallbackTagIds.Count;
        if (fallback == 0)
        {
            _log.LogInformation("设备 {dev} 原生订阅首帧批量读取完成：{count} 点，后续使用变化通知",
                _name, _subscribedTagIds.Count);
        }
        return fallback;
    }

    private bool HasDemand() => !_writes.IsEmpty || !_arrayWrites.IsEmpty || !_onDemandReads.IsEmpty ||
        Volatile.Read(ref _explicitDemand) > 0 ||
        _scanDemands.HasDemand(_name) || _hub.HasDeviceDemand(_name, _activeWindowMs);

    private bool RecordCommunicationFailure(string reason)
    {
        int failures = ++_communicationFailures;
        if (_circuit != CircuitState.HalfOpen && failures < _circuitFailureThreshold) return false;
        _circuit = CircuitState.Open;
        Volatile.Write(ref _circuitOpenUntil, _clock.NowMilliseconds + _circuitOpenMs);
        _log.LogWarning("设备 {dev} 通信连续失败 {n} 次,熔断 {ms}ms:{reason}",
            _name, failures, _circuitOpenMs, reason);
        // 只有网络/传输层断线才重建整条通道；认证、权限、型号、DB 地址等配置问题
        // 保留当前对象并持续展示准确诊断，避免无意义地冲击同通道其它在线设备。
        var descriptor = ConnectionDiagnosticClassifier.Classify(new IOException(reason), null);
        if (descriptor.Scope == ConnectionDiagnosticScope.Transport)
            _requestChannelHardReconnect?.Invoke(_channelName);
        return true;
    }

    private void RecordCommunicationSuccess()
    {
        bool recovered = _circuit == CircuitState.HalfOpen;
        _circuit = CircuitState.Closed;
        _communicationFailures = 0;
        if (recovered) _log.LogInformation("设备 {dev} 半开探测读取成功,恢复正常采集", _name);
    }

    // 一次失败:到阈值后优先「冗余切换到备用」,否则「自动降级」。返回 true=已处理(调用方 continue)
    private async Task<bool> HandleFailureAsync(CancellationToken ct)
    {
        _fails++;
        if (_fails < _demoteAfter) return false;          // 未到阈值 → 正常重试
        _fails = 0;
        if (_secondary != null && !_onSecondary)          // 冗余:切到备用
        {
            try { await _active.DisconnectAsync(); } catch { }
            _active = _secondary; _onSecondary = true;
            _planInitialized = false;
            _nextProbe = Environment.TickCount64 + _monitorIntervalMs;
            _log.LogWarning("设备 {dev} 主路径连续失败 {n} 次 → 切换到备用(次级路径)", _name, _demoteAfter);
            return true;
        }
        if (_demoteEnabled)                                // 降级
        {
            _demotedUntil = Environment.TickCount64 + _demotePeriodMs;
            _log.LogWarning("设备 {dev} 连续失败 {n} 次 → 降级 {p}ms", _name, _demoteAfter, _demotePeriodMs);
            return true;
        }
        return false;
    }

    // 探测主路径是否恢复:连上且最小读取块全 Good 才算恢复
    private async Task<bool> ProbePrimaryAsync(CancellationToken ct)
    {
        try
        {
            if (_primary.State != ConnectionState.Connected) await ConnectTrackedAsync(_primary, ct);
            IReadOnlyList<TagBinding> probe = _plan.Count > 0
                ? _plan[0].Tags
                : _bindings.Count > 0 ? new[] { _bindings[0] } : Array.Empty<TagBinding>();
            if (probe.Count > 0)
            {
                var s = await _primary.ReadAsync(probe, ct);
                if (s.Count == 0 || !s.All(x => x.Value.IsGood)) { try { await _primary.DisconnectAsync(); } catch { } return false; }
            }
            return true;
        }
        catch { try { await _primary.DisconnectAsync(); } catch { } return false; }
    }

    private static bool HasUsableSample(IReadOnlyList<TagSample> samples)
    {
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Value.IsGood) return true;
            if (samples[i].Value.Kind == ValueKind.Ref &&
                samples[i].Value.Value is ArrayRuntimeValue array && array.GoodCount > 0) return true;
        }
        return false;
    }

    private IReadOnlyList<ArrayTagHealth> RecordArrayHealth(IReadOnlyList<TagSample> samples)
    {
        List<ArrayTagHealth>? result = null;
        ArrayDriverCapabilities? capabilities = null;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.Value.Kind != ValueKind.Ref ||
                sample.Value.Value is not ArrayRuntimeValue array ||
                !_pub.TryGetValue(sample.TagId, out var published) || published.def.Array is null)
                continue;
            capabilities ??= (_active as IArrayCapableDriver)?.ArrayCapabilities ?? UnknownArrayCapabilities;
            ArrayTagHealth health;
            lock (_arrayHealthGate)
            {
                if (array.BadCount == 0 && _arrayHealth.TryGetValue(sample.TagId, out var prior) &&
                    prior.Total == array.Count && prior.Good == array.GoodCount &&
                    prior.Uncertain == array.UncertainCount)
                    health = prior;
                else
                    health = ArrayHealthDiagnostics.Summarize(sample.TagId, array, published.def.Array, capabilities);
                _arrayHealth[sample.TagId] = health;
            }
            if (health.Bad > 0 || health.Uncertain > 0) (result ??= []).Add(health);
        }
        return result is null ? Array.Empty<ArrayTagHealth>() : result;
    }

    /// <param name="isolated">
    /// 已隔离的坏点判定。已隔离 = 问题已识别、已在点位异常里逐点记录、每 30 秒复探一次，
    /// 不再计入「部分在线」：否则一个配错的点位会让设备状态随复探周期在在线/异常之间反复跳。
    /// </param>
    private static bool TryBuildPartialReadMessage(
        IReadOnlyList<TagSample> samples,
        IReadOnlyList<ArrayTagHealth> arrayHealth,
        Func<string, bool>? isolated,
        out string message)
    {
        int scalarBad = 0;
        int isolatedBad = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Value.IsGood) continue;
            if (samples[i].Value.Kind == ValueKind.Ref && samples[i].Value.Value is ArrayRuntimeValue) continue;
            if (isolated?.Invoke(samples[i].TagId) == true) { isolatedBad++; continue; }
            scalarBad++;
        }

        int affectedArrayCount = 0;
        for (int i = 0; i < arrayHealth.Count; i++)
            if (arrayHealth[i].Bad > 0 || arrayHealth[i].Uncertain > 0) affectedArrayCount++;
        if (scalarBad == 0 && affectedArrayCount == 0)
        {
            // 全部坏点都已隔离：设备照常算在线，明细在监控中心的「点位异常」里逐点可见
            message = isolatedBad > 0 ? $"已隔离坏点 {isolatedBad} 个，30 秒复探一次" : "";
            return false;
        }

        var details = new List<string>();
        if (scalarBad > 0) details.Add($"普通点位 Bad {scalarBad}/{samples.Count}");
        for (int i = 0; i < arrayHealth.Count; i++)
            if (arrayHealth[i].Bad > 0 || arrayHealth[i].Uncertain > 0)
                details.Add(arrayHealth[i].Message);
        message = "部分数据读取异常：" + string.Join("；", details);
        return true;
    }

    private bool IsReadSuperseded(string tagId, long writeEpochAtReadStart) =>
        writeEpochAtReadStart >= 0 &&
        _lastWriteEpochByTag.TryGetValue(tagId, out long tagWriteEpoch) && tagWriteEpoch > writeEpochAtReadStart;

    private void QueueVerifiedNodeRecovery(IReadOnlyList<TagSample> samples)
    {
        if (_invalidNodeReads.QueueVerifiedRecovery(samples)) _writeSignal.Release();
    }

    private readonly object _publicationGate = new();

    private void Publish(
        IReadOnlyList<TagSample> samples,
        CompiledScanBlock? block = null,
        long writeEpochAtReadStart = -1,
        bool writeVerification = false)
    {
        // Only cache publication is serialized; PLC reads and writes remain independent.
        // A scan begun after enqueue but before verification must not restore its old value.
        lock (_publicationGate)
        {
            if (writeVerification)
                foreach (var sample in samples)
                    _lastWriteEpochByTag[sample.TagId] = Interlocked.Increment(ref _writeEpoch);
            PublishCore(samples, block, writeEpochAtReadStart);
        }
    }

    private void PublishCore(
        IReadOnlyList<TagSample> samples,
        CompiledScanBlock? block = null,
        long writeEpochAtReadStart = -1)
    {
        var serverTs = DateTime.UtcNow;
        var updates = ArrayPool<DataHubUpdate>.Shared.Rent(Math.Max(1, samples.Count));
        int count = 0;
        try
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                object handle;
                TagDef def;
                if (block is not null && i < block.Tags.Count &&
                    (ReferenceEquals(s.TagId, block.Tags[i].TagId) ||
                     string.Equals(s.TagId, block.Tags[i].TagId, StringComparison.Ordinal)))
                {
                    handle = block.HubHandles[i];
                    def = block.Tags[i].Def;
                }
                else
                {
                    if (!_pub.TryGetValue(s.TagId, out var p)) continue;
                    handle = p.h;
                    def = p.def;
                }
                // 支持并发服务调用的协议允许写入绕过大型扫描。若某点在本次读取发出后被写过，
                // 这份较早的读取响应不得覆盖写后回读已经发布的新值。
                if (writeEpochAtReadStart >= 0 &&
                    Volatile.Read(ref _writeEpoch) != writeEpochAtReadStart &&
                    IsReadSuperseded(s.TagId, writeEpochAtReadStart))
                    continue;
                var sv = s.Value;
                // 变体路径:缩放/坏质量透传/非规范浮点归零 全程零装箱(驱动产出的短命箱已在 DataValue 构造时拆掉)
                DataValue nv = sv.IsGood ? ValueOps.ApplyScaling(in sv, def.Scaling, serverTs) : sv.WithServer(serverTs);
                if (_zeroNonNormalFloat && sv.IsGood) nv = nv.NonNormalToZero();

                bool notify = false;
                if (_hub.HasSubscribers(handle) && DataHub.TryReadHandle(handle, out var old))
                    notify = old.Quality != nv.Quality || !old.IsGood ||
                        ValueOps.ExceedsDeadband(in old, in nv, def);
                updates[count++] = new DataHubUpdate(handle, nv, notify);
            }
            _hub.UpdateBatch(updates.AsSpan(0, count));
        }
        finally
        {
            Array.Clear(updates, 0, count);
            ArrayPool<DataHubUpdate>.Shared.Return(updates);
        }
    }

    private void MarkAllBad(StatusCode code)
    {
        var ts = DateTime.UtcNow;
        foreach (var binding in _bindings)
        {
            if (!_pub.TryGetValue(binding.TagId, out var p)) continue;
            if (DataHub.TryReadHandle(p.h, out var old) && old.Quality == code) continue;
            _hub.UpdateByHandle(p.h, BadValueFor(binding, code, ts));
        }
    }

    private static DataValue BadValueFor(TagBinding binding, StatusCode code, DateTime ts)
    {
        // 数组点位保持「全 Bad 数组壳」,监控/切片接口才不会退化成 409。
        var shell = binding.Def.Array is null ? null : ArrayBadShell.CreateBadShell(binding.Def.Array, code);
        return shell is null ? DataValue.BadAt(code, ts) : new DataValue(shell, code, ts, ts);
    }

    private void MarkBlockBad(CompiledScanBlock block, StatusCode code) => MarkBindingsBad(block.Tags, code);

    private void MarkBindingsBad(IReadOnlyList<TagBinding> bindings, StatusCode code)
    {
        var ts = DateTime.UtcNow;
        foreach (var b in bindings)
            if (_pub.TryGetValue(b.TagId, out var p)) _hub.UpdateByHandle(p.h, BadValueFor(b, code, ts));
    }

    private static async Task<bool> DelaySafe(double seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.001, seconds)), ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // 北向写:排入本设备的写队列；由串行采集线程或并发写泵处理，返回真实写后回读结果。
    public Task<StatusCode> EnqueueWriteAsync(WriteRequest req, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<StatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_circuit != CircuitState.Closed) { tcs.SetResult(StatusCode.BadNotConnected); return tcs.Task; }
        if (!_acceptWrites) { tcs.SetResult(StatusCode.BadNotConnected); return tcs.Task; }
        if (!TryReserveWriteSlot())
        {
            tcs.SetResult(StatusCode.BadTooManyOperations);
            return tcs.Task;
        }

        long generation = _writeGenerations.AddOrUpdate(req.TagId, 1, static (_, current) => unchecked(current + 1));
        _lastWriteEpochByTag[req.TagId] = Interlocked.Increment(ref _writeEpoch);
        var pending = new PendingWrite(req, tcs, _clock.NowMilliseconds, generation,
            cancellationToken, ReleaseWriteSlot);
        if (!pending.IsTerminal) _writes.Enqueue(pending);
        SignalWriteArrival();
        if (!_acceptWrites) FailPendingWrites(StatusCode.BadNotConnected);   // 入队后再查:若刚好停机,兜底排空(TrySetResult 幂等),防 TCS 永久挂起
        return tcs.Task;
    }

    public Task<ArrayWriteResult> EnqueueArrayWriteAsync(
        ArrayWriteRequest req,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ArrayWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_circuit != CircuitState.Closed || !_acceptWrites)
        {
            tcs.SetResult(ArrayWriteResult.Failed(StatusCode.BadNotConnected, "设备未连接"));
            return tcs.Task;
        }
        if (_active is not IArrayCapableDriver)
        {
            tcs.SetResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, "当前驱动不支持数组写入"));
            return tcs.Task;
        }
        if (!TryReserveWriteSlot())
        {
            tcs.SetResult(ArrayWriteResult.Failed(StatusCode.BadTooManyOperations, "设备写入队列已达到上限"));
            return tcs.Task;
        }

        long generation = _writeGenerations.AddOrUpdate(
            req.Binding.TagId, 1, static (_, current) => unchecked(current + 1));
        _lastWriteEpochByTag[req.Binding.TagId] = Interlocked.Increment(ref _writeEpoch);
        var pending = new PendingArrayWrite(req, tcs, generation, cancellationToken, ReleaseWriteSlot);
        if (!pending.IsTerminal) _arrayWrites.Enqueue(pending);
        SignalWriteArrival();
        if (!_acceptWrites) FailPendingWrites(StatusCode.BadNotConnected);
        return tcs.Task;
    }

    private bool TryReserveWriteSlot()
    {
        int depth = Interlocked.Increment(ref _pendingWriteCount);
        if (depth <= _maxPendingWrites)
        {
            _timing.ObserveWriteQueueDepth(depth);
            return true;
        }
        Interlocked.Decrement(ref _pendingWriteCount);
        return false;
    }

    private void ReleaseWriteSlot() => Interlocked.Decrement(ref _pendingWriteCount);

    private static TaskCompletionSource<bool> NewWriteArrivalSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> CaptureNextWriteArrival()
    {
        while (true)
        {
            var current = Volatile.Read(ref _writeArrivalSignal);
            if (!current.Task.IsCompleted) return current;
            var next = NewWriteArrivalSignal();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _writeArrivalSignal, next, current), current))
                return next;
        }
    }

    private void SignalWriteArrival()
    {
        Volatile.Read(ref _writeArrivalSignal).TrySetResult(true);
        SignalWriteWork();
    }

    private void SignalWriteWork()
    {
        if (Interlocked.Exchange(ref _writeWakePending, 1) == 0) _writeSignal.Release();
        SignalConcurrentWriteWork();
    }

    private void SignalConcurrentWriteWork()
    {
        if (Interlocked.Exchange(ref _concurrentWriteWakePending, 1) == 0)
            _concurrentWriteSignal.Release();
    }

    private async Task ConcurrentWritePumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _concurrentWriteSignal.WaitAsync(ct);
                Interlocked.Exchange(ref _concurrentWriteWakePending, 0);
                if (!SupportsConcurrentWritePipeline || _active.State != ConnectionState.Connected ||
                    _circuit != CircuitState.Closed)
                    continue;

                while (SupportsConcurrentWritePipeline && _active.State == ConnectionState.Connected &&
                       _circuit == CircuitState.Closed && (!_writes.IsEmpty || !_arrayWrites.IsEmpty))
                {
                    if (await DrainWritesAsync(ct))
                    {
                        SignalWriteWork();
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停机。
        }
    }

    private readonly ConcurrentDictionary<string, PendingWrite> _retryingWrites = new(StringComparer.Ordinal);
    private const int FreshWriteBurstLimit = 8;
    private int _freshWriteBatchesSinceRetry;
    private int _arrayRetryDeferrals;

    private void CompletePendingWrite(PendingWrite pending, StatusCode status)
    {
        _retryingWrites.TryRemove(new KeyValuePair<string, PendingWrite>(pending.Req.TagId, pending));
        pending.Complete(status);
    }

    private List<PendingWrite> TakeWriteBatch(int batchLimit)
    {
        int scanBudget = Math.Min(Math.Max(1, Volatile.Read(ref _pendingWriteCount)),
            Math.Max(1_024, batchLimit));
        var batch = new List<PendingWrite>(Math.Min(batchLimit, 1_024));
        var deferred = new List<PendingWrite>();
        var retries = new List<PendingWrite>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int examined = 0; examined < scanBudget; examined++)
        {
            if (!_writes.TryDequeue(out var pending)) break;
            if (pending.IsTerminal)
            {
                _retryingWrites.TryRemove(new KeyValuePair<string, PendingWrite>(pending.Req.TagId, pending));
                pending.Dispose();
                continue;
            }

            bool collapse = _writeOpt == "latest" ||
                (_writeOpt == "nonbool-latest" && pending.Req.Def.DataType != IgwDataType.Bool);
            long latestGeneration = _writeGenerations.GetValueOrDefault(pending.Req.TagId, pending.Generation);
            if ((collapse && pending.Generation < latestGeneration) ||
                (pending.Attempts > 0 && latestGeneration > pending.DispatchGenerationCeiling))
            {
                CompletePendingWrite(pending, StatusCode.BadWriteSuperseded);
                continue;
            }

            // Previously queued pulse edges and all-values writes may not overtake an
            // unresolved write of the same tag when that write yields to unrelated work.
            if (_retryingWrites.TryGetValue(pending.Req.TagId, out var earlier) &&
                !earlier.IsTerminal && !ReferenceEquals(earlier, pending))
            {
                deferred.Add(pending);
                continue;
            }
            if (pending.Attempts > 0)
            {
                retries.Add(pending);
                continue;
            }

            // 一个协议批次中同一 TagId 只能出现一次；顺序敏感值留到下一轮，
            // 避免 ToDictionary 重复键和驱动对同点多写的未定义行为。
            if (!seen.Add(pending.Req.TagId) || batch.Count >= batchLimit)
            {
                deferred.Add(pending);
                continue;
            }

            batch.Add(pending);
        }

        // Keep new, unrelated work out of slow retry batches. A retry may only resume
        // after fresh work gets a turn, but continuous arrivals cannot retain a slot forever.
        if (_freshWriteBatchesSinceRetry >= FreshWriteBurstLimit &&
            retries.Any(item => item.RetryNotBeforeMs <= Environment.TickCount64))
        {
            // These fresh items have not transitioned to dispatched yet: cancellation and
            // all-values ordering still apply while they wait behind the bounded retry turn.
            deferred.InsertRange(0, batch);
            batch.Clear();
        }
        bool allowRetries = batch.Count == 0;
        foreach (var pending in retries)
        {
            if (batch.Count < batchLimit && allowRetries &&
                pending.RetryNotBeforeMs <= Environment.TickCount64)
                batch.Add(pending);
            else deferred.Add(pending);
        }
        if (allowRetries && batch.Count > 0) _freshWriteBatchesSinceRetry = 0;
        else if (batch.Count > 0 && !_retryingWrites.IsEmpty)
            _freshWriteBatchesSinceRetry = Math.Min(FreshWriteBurstLimit, _freshWriteBatchesSinceRetry + 1);
        else if (_retryingWrites.IsEmpty) _freshWriteBatchesSinceRetry = 0;
        batch.RemoveAll(item =>
        {
            long latest = _writeGenerations.GetValueOrDefault(item.Req.TagId, item.Generation);
            bool collapse = _writeOpt == "latest" ||
                (_writeOpt == "nonbool-latest" && item.Req.Def.DataType != IgwDataType.Bool);
            if (collapse && item.Generation < latest)
            {
                CompletePendingWrite(item, StatusCode.BadWriteSuperseded);
                return true;
            }
            if (item.TryBeginDispatch(latest))
                return false;
            item.Dispose();
            return true;
        });
        foreach (var pending in deferred) _writes.Enqueue(pending);
        if (!_writes.IsEmpty) SignalWriteWork();
        return batch;
    }

    // 排空写队列:按优化方法合并 → 工作循环限流 → 批量下发。返回 true=写失败疑似断线(调用方触发重连)
    private async Task<bool> DrainWritesAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _writeWakePending, 0);
        if (await DrainArrayWritesAsync(ct)) return true;
        if (_writes.IsEmpty) return false;

        // 批量大小来自驱动/服务器真实能力；旧 dutyCycle 不再把大型产线固定卡在 10 点。
        int batchLimit = Math.Clamp(ActiveCapabilities.MaxTagsPerWrite, 1, 100_000);
        var arrival = CaptureNextWriteArrival();
        var batch = TakeWriteBatch(batchLimit);
        if (batch.Count == 0)
        {
            long readyAt = _writes.Where(item => !item.IsTerminal && item.Attempts > 0)
                .Select(item => item.RetryNotBeforeMs).DefaultIfEmpty(Environment.TickCount64).Min();
            int delayMs = (int)Math.Clamp(readyAt - Environment.TickCount64, 1, 250);
            await Task.WhenAny(Task.Delay(delayMs, ct), arrival.Task);
            ct.ThrowIfCancellationRequested();
            return false;
        }

        try
        {
            int queueDelayMs = (int)Math.Clamp(
                _clock.NowMilliseconds - batch.Min(item => item.EnqueuedMs), 0, int.MaxValue);
            Interlocked.Increment(ref _priorityProtocolOperationDepth);
            WriteVerificationBatchOutcome outcome;
            try { outcome = await WriteAndVerifyAsync(batch, ct); }
            finally { Interlocked.Decrement(ref _priorityProtocolOperationDepth); }
            _timing.RecordWritePipeline(queueDelayMs, outcome.ResponseMs,
                outcome.VerificationMs, outcome.RetryRounds);
            var map = outcome.Statuses;
            foreach (var b in batch)
            {
                if (b.IsTerminal) continue;
                _retryingWrites[b.Req.TagId] = b;
                b.EnqueuedMs = _clock.NowMilliseconds;
                _writes.Enqueue(b);
            }
            if (!_acceptWrites) FailPendingWrites(StatusCode.BadNotConnected);
            if (!_writes.IsEmpty) SignalWriteWork();
            bool anyBad = map.Values.Any(status => !status.IsGood);
            bool driverHasIncident = ReportDriverDiagnosticIfPresent(
                ConnectionState.Connected, retrying: false, nextRetryMs: 0);
            if (anyBad && !driverHasIncident)
                ReportDiagnostic(null, new DeviceConnectionDetail("PartialOnline", "部分点位写入失败"),
                    ConnectionState.Connected, retrying: false, 0);
            else if (!anyBad && batch.All(item => item.IsTerminal) &&
                     _incidents?.Current(_name) is { Category: ConnectionDiagnosticCategory.PartialDataFailure, RawError: "部分点位写入失败" })
                RecoverDiagnostic();
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var b in batch) CompletePendingWrite(b, StatusCode.BadNotConnected);
            throw;
        }
        catch (Exception ex)
        {
            var unresolved = batch.Where(item => !item.IsTerminal).ToArray();
            _writeErr += unresolved.Length;
            var failure = ClassifyWriteException(ex, _active.State);
            if (!failure.ConnectionLost)
            {
                ReportDiagnostic(ex,
                    new DeviceConnectionDetail("PartialOnline", "点位写入失败，会话仍在线"),
                    ConnectionState.Connected, retrying: false, 0);
                LogBatchDiagnostic(LogLevel.Warning,
                    "设备 {dev} 写入批次异常但没有发现传输断线：{endpoint}；错误={msg}；明细={details}",
                    _name, IoEndpointSummary(), ex.Message, DescribeWriteRequests(unresolved, failure.Status));
                foreach (var b in unresolved) CompletePendingWrite(b, failure.Status);
                return false;
            }
            ReportDiagnostic(ex, DriverDetail(), ConnectionState.Faulted, retrying: true, _circuitOpenMs);
            LogBatchDiagnostic(LogLevel.Warning,
                "设备 {dev} 写入批次异常并确认连接失效：{endpoint}；错误={msg}；明细={details}",
                _name, IoEndpointSummary(), ex.Message,
                DescribeWriteRequests(unresolved, StatusCode.BadCommFailure));
            foreach (var b in unresolved) CompletePendingWrite(b, StatusCode.BadCommFailure);
            await ResetTransportAsync(_active, ct);
            return true;   // 疑似断线
        }
    }

    private readonly record struct WriteVerificationBatchOutcome(
        Dictionary<string, StatusCode> Statuses,
        int ResponseMs,
        int VerificationMs,
        int RetryRounds);

    private readonly record struct WriteVerificationRead(
        string WriteTagId,
        TagBinding Binding,
        object ExpectedValue);

    private async Task<WriteVerificationBatchOutcome> WriteAndVerifyAsync(
        IReadOnlyList<PendingWrite> batch,
        CancellationToken ct)
    {
        var final = new Dictionary<string, StatusCode>(batch.Count, StringComparer.Ordinal);
        var remaining = batch.ToDictionary(item => item.Req.TagId, StringComparer.Ordinal);
        int queueDelayMs = (int)Math.Clamp(
            _clock.NowMilliseconds - batch.Min(item => item.EnqueuedMs), 0, int.MaxValue);
        long responseMs = 0;
        long verificationMs = 0;
        int retryRounds = 0;

        var newlyResolved = new List<PendingWrite>();
        void Resolve(PendingWrite item, StatusCode status)
        {
            final[item.Req.TagId] = status;
            remaining.Remove(item.Req.TagId);
            newlyResolved.Add(item);
        }
        void RemoveSuperseded()
        {
            foreach (var item in remaining.Values.ToArray())
                if (IsWriteSuperseded(item)) Resolve(item, StatusCode.BadWriteSuperseded);
        }
        void CompleteResolved()
        {
            CompleteResolvedWrites(newlyResolved, final, queueDelayMs, responseMs, verificationMs);
            newlyResolved.Clear();
        }

        foreach (var item in remaining.Values.ToArray())
        {
            if (item.Req.Def.Access == TagAccess.Read)
            {
                Resolve(item, StatusCode.BadNotWritable);
            }
            else if (item.Req.Def.Access == TagAccess.Write)
            {
                var confirmation = item.Req.Def.WriteConfirmation;
                if (confirmation is null || confirmation.Mode == WriteConfirmationMode.ReadBack)
                {
                    Resolve(item, StatusCode.BadNotReadable);
                }
                else if (confirmation.Mode == WriteConfirmationMode.ConfirmationTag &&
                         (!TryResolveConfirmationBinding(confirmation, out _) ||
                          string.IsNullOrWhiteSpace(confirmation.ExpectedValue)))
                {
                    Resolve(item, StatusCode.BadNotReadable);
                }
            }
        }

        CompleteResolved();
        // One protocol round per pump turn. Unresolved intents keep their attempt budget
        // and dispatch state when requeued, allowing unrelated new work to run first.
        do
        {
            ct.ThrowIfCancellationRequested();
            RemoveSuperseded();
            CompleteResolved();
            if (remaining.Count == 0) break;
            var intents = remaining.Values.ToArray();
            foreach (var item in intents)
            {
                item.Attempts++;
                item.RetryNotBeforeMs = 0;
            }
            retryRounds = intents.Any(item => item.Attempts > 1) ? 1 : 0;
            long responseStarted = _clock.NowMilliseconds;
            var requests = intents.Select(item => item.Req).ToArray();
            var capabilities = ActiveCapabilities;
            int writeLimit = capabilities.SupportsBatchWrite
                ? Math.Max(1, capabilities.MaxTagsPerWrite)
                : 1;
            var results = new List<WriteResult>(requests.Length);
            foreach (var writeBatch in TransferBatchPlanner.Partition(
                         requests,
                         writeLimit,
                         capabilities.MaxWriteBytes,
                         static request => TransferBatchPlanner.EstimateBytes(request.Def, request.Address)))
                results.AddRange(await _active.WriteAsync(writeBatch, ct));
            responseMs += Math.Max(0, _clock.NowMilliseconds - responseStarted);
            foreach (var diagnostic in results.Where(result => !string.IsNullOrWhiteSpace(result.Error)))
            {
                if (diagnostic.Status.IsGood)
                    LogBatchDiagnostic(LogLevel.Debug, "设备 {dev} 点位 {tag} 写入诊断：{detail}",
                        _name, diagnostic.TagId, diagnostic.Error);
                else
                    LogBatchDiagnostic(LogLevel.Warning, "设备 {dev} 点位 {tag} 写入诊断：{detail}",
                        _name, diagnostic.TagId, diagnostic.Error);
            }
            var writeResults = results
                .GroupBy(result => result.TagId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            // 写请求在协议调用期间可能已有更新代次。旧值不得继续回读、重写或覆盖新值。
            RemoveSuperseded();

            var readable = new List<WriteVerificationRead>(intents.Length);
            foreach (var item in intents)
            {
                if (!remaining.ContainsKey(item.Req.TagId)) continue;
                bool hasStatus = writeResults.TryGetValue(item.Req.TagId, out var writeResult);
                StatusCode status = hasStatus ? writeResult!.Status : StatusCode.Bad;
                if (hasStatus && status.IsGood)
                {
                    var confirmation = item.Req.Def.WriteConfirmation;
                    if (!item.Req.Policy.Verify)
                    {
                        // 调用方声明了不回读。PLC 里到底是什么值没人看过，如实标 GoodUnverified。
                        Resolve(item, StatusCode.GoodUnverified);
                        LogBatchDiagnostic(LogLevel.Debug,
                            "设备 {dev} 点位 {tag} 按调用方策略跳过写后回读；状态={status}",
                            _name, item.Req.TagId, StatusCode.GoodUnverified);
                        continue;
                    }
                    if (confirmation?.Mode == WriteConfirmationMode.ProtocolAcknowledged)
                    {
                        Resolve(item, StatusCode.GoodUnverified);
                        LogBatchDiagnostic(LogLevel.Information,
                            "设备 {dev} 点位 {tag} 写入仅获得协议确认，未回读 PLC 实际值；状态={status}",
                            _name, item.Req.TagId, StatusCode.GoodUnverified);
                        continue;
                    }

                    // 驱动可能根据 PLC 公布的字符串容量安全截断后再写入。
                    // 回读必须校验驱动实际提交的值，否则会把一次成功写入误判为失败并持续重写。
                    if (writeResult!.ActualValue is not null)
                        item.ExpectedValue = writeResult.ActualValue;

                    if (confirmation?.Mode == WriteConfirmationMode.ConfirmationTag &&
                        TryResolveConfirmationBinding(confirmation, out var confirmationBinding))
                    {
                        readable.Add(new WriteVerificationRead(
                            item.Req.TagId,
                            confirmationBinding,
                            confirmation.ExpectedValue!));
                    }
                    else
                    {
                        readable.Add(new WriteVerificationRead(
                            item.Req.TagId,
                            new TagBinding
                            {
                                TagId = item.Req.TagId,
                                Def = item.Req.Def,
                                Address = item.Req.Address,
                            },
                            item.ExpectedValue));
                    }
                }
                else if (hasStatus && IsPermanentWriteRejection(status))
                {
                    // 权限、只读和类型错误重试不会改变结果；立即隔离该点，
                    // 让同批其它可写点继续回读验证。
                    Resolve(item, status);
                }
                else if (item.Attempts > item.Req.Policy.MaxRewrites)
                {
                    Resolve(item, hasStatus && status.IsBad ? status : StatusCode.BadNotVerified);
                }
                else
                {
                    int delayMs = Math.Min(250, 5 << Math.Min(item.Attempts - 1, 6));
                    item.RetryNotBeforeMs = Environment.TickCount64 + delayMs;
                }
            }

            CompleteResolved();
            if (readable.Count == 0)
                break;
            long verificationStarted = _clock.NowMilliseconds;
            int delay = Math.Clamp(ActiveCapabilities.SuggestedVerificationDelayMs, 0, 5_000);
            if (delay > 0) await WaitForSupersedingWriteAsync(delay, remaining.Values, ct);
            RemoveSuperseded();
            CompleteResolved();
            if (remaining.Count == 0) break;
            readable.RemoveAll(read => !remaining.ContainsKey(read.WriteTagId));
            if (readable.Count == 0) continue;
            capabilities = ActiveCapabilities;
            int readLimit = capabilities.SupportsBatchRead
                ? Math.Max(1, capabilities.MaxTagsPerRead)
                : 1;
            var readsByBinding = readable.GroupBy(read => read.Binding.TagId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            TagBinding[] uniqueReadBindings = readsByBinding.Values
                .Select(reads => reads[0].Binding)
                .ToArray();
            foreach (var readBatch in TransferBatchPlanner.Partition(
                         uniqueReadBindings,
                         readLimit,
                         capabilities.MaxReadBytes,
                         static binding => TransferBatchPlanner.EstimateBytes(binding.Def, binding.Address)))
            {
                var samples = await ReadForWriteVerificationAsync(readBatch, ct);
                verificationMs += Math.Max(0, _clock.NowMilliseconds - verificationStarted);
                verificationStarted = _clock.NowMilliseconds;
                // Preserve each successful physical read even if a later partition fails.
                // Publish authoritative PLC values and diagnostics before completing callers.
                Publish(samples, writeVerification: true);
                QueueVerifiedNodeRecovery(samples);
                var sampleMap = samples.GroupBy(sample => sample.TagId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
                foreach (var read in readBatch.SelectMany(binding => readsByBinding[binding.TagId]))
                {
                    if (!remaining.TryGetValue(read.WriteTagId, out var item)) continue;
                    if (IsWriteSuperseded(item))
                    {
                        Resolve(item, StatusCode.BadWriteSuperseded);
                        continue;
                    }
                    if (sampleMap.TryGetValue(read.Binding.TagId, out var actual) &&
                        WriteVerificationComparer.IsMatch(
                            read.Binding.Def, read.ExpectedValue, actual, out _))
                    {
                        Resolve(item, StatusCode.Good);
                    }
                    else if (item.Attempts > item.Req.Policy.MaxRewrites)
                    {
                        Resolve(item, StatusCode.BadNotVerified);
                    }
                }
                CompleteResolved();
            }
        } while (false);

        CompleteResolved();
        return new WriteVerificationBatchOutcome(
            final,
            (int)Math.Clamp(responseMs, 0, int.MaxValue),
            (int)Math.Clamp(verificationMs, 0, int.MaxValue),
            retryRounds);
    }

    private Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(
        IReadOnlyList<TagBinding> bindings, CancellationToken ct) =>
        _active is IWriteVerificationReader verificationReader
            ? verificationReader.ReadForWriteVerificationAsync(bindings, ct)
            : _active.ReadAsync(bindings, ct);

    /// <summary>
    /// 分层采集的按需拉取：MES 触发条件满足后，立即读取该轮上报需要的点位并发布到 DataHub。
    /// 支持多在途请求的协议直接下发(与周期读取同一读取通道，小请求优先)；串行协议(Modbus/MC/FINS 等)的驱动
    /// 只允许设备循环一个调用者，因此排进设备循环，在当前报文结束后、下一个读取块之前执行。
    /// </summary>
    internal Task<OnDemandReadResult> ReadOnDemandAsync(
        IReadOnlyList<TagBinding> bindings, CancellationToken ct)
    {
        if (bindings.Count == 0) return Task.FromResult(new OnDemandReadResult(0, 0, null));
        if (!_acceptWrites || _active.State != ConnectionState.Connected || _circuit != CircuitState.Closed)
            return Task.FromResult(new OnDemandReadResult(0, 0, "设备未连接"));
        if (SupportsConcurrentWritePipeline) return ReadOnDemandCoreAsync(bindings, ct);

        var pending = new PendingOnDemandRead(bindings, ct);
        _onDemandReads.Enqueue(pending);
        SignalWriteWork();
        // 入队后再查一次：若设备循环恰好在此刻退出，兜底完成，避免调用方永久等待。
        if (!_acceptWrites) FailPendingOnDemandReads("设备采集循环已停止");
        return pending.Completion.Task.WaitAsync(ct);
    }

    private sealed class PendingOnDemandRead(IReadOnlyList<TagBinding> bindings, CancellationToken cancellation)
    {
        public IReadOnlyList<TagBinding> Bindings { get; } = bindings;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<OnDemandReadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>设备循环内执行排队的按需读取；执行期间不让驱动因“有优先任务”而中途让出。</summary>
    private async Task DrainOnDemandReadsAsync(CancellationToken ct)
    {
        while (_onDemandReads.TryDequeue(out var pending))
        {
            // 停机已经开始时不再发起新的协议请求。
            if (ct.IsCancellationRequested)
            {
                pending.Completion.TrySetResult(new OnDemandReadResult(0, 0, "设备采集循环已停止"));
                FailPendingOnDemandReads("设备采集循环已停止");
                return;
            }
            if (pending.Cancellation.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled(pending.Cancellation);
                continue;
            }
            Interlocked.Increment(ref _priorityProtocolOperationDepth);
            try { pending.Completion.TrySetResult(await ReadOnDemandCoreAsync(pending.Bindings, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pending.Completion.TrySetResult(new OnDemandReadResult(0, 0, "设备采集循环已停止"));
                throw;
            }
            catch (Exception ex) { pending.Completion.TrySetResult(new OnDemandReadResult(0, 0, ex.Message)); }
            finally { Interlocked.Decrement(ref _priorityProtocolOperationDepth); }
        }
    }

    /// <summary>按需读取是幂等的，连接不可用时直接失败，让 MES 沿用缓存继续，而不是等到重连成功。</summary>
    private void FailPendingOnDemandReads(string reason)
    {
        while (_onDemandReads.TryDequeue(out var pending))
            pending.Completion.TrySetResult(new OnDemandReadResult(0, 0, reason));
    }

    private async Task<OnDemandReadResult> ReadOnDemandCoreAsync(
        IReadOnlyList<TagBinding> bindings, CancellationToken ct)
    {
        var capabilities = ActiveCapabilities;
        int readLimit = Math.Clamp(capabilities.MaxTagsPerRead, 1, 100_000);
        int good = 0, total = 0;
        string? error = null;
        foreach (var batch in TransferBatchPlanner.Partition(
                     bindings, readLimit, capabilities.MaxReadBytes,
                     static binding => TransferBatchPlanner.EstimateBytes(binding.Def, binding.Address)))
        {
            long writeEpochAtReadStart = Volatile.Read(ref _writeEpoch);
            IReadOnlyList<TagSample> samples;
            // 走读取通道：共享会话按点数分入小请求/大块读取，写入与回读保留独立执行槽，
            // 上千点的按需拉取不会挡住同一 PLC 其他工位的握手回写。
            try { samples = await _active.ReadAsync(batch, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 保留前面分片已发布的新值；本轮剩余点位沿用缓存，由调用方决定是否继续上报。
                error ??= ex.Message;
                MarkBindingsBad(batch, DriverOperationFailure.FromException(ex, _active.State).Status);
                continue;
            }
            Publish(samples, writeEpochAtReadStart: writeEpochAtReadStart);
            QueueVerifiedNodeRecovery(samples);
            total += samples.Count;
            good += samples.Count(sample => sample.Value.Quality.IsGood);
        }
        return new OnDemandReadResult(good, total, error);
    }

    internal IReadOnlyList<TagBinding> BindingsFor(IEnumerable<string> tagIds)
    {
        var result = new List<TagBinding>();
        foreach (string tagId in tagIds)
            if (_bindingsByTagId.TryGetValue(tagId, out var binding)) result.Add(binding);
        return result;
    }

    private void CompleteResolvedWrites(
        IReadOnlyList<PendingWrite> batch,
        IReadOnlyDictionary<string, StatusCode> statuses,
        int queueDelayMs, long responseMs, long verificationMs)
    {
        var resolved = batch.Where(item => !item.IsTerminal && statuses.ContainsKey(item.Req.TagId)).ToArray();
        if (resolved.Length == 0) return;
        int good = resolved.Count(item => statuses[item.Req.TagId].IsGood);
        int bad = resolved.Length - good;
        _writeOk += good;
        _writeErr += bad;
        NotifyRuntimeState();
        long diagnosticNow = _clock.NowMilliseconds;
        if (bad > 0 || diagnosticNow >= Volatile.Read(ref _nextWriteSuccessDiagnosticMs))
        {
            if (bad == 0) Volatile.Write(ref _nextWriteSuccessDiagnosticMs, diagnosticNow + 5_000);
            string message =
                $"设备 {_name} {(bad > 0 ? "写入批次异常" : "写入批次完成")}：{IoEndpointSummary()}；" +
                $"点数={resolved.Length}；Good={good}；Bad={bad}；" +
                $"排队={queueDelayMs}ms；响应={responseMs}ms；回读验证={verificationMs}ms；" +
                $"重写轮次={resolved.Max(item => Math.Max(0, item.Attempts - 1))}；" +
                $"明细={DescribeWriteRequests(resolved, statuses)}";
            LogBatchDiagnostic(bad > 0 ? LogLevel.Warning : LogLevel.Information, "{diagnostic}", message);
        }
        foreach (var item in resolved)
        {
            CompletePendingWrite(item, statuses[item.Req.TagId]);
        }
    }

    private bool TryResolveConfirmationBinding(
        WriteConfirmationDef confirmation,
        out TagBinding binding)
    {
        binding = null!;
        if (string.IsNullOrWhiteSpace(confirmation.ConfirmationTagId) ||
            !_bindingsByTagId.TryGetValue(confirmation.ConfirmationTagId.Trim(), out var candidate) ||
            candidate.Def.Access == TagAccess.Write)
            return false;
        binding = candidate;
        return true;
    }

    private static bool IsPermanentWriteRejection(StatusCode status) =>
        status == StatusCode.BadNotWritable ||
        status == StatusCode.BadNotReadable ||
        status == StatusCode.BadDecodeError ||
        status.Code is
            0x801F0000 or // BadUserAccessDenied
            0x80340000 or // BadNodeIdUnknown
            0x80350000 or // BadAttributeIdInvalid
            0x80360000 or // BadIndexRangeInvalid
            0x80370000 or // BadIndexRangeNoData
            0x80380000 or // BadDataEncodingInvalid
            0x80390000 or // BadDataEncodingUnsupported
            0x803C0000 or // BadOutOfRange
            0x803D0000 or // BadNotSupported
            0x80740000;   // BadTypeMismatch

    private string IoEndpointSummary()
    {
        RemoteEndpointSnapshot? endpoint = (_active as IRemoteEndpointDiagnostics)?.RemoteEndpoint();
        string remote = FirstNonBlank(_configuredRemoteAddress, endpoint?.ConfiguredEndpoint, "(未配置)");
        string local = FirstNonBlank(_localAddress, endpoint?.SessionEndpoint, "(系统默认网卡直连)");
        string discovered = FirstNonBlank(endpoint?.DiscoveredEndpoint, "(未返回)");
        string session = FirstNonBlank(endpoint?.SessionEndpoint, "(未建立)");
        return $"远程地址={remote}；本地地址={local}；服务器发现={discovered}；SDK会话={session}；连接={_active.State}";
    }

    private static string DescribeProtocolTiming(ProtocolTimingSnapshot? timing)
    {
        if (timing is null)
            return "实际协议请求数=未提供；请求累计=未提供；请求最大=未提供；共享队列等待=未提供；失败请求=未提供；取消请求=未提供";
        static string Milliseconds(double value) =>
            value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "ms";
        return $"实际协议请求数={timing.PhysicalRequests?.ToString() ?? "未提供"}；请求样本={timing.Samples?.Count.ToString() ?? "未提供"}；省略请求样本={timing.OmittedSamples}；请求累计={Milliseconds(timing.RequestTotalMs)}；" +
               $"请求最大={Milliseconds(timing.RequestMaxMs)}；共享队列等待={Milliseconds(timing.QueueMs)}；" +
               $"失败请求={timing.FailedRequests}；取消请求={timing.CancelledRequests}";
    }

    private static string DescribeScanDiagnostics(ScanTimingRecord record)
    {
        static string Measurement(double? value, string unit) => value is double measured && double.IsFinite(measured) && measured >= 0
            ? measured.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + unit : "未提供";
        var attribution = record.Attribution;
        return $"轮询点位={record.TagCount}；逻辑批次={record.LogicalBatchCount?.ToString() ?? "未提供"}；设备会话指纹={record.DeviceSessionFingerprint ?? "未提供"}；会话代次={record.SessionGeneration?.ToString() ?? "未提供"}；{DescribeProtocolTiming(record.Protocol)}；网络探测状态={record.Network?.Status ?? "未提供"}；" +
            $"网络RTT={Measurement(record.Network?.LatestRttMs, "ms")}；窗口丢包率={Measurement(record.Network?.LossPercent, "%")}；" +
            $"归因={attribution?.Label ?? "证据不足"}；置信度={attribution?.Confidence ?? "未提供"}；" +
            $"归因证据={(attribution is null ? "证据不足" : string.Join(" | ", attribution.Evidence))}";
    }

    private void LogBatchDiagnostic(LogLevel level, string message, params object?[] arguments)
    {
        // Provider/formatter failures are diagnostic failures, never PLC read failures.
        try { _log.Log(level, message, arguments); }
        catch (Exception) { }
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    /// <summary>
    /// 把本轮读取的坏点按点位记入诊断：带上驱动能给出的具体原因（如地址形式与协议不匹配），
    /// 恢复成好值的点位标记恢复时间后保留，方便现场回看刚才是什么问题。
    /// </summary>
    private void RecordTagFaults(IReadOnlyList<TagBinding> requested, IReadOnlyList<TagSample> samples)
    {
        if (samples.Count == 0) return;
        var reasons = _active.TagFaultReasons;
        var now = DateTimeOffset.Now;
        foreach (var sample in samples)
        {
            if (!_bindingsByTagId.TryGetValue(sample.TagId, out var binding)) continue;
            if (sample.Value.Quality.IsGood)
            {
                _tagFaults.RecordGood(sample.TagId, now);
                continue;
            }
            reasons.TryGetValue(sample.TagId, out string? reason);
            _tagFaults.RecordBad(binding, sample.Value.Quality, reason, now,
                isolated: _invalidNodeReads.IsIsolated(sample.TagId));
        }
    }

    /// <summary>本设备当前的点位级故障（监控中心逐点展示：路径、原因、处置建议）。</summary>
    public TagFaultSnapshot TagFaults() => _tagFaults.Snapshot();

    private static string DescribeReadBindings(
        IReadOnlyList<TagBinding> bindings,
        IReadOnlyList<TagSample> samples,
        StatusCode missingStatus,
        bool onlyBad = false)
    {
        var statusByTag = samples.GroupBy(sample => sample.TagId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value.Quality, StringComparer.Ordinal);
        var details = bindings.Select(binding =>
        {
            StatusCode status = statusByTag.GetValueOrDefault(binding.TagId, missingStatus);
            return (binding, status);
        });
        if (onlyBad) details = details.Where(item => !item.status.IsGood);
        return string.Join(" | ", details.Take(16).Select(item =>
            DescribeTag(item.binding.TagId, item.binding.Def, item.binding.Address, item.status, null)));
    }

    private static string DescribeWriteRequests(
        IReadOnlyList<PendingWrite> writes,
        IReadOnlyDictionary<string, StatusCode> statuses) =>
        string.Join(" | ", writes.Take(16).Select(item =>
            DescribeTag(item.Req.TagId, item.Req.Def, item.Req.Address,
                statuses.GetValueOrDefault(item.Req.TagId, StatusCode.BadCommFailure), item.Req.Value)));

    private static string DescribeWriteRequests(IReadOnlyList<PendingWrite> writes, StatusCode status) =>
        string.Join(" | ", writes.Take(16).Select(item =>
            DescribeTag(item.Req.TagId, item.Req.Def, item.Req.Address, status, item.Req.Value)));

    private static string DescribeTag(string tagId, TagDef definition, TagAddress address, StatusCode status, object? writeValue)
    {
        string addressLabel = definition.Address.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)
            ? "NodeId"
            : "地址";
        string fieldLength = definition.DataType is IgwDataType.String or IgwDataType.WString
            ? (definition.StringLength?.ToString() ?? "未知")
            : definition.IsArray && definition.Array is not null
                ? $"{definition.Array.Dimensions.Aggregate(1L, (total, dimension) => total * Math.Max(1, dimension))}元素"
                : $"{TransferBatchPlanner.EstimateBytes(definition, address)}字节";
        string writeLength = writeValue switch
        {
            null => "",
            string text => $"；写入长度=UTF16:{text.Length}/UTF8:{Encoding.UTF8.GetByteCount(text)}",
            Array array => $"；写入长度={array.Length}元素",
            System.Collections.ICollection collection => $"；写入长度={collection.Count}元素",
            _ => $"；写入长度={TransferBatchPlanner.EstimateBytes(definition, address)}字节",
        };
        return $"IGW路径={tagId}；{addressLabel}={definition.Address}；类型={definition.DataType}；权限={definition.Access}；字段长度={fieldLength}{writeLength}；状态={status.SymbolicName}({status.Hex})";
    }

    private bool IsWriteSuperseded(PendingWrite item) =>
        _writeGenerations.GetValueOrDefault(item.Req.TagId, item.Generation) > item.DispatchGenerationCeiling;

    private async Task WaitForSupersedingWriteAsync(
        int delayMs,
        IEnumerable<PendingWrite> pending,
        CancellationToken ct)
    {
        PendingWrite[] snapshot = pending.ToArray();
        long deadline = Environment.TickCount64 + Math.Max(0, delayMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshot.Any(item =>
                _writeGenerations.GetValueOrDefault(item.Req.TagId, item.Generation) >
                item.DispatchGenerationCeiling))
                return;

            int remainingMs = (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
            if (remainingMs == 0) return;
            var arrival = CaptureNextWriteArrival();
            Task delay = Task.Delay(remainingMs, ct);
            Task completed = await Task.WhenAny(delay, arrival.Task);
            if (ReferenceEquals(completed, delay))
            {
                await delay;
                return;
            }
        }
    }

    private async Task<bool> DrainArrayWritesAsync(CancellationToken ct)
    {
        if (_arrayWrites.IsEmpty)
        {
            _arrayRetryDeferrals = 0;
            return false;
        }
        var batch = new List<PendingArrayWrite>(1);
        var retries = new List<PendingArrayWrite>();
        var deferred = new List<PendingArrayWrite>();
        int scanBudget = Math.Min(_arrayWrites.Count, 1_024);
        for (int scanned = 0; scanned < scanBudget &&
             _arrayWrites.TryDequeue(out var write); scanned++)
        {
            if (write.IsTerminal)
            {
                write.Dispose();
                continue;
            }
            long latestGeneration = _writeGenerations.GetValueOrDefault(
                write.Req.Binding.TagId, write.Generation);
            if (write.Generation < latestGeneration)
            {
                write.Complete(ArrayWriteResult.Failed(
                    StatusCode.BadWriteSuperseded, "同一数组已有更新的待写值"));
                continue;
            }
            if (write.Attempts > 0)
            {
                retries.Add(write);
                continue;
            }
            if (batch.Count == 0) batch.Add(write);
            else deferred.Add(write);
        }
        bool forceRetry = _arrayRetryDeferrals >= FreshWriteBurstLimit;
        if (forceRetry && retries.Count > 0)
        {
            deferred.InsertRange(0, batch);
            batch.Clear();
        }
        bool freshScalarWaiting = _writes.Any(item => !item.IsTerminal && item.Attempts == 0);
        bool retrySelected = false;
        foreach (var retry in retries)
        {
            // Fresh arrays retain one turn per pass alongside the scalar batch; only
            // failed array retries yield, for at most eight turns, to fresh values.
            if (batch.Count == 0 && (forceRetry || !freshScalarWaiting))
            {
                batch.Add(retry);
                retrySelected = true;
            }
            else deferred.Add(retry);
        }
        if (retrySelected || retries.Count == 0) _arrayRetryDeferrals = 0;
        else _arrayRetryDeferrals = Math.Min(FreshWriteBurstLimit, _arrayRetryDeferrals + 1);
        batch.RemoveAll(item =>
        {
            long latest = _writeGenerations.GetValueOrDefault(item.Req.Binding.TagId, item.Generation);
            if (item.Generation < latest)
            {
                item.Complete(ArrayWriteResult.Failed(StatusCode.BadWriteSuperseded, "同一数组已有更新的待写值"));
                return true;
            }
            if (item.TryBeginDispatch(latest))
                return false;
            item.Dispose();
            return true;
        });
        foreach (var item in deferred) _arrayWrites.Enqueue(item);
        if (!_arrayWrites.IsEmpty) SignalWriteWork();
        if (batch.Count == 0) return false;

        if (_active is not IArrayCapableDriver arrayDriver)
        {
            foreach (var pending in batch)
                pending.Complete(ArrayWriteResult.Failed(
                    StatusCode.BadDeviceFailure, "当前驱动不支持数组写入"));
            _writeErr += batch.Count;
            NotifyRuntimeState();
            return false;
        }

        try
        {
            Interlocked.Increment(ref _priorityProtocolOperationDepth);
            foreach (var pending in batch)
            {
                var result = await WriteAndVerifyArrayAsync(arrayDriver, pending, ct);
                if (result is null)
                {
                    _arrayWrites.Enqueue(pending);
                    // DrainWritesAsync will give the scalar queue its turn immediately
                    // after this array round, so include that opportunity in the burst.
                    if (_writes.Any(item => !item.IsTerminal && item.Attempts == 0))
                        _arrayRetryDeferrals = Math.Min(FreshWriteBurstLimit, _arrayRetryDeferrals + 1);
                    if (!_acceptWrites) FailPendingWrites(StatusCode.BadNotConnected);
                    SignalWriteWork();
                    continue;
                }
                if (result.Status.IsGood && result.Failures.Count == 0) _writeOk++;
                else _writeErr++;
                pending.Complete(result);
            }
            NotifyRuntimeState();
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var pending in batch)
                pending.Complete(ArrayWriteResult.Failed(StatusCode.BadNotConnected, "写入已取消"));
            throw;
        }
        catch (Exception ex)
        {
            _writeErr += batch.Count;
            var failure = ClassifyWriteException(ex, _active.State);
            if (!failure.ConnectionLost)
            {
                ReportDiagnostic(ex,
                    new DeviceConnectionDetail("PartialOnline", "数组写入失败，会话仍在线"),
                    ConnectionState.Connected, retrying: false, 0);
                LogBatchDiagnostic(LogLevel.Warning, "设备 {dev} 数组写入失败，但没有发现传输断线；保留连接并等待后续读写验证:{msg}",
                    _name, ex.Message);
                foreach (var pending in batch)
                    pending.Complete(ArrayWriteResult.Failed(failure.Status, ex.Message));
                return false;
            }
            ReportDiagnostic(ex, DriverDetail(), ConnectionState.Faulted, retrying: true, _circuitOpenMs);
            LogBatchDiagnostic(LogLevel.Warning, "设备 {dev} 数组写入失败:{msg}", _name, ex.Message);
            foreach (var pending in batch)
                pending.Complete(ArrayWriteResult.Failed(StatusCode.BadCommFailure, ex.Message));
            await ResetTransportAsync(_active, ct);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _priorityProtocolOperationDepth);
        }
    }

    private async Task<ArrayWriteResult?> WriteAndVerifyArrayAsync(
        IArrayCapableDriver arrayDriver,
        PendingArrayWrite pendingWrite,
        CancellationToken ct)
    {
        const int maxRewriteAttempts = 10;
        ArrayWriteRequest request = pendingWrite.Req;
        IReadOnlyList<ArrayWriteRequest> pending = pendingWrite.RetryRequests;
        ArrayWriteResult last = ArrayWriteResult.Failed(StatusCode.BadNotVerified, "尚未执行写入");
        // Preserve failed slices across pump turns instead of running all eleven rounds
        // ahead of unrelated scalar or array writes.
        pendingWrite.Attempts++;
        {
            if (IsArrayWriteSuperseded(pendingWrite))
                return ArrayWriteResult.Failed(StatusCode.BadWriteSuperseded, "同一数组已有更新的写入值");
            int succeeded = 0;
            var failures = new List<ArrayElementFailure>();
            foreach (var retry in pending)
            {
                if (IsArrayWriteSuperseded(pendingWrite))
                    return ArrayWriteResult.Failed(StatusCode.BadWriteSuperseded, "同一数组已有更新的写入值");
                var write = await arrayDriver.WriteArrayAsync(retry, ct);
                succeeded += write.Succeeded;
                failures.AddRange(write.Failures);
            }
            last = new ArrayWriteResult(failures.Count == 0 ? StatusCode.Good :
                succeeded > 0 ? StatusCode.Uncertain : StatusCode.BadDeviceFailure, succeeded, failures);

            if (IsArrayWriteSuperseded(pendingWrite))
                return ArrayWriteResult.Failed(StatusCode.BadWriteSuperseded, "同一数组已有更新的写入值");
            int delay = Math.Clamp(ActiveCapabilities.SuggestedVerificationDelayMs, 0, 5_000);
            if (delay > 0 && await WaitForArraySupersedingWriteAsync(delay, pendingWrite, ct))
                return ArrayWriteResult.Failed(StatusCode.BadWriteSuperseded, "同一数组已有更新的写入值");
            var samples = await ReadForWriteVerificationAsync([request.Binding], ct);
            // 与普通点一致：成功写入返回前，把回读到的完整数组快照发布给 QC/REST。
            Publish(samples, writeVerification: true);
            QueueVerifiedNodeRecovery(samples);
            var sample = samples.LastOrDefault(item =>
                string.Equals(item.TagId, request.Binding.TagId, StringComparison.Ordinal));
            if (sample.Value.Value is ArrayRuntimeValue actual)
            {
                var actualSlice = actual.Slice(request.Slice);
                pending = ArrayWriteVerification.BuildRetryRequests(request, actualSlice);
                if (pending.Count == 0)
                    return new ArrayWriteResult(StatusCode.Good, request.Value.Count, []);
            }
            else pending = [request];

            pendingWrite.RetryRequests = pending;
            if (pendingWrite.Attempts <= maxRewriteAttempts) return null;
            return ArrayWriteResult.Failed(StatusCode.BadNotVerified,
                $"数组写入后仍有 {pending.Sum(item => item.Value.Count)} 个元素不一致，已重写 {maxRewriteAttempts} 次；最后写状态={last.Status}");
        }
    }

    private bool IsArrayWriteSuperseded(PendingArrayWrite pending) =>
        _writeGenerations.GetValueOrDefault(pending.Req.Binding.TagId, pending.Generation) >
        pending.DispatchGenerationCeiling;

    private async Task<bool> WaitForArraySupersedingWriteAsync(
        int delayMs,
        PendingArrayWrite pending,
        CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, delayMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsArrayWriteSuperseded(pending)) return true;
            int remainingMs = (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
            if (remainingMs == 0) return false;
            var arrival = CaptureNextWriteArrival();
            Task delay = Task.Delay(remainingMs, ct);
            Task completed = await Task.WhenAny(delay, arrival.Task);
            if (ReferenceEquals(completed, delay))
            {
                await delay;
                return IsArrayWriteSuperseded(pending);
            }
        }
    }

    private static (bool ConnectionLost, StatusCode Status) ClassifyWriteException(
        Exception error,
        ConnectionState driverState)
    {
        var failure = DriverOperationFailure.FromException(error, driverState);
        return (failure.ConnectionLost, failure.Status);
    }

    private static async Task ResetTransportAsync(IDeviceDriver driver, CancellationToken ct)
    {
        try
        {
            if (driver is IConnectionResettableDriver resettable)
                await resettable.ResetConnectionAsync(ct);
            else
                await driver.DisconnectAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            try { await driver.DisconnectAsync(); } catch { }
        }
    }

    private DeviceConnectionDetail? DriverDetail() =>
        (_active as IDeviceConnectionDiagnostics)?.ConnectionDetail();

    private void ReportDiagnostic(
        Exception? error,
        DeviceConnectionDetail? detail,
        ConnectionState state,
        bool retrying,
        int nextRetryMs)
    {
        var descriptor = ConnectionDiagnosticClassifier.Classify(error, detail);
        _lastError = descriptor.RawError;
        _incidents?.Report(_name, _channelName, _protocol, descriptor, state, retrying, nextRetryMs);
        NotifyRuntimeState();
    }

    private bool ApplyDriverDiagnostic(Exception? error, ConnectionState state, bool retrying, int nextRetryMs)
    {
        var detail = DriverDetail();
        bool normal = detail is null || detail.Code is "" or "None" or "Connected";
        if (error is null && normal)
        {
            RecoverDiagnostic();
            return false;
        }
        ReportDiagnostic(error, detail, state, retrying, nextRetryMs);
        return true;
    }

    private bool ReportDriverDiagnosticIfPresent(ConnectionState state, bool retrying, int nextRetryMs)
    {
        var detail = DriverDetail();
        if (detail is null || detail.Code is "" or "None" or "Connected") return false;
        ReportDiagnostic(null, detail, state, retrying, nextRetryMs);
        return true;
    }

    private void RecoverDiagnostic()
    {
        _incidents?.Recover(_name);
        _lastError = "";
        NotifyRuntimeState();
    }

    private void NotifyRuntimeState()
    {
        if (_runtimeStateChanged is null) return;
        long now = _clock.NowMilliseconds;
        string detail = DriverDetail()?.Code ?? "";
        string signature = $"{_active.State}|{detail}|{_lastError}";
        if (string.Equals(signature, _lastRuntimeSignature, StringComparison.Ordinal)
            && now - Volatile.Read(ref _lastRuntimeNotificationMs) < 1_000) return;
        _lastRuntimeSignature = signature;
        Volatile.Write(ref _lastRuntimeNotificationMs, now);
        try { _runtimeStateChanged(); }
        catch { /* 展示订阅者不得影响 PLC 采集循环。 */ }
    }

    private void FailPendingWrites(StatusCode code)
    {
        FailPendingOnDemandReads(code == StatusCode.BadNotConnected ? "设备未连接" : "设备通信失败");
        while (_writes.TryDequeue(out var w)) CompletePendingWrite(w, code);
        while (_arrayWrites.TryDequeue(out var w))
            w.Complete(ArrayWriteResult.Failed(code, "设备连接不可用"));
    }

    private List<TaskCompletionSource<bool>> DrainManualReconnectRequests()
    {
        var requests = new List<TaskCompletionSource<bool>>();
        while (_manualReconnects.TryDequeue(out var request)) requests.Add(request);
        return requests;
    }

    private static void CompleteManualReconnectRequests(
        IReadOnlyList<TaskCompletionSource<bool>> requests,
        bool connected)
    {
        for (int i = 0; i < requests.Count; i++) requests[i].TrySetResult(connected);
    }

    // 睡到下个到期组,或被新写请求提前唤醒(返回 false=已取消)
    private async Task<bool> WaitWorkOrDelay(int ms, CancellationToken ct)
    {
        if (ms < 20 && _highResolutionWaiter?.IsAvailable == true)
            // 内核计时器唤醒存在约 1ms 调度尾差，提前 1ms 设定后由设备循环重新核对截止时间；
            // 不忙等，也不会提前执行读取块。
            return _highResolutionWaiter.Wait(Math.Max(1, ms - 1), ct);
        try { await _writeSignal.WaitAsync(Math.Clamp(ms, 1, 30_000), ct); }
        catch (OperationCanceledException) { return false; }
        finally { Interlocked.Exchange(ref _demandWakePending, 0); }
        return !ct.IsCancellationRequested;
    }
}

/// <summary>运行时引擎:装配工程 → 建驱动 → 绑定点位 → 起每设备采集循环。</summary>
public sealed class RuntimeEngine : IAsyncDisposable
{
    private IgwProject _project;   // 可变:增量热重载 ApplyAsync 后指向新工程
    private readonly DriverRegistry _registry;
    private readonly DataHub _hub;
    private readonly ScanDemandRegistry _scanDemands;
    private readonly INetworkAdapterCatalog _networkAdapters;
    private readonly ILoggerFactory _lf;
    private readonly ILogger _log;
    private readonly List<DeviceRuntime> _devices = new();
    private readonly object _devicesGate = new();
    private readonly ConcurrentDictionary<string, (DeviceLoop loop, TagBinding binding)> _writeMap = new();
    private readonly ConnectionIncidentRegistry _connectionIncidents = new();
    private readonly SemaphoreSlim _deviceLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _autoReconnectSignal = new(0);
    private readonly ConcurrentDictionary<string, byte> _autoReconnectChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ChannelReconnectBackoff> _autoReconnectBackoff = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task _autoReconnectWorker = Task.CompletedTask;
    private OpcUaConnectionPool? _opcUaConnections;
    private readonly NetworkPerformanceProbeCoordinator? _injectedNetworkProbes;
    private NetworkPerformanceProbeCoordinator? _networkProbes;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveNetworkHost;
    private readonly ConcurrentDictionary<string, Lazy<Task<IPAddress?>>> _networkHosts = new(StringComparer.OrdinalIgnoreCase);

    public DataHub Hub => _hub;
    public IgwProject Project => _project;
    public ScanDemandRegistry ScanDemands => _scanDemands;
    public event Action<DeviceStatus>? DeviceStatusChanged;

    public RuntimeEngine(IgwProject project, DriverRegistry registry, DataHub hub, ILoggerFactory lf,
        ScanDemandRegistry? scanDemands = null, INetworkAdapterCatalog? networkAdapters = null,
        NetworkPerformanceProbeCoordinator? networkProbes = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveNetworkHost = null)
    {
        _project = project; _registry = registry; _hub = hub; _lf = lf;
        _scanDemands = scanDemands ?? new ScanDemandRegistry(hub);
        _networkAdapters = networkAdapters ?? new NetworkAdapterResolver();
        _log = lf.CreateLogger("RuntimeEngine");
        _injectedNetworkProbes = networkProbes;
        _resolveNetworkHost = resolveNetworkHost ?? ((host, ct) => Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct));
    }

    /// <summary>Refresh documentation without replacing device loops, cache values or slot handles.</summary>
    public void UpdateProjectMetadata(IgwProject project)
    {
        foreach (var channel in project.Channels)
        foreach (var configured in channel.Devices)
            RefreshDeviceDescriptions(project, channel, configured);
        _project = project;
    }

    // 把单台设备(模板实例先展开)的点位备注写回 Hub 中已登记的定义
    private void RefreshDeviceDescriptions(IgwProject project, ChannelDef channel, DeviceDef configured)
    {
        void Tags(IEnumerable<TagDef> tags, string parent)
        {
            foreach (var tag in tags)
                if (_hub.TryGetDef(parent + "." + tag.Name, out var current)) current.Description = tag.Description;
        }
        void Groups(IEnumerable<TagGroupDef> groups, string parent)
        {
            foreach (var group in groups)
            {
                string path = parent + "." + group.Name;
                Tags(group.Tags, path);
                Groups(group.SubGroups, path);
            }
        }
        var device = TemplateExpander.ExpandInProject(project, configured);
        string path = channel.Name + "." + device.Name;
        Tags(device.Tags, path);
        Groups(device.Groups, path);
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _networkHosts.Clear();
        _networkProbes = _injectedNetworkProbes ?? new NetworkPerformanceProbeCoordinator(
            logger: _lf.CreateLogger<NetworkPerformanceProbeCoordinator>(), cancellationToken: _cts.Token, maxConcurrentProbes: 8);
        _opcUaConnections = new OpcUaConnectionPool();
        _autoReconnectWorker = Task.Run(() => AutomaticChannelReconnectLoopAsync(_cts.Token));
        var (templates, hashes) = TemplateMaps(_project);
        var sigs = DeviceSigs(_project, hashes);
        foreach (var ch in _project.Channels)
        {
            if (!ResolveFactory(ch, out var factory)) continue;
            foreach (var dev in ch.Devices) StartDevice(ch, dev, factory, templates, hashes, knownSig: sigs[dev]);
        }
        return Task.CompletedTask;
    }

    // 模板名→模板 与 模板名→内容哈希:每次 Start/Apply 一次性预算,供运行时展开与设备签名复用(避免每实例重复哈希模板 500 点)
    internal static (Dictionary<string, DeviceTemplate> byName, Dictionary<string, string> hashes) TemplateMaps(IgwProject p)
    {
        var byName = new Dictionary<string, DeviceTemplate>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in p.Templates ?? new())
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            byName[t.Name] = t;
            hashes[t.Name] = JsonHash.OfRuntime(t);   // 纯备注不改变设备通信配置。
        }
        return (byName, hashes);
    }

    // 模板实例→运行时展开为有效定义(点位来自模板+差异);手工设备原样返回。模板缺失(校验应已挡)→退化为原样并告警。
    private DeviceDef EffectiveDevice(ChannelDef ch, DeviceDef dev, IReadOnlyDictionary<string, DeviceTemplate> templates)
    {
        if (string.IsNullOrWhiteSpace(dev.TemplateRef)) return dev;
        if (templates.TryGetValue(dev.TemplateRef, out var tmpl)) return TemplateExpander.Expand(tmpl, dev);
        _log.LogWarning("设备 {ch}.{dev} 引用的模板不存在:{t},按无点设备处理", ch.Name, dev.Name, dev.TemplateRef);
        return dev;
    }

    // 通道 → 真实驱动工厂。旧工程残留的 simulate 设置不再改变运行驱动。
    private bool ResolveFactory(ChannelDef ch, out IDriverFactory factory)
    {
        if (!_registry.TryGet(ch.DriverId, out factory!))
        {
            // 旧工程可能残留已下线驱动的通道：只跳过该通道，其它设备照常运行。
            _log.LogWarning("通道 {ch} 的驱动 {drv} 未注册,跳过", ch.Name, ch.DriverId);
            return false;
        }
        if (!_registry.Allows(ch.DriverId))
        {
            // 授权码不包含这个驱动：通道配置保留，只是不采集
            _log.LogWarning("通道 {ch} 的驱动 {drv} 未授权,跳过", ch.Name, ch.DriverId);
            return false;
        }
        return true;
    }

    // 建一个设备:登记点位、建循环、启动(禁用采集则只登记不启动)。记录每设备 CTS/签名/点位,便于增量热重载按设备增删。
    // 模板实例在此展开为有效定义(点位来自模板+差异);下游(绑定/注册/扫描/写映射)对模板无感。签名用原始 dev + 模板哈希(见 DeviceSig)。
    private IDeviceDriver CreateRuntimeDriver(ChannelDef channel, DeviceDef device, IDriverFactory factory,
        out NetworkBindingLease? ownedBinding)
    {
        ownedBinding = null;
        if (string.Equals(channel.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(device.Settings.GetValueOrDefault("dataCollection", "启用"), "禁用", StringComparison.Ordinal))
        {
            return _opcUaConnections!.Acquire(OpcUaConnectionPool.ConnectionKey(channel, device, $"{channel.Name}.{device.Name}"), () =>
            {
                var prepared = NetworkBindingPlan.Prepare(channel, device, _networkAdapters);
                try { return new OpcUaConnectionResource(factory.CreateDevice(prepared.Channel, prepared.Device), prepared.Lease); }
                catch { prepared.Dispose(); throw; }
            },
            int.TryParse(channel.Settings.GetValueOrDefault("maxConcurrentRequests"), out int configured)
                ? configured
                : OpcUaConnectionPool.Entry.DefaultMaxConcurrentRequests);
        }
        var direct = NetworkBindingPlan.Prepare(channel, device, _networkAdapters);
        try
        {
            var driver = factory.CreateDevice(direct.Channel, direct.Device);
            ownedBinding = direct.Lease;
            return driver;
        }
        catch { direct.Dispose(); throw; }
    }

    private void StartDevice(ChannelDef ch, DeviceDef dev, IDriverFactory factory,
        IReadOnlyDictionary<string, DeviceTemplate> templates, IReadOnlyDictionary<string, string> templateHashes,
        bool reuseHubEntries = false, string? knownSig = null)
    {
        var eff = EffectiveDevice(ch, dev, templates);   // 模板实例→展开;手工设备原样
        string configuredRemoteAddress = string.Equals(ch.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase)
            ? ch.Settings.GetValueOrDefault("url", "").Trim()
            : eff.Endpoint.Trim();
        if (configuredRemoteAddress.Length == 0) configuredRemoteAddress = eff.Endpoint.Trim();
        if (configuredRemoteAddress.Length == 0) configuredRemoteAddress = "(未配置)";
        NetworkBindingLease? primaryBinding = null;
        IDeviceDriver driver;
        try
        {
            driver = CreateRuntimeDriver(ch, eff, factory, out primaryBinding);
            if (string.Equals(ch.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase))
            {
                string upstream = ch.Settings.GetValueOrDefault("url", "").Trim();
                if (upstream.Length == 0) upstream = eff.Endpoint.Trim();
                _log.LogInformation("OPC UA Client 通道 {channel} 创建设备 {device}：远端上游 {endpoint}{binding}",
                    ch.Name, eff.Name, upstream,
                    (driver as SharedOpcUaDriverLease)?.Binding is { } sharedBinding
                        ? $"，共享会话，经网卡绑定转发 {sharedBinding.LoopbackEndpoint}"
                        : driver is SharedOpcUaDriverLease ? "，共享同配置 OPC UA 会话"
                        : primaryBinding is null ? "" : $"，经网卡绑定转发 {primaryBinding.LoopbackEndpoint}");
            }
        }
        catch (Exception ex)
        {
            primaryBinding?.Dispose();
            _log.LogError(ex, "创建设备 {dev} 失败", eff.Name);
            return;
        }

        string devName = $"{ch.Name}.{eff.Name}";
        var tagIds = new List<string>();
        var bindings = BindDevice(eff, driver.AddressParser, devName, reuseHubEntries, tagIds);
        _scanDemands.RefreshDevice(devName);   // 热重载后把既有 MES 租约重新绑定到新槽位,避免句柄复用 ABA
        // 调用方已按同一 (ch, dev, 模板哈希) 算过签名时直接复用(启动/整体应用并行预算;按设备应用逐台已算):
        // 十万点设备的签名是一次 O(点数) 的 JSON 流式哈希(约数百毫秒)
        string sig = knownSig ?? DeviceSig(ch, dev, templateHashes);

        bool zeroFloat = !string.Equals(ch.Settings.GetValueOrDefault("nonNormalFloat", "替换为零"), "不修改", StringComparison.Ordinal);
        string writeOpt = WriteOptKey(ch.Settings.GetValueOrDefault("writeOptimization", "仅写入非布尔型标记的最新值"));
        int dutyCycle = SettingInt(ch.Settings, "dutyCycle", 10);
        string scanMode = ScanModeKey(
            eff.Settings.GetValueOrDefault("scanMode", "遵循客户端指定的扫描速率"),
            value => _log.LogWarning(
                "设备 {device} 的扫描模式 \"{value}\" 无法识别，已按「遵循客户端指定的扫描速率」处理；" +
                "合法取值为「请求所有数据以指定的扫描速率」或「遵循客户端指定的扫描速率」",
                devName, value));
        int activeWindowMs = SettingInt(eff.Settings, "activeWindowSec", 30) * 1000;
        bool demote = string.Equals(eff.Settings.GetValueOrDefault("demote", "禁用"), "启用", StringComparison.Ordinal);
        int demoteAfter = SettingInt(eff.Settings, "demoteAfter", 3);
        int demotePeriod = SettingInt(eff.Settings, "demotePeriodMs", 10000);
        IDeviceDriver? secondary = null;
        NetworkBindingLease? secondaryBinding = null;
        DeviceDef? secondaryDef = null;
        string secEp = eff.Settings.GetValueOrDefault("secondaryEndpoint", "");
        if (!string.IsNullOrWhiteSpace(secEp))
        {
            try
            {
                secondaryDef = new DeviceDef
                {
                    Id = eff.Id,
                    Name = eff.Name,
                    Endpoint = secEp.Trim(),
                    Description = eff.Description,
                    Settings = eff.Settings,
                    TemplateRef = eff.TemplateRef,
                    Delta = eff.Delta,
                    Groups = eff.Groups,
                    Tags = eff.Tags,
                };
                secondary = CreateRuntimeDriver(ch, secondaryDef, factory, out secondaryBinding);
            }
            catch (Exception ex)
            {
                secondaryBinding?.Dispose();
                secondaryBinding = null;
                _log.LogWarning("设备 {dev} 备用路径创建失败:{m}", devName, ex.Message);
            }
        }
        int monitorSec = SettingInt(eff.Settings, "monitorIntervalSec", 300);
        bool returnPrimary = !string.Equals(eff.Settings.GetValueOrDefault("returnToPrimary", "是"), "否", StringComparison.Ordinal);
        RuntimeNetworkProbe? primaryNetworkProbe = null, secondaryNetworkProbe = null;
        var loop = new DeviceLoop(driver, bindings, _hub, _lf.CreateLogger("Device:" + devName), devName,
            zeroFloat, demote, demoteAfter, demotePeriod, secondary, monitorSec, returnPrimary,
            writeOpt, dutyCycle, scanMode, activeWindowMs, scanDemands: _scanDemands,
            incidents: _connectionIncidents, channelName: ch.Name, protocol: driver.DriverId,
            requestChannelHardReconnect: RequestAutomaticChannelReconnect,
            configuredRemoteAddress: configuredRemoteAddress,
            localAddress: ((driver as SharedOpcUaDriverLease)?.Binding ?? primaryBinding)?.LoopbackEndpoint.ToString() ?? "(系统默认网卡直连)",
            runtimeStateChanged: () => RaiseDeviceStatusChanged(devName), captureNetworkWindow: scanDriver =>
                ReferenceEquals(scanDriver, driver) ? primaryNetworkProbe?.Capture()
                : ReferenceEquals(scanDriver, secondary) ? secondaryNetworkProbe?.Capture() : null,
            deviceIdentity: eff.Id, secondaryDiagnosticEndpoint: secondaryDef?.Endpoint);
        var hostToken = _cts!.Token;
        var devCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        // Construct the loop first: a driver/logger constructor failure must not leave an unowned probe lease.
        if (!string.Equals(eff.Settings.GetValueOrDefault("dataCollection", "启用"), "禁用", StringComparison.Ordinal))
        {
            primaryNetworkProbe = CreateNetworkProbe(ch, eff, driver, primaryBinding, hostToken, devCts.Token);
            if (secondary is not null && secondaryDef is not null)
                secondaryNetworkProbe = CreateNetworkProbe(ch, secondaryDef, secondary, secondaryBinding, hostToken, devCts.Token);
        }
        lock (_devicesGate)
            _devices.Add(new DeviceRuntime(devName, driver, loop, bindings.Count, devCts, sig, tagIds,
                primaryBinding, secondaryBinding, primaryNetworkProbe, secondaryNetworkProbe));

        // 设备「数据收集=禁用」:点位登记但不轮询,标记坏质量(对齐 KEP);不入写映射→写直接 Bad
        if (string.Equals(eff.Settings.GetValueOrDefault("dataCollection", "启用"), "禁用", StringComparison.Ordinal))
        {
            var ts = DateTime.UtcNow;
            foreach (var b in bindings) _hub.Update(b.TagId, DataValue.BadAt(StatusCode.BadNotConnected, ts));
            if (secondary != null) { try { _ = secondary.DisposeAsync(); } catch { } }   // 禁用采集:loop 不启动→备用驱动不会被释放,显式释放
            _log.LogInformation("设备 {dev} 数据收集已禁用,跳过轮询({n} 点)", devName, bindings.Count);
            return;
        }

        foreach (var b in bindings) _writeMap[b.TagId] = (loop, b);   // 写经设备循环排队下发
        loop.Start(devCts.Token);
        _log.LogInformation("启动设备 {dev}:{n} 点", devName, bindings.Count);
    }

    // 增量应用新工程(热重载):只停/起"新增或配置变化"的设备;配置未变的设备保持连接、缓存不断。
    // beginApply 在生命周期锁真正取得后执行，调用方可把对外重载窗口限制在实际切换阶段，
    // 避免排队等待另一个通道硬重连时提前中断实时流。
    public async Task ApplyAsync(IgwProject newProject, Func<Task>? beginApply = null)
    {
        await _deviceLifecycleGate.WaitAsync();
        try
        {
            if (beginApply is not null) await beginApply();
            await ApplyCoreAsync(newProject);
        }
        finally { _deviceLifecycleGate.Release(); }
    }

    private async Task ApplyCoreAsync(IgwProject newProject)
    {
        var (templates, hashes) = TemplateMaps(newProject);
        var sigs = DeviceSigs(newProject, hashes);
        var desired = new List<(string name, ChannelDef ch, DeviceDef dev, IDriverFactory factory, string sig)>();
        foreach (var ch in newProject.Channels)
        {
            if (!ResolveFactory(ch, out var factory)) continue;
            foreach (var dev in ch.Devices)
                desired.Add(($"{ch.Name}.{dev.Name}", ch, dev, factory, sigs[dev]));
        }
        var desiredSig = new Dictionary<string, string>();
        foreach (var d in desired)   // 容忍重名(Validate 已挡,这里兜底):后者跳过并告警,不让整个重载炸掉
            if (!desiredSig.TryAdd(d.name, d.sig)) _log.LogWarning("设备全名重复:{n},后者被忽略", d.name);
        // 停:已删除 或 配置签名变了的设备。并行停止，且只等一小段时间——PLC 不在线时驱动正卡在
        // 阻塞式连接(超时可达十几秒)，不能让保存工程跟着等；超时后先摘掉，旧循环退出后在后台释放。
        var stopping = DeviceSnapshot()
            .Where(dr => !desiredSig.TryGetValue(dr.Name, out var sig) || sig != dr.Sig)
            .Select(dr => StopDeviceAsync(dr, drainTimeout: ApplyStopGrace)).ToArray();
        await Task.WhenAll(stopping);

        // 起:新工程里当前未在跑的(新增 或 刚停掉的变化设备)。分批启动:改模板会波及全部引用实例,
        // 一次性起 2000 台会同时发起连接引发重连风暴——每批 100 台、批间短间隔,把初次连接摊开(叠加已有退避抖动)。
        var runningNames = DeviceSnapshot().Select(device => device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toStart = desired.Where(d => !runningNames.Contains(d.name)).ToList();
        const int batchSize = 100;
        for (int i = 0; i < toStart.Count; i++)
        {
            var d = toStart[i];
            StartDevice(d.ch, d.dev, d.factory, templates, hashes, knownSig: d.sig);
            if ((i + 1) % batchSize == 0 && i + 1 < toStart.Count)
                await Task.Delay(100);   // 批间隔:分散初次连接
        }
        UpdateProjectMetadata(newProject);
        _log.LogInformation("增量应用完成:运行设备 {n} 个", DeviceCount());
    }

    /// <summary>仅供测试:不带删除设备的简写(removedDevices 为空)。</summary>
    internal Task<ScopedDeviceApplyResult> ApplyDevicesAsync(IgwProject newProject,
        IReadOnlyCollection<string> changedDevices, Func<Task>? beginApply = null) =>
        ApplyDevicesAsync(newProject, changedDevices, Array.Empty<string>(), beginApply);

    /// <summary>
    /// 按设备应用(批量点位写入与界面局部保存共用的唯一实现):只处理点名的设备,其它设备不遍历、不算签名、运行实例原样保留。
    /// <paramref name="changedDevices"/>(新工程中的设备全名):签名未变→跳过;变了→停旧循环(不注销点位)后以复用句柄方式重建:
    /// 既有点句柄/订阅/最后值保留且定义就地更新,新增点登记新条目,删除点从 Hub 注销(先注销再起新循环)。点名设备在新工程中不存在则忽略。
    /// <paramref name="removedDevices"/>(旧工程中的设备全名,删除/改名/移动前的名字):停掉并注销其全部点位;仍在新工程中的名字不处理。
    /// 例外:旧循环未在 <see cref="ApplyStopGrace"/> 内退出(排空超时)时不复用句柄,退回全量语义——
    /// 旧点全部注销、新循环登记全新条目;此时结果里该设备的全部旧点 id 计入 RemovedTagIds、全部新点 id 计入 AddedTagIds
    /// (同一 id 可同时出现在两边,表示条目已重建,订阅方须改绑)。
    /// 驱动不可用或建驱动失败时该设备停掉不再起,其点位计入 RemovedTagIds。
    /// </summary>
    public async Task<ScopedDeviceApplyResult> ApplyDevicesAsync(IgwProject newProject,
        IReadOnlyCollection<string> changedDevices, IReadOnlyCollection<string> removedDevices, Func<Task>? beginApply = null)
    {
        if (_cts is null) throw new InvalidOperationException("Runtime 尚未启动。");
        await _deviceLifecycleGate.WaitAsync();
        try
        {
            if (beginApply is not null) await beginApply();
            return await ApplyDevicesCoreAsync(newProject, changedDevices, removedDevices);
        }
        finally { _deviceLifecycleGate.Release(); }
    }

    private sealed record ScopedRestart(string Name, ChannelDef Channel, DeviceDef Device, DeviceRuntime? Running,
        string Sig, IDriverFactory? Factory);

    private async Task<ScopedDeviceApplyResult> ApplyDevicesCoreAsync(IgwProject newProject,
        IReadOnlyCollection<string> changedDevices, IReadOnlyCollection<string> removedDevices)
    {
        var (templates, hashes) = TemplateMaps(newProject);
        var running = new Dictionary<string, DeviceRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in DeviceSnapshot()) running.TryAdd(device.Name, device);   // 只按名查表,不算其它设备签名
        var restarted = new List<string>();
        var removedAll = new List<string>();
        var addedAll = new List<string>();
        var handled = new List<string>();

        // 1) 定位变化设备
        var targets = new List<(string Name, ChannelDef Channel, DeviceDef Device)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string requested in changedDevices)
        {
            if (string.IsNullOrWhiteSpace(requested)) continue;
            if (!TryFindDevice(newProject, requested, out var ch, out var dev))
            {
                _log.LogWarning("按设备应用:新工程中不存在设备 {dev},跳过", requested);
                continue;
            }
            string name = $"{ch.Name}.{dev.Name}";
            if (!seen.Add(name)) continue;
            handled.Add(name);
            targets.Add((name, ch, dev));
        }

        // 2) 签名(通道改名等会一次涉及整条通道的设备:并行算)
        var sigs = new string[targets.Count];
        if (targets.Count == 1) sigs[0] = DeviceSig(targets[0].Channel, targets[0].Device, hashes);
        else Parallel.For(0, targets.Count, i => sigs[i] = DeviceSig(targets[i].Channel, targets[i].Device, hashes));
        var plans = new List<ScopedRestart>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            var (name, ch, dev) = targets[i];
            running.TryGetValue(name, out var dr);
            if (dr is not null && dr.Sig == sigs[i]) continue;   // 配置未变:连接与缓存不动
            plans.Add(new ScopedRestart(name, ch, dev, dr, sigs[i], ResolveFactory(ch, out var factory) ? factory : null));
        }

        // 3) 已删除/改名的旧设备(新工程里已不存在)
        var removing = new List<DeviceRuntime>();
        var removingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string oldName in removedDevices)
        {
            if (string.IsNullOrWhiteSpace(oldName) || seen.Contains(oldName) || !removingSeen.Add(oldName)) continue;
            if (TryFindDevice(newProject, oldName, out _, out _))
            {
                _log.LogWarning("按设备应用:设备 {dev} 仍在新工程中,不按删除处理", oldName);
                continue;
            }
            if (running.TryGetValue(oldName, out var dr)) removing.Add(dr);
        }

        // 4) 并行停旧:待删设备与驱动不可用的设备随停止注销点位;其余保留条目以便复用句柄
        var removeTasks = removing.Select(dr => StopDeviceAsync(dr, unregisterTags: true, drainTimeout: ApplyStopGrace)).ToList();
        var stopTasks = plans.Where(p => p.Running is not null)
            .Select(async p => (p.Name, Drained: await StopDeviceAsync(p.Running!, unregisterTags: p.Factory is null,
                drainTimeout: ApplyStopGrace)))
            .ToList();
        await Task.WhenAll(removeTasks);
        var drainedByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, drained) in await Task.WhenAll(stopTasks)) drainedByName[name] = drained;
        foreach (var dr in removing) removedAll.AddRange(dr.TagIds);

        // 5) 逐台起新(删除点先注销再起:StartDevice 内 RefreshDevice 重绑 MES 租约时不会再指向已删点的槽位)
        foreach (var plan in plans)
        {
            var dr = plan.Running;
            var oldIds = dr?.TagIds ?? (IReadOnlyList<string>)Array.Empty<string>();
            if (plan.Factory is null)
            {
                // 驱动不可用:等同全量应用里「停掉且不再起」,点位已随停止注销
                if (dr is not null) removedAll.AddRange(oldIds);
                continue;
            }

            var newIds = EnumDeviceTagIds(EffectiveDevice(plan.Channel, plan.Device, templates), plan.Name)
                .ToHashSet(StringComparer.Ordinal);
            var oldSet = oldIds.ToHashSet(StringComparer.Ordinal);
            bool drained = dr is null || drainedByName.GetValueOrDefault(plan.Name, true);
            // 旧循环没在宽限内退出:它仍持有旧条目句柄、退出时还会 MarkAllBad,复用句柄会出现双写者(破坏 seqlock 单写者)。
            // 此时退回全量应用语义:旧点全部注销、新循环登记全新条目,旧循环的残留写入只落到孤儿条目上。
            bool reuse = dr is not null && drained;
            if (dr is not null && !reuse)
            {
                foreach (string id in oldIds) _hub.Unregister(id);
                removedAll.AddRange(oldIds);
                oldSet.Clear();   // 新点全部算新增
            }
            else
            {
                foreach (string id in oldIds)
                    if (!newIds.Contains(id)) { _hub.Unregister(id); removedAll.Add(id); }
            }

            StartDevice(plan.Channel, plan.Device, plan.Factory, templates, hashes, reuseHubEntries: reuse, knownSig: plan.Sig);
            bool started;
            lock (_devicesGate)
                started = _devices.Any(d => string.Equals(d.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
            if (!started)
            {
                // 建驱动失败(StartDevice 已记错误日志):与全量应用一致,保留下来的旧点也注销,不留无主条目
                if (reuse)
                    foreach (string id in oldIds)
                        if (newIds.Contains(id)) { _hub.Unregister(id); removedAll.Add(id); }
                continue;
            }
            restarted.Add(plan.Name);
            foreach (string id in newIds)
                if (!oldSet.Contains(id)) addedAll.Add(id);
        }
        UpdateDeviceMetadata(newProject, handled);
        _log.LogInformation("按设备应用完成:重启 {n} 台,移除 {removedDevices} 台,新增点 {added} 个,删除点 {removed} 个",
            restarted.Count, removing.Count, addedAll.Count, removedAll.Count);
        return new ScopedDeviceApplyResult(restarted, removedAll, addedAll);
    }

    // 设备下所有点位完整路径(与 BindDevice 登记口径一致):按设备应用在起新循环之前据此算出删除点
    private static IEnumerable<string> EnumDeviceTagIds(DeviceDef dev, string devName)
    {
        foreach (var t in dev.Tags) if (!string.IsNullOrWhiteSpace(t.Name)) yield return $"{devName}.{t.Name}";
        IEnumerable<string> Rec(IEnumerable<TagGroupDef> gs, string pp)
        {
            foreach (var g in gs)
            {
                string gp = $"{pp}.{g.Name}";
                foreach (var t in g.Tags) if (!string.IsNullOrWhiteSpace(t.Name)) yield return $"{gp}.{t.Name}";
                foreach (var id in Rec(g.SubGroups, gp)) yield return id;
            }
        }
        foreach (var id in Rec(dev.Groups, devName)) yield return id;
    }

    // 在工程中按「通道.设备」全名定位设备(设备名可含点号,故按通道名前缀匹配后再比设备名)
    private static bool TryFindDevice(IgwProject project, string fullName, out ChannelDef channel, out DeviceDef device)
    {
        foreach (var ch in project.Channels)
        {
            if (fullName.Length <= ch.Name.Length + 1 || fullName[ch.Name.Length] != '.' ||
                !fullName.StartsWith(ch.Name, StringComparison.OrdinalIgnoreCase)) continue;
            string devName = fullName[(ch.Name.Length + 1)..];
            foreach (var dev in ch.Devices)
                if (string.Equals(dev.Name, devName, StringComparison.OrdinalIgnoreCase))
                {
                    channel = ch;
                    device = dev;
                    return true;
                }
        }
        channel = null!;
        device = null!;
        return false;
    }

    /// <summary>
    /// <see cref="UpdateProjectMetadata"/> 的按设备版本:只刷新点名设备的点位备注,并切换当前工程引用。
    /// 不遍历其它设备的点位(大工程下保持 O(本设备点数))。
    /// </summary>
    public void UpdateDeviceMetadata(IgwProject project, IReadOnlyCollection<string> deviceFullNames)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in deviceFullNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            if (TryFindDevice(project, name, out var ch, out var dev))
                RefreshDeviceDescriptions(project, ch, dev);
        }
        _project = project;
    }

    // 仅供测试:返回运行中设备实例的身份(引用相等即表示该设备未被重启)
    internal object? RunningDeviceIdentity(string deviceFullName)
    {
        lock (_devicesGate)
            return _devices.FirstOrDefault(d => string.Equals(d.Name, deviceFullName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从网卡解析开始硬重建指定通道：销毁本地绑定/转发、协议驱动和设备循环，再按当前工程创建全新对象。
    /// Hub 点位条目不替换，因此 QC、OPC UA、MES 等按句柄建立的订阅和最后值缓存不会丢失。
    /// </summary>
    public async Task<int> ReconnectChannelAsync(string channelName, CancellationToken ct)
    {
        if (_cts is null) throw new InvalidOperationException("Runtime 尚未启动。");
        ct.ThrowIfCancellationRequested();
        _autoReconnectChannels.TryRemove(channelName, out _);
        await _deviceLifecycleGate.WaitAsync(ct);
        bool gateHeld = true;
        var backoff = _autoReconnectBackoff.GetOrAdd(channelName, _ => new());
        try
        {
            if (_cts is null || _cts.IsCancellationRequested) throw new InvalidOperationException("Runtime 正在停止。");
            backoff.TryBegin(Environment.TickCount64, manual: true);
            CancellationToken token = _cts.Token;
            var rebuilt = await HardReconnectChannelCoreAsync(channelName, token, automatic: false);
            // 重建完成即释放生命周期锁；等首轮连接结果(PLC 离线时要等连接超时)不能挡住保存工程。
            _deviceLifecycleGate.Release();
            gateHeld = false;
            await AwaitFirstReconnectAsync(channelName, rebuilt, automatic: false, token);
            return rebuilt.Length;
        }
        finally
        {
            _autoReconnectChannels.TryRemove(channelName, out _);
            backoff.Complete(Environment.TickCount64);
            if (gateHeld) _deviceLifecycleGate.Release();
        }
    }

    private async Task AwaitFirstReconnectAsync(string channelName, DeviceRuntime[] rebuilt, bool automatic, CancellationToken ct)
    {
        if (rebuilt.Length == 0) return;
        var results = await Task.WhenAll(rebuilt.Select(device => device.Loop.ReconnectNowAsync(ct)));
        _log.LogInformation("通道 {channel} {mode}网络级硬重连完成:重建 {count} 台，首轮连接成功 {online} 台",
            channelName, automatic ? "自动" : "手动", rebuilt.Length, results.Count(connected => connected));
    }

    private async Task<DeviceRuntime[]> HardReconnectChannelCoreAsync(string channelName, CancellationToken ct, bool automatic)
    {
        var channel = _project.Channels.FirstOrDefault(ch =>
            string.Equals(ch.Name, channelName, StringComparison.OrdinalIgnoreCase));
        if (channel is null) throw new KeyNotFoundException($"通道 {channelName} 不存在。");
        if (!ResolveFactory(channel, out var factory))
            throw new InvalidOperationException($"通道 {channel.Name} 的驱动 {channel.DriverId} 未注册。");
        var targetNames = channel.Devices
            .Select(device => $"{channel.Name}.{device.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = DeviceSnapshot();
        var targets = snapshot.Where(device => targetNames.Contains(device.Name) &&
            (!automatic || device.Driver.State != ConnectionState.Connected) &&
            (!automatic || device.Driver is not SharedOpcUaDriverLease shared || !shared.PhysicalConnectionHealthy)).ToArray();
        if (targets.Length == 0) return [];
        var sharedResources = targets.SelectMany(device => device.Loop.SharedOpcUaConnections)
            .Where(shared => !automatic || !shared.PhysicalConnectionHealthy).ToArray();
        // A physical rebuild must release every owner, including aliases in other channels.
        // Otherwise the new logical device would just re-acquire the damaged relay/session.
        if (sharedResources.Length > 0)
        {
            targets = targets.Concat(snapshot.Where(device => device.Loop.SharedOpcUaConnections.Any(lease =>
                sharedResources.Any(lease.SharesConnectionWith)))).Distinct().ToArray();
            _log.LogInformation("通道 {channel} 的共享 OPC UA 连接关联 {count} 台逻辑设备，统一重建并保留点位句柄", channel.Name, targets.Length);
        }
        targetNames = targets.Select(device => device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _log.LogInformation("通道 {channel} 开始{mode}网络级硬重连:销毁 {count} 台设备的网卡绑定、转发器和协议会话",
            channel.Name, automatic ? "自动" : "手动", targets.Length);

        await Task.WhenAll(targets.Select(device => StopDeviceAsync(device, unregisterTags: false)));
        ct.ThrowIfCancellationRequested();

        var (templates, hashes) = TemplateMaps(_project);
        foreach (var affectedChannel in _project.Channels)
        {
            if (!affectedChannel.Devices.Any(device => targetNames.Contains($"{affectedChannel.Name}.{device.Name}"))) continue;
            if (!ResolveFactory(affectedChannel, out var affectedFactory)) continue;
            foreach (var device in affectedChannel.Devices)
            {
                if (!targetNames.Contains($"{affectedChannel.Name}.{device.Name}")) continue;
                ct.ThrowIfCancellationRequested();
                if (!automatic && string.Equals(affectedChannel.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase))
                    _opcUaConnections?.RetryNow(OpcUaConnectionPool.ConnectionKey(affectedChannel, EffectiveDevice(affectedChannel, device, templates), $"{affectedChannel.Name}.{device.Name}"));
                StartDevice(affectedChannel, device, affectedFactory, templates, hashes, reuseHubEntries: true);
            }
        }

        return DeviceSnapshot().Where(device => targetNames.Contains(device.Name)).ToArray();
    }

    private void RequestAutomaticChannelReconnect(string channelName)
    {
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested || string.IsNullOrWhiteSpace(channelName)) return;
        long now = Environment.TickCount64;
        if (!_autoReconnectBackoff.GetOrAdd(channelName, _ => new()).IsReady(now)) return;
        if (_autoReconnectChannels.TryAdd(channelName, 0))
            _autoReconnectSignal.Release();
    }

    private async Task AutomaticChannelReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _autoReconnectSignal.WaitAsync(ct);
                foreach (string channelName in _autoReconnectChannels.Keys.ToArray())
                {
                    if (!_autoReconnectChannels.TryRemove(channelName, out _)) continue;
                    await _deviceLifecycleGate.WaitAsync(ct);
                    bool gateHeld = true;
                    var backoff = _autoReconnectBackoff.GetOrAdd(channelName, _ => new());
                    bool admitted = false;
                    try
                    {
                        admitted = backoff.TryBegin(Environment.TickCount64);
                        if (!admitted) continue;
                        var rebuilt = await HardReconnectChannelCoreAsync(channelName, ct, automatic: true);
                        _deviceLifecycleGate.Release();
                        gateHeld = false;
                        await AwaitFirstReconnectAsync(channelName, rebuilt, automatic: true, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (KeyNotFoundException) { }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "通道 {channel} 自动网络级硬重连失败", channelName);
                    }
                    finally
                    {
                        if (admitted)
                        {
                            int delay = backoff.Complete(Environment.TickCount64);
                            _log.LogInformation("通道 {channel} 自动网络重建冷却 {delay}ms，期间设备继续独立重试连接", channelName, delay);
                        }
                        if (gateHeld) _deviceLifecycleGate.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>热重载停旧设备时最多等它的循环退出这么久，之后转入后台释放。</summary>
    internal static TimeSpan ApplyStopGrace { get; set; } = TimeSpan.FromMilliseconds(1500);

    // 返回 true=旧循环已在宽限内退出;false=超时转入后台释放(旧循环可能仍在写它持有的 Hub 条目)
    private async Task<bool> StopDeviceAsync(DeviceRuntime dr, bool unregisterTags = true, TimeSpan? drainTimeout = null)
    {
        try { dr.Cts.Cancel(); } catch { }
        if (drainTimeout is { } grace)
        {
            bool drained;
            try { drained = await Task.WhenAny(dr.Loop.Completion, Task.Delay(grace)) == dr.Loop.Completion; }
            catch { drained = true; }
            if (!drained)
            {
                // 先把点位与写映射摘掉、从运行表移除，新设备可以立刻接管同名点位；
                // 旧循环持有的是旧句柄，退出前的残留写入只落到孤儿条目上。
                DetachDevice(dr, unregisterTags);
                _log.LogWarning("设备 {dev} 正在等待连接超时，已转入后台释放，不阻塞本次工程应用", dr.Name);
                _ = Task.Run(async () =>
                {
                    try { await dr.Loop.Completion; } catch { }
                    await ReleaseDeviceResourcesAsync(dr);
                });
                return false;
            }
        }
        try { await dr.Loop.Completion; } catch { }
        await ReleaseDeviceResourcesAsync(dr);
        DetachDevice(dr, unregisterTags);
        return true;
    }

    private static async Task ReleaseDeviceResourcesAsync(DeviceRuntime dr)
    {
        if (dr.PrimaryNetworkProbe is not null) { try { await dr.PrimaryNetworkProbe.DisposeAsync(); } catch { } }
        if (dr.SecondaryNetworkProbe is not null) { try { await dr.SecondaryNetworkProbe.DisposeAsync(); } catch { } }
        try { await dr.Driver.DisposeAsync(); } catch { }
        if (dr.PrimaryBinding is not null) { try { await dr.PrimaryBinding.DisposeAsync(); } catch { } }
        if (dr.SecondaryBinding is not null) { try { await dr.SecondaryBinding.DisposeAsync(); } catch { } }
        try { dr.Cts.Dispose(); } catch { }
    }

    private void DetachDevice(DeviceRuntime dr, bool unregisterTags)
    {
        foreach (var id in dr.TagIds)
        {
            if (unregisterTags) _hub.Unregister(id);
            // 只移除仍指向这台旧设备的写映射，不能误删新设备刚登记的同名点位。
            if (_writeMap.TryGetValue(id, out var entry) && ReferenceEquals(entry.loop, dr.Loop))
                _writeMap.TryRemove(id, out _);
        }
        lock (_devicesGate)
            _devices.Remove(dr);
    }

    // 设备配置签名:驱动 + 通道设置 + 设备完整定义(端点/设置/差异) + 所引用模板内容哈希;任一变化 → 该设备需重启。流式哈希不落大字符串。
    // 模板实例:dev 含 TemplateRef/Delta/端点/设置(改差异只动本实例),tmplHash 含模板全部点位(改模板→全体引用实例签名变);手工设备 tmplHash 为空。
    /// <summary>一次启动/整体应用中所有设备的签名，并行计算(百万点工程串行要数秒)。</summary>
    internal static Dictionary<DeviceDef, string> DeviceSigs(IgwProject project, IReadOnlyDictionary<string, string> templateHashes)
    {
        var pairs = project.Channels.SelectMany(ch => ch.Devices.Select(dev => (ch, dev))).ToArray();
        var sigs = new string[pairs.Length];
        Parallel.For(0, pairs.Length, index => sigs[index] = DeviceSig(pairs[index].ch, pairs[index].dev, templateHashes));
        var result = new Dictionary<DeviceDef, string>(pairs.Length, ReferenceEqualityComparer.Instance);
        for (int index = 0; index < pairs.Length; index++) result[pairs[index].dev] = sigs[index];
        return result;
    }

    internal static string DeviceSig(ChannelDef ch, DeviceDef dev, IReadOnlyDictionary<string, string> templateHashes)
    {
        string tmplHash = !string.IsNullOrWhiteSpace(dev.TemplateRef) && templateHashes.TryGetValue(dev.TemplateRef, out var h) ? h : "";
        return JsonHash.OfRuntime(new { ch.DriverId, ch.Settings, dev, tmplHash });
    }

    private static int SettingInt(Dictionary<string, string> s, string key, int def) =>
        s.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

    public async Task StopAsync()
    {
        var cts = _cts;
        cts?.Cancel();   // 链式取消所有设备 CTS + 自动通道重连工作器
        try { await _autoReconnectWorker; } catch { }
        await _deviceLifecycleGate.WaitAsync();
        try
        {
            var devices = DeviceSnapshot();
            try { await Task.WhenAll(devices.Select(d => d.Loop.Completion)); } catch { }
            foreach (var d in devices)
            {
                if (d.PrimaryNetworkProbe is not null) { try { await d.PrimaryNetworkProbe.DisposeAsync(); } catch { } }
                if (d.SecondaryNetworkProbe is not null) { try { await d.SecondaryNetworkProbe.DisposeAsync(); } catch { } }
                try { await d.Driver.DisposeAsync(); } catch { }
                if (d.PrimaryBinding is not null) { try { await d.PrimaryBinding.DisposeAsync(); } catch { } }
                if (d.SecondaryBinding is not null) { try { await d.SecondaryBinding.DisposeAsync(); } catch { } }
                try { d.Cts.Dispose(); } catch { }
            }
            lock (_devicesGate)
                _devices.Clear();
            _autoReconnectChannels.Clear();
            _autoReconnectBackoff.Clear();
            if (_opcUaConnections is not null) await _opcUaConnections.DisposeAsync();
            _opcUaConnections = null;
            if (_injectedNetworkProbes is null && _networkProbes is not null)
                await _networkProbes.DisposeAsync();
            _networkProbes = null;
            _networkHosts.Clear();
            cts?.Dispose();
            _cts = null;   // 释放全局 CTS
        }
        finally { _deviceLifecycleGate.Release(); }
    }

    /// <summary>该点当前是否被持续采集；未知点位返回 false。</summary>
    public bool IsAcquiring(string tagId) =>
        _writeMap.TryGetValue(tagId, out var target) && target.loop.IsAcquiring(tagId);

    /// <summary>
    /// 外部读取接口(GET 值、KEP/DCS 兼容读取)使用：没有在持续采集的点先从 PLC 读一次再返回，
    /// 保证拿到的是最新值；同时激活这些点，活动窗口内的后续读取直接用轮询缓存。
    /// 超时或读取失败时返回缓存，由值的质量和时间戳说明情况。
    /// </summary>
    /// <param name="activate">
    /// true = 顺带把这批点拉进活动窗口。默认 false：取一次当前值不该让点位
    /// 被白白轮询 activeWindowSec。需要持续采集的显式声明扫描需求(MES 走 ScanDemandRegistry 租约)。
    /// </param>
    public async Task RefreshIdleAsync(IEnumerable<string> tagIds, TimeSpan timeout, CancellationToken ct,
        bool activate = false)
    {
        var idle = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tagId in tagIds)
        {
            if (string.IsNullOrWhiteSpace(tagId) || !seen.Add(tagId)) continue;
            if (_writeMap.ContainsKey(tagId) && !IsAcquiring(tagId)) idle.Add(tagId);
            if (activate) Touch(tagId);
        }
        if (idle.Count == 0) return;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try { await ReadNowAsync(idle, bounded.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>标记被 QC/REST 引用：激活按需扫描并立即唤醒所属设备。</summary>
    public void Touch(string tagId)
    {
        _hub.Touch(tagId);
        if (_writeMap.TryGetValue(tagId, out var target)) target.loop.WakeForDemand();
    }

    /// <summary>
    /// 分层采集的按需拉取：立即读取指定点位并发布到 DataHub，返回后缓存即触发时刻的值。
    /// 按所属设备分组并行下发；同一设备内部仍按驱动批量能力拆分。未知点位被忽略。
    /// </summary>
    public async Task<OnDemandReadResult> ReadNowAsync(
        IReadOnlyCollection<string> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return new OnDemandReadResult(0, 0, null);
        var byLoop = new Dictionary<DeviceLoop, List<string>>();
        foreach (string tagId in tagIds)
        {
            if (!_writeMap.TryGetValue(tagId, out var target)) continue;
            if (!byLoop.TryGetValue(target.loop, out var list)) byLoop[target.loop] = list = new();
            list.Add(tagId);
        }
        if (byLoop.Count == 0) return new OnDemandReadResult(0, 0, null);

        var results = await Task.WhenAll(byLoop.Select(pair =>
            pair.Key.ReadOnDemandAsync(pair.Key.BindingsFor(pair.Value), ct)));
        return new OnDemandReadResult(
            results.Sum(item => item.GoodCount),
            results.Sum(item => item.SampleCount),
            results.Select(item => item.Error).FirstOrDefault(message => message is not null));
    }

    /// <summary>北向写点(eng 值);内部反向缩放为 raw 后交给驱动。</summary>
    /// <param name="policy">
    /// 写后怎么确认。不传按引擎历来行为(回读验证、最多重写 10 次)，
    /// 调用方要改判定就显式声明，不用动其它调用点。
    /// </param>
    public async Task<StatusCode> WriteAsync(string tagId, object value, CancellationToken ct,
        WritePolicy? policy = null)
    {
        if (!_writeMap.TryGetValue(tagId, out var t))
        {
            if (!ArrayAliasResolver.TryParse(tagId, out var alias) ||
                !_writeMap.TryGetValue(alias.ParentTagId, out var parent) ||
                parent.binding.Def.Array is null)
                return StatusCode.BadNotConnected;
            Touch(alias.ParentTagId);
            if (!_hub.TryGet(alias.ParentTagId, out var data) || data.Value is not ArrayRuntimeValue current)
                return StatusCode.BadNotConnected;
            try
            {
                var replacement = ArrayValueEditor.ReplaceSingle(current, alias.Indices, alias.MemberPath,
                    value, parent.binding.Def.TextEncoding ?? PlcTextEncoding.Utf8);
                var slice = new ArraySlice(alias.Indices, Enumerable.Repeat(1, alias.Indices.Count).ToArray());
                var result = await WriteArrayAsync(alias.ParentTagId, slice, replacement, ct);
                return result.Status;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                                       KeyNotFoundException or NotSupportedException or OverflowException or
                                       FormatException or InvalidCastException)
            {
                return StatusCode.BadDecodeError;
            }
        }
        if (t.binding.Def.Access == TagAccess.Read) return StatusCode.BadNotWritable;
        if (t.binding.Def.Access == TagAccess.Write &&
            (t.binding.Def.WriteConfirmation is null ||
             t.binding.Def.WriteConfirmation.Mode == WriteConfirmationMode.ReadBack))
            return StatusCode.BadNotReadable;
        object normalized;
        object raw;
        try
        {
            normalized = PlcWriteValueNormalizer.Normalize(t.binding.Def, value);
            raw = ValueOps.ReverseScaling(normalized, t.binding.Def.Scaling) ?? normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                                   NotSupportedException or OverflowException or
                                   FormatException or InvalidCastException)
        {
            string supplied = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (supplied.Length > 256) supplied = supplied[..256] + "...";
            _log.LogWarning(ex,
                "点位写入值转换失败：Tag={tag}，目标类型={type}，输入={value}，阶段=值转换，状态={status}",
                tagId, t.binding.Def.DataType, supplied, StatusCode.BadDecodeError);
            return StatusCode.BadDecodeError;
        }
        var req = new WriteRequest
        {
            TagId = tagId, Def = t.binding.Def, Address = t.binding.Address, Value = raw,
            Policy = policy ?? WritePolicy.Default,
        };
        // 排入设备写队列；是否与读取并行由驱动协商的 MaxInFlight 决定。
        return await t.loop.EnqueueWriteAsync(req, ct);
    }

    /// <summary>读取数组切片；只访问父数组的一个 DataHub 槽位。</summary>
    public ArrayRuntimeValue ReadArraySlice(string tagId, ArraySlice slice)
    {
        Touch(tagId);
        if (!_hub.TryGet(tagId, out var data) || data.Value is not ArrayRuntimeValue array)
            throw new KeyNotFoundException($"数组标记 {tagId} 尚无运行数据。");
        return array.Slice(slice);
    }

    /// <summary>读取数组虚拟元素/成员路径，不新增 TagDef、槽位或 PLC 请求。</summary>
    public DataValue ReadArrayElement(string aliasPath)
    {
        if (!ArrayAliasResolver.TryParse(aliasPath, out var alias))
            throw new FormatException($"数组元素路径格式无效:{aliasPath}");
        Touch(alias.ParentTagId);
        if (!_hub.TryGet(alias.ParentTagId, out var parent) || parent.Value is not ArrayRuntimeValue current)
            throw new KeyNotFoundException($"数组标记 {alias.ParentTagId} 尚无运行数据。");
        if (alias.Indices.Count != current.Shape.Rank)
            throw new IndexOutOfRangeException("数组索引数量与维度不一致。");

        var linearOffset = checked((int)current.Shape.GetLinearOffset(alias.Indices));
        var quality = current.ElementQuality.Span[linearOffset];
        foreach (var member in alias.MemberPath)
        {
            if (current is not StructArrayValue structure)
                throw new KeyNotFoundException($"结构体成员 {member} 不存在。");
            var field = structure.Schema.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, member, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.JsonName, member, StringComparison.OrdinalIgnoreCase));
            if (field is null || field.Array is not null ||
                !structure.Members.TryGetValue(field.Name, out var next))
                throw new KeyNotFoundException($"结构体成员 {member} 不存在或不是标量成员。");
            current = next;
            var memberQuality = current.ElementQuality.Span[linearOffset];
            quality = MergeElementQuality(quality, memberQuality);
        }

        return new DataValue(current.GetBoxedValue(linearOffset), quality,
            parent.SourceTimestamp, parent.ServerTimestamp);
    }

    /// <summary>数组完整/切片写入；进入所属设备的现有串行通信队列。</summary>
    public async Task<ArrayWriteResult> WriteArrayAsync(
        string tagId, ArraySlice slice, ArrayRuntimeValue value, CancellationToken ct)
    {
        if (!_writeMap.TryGetValue(tagId, out var target) || target.binding.Def.Array is null)
            return ArrayWriteResult.Failed(StatusCode.BadNotConnected, $"数组标记 {tagId} 不存在或设备未运行");
        if (target.binding.Def.Access == TagAccess.Read)
            return ArrayWriteResult.Failed(StatusCode.BadNotWritable, "数组点位为只读");
        if (target.binding.Def.Access == TagAccess.Write)
            return ArrayWriteResult.Failed(StatusCode.BadNotReadable, "数组点位不可回读，无法验证写入");

        ArrayShape requestedShape;
        try { requestedShape = slice.ValidateAndCreateShape(target.binding.Def.Array.Shape); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return ArrayWriteResult.Failed(StatusCode.BadDecodeError, ex.Message);
        }
        if (value.ElementType != target.binding.Def.Array.ElementType)
            return ArrayWriteResult.Failed(StatusCode.BadDecodeError, "写入元素类型与数组定义不一致");
        if (requestedShape.Rank != value.Shape.Rank ||
            Enumerable.Range(0, requestedShape.Rank).Any(i =>
                requestedShape.Dimensions[i] != value.Shape.Dimensions[i] ||
                requestedShape.LowerBounds[i] != value.Shape.LowerBounds[i]))
            return ArrayWriteResult.Failed(StatusCode.BadDecodeError, "写入值形状与目标切片不一致");

        var request = new ArrayWriteRequest(target.binding, slice, value);
        return await target.loop.EnqueueArrayWriteAsync(request, ct);
    }

    private static StatusCode MergeElementQuality(StatusCode parent, StatusCode member)
    {
        if (parent.IsBad) return parent;
        if (member.IsBad) return member;
        if (parent.Class == QualityClass.Uncertain || member.Class == QualityClass.Uncertain)
            return StatusCode.Uncertain;
        return StatusCode.Good;
    }

    // KEP「优化方法」→ 内部键
    private static string WriteOptKey(string? method) => method switch
    {
        "写入所有标记的所有值" => "all",
        "仅写入非布尔型标记的最新值" => "nonbool-latest",
        _ => "latest",
    };

    // KEP「扫描模式」→ 内部键(默认 all=全轮,安全;active=遵循客户端只轮被引用的点)
    /// <summary>
    /// 扫描模式归一化。原先「除了那一个中文串以外一律当 all」，于是配置里任何偏差
    /// (拼写不同、写成英文 active、多个空格)都会静默变成全量轮询——几十万点的设备上
    /// 这等于把整台 PLC 压满，而且现场看不出是配置问题。
    /// 现在认得的写法按语义归一，认不出来的记一条警告并按 active 处理(保守：宁可少读也不要
    /// 悄悄把百万点全轮)；未配置仍按各调用方自己的默认值。
    /// </summary>
    internal static string ScanModeKey(string? mode, Action<string>? warn = null)
    {
        string value = mode?.Trim() ?? "";
        if (value.Length == 0) return "all";
        if (value is "遵循客户端指定的扫描速率" or "active") return "active";
        if (value is "请求所有数据以指定的扫描速率" or "all") return "all";
        warn?.Invoke(value);
        return "active";
    }

    public IReadOnlyList<DeviceStatus> DeviceStatuses() => DeviceSnapshot().Select(DeviceStatusOf).ToList();

    public ScanTimingSnapshot ScanTimings(string? device = null, string? tag = null, int limit = 100)
    {
        var devices = DeviceSnapshot();
        var selected = string.IsNullOrWhiteSpace(device) ? devices.FirstOrDefault() :
            devices.FirstOrDefault(item => string.Equals(item.Name, device, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(device))
            throw new KeyNotFoundException($"设备不存在：{device}");
        return new ScanTimingSnapshot(DiagnosticSummaries(devices).Values.ToArray(),
            selected?.Name, selected?.Loop.ScanHistory(limit, tag) ?? [], ScanTimingHistory.Capacity);
    }

    public IReadOnlyList<TagScanTiming> TagScanTimings(IReadOnlyCollection<string> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        if (tagIds.Count > 200 || tagIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 4096))
            throw new ArgumentException("每次最多查询 200 个有效标签 ID，每个 ID 不超过 4096 个字符。", nameof(tagIds));
        var requested = tagIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0) return [];
        var result = new List<TagScanTiming>(requested.Count);
        foreach (var device in DeviceSnapshot())
        {
            var items = device.Loop.TagScanTimings(requested);
            result.AddRange(items);
            foreach (var item in items) requested.Remove(item.TagId);
            if (requested.Count == 0) break;
        }
        return result;
    }

    private DeviceStatus DeviceStatusOf(DeviceRuntime device)
    {
        var detail = ConnectionDetail(device);
        var current = _connectionIncidents.Current(device.Name);
        if (detail is not null && detail.Code is not ("" or "None" or "Connected"))
        {
            var descriptor = ConnectionDiagnosticClassifier.Classify(null, detail);
            if (current is null || current.Category != descriptor.Category || current.RawError != descriptor.RawError)
                current = _connectionIncidents.Report(device.Name, ChannelName(device.Name), device.Driver.DriverId,
                    descriptor, device.Loop.State, device.Loop.DemandActive, 0);
        }
        return new DeviceStatus(
            device.Name, device.Loop.State, device.TagCount, device.Loop.ActiveTagCount,
            device.Loop.DemandActive, device.Loop.Stats, detail, current);
    }

    private void RaiseDeviceStatusChanged(string deviceName)
    {
        DeviceRuntime? device;
        lock (_devicesGate)
            device = _devices.FirstOrDefault(item => string.Equals(item.Name, deviceName, StringComparison.Ordinal));
        if (device is null) return;
        try { DeviceStatusChanged?.Invoke(DeviceStatusOf(device)); }
        catch (Exception ex) { _log.LogDebug(ex, "设备实时状态订阅者异常，已隔离: {device}", deviceName); }
    }

    public ConnectionIncidentSnapshot ConnectionDiagnostics() => _connectionIncidents.Snapshot();

    /// <summary>
    /// 全部设备的点位级故障：哪个点位、为什么不对、该怎么处理。
    /// 连接诊断回答「这台设备为什么连不上」，这里回答「这台设备上哪些点位读不了、怎么改」。
    /// </summary>
    public TagFaultSnapshot TagFaults()
    {
        var active = new List<TagFault>();
        var recovered = new List<TagFault>();
        int tracked = 0, overflow = 0;
        DeviceRuntime[] devices;
        lock (_devicesGate) devices = _devices.ToArray();
        foreach (var device in devices)
        {
            var snapshot = device.Loop.TagFaults();
            active.AddRange(snapshot.Active);
            recovered.AddRange(snapshot.Recovered);
            tracked += snapshot.TrackedCount;
            overflow += snapshot.OverflowCount;
        }
        active.Sort(static (left, right) => right.LastSeenAt.CompareTo(left.LastSeenAt));
        recovered.Sort(static (left, right) => right.LastSeenAt.CompareTo(left.LastSeenAt));
        return new TagFaultSnapshot(active, recovered, tracked, overflow);
    }

    private static IReadOnlyDictionary<string, ScanTimingDeviceSummary> DiagnosticSummaries(IReadOnlyList<DeviceRuntime> devices)
    {
        // All inputs are retained state. Never acquire a session, scheduler permit or probe here.
        var snapshots = devices.Select(device => new { device.Name, Summary = device.Loop.ScanTimingSummary(),
            Endpoint = device.Loop.DiagnosticEndpointIdentity, Connected = device.Loop.DiagnosticSessionConnected,
            PhysicalSession = device.Loop.DiagnosticPhysicalSessionFingerprint }).ToArray();
        var groups = snapshots.Where(item => item.Endpoint is not null).GroupBy(item => item.Endpoint!)
            .ToDictionary(group => group.Key, group => (Devices: group.Count(), Sessions: group.Where(item => item.Connected)
                .Select(item => (item.PhysicalSession, item.Summary.SessionGeneration)).Distinct().Count()));
        return snapshots.ToDictionary(item => item.Name, item => item.Endpoint is not null ? item.Summary with
        {
            SameEndpointDeviceCount = groups[item.Endpoint].Devices, SameEndpointSessionCount = groups[item.Endpoint].Sessions
        } : item.Summary);
    }

    /// <summary>Copies only retained diagnostics; never touches demand, drivers, sessions or probe acquisition.</summary>
    public AcquisitionDiagnosticsSnapshot AcquisitionDiagnostics(CancellationToken cancellationToken = default)
    {
        const int maxDevices = 128, maxTagReferences = 100_000;
        var runtimeDevices = DeviceSnapshot();
        var summaries = DiagnosticSummaries(runtimeDevices);
        bool truncated = runtimeDevices.Count > maxDevices;
        IReadOnlyList<string> CopyAttributionItems(IReadOnlyList<string> items)
        {
            if (items.Count <= 64) return Array.AsReadOnly(items.ToArray());
            truncated = true;
            return Array.AsReadOnly(items.Take(63).Append("[truncated]").ToArray());
        }
        int remaining = maxTagReferences;
        var devices = new List<AcquisitionDiagnosticDevice>();
        var network = new Dictionary<(string Remote, string Source), IReadOnlyList<NetworkProbeSample>>();
        foreach (var device in runtimeDevices.Take(maxDevices))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<ScanTimingRecord>();
            var addresses = new Dictionary<string, string?>();
            foreach (var record in device.Loop.ScanHistory(ScanTimingHistory.Capacity, null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(record.TagIds.Count, Math.Min(4096, remaining));
                if (count < record.TagIds.Count) truncated = true;
                var ids = record.TagIds.Take(count).ToArray();
                remaining -= count;
                foreach (var id in ids) addresses.TryAdd(id, device.Loop.DiagnosticAddress(id));
                records.Add(record with
                {
                    TagIds = Array.AsReadOnly(ids),
                    Attribution = record.Attribution is { } attribution ? attribution with
                    {
                        Evidence = CopyAttributionItems(attribution.Evidence),
                        Factors = CopyAttributionItems(attribution.Factors)
                    } : null
                });
            }
            devices.Add(new(summaries[device.Name], records.AsReadOnly(),
                Array.AsReadOnly(addresses.Select(pair => new AcquisitionDiagnosticAddress(pair.Key, pair.Value)).ToArray())));
            foreach (var probe in new[] { device.PrimaryNetworkProbe, device.SecondaryNetworkProbe })
            {
                var retained = probe?.Snapshot();
                if (retained is { Count: > 0 })
                    network.TryAdd((retained[0].RemoteAddress, retained[0].SourceAddress), retained);
            }
        }
        var incidents = _connectionIncidents.Snapshot();
        if (incidents.Active.Count > maxDevices || incidents.Recovered.Count > 200) truncated = true;
        var assembly = typeof(RuntimeEngine).Assembly;
        string? build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        return new(DateTimeOffset.UtcNow, build,
            _project.Name, _project.SchemaVersion, devices.AsReadOnly(),
            Array.AsReadOnly(network.Values.SelectMany(samples => samples).ToArray()),
            new(Array.AsReadOnly(incidents.Active.Take(maxDevices).ToArray()),
                Array.AsReadOnly(incidents.Recovered.Take(200).ToArray())), truncated);
    }

    private static string ChannelName(string deviceName)
    {
        int separator = deviceName.IndexOf('.');
        return separator > 0 ? deviceName[..separator] : deviceName;
    }

    /// <summary>指定设备当前的源网卡绑定状态；使用系统默认路由时返回 null。</summary>
    public NetworkRelaySnapshot? NetworkBindingSnapshot(string deviceName)
    {
        lock (_devicesGate)
        {
            var device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            return (device?.PrimaryBinding ?? (device?.Driver as SharedOpcUaDriverLease)?.Binding)?.Snapshot();
        }
    }

    private static DeviceConnectionDetail? ConnectionDetail(DeviceRuntime device)
    {
        static DeviceConnectionDetail? NetworkDetail(NetworkBindingLease? binding)
        {
            var snapshot = binding?.Snapshot();
            if (snapshot is null) return null;
            return snapshot.Code switch
            {
                "AdapterUnavailable" => new DeviceConnectionDetail("NetworkAdapterUnavailable", snapshot.Message),
                "AdapterResolutionFailed" => new DeviceConnectionDetail("NetworkAdapterUnavailable", snapshot.Message),
                "ConnectFailed" or "ConnectTimeout" => new DeviceConnectionDetail("NetworkConnectFailure", snapshot.Message),
                "RelayAcceptFailed" => new DeviceConnectionDetail("NetworkBindingFailure", snapshot.Message),
                _ => null,
            };
        }

        return NetworkDetail(device.PrimaryBinding ?? (device.Driver as SharedOpcUaDriverLease)?.Binding)
            ?? NetworkDetail(device.SecondaryBinding)
            ?? (device.Driver as IDeviceConnectionDiagnostics)?.ConnectionDetail();
    }

    /// <summary>直接查询单台运行设备的连接状态，供高频路径使用，避免创建完整状态快照。</summary>
    public ConnectionState? DeviceState(string deviceName)
    {
        DeviceRuntime? device;
        lock (_devicesGate)
            device = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        return device?.Driver.State;
    }

    /// <summary>浏览设备地址空间(标记生成)。设备需在运行中且驱动支持浏览。</summary>
    public async Task<BrowseResult> BrowseAsync(string deviceName, CancellationToken ct)
    {
        DeviceRuntime? dr;
        lock (_devicesGate)
            dr = _devices.FirstOrDefault(d => d.Name == deviceName);
        if (dr is null) throw new InvalidOperationException($"设备 {deviceName} 未运行(请先保存并应用)。");
        if (dr.Driver is not IBrowsableDriver b) throw new NotSupportedException($"驱动 {dr.Driver.DriverId} 不支持浏览(仅 OPC UA / AB 等可浏览)。");
        return await dr.Loop.BrowseAsync(ct);
    }

    /// <summary>
    /// 在线列出设备 PLC 上的数据块(块名 → DB 号)。设备没在运行、驱动不支持(非 S7-1200/1500、
    /// 原生组件太旧)时返回 null，由调用方让用户手填 DB 号。
    /// </summary>
    public async Task<IReadOnlyList<DataBlockInfo>?> ListDataBlocksAsync(string deviceName, CancellationToken ct)
    {
        DeviceRuntime? dr;
        lock (_devicesGate)
            dr = _devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (dr?.Driver is not IDataBlockDirectory directory) return null;
        return await directory.ListDataBlocksAsync(ct);
    }

    private List<DeviceRuntime> DeviceSnapshot()
    {
        lock (_devicesGate)
            return _devices.ToList();
    }

    private int DeviceCount()
    {
        lock (_devicesGate)
            return _devices.Count;
    }

    private List<TagBinding> BindDevice(DeviceDef dev, IAddressParser parser, string deviceFullName,
        bool reuseHubEntries = false, List<string>? registeredTagIds = null)
    {
        var list = new List<TagBinding>();

        void AddTag(TagDef tag, string parentPath)
        {
            if (string.IsNullOrWhiteSpace(tag.Name)) return;
            string tagId = parentPath + "." + tag.Name;
            registeredTagIds?.Add(tagId);   // 设备全部点位路径(含地址解析失败的)，供增量卸载时从缓存注销
            // reuseHubEntries:已存在的条目复用句柄(句柄/订阅/最后值保留)，定义不是同一对象时就地替换
            // (DataHub.UpdateDefinition:原子发布新定义，值类型/形状变化时置等待初值)；不存在的(新增点)照常登记
            if (!reuseHubEntries || !_hub.TryGetDef(tagId, out var existingDef))
                _hub.Register(tagId, tag, deviceFullName);   // 真实设备全路径(设备名可含点号,不能靠 tagId 启发式截断)
            else if (!ReferenceEquals(existingDef, tag))
                _hub.UpdateDefinition(tagId, tag);
            TagAddress addr;
            string? err;
            bool parsed = parser is ITagDefinitionAddressParser definitionParser
                ? definitionParser.TryParse(tag, out addr, out err)
                : parser.TryParse(tag.Address, tag.DataType, out addr, out err);
            if (!parsed)
            {
                _log.LogWarning("点位 {id} 地址 [{addr}] 解析失败:{err}(不参与轮询)", tagId, tag.Address, err);
                return;
            }
            list.Add(new TagBinding { TagId = tagId, Def = tag, Address = addr });
        }

        void Recurse(IEnumerable<TagGroupDef> groups, string parentPath)
        {
            foreach (var g in groups)
            {
                string gp = parentPath + "." + g.Name;
                foreach (var t in g.Tags) AddTag(t, gp);
                Recurse(g.SubGroups, gp);
            }
        }

        foreach (var t in dev.Tags) AddTag(t, deviceFullName);
        Recurse(dev.Groups, deviceFullName);
        return list;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private RuntimeNetworkProbe? CreateNetworkProbe(ChannelDef channel, DeviceDef device, IDeviceDriver driver,
        NetworkBindingLease? ownedBinding, CancellationToken hostToken, CancellationToken deviceToken)
    {
        try
        {
            var binding = (driver as SharedOpcUaDriverLease)?.Binding ?? ownedBinding;
            var endpoint = NetworkBindingPlan.ProbeEndpoint(channel, device, binding);
            return endpoint is { } target ? new RuntimeNetworkProbe(_networkProbes!,
                () => ResolveProbeHostAsync(target.Host, hostToken), binding, deviceToken) : null;
        }
        catch (Exception) { return null; }
    }

    private Task<IPAddress?> ResolveProbeHostAsync(string host, CancellationToken ct) =>
        _networkHosts.GetOrAdd(host, name => new Lazy<Task<IPAddress?>>(async () =>
        {
            if (IPAddress.TryParse(name, out var address))
                return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
            try
            {
                var addresses = await _resolveNetworkHost(name, ct).WaitAsync(ct).ConfigureAwait(false);
                return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception) { return null; }
        })).Value;

    /// <summary>Owns one physical path's logical lease; DNS and lease refresh stay off acquisition threads.</summary>
    private sealed class RuntimeNetworkProbe : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly SemaphoreSlim _refresh = new(0, 1);
        private readonly NetworkBindingLease? _binding;
        private readonly Task _worker;
        private IPAddress? _remote;
        private ProbeState? _current;
        private int _disposed;
        private sealed record ProbeState(NetworkProbeKey Key, INetworkProbe Probe);

        public RuntimeNetworkProbe(NetworkPerformanceProbeCoordinator coordinator, Func<Task<IPAddress?>> resolve,
            NetworkBindingLease? binding, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _binding = binding;
            if (binding is not null) binding.BoundConnectionChanged += RequestRefresh;
            _worker = Task.Run(async () =>
            {
                try
                {
                    // A relay already resolved and connected its real remote socket. Direct paths use the host DNS cache.
                    if (_binding is null) _remote = await resolve().WaitAsync(_stop.Token).ConfigureAwait(false);
                    while (!_stop.IsCancellationRequested)
                    {
                        var key = CurrentKey();
                        if (Volatile.Read(ref _current)?.Key != key)
                        {
                            Interlocked.Exchange(ref _current, null)?.Probe.Dispose();
                            _stop.Token.ThrowIfCancellationRequested();
                            key = CurrentKey(); // Source may have changed again while the old echo was cancelling.
                            if (key is not null)
                                Volatile.Write(ref _current, new(key, coordinator.Acquire(key)));
                        }
                        await _refresh.WaitAsync(_stop.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { /* DNS and probe failures are absence of evidence, never collection failures. */ }
                finally { Interlocked.Exchange(ref _current, null)?.Probe.Dispose(); }
            });
        }

        private NetworkProbeKey? CurrentKey() => _binding is not null
            ? _binding.BoundConnection is { } connection
                ? new(connection.RemoteAddress.ToString(), connection.SourceAddress.ToString(), true) : null
            : _remote is not null ? new(_remote.ToString(), "", false) : null;

        private void RequestRefresh()
        {
            try { _refresh.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public Func<DateTimeOffset, NetworkProbeWindow?>? Capture()
        {
            var state = Volatile.Read(ref _current);
            var connection = _binding?.BoundConnection;
            if (state is null || state.Key != CurrentKey()) return null;
            return completedAt => ReferenceEquals(Volatile.Read(ref _current), state)
                && (_binding is null || ReferenceEquals(_binding.BoundConnection, connection))
                ? state.Probe.Window(completedAt.AddSeconds(-30), completedAt) : null;
        }

        public IReadOnlyList<NetworkProbeSample>? Snapshot()
        {
            var state = Volatile.Read(ref _current);
            if (state is null || state.Key != CurrentKey()) return null;
            var samples = state.Probe.Snapshot().Take(120).ToArray();
            return ReferenceEquals(Volatile.Read(ref _current), state) && state.Key == CurrentKey()
                ? Array.AsReadOnly(samples) : null;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _worker.ConfigureAwait(false); return; }
            if (_binding is not null) _binding.BoundConnectionChanged -= RequestRefresh;
            _stop.Cancel();
            await _worker.ConfigureAwait(false);
            _refresh.Dispose();
            _stop.Dispose();
        }
    }

    private sealed record DeviceRuntime(string Name, IDeviceDriver Driver, DeviceLoop Loop, int TagCount,
        CancellationTokenSource Cts, string Sig, IReadOnlyList<string> TagIds,
        NetworkBindingLease? PrimaryBinding, NetworkBindingLease? SecondaryBinding,
        RuntimeNetworkProbe? PrimaryNetworkProbe, RuntimeNetworkProbe? SecondaryNetworkProbe);
}
