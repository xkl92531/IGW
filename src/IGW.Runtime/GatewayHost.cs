using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.OpcUaServer;
using IGW.Northbound.Mqtt;
using IGW.Northbound.Mes;
using IGW.Licensing;
using Microsoft.Extensions.Logging;
using IGW.Core.Security;

namespace IGW.Runtime;

/// <summary>可热重载的网关宿主:持有当前 DataHub/Engine/OPC UA,支持下发新配置后整体重建。</summary>
public sealed class GatewayHost : IGatewayHost, IAsyncDisposable
{
    private readonly DriverRegistry _registry;
    private readonly ILoggerFactory _lf;
    private readonly ILogger _log;
    private readonly IProjectRepository _projectRepository;
    private readonly ProjectConfigurationCoordinator _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OpcUaPreviewSessionPool _previewSessions = new();
    private readonly INetworkAdapterCatalog _networkAdapters = new NetworkAdapterResolver();
    private OpcUaServerHost? _opcua;
    private MqttPublisher? _publisher;
    private MesUploadEngine? _mes;
    private System.Threading.Timer? _opcuaRetry;          // OPC UA 启动失败后的后台重试(60s 一次直到成功)
    private string _opcuaState = "off", _mqttState = "off", _mesState = "off", _historyState = "off";   // 各北向组件状态(off/running/failed),暴露给 /health

    public DataHub Hub { get; private set; } = null!;
    public ScanDemandRegistry ScanDemands { get; private set; } = null!;
    public RuntimeEngine Engine { get; private set; } = null!;
    public AlarmEngine Alarms { get; private set; } = null!;
    public DataLogger? Logger { get; private set; }
    public ComputedEngine Computed { get; private set; } = null!;
    public ScheduleEngine Scheduler { get; private set; } = null!;
    public IgwProject Project { get; private set; }
    public string? LastApplyTiming { get; private set; }
    private readonly List<string> _applySteps = [];
    // 批量注册调用链上下文：由 ApplyBulkTagsAsync 放入执行上下文，协调器回调的运行态应用(按设备/全量)沿同一异步流读到：
    // 回传「目标设备是否重启」，并在拿到宿主闸门后才重置分段耗时（不会清掉并发应用正在记录的分段）。
    // 并发的多个批量请求各自持有独立实例。
    private readonly AsyncLocal<BulkApplyContext?> _bulkContext = new();

    private sealed class BulkApplyContext(long precheckMs)
    {
        public bool DeviceRestarted;
        public long PrecheckMs { get; } = precheckMs;
        public bool StepsReset;
    }
    private long _projectRevisionValue;
    private string _realtimeRevision = "";
    // 1 = 按设备应用改了工程但未退役实时流：_realtimeRevision 仍是在用协调器的准入版本，不再等于当前工程的指纹
    private int _realtimeBasisStale;
    // 1 = 按设备应用中已退役实时流但重建前失败：下次按设备应用/重试时补建
    private int _realtimeStreamsPendingRestart;
    // 按设备应用中 OPC UA 子树替换失败时记下的「Hub 条目已重建」点位，重试时并入（引擎签名已不变、不会再报告它们）
    private readonly HashSet<string> _pendingOpcUaRecreated = new(StringComparer.Ordinal);
    // 局部应用中已停止但尚未重新启动的北向组件(NorthboundComponents 位)：重试或下一次局部应用补做
    private int _pendingNorthboundRestart;
    /// <summary>测试钩子：OPC UA 替换设备子树之前调用（可抛出以模拟部分失败）。</summary>
    internal Action<ChannelDef, DeviceDef>? BeforeOpcUaReplaceDevice { get; set; }
    /// <summary>测试钩子：按设备应用等待宿主闸门之前调用（此时仓储已提交）。</summary>
    internal Action? BeforeScopedApplyGate { get; set; }
    public string ProjectRevision => new ProjectRevision(Interlocked.Read(ref _projectRevisionValue)).ETag;
    public IProjectRepository ProjectRepository => _projectRepository;
    public bool ConfigurationDegraded => _configuration.Degraded;
    public string? ConfigurationDegradedMessage => _configuration.DegradedMessage;
    public long ScopedApplyFallbacks => _configuration.ScopedApplyFallbacks;
    public string RealtimeRevision => Volatile.Read(ref _realtimeRevision);
    public RealtimeStreamCoordinator? RealtimeStreams { get; private set; }
    public IReadOnlyList<DriverDescriptor> Drivers => _registry.Descriptors;
    public INetworkAdapterCatalog NetworkAdapters => _networkAdapters;
    public int OpcUaSessions => _opcua?.SessionCount ?? 0;
    public bool Reloading { get; private set; }   // 热重载窗口:REST 数据端点据此返回 503,避免命中已 Dispose 的 Hub/已停 Engine

    // ---- 授权:未授权/过期(过宽限)时只起 REST(为了能激活),不采集/不北向 ----
    private readonly LicenseManager _license;
    private System.Threading.Timer? _licTimer;   // 每 6h 复查:临期→告警;跨界到 Blocked→停;激活恢复→起
    public LicenseState License => _license.Evaluate();
    public bool Licensed => !License.Blocked;

    private readonly string _dataDir;   // 可写数据根(%ProgramData%\IGW):license/history/mes 库/pki 都落这里
    private readonly Func<string, CancellationToken, Task<string?>>? _saveLogPayload;
    private readonly SiemensAuthenticationService _siemensAuthentication;

    public async Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct)
    {
        return await WithOpcUaPreviewDriverAsync(request, async (driver, browseCt) =>
        {
            if (driver is not IBrowsableDriver browser)
                throw new NotSupportedException("OPC UA Client 驱动不支持地址空间浏览。");
            var result = await browser.BrowseAsync(browseCt);
            _log.LogInformation("OPC UA Client 全量浏览完成：发现 {count} 个变量，截断={truncated}",
                result.Tags.Count, result.Truncated);
            return result;
        }, "全量浏览地址空间", ct);
    }

    public async Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(
        BrowsePreviewRequest connection,
        string nodeId,
        IReadOnlyList<string> pathSegments,
        CancellationToken ct)
    {
        return await WithOpcUaPreviewDriverAsync(connection, async (driver, browseCt) =>
        {
            if (driver is not IHierarchicalBrowsableDriver browser)
                throw new NotSupportedException("OPC UA Client 驱动不支持按层浏览。");
            return await browser.BrowseChildrenAsync(nodeId, pathSegments, browseCt);
        }, $"按层浏览节点 {nodeId}", ct);
    }

    public async Task<BrowseResult> BrowseOpcUaBranchAsync(
        BrowsePreviewRequest connection,
        OpcUaBrowseNode selectedRoot,
        int limit,
        CancellationToken ct)
    {
        if (limit is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "单个分支导入上限必须在 1 到 100000 之间。");
        return await WithOpcUaPreviewDriverAsync(connection, async (driver, browseCt) =>
        {
            if (driver is not IHierarchicalBrowsableDriver browser)
                throw new NotSupportedException("OPC UA Client 驱动不支持所选分支浏览。");
            var result = await browser.BrowseBranchAsync(selectedRoot, limit, browseCt);
            _log.LogInformation("OPC UA Client 所选分支扫描完成：路径={path}，发现 {count} 个变量，截断={truncated}",
                string.Join('.', selectedRoot.PathSegments), result.Tags.Count, result.Truncated);
            return result;
        }, $"扫描所选分支 {string.Join('.', selectedRoot.PathSegments)}", ct);
    }

    private async Task<T> WithOpcUaPreviewDriverAsync<T>(
        BrowsePreviewRequest request,
        Func<IDeviceDriver, CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken ct)
    {
        request.Validate();
        if (!_registry.TryGet("OpcUa.Client", out var factory))
            throw new NotSupportedException("OPC UA Client 驱动未注册。");

        var channel = new ChannelDef { DriverId = "OpcUa.Client" };
        channel.Settings["url"] = request.Url.Trim();
        channel.Settings["securityPolicy"] = request.SecurityPolicy;
        channel.Settings["anonymous"] = request.Anonymous ? "true" : "false";
        channel.Settings["username"] = request.Username;
        channel.Settings["password"] = request.Password;
        channel.Settings["timeoutMs"] = request.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string upstream = IGW.Drivers.OpcUaClient.OpcUaClientDriverFactory.ResolveUrl(channel, new DeviceDef());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        string stage = "创建客户端";
        _log.LogInformation("OPC UA Client 浏览开始：远端上游 {endpoint}，安全策略 {policy}，登录 {login}",
            upstream, request.SecurityPolicy, request.Anonymous ? "匿名" : "用户名");

        // The complete connection contract (including credentials) identifies a preview session.
        // Only its digest is retained as a cache key; credentials never appear in diagnostics.
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(channel.Settings))));
        return await _previewSessions.UseAsync(key, () => factory.CreateDevice(channel, new DeviceDef()), async (driver, browseCt) =>
        {
            Action<BrowseDiagnosticEvent>? onBrowseDiagnostic = null;
            if (driver is IBrowseDiagnosticsSource diagnostics)
            {
                onBrowseDiagnostic = item =>
                {
                    string node = string.IsNullOrWhiteSpace(item.NodeId) ? "(无)" : item.NodeId;
                    const string template = "OPC UA 自动标记诊断：阶段={stage}，节点={node}，已扫描={scanned}，已发现={found}，耗时={elapsed}ms；{message}";
                    switch (item.Level)
                    {
                        case BrowseDiagnosticLevel.Error:
                            _log.LogError(template, item.Stage, node, item.ScannedNodeCount,
                                item.DiscoveredTagCount, item.ElapsedMilliseconds, item.Message);
                            break;
                        case BrowseDiagnosticLevel.Warning:
                            _log.LogWarning(template, item.Stage, node, item.ScannedNodeCount,
                                item.DiscoveredTagCount, item.ElapsedMilliseconds, item.Message);
                            break;
                        default:
                            _log.LogInformation(template, item.Stage, node, item.ScannedNodeCount,
                                item.DiscoveredTagCount, item.ElapsedMilliseconds, item.Message);
                            break;
                    }
                };
                diagnostics.BrowseDiagnostic += onBrowseDiagnostic;
            }

            try
            {
                stage = "建立会话";
                bool reused = driver.State == ConnectionState.Connected;
                if (!reused) await driver.ConnectAsync(browseCt);
                _log.LogInformation("OPC UA Client 浏览会话：远端上游 {endpoint}；复用={reused}；排队及建连={elapsed}ms",
                    upstream, reused, elapsed.ElapsedMilliseconds);
                if (driver is IRemoteEndpointDiagnostics endpointDiagnostics)
                {
                    var endpoint = endpointDiagnostics.RemoteEndpoint();
                    _log.LogInformation(
                        "OPC UA Client 已连接远端上游 {endpoint}，服务器发现={discovered}，SDK 最终会话={session}，耗时 {elapsed}ms",
                        upstream,
                        endpoint.DiscoveredEndpoint ?? "(未返回)",
                        endpoint.SessionEndpoint ?? "(未建立)",
                        elapsed.ElapsedMilliseconds);
                }
                else
                    _log.LogInformation("OPC UA Client 已连接远端上游 {endpoint}，耗时 {elapsed}ms", upstream, elapsed.ElapsedMilliseconds);
                stage = operationName;
                var result = await operation(driver, browseCt);
                _log.LogInformation("OPC UA Client 操作完成：远端上游 {endpoint}，操作={operation}，总耗时 {elapsed}ms",
                    upstream, operationName, elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                string diagnostic = ex.ToString();
                if (!string.IsNullOrEmpty(request.Password))
                    diagnostic = diagnostic.Replace(request.Password, "***", StringComparison.Ordinal);
                _log.LogError("OPC UA Client 浏览失败：远端上游 {endpoint}，阶段={stage}，耗时 {elapsed}ms，异常={diagnostic}",
                    upstream, stage, elapsed.ElapsedMilliseconds, diagnostic);
                throw;
            }
            finally
            {
                if (driver is IBrowseDiagnosticsSource diagnosticsSource && onBrowseDiagnostic is not null)
                    diagnosticsSource.BrowseDiagnostic -= onBrowseDiagnostic;
            }
        }, ct);
    }

    public async Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Engine is null) throw new InvalidOperationException("Runtime 尚未启动，无法重连通道。");
            int restarted = await Engine.ReconnectChannelAsync(channelName, ct);
            return new { channel = channelName, restarted };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>只保存日志保留策略，不重启 PLC、MES 或北向服务。</summary>
    public Task UpdateLogRetentionDaysAsync(int days, CancellationToken ct) =>
        UpdateRuntimeLogSettingsAsync(days, Project.Settings.LogStorageDirectory, ct);

    public async Task UpdateRuntimeLogSettingsAsync(int days, string directory, CancellationToken ct)
    {
        if (days is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days), "日志保留天数必须在 1 到 3650 天之间。");
        var updated = ProjectStore.Clone(Project);
        updated.Settings.LogRetentionDays = days;
        updated.Settings.LogStorageDirectory = directory;
        ProjectApplyResult applied = await _configuration.ApplySnapshotAsync(
            updated, CurrentStorageRevision, ProjectApplyScope.SettingsOnly, ct).ConfigureAwait(false);
        UpdateStorageRevision(applied);
        if (!applied.RuntimeApplied)
            throw new InvalidOperationException(applied.ErrorMessage ?? "日志保留策略应用失败。");
        _log.LogInformation("日志设置已更新：保留 {days} 天；目录={directory}；PLC、实时流与北向服务均未重启", days,
            string.IsNullOrWhiteSpace(directory) ? "默认目录" : directory);
    }

    public GatewayHost(
        IgwProject project,
        IProjectRepository projectRepository,
        ProjectRevision projectRevision,
        DriverRegistry registry,
        ILoggerFactory lf,
        string? dataDir = null,
        IDeviceCredentialStore? deviceCredentials = null,
        Func<string, CancellationToken, Task<string?>>? saveLogPayload = null)
    {
        ProjectMemoryCompactor.Compact(project);   // 大工程里重复的点名/元数据共用同一份字符串
        Project = project;
        _projectRepository = projectRepository ?? throw new ArgumentNullException(nameof(projectRepository));
        _projectRevisionValue = projectRevision.Value;
        SetRealtimeRevision(RealtimeProjectRevision.Compute(project));
        _registry = registry;
        _lf = lf;
        _log = lf.CreateLogger("GatewayHost");
        _dataDir = dataDir ?? IGW.Abstractions.IgwPaths.DataDir;
        _saveLogPayload = saveLogPayload;
        _siemensAuthentication = new SiemensAuthenticationService(
            () => Project,
            ReloadAsync,
            deviceCredentials ?? new DeviceCredentialStore(Path.Combine(_dataDir, "secrets")));
        _configuration = new ProjectConfigurationCoordinator(
            _projectRepository,
            project,
            ApplyPersistedRuntimeAsync,
            applyPatch: ApplyPatchedRuntimeAsync);
        _license = new LicenseManager(_dataDir);
        // 授权码限定的驱动：未授权的驱动不能新建通道、运行时不采集(Evaluate 有缓存，频繁调用无妨)
        _registry.Allows = driverId => _license.Evaluate().AllowsDriver(driverId);
        _appliedEntitlements = EntitlementKey(_license.Evaluate());
        // 授权复查(每 10min,Evaluate 有 15s 缓存开销可忽略):跨过到期/宽限边界 10 分钟内起停,不再等半天
        _licTimer = new System.Threading.Timer(async _ => await LicenseRecheckAsync(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        // 证书临期巡检(每 24h):自签证书若在运行中过期,加密客户端断线后将无法重连——提前 30 天开始报错提醒
        _certTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var exp = _opcua?.CertExpiry;
                if (exp != null && exp.Value < DateTime.Now.AddDays(30))
                    _log.LogError("OPC UA 自签证书将于 {d:yyyy-MM-dd} 过期:请择机删除 {p} 目录并重启网关自动重建(过期后加密客户端断线将无法重连)",
                        exp.Value, System.IO.Path.Combine(_dataDir, "pki", "own"));
            }
            catch { }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
    }

    public async Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct) =>
        await _siemensAuthentication.GetStatusAsync(deviceName, ct);

    public async Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct) =>
        await _siemensAuthentication.VerifyAndSaveAsync(deviceName, username, password, ct);

    public Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct) =>
        _siemensAuthentication.DeleteAsync(deviceName, ct);

    private readonly System.Threading.Timer _certTimer;

    public async Task StartAsync()
    {
        var lic = _license.Evaluate();
        if (lic.Blocked)
        {
            // 未授权/过期(过宽限)/机器不符/时间回拨:不采集、不北向;只留 REST 便于激活。
            _log.LogError("授权未通过({s}):{m} 采集与北向已停止,请在配置工具/托盘「授权」激活。本机机器码 {mid}",
                lic.Status, lic.Message, lic.MachineId);
            _opcuaState = _mqttState = _mesState = _historyState = "off";
            return;
        }
        if (lic.NeedsAttention)   // 临期/宽限:仍全量启动,但持续告警
            _log.LogWarning("授权提醒:{m}", lic.Message);
        var total = System.Diagnostics.Stopwatch.StartNew();
        lock (_applySteps) _applySteps.Clear();
        await TimedAsync("启动 PLC 采集", StartSouthAsync);
        await StartNorthboundTimedAsync(NorthboundComponents.All);
        string[] steps;
        lock (_applySteps) steps = [.. _applySteps];
        _log.LogInformation("运行时启动耗时：总计 {total}ms；{steps}", total.ElapsedMilliseconds, string.Join("；", steps));
        ScheduleHeapCompaction("启动完成");
    }

    private int _heapCompactionScheduled;
    /// <summary>
    /// 载入/整体应用百万点工程会留下大量大对象堆碎片(默认 GC 不压缩大对象堆)，
    /// 在这些本来就在重建运行时的时刻之后，于后台做一次压缩回收；局部保存不会触发。
    /// </summary>
    private void ScheduleHeapCompaction(string reason)
    {
        if (Interlocked.Exchange(ref _heapCompactionScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                long fragmented = GC.GetGCMemoryInfo().FragmentedBytes;
                if (fragmented < 256L * 1024 * 1024) return;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                _log.LogInformation("{reason}后压缩内存：回收碎片 {mb} MB，暂停 {ms} ms", reason,
                    (fragmented - GC.GetGCMemoryInfo().FragmentedBytes) / 1048576, watch.ElapsedMilliseconds);
            }
            catch (Exception ex) { _log.LogDebug(ex, "内存压缩失败"); }
            finally { Volatile.Write(ref _heapCompactionScheduled, 0); }
        });
    }

    // 南向:数据中枢 + 采集引擎(设备连接/轮询/缓存)。热重载时南向不变则整段不碰。
    private async Task StartSouthAsync()
    {
        EnsureThreadPoolFor(Project.Channels.Sum(c => c.Devices.Count));
        Hub = new DataHub();
        ScanDemands = new ScanDemandRegistry(Hub);
        Engine = new RuntimeEngine(Project, _registry, Hub, _lf, ScanDemands, _networkAdapters);
        Engine.DeviceStatusChanged += OnDeviceStatusChanged;
        await Engine.StartAsync();
        StartRealtimeStreams();
    }

    private void StartRealtimeStreams()
    {
        if (Hub is null || RealtimeStreams is not null) return;
        RealtimeStreams = new RealtimeStreamCoordinator(
            Hub,
            RealtimeRevision,
            Project.Settings.TagStream,
            _lf.CreateLogger<RealtimeStreamCoordinator>());
        Volatile.Write(ref _realtimeStreamsPendingRestart, 0);
        foreach (var status in Engine.DeviceStatuses()) RealtimeStreams.PublishDeviceState(status);
        PublishMesRuntimeSnapshot();
    }

    /// <summary>
    /// 设置实时流代际版本（=当前协调器准入用的版本号）——对 <see cref="_realtimeRevision"/> 的唯一写入口。
    /// basisStale=false：版本就是当前工程的指纹，清除「基准滞后」标记；
    /// basisStale=true：版本只是准入令牌（局部应用用序号版本，避免 O(N) 指纹），比较基准需按当前工程重算。
    /// </summary>
    private void SetRealtimeRevision(string revision, bool basisStale = false)
    {
        Volatile.Write(ref _realtimeRevision, revision);
        Volatile.Write(ref _realtimeBasisStale, basisStale ? 1 : 0);
    }

    /// <summary>
    /// 判断实时点表是否变化时的「上一版」指纹。按设备应用未退役实时流时 <see cref="_realtimeRevision"/>
    /// 仍是应用前工程的版本（在用协调器靠它准入），此时按当前工程重算一次作为比较基准，避免下一次保存误判变化而断开全部流。
    /// </summary>
    private string RealtimeBasis() =>
        Volatile.Read(ref _realtimeBasisStale) != 0 ? RealtimeProjectRevision.Compute(Project) : RealtimeRevision;

    /// <summary>
    /// 应用后推进实时流版本：已退役（或基准不滞后）则切到新版本；
    /// 基准滞后且未退役时保留在用协调器的版本号（否则新客户端会因版本不符被拒），标记继续保持。
    /// </summary>
    private void AdvanceRealtimeRevision(string nextRealtimeRevision, bool retired)
    {
        if (retired || Volatile.Read(ref _realtimeBasisStale) == 0) SetRealtimeRevision(nextRealtimeRevision);
    }

    private void OnDeviceStatusChanged(DeviceStatus status) => RealtimeStreams?.PublishDeviceState(status);

    private void OnMesStateChanged(MesTaskStatus status) => RealtimeStreams?.PublishMesState(status);

    private void OnMesRuntimeEvent(string taskId, string taskName, MesFlowEvent evt) =>
        RealtimeStreams?.PublishMesRuntimeEvent(taskId, taskName, evt);

    private void PublishMesRuntimeSnapshot()
    {
        var streams = RealtimeStreams;
        if (streams is null) return;
        streams.PublishMesAvailability(_mes is not null);
        foreach (var status in _mes?.Snapshot() ?? []) streams.PublishMesState(status);
    }

    public async Task UpdateMesUploadAsync(MesUploadSettings mesUpload, CancellationToken ct)
    {
        ProjectUpdateResult result = await ApplyMesUploadAsync(mesUpload, expectedRevision: null, ct);
        if (!result.Applied) throw new InvalidOperationException(result.Error ?? "MES 配置应用失败。");
    }

    public async Task<ProjectUpdateResult> ApplyMesUploadAsync(
        MesUploadSettings mesUpload, string? expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mesUpload);
        if (RevisionChanged(expectedRevision))
            return ProjectUpdateResult.RevisionConflict(ProjectRevision);
        var updated = ProjectStore.WithMesUpload(Project, mesUpload);
        MesTaskTopology.Reconcile(updated);
        ProjectApplyResult applied = await _configuration.ApplySnapshotAsync(
            updated, CurrentStorageRevision, ProjectApplyScope.MesOnly, ct).ConfigureAwait(false);
        UpdateStorageRevision(applied);
        return ToProjectUpdateResult(applied);
    }

    private bool RevisionChanged(string? expectedRevision) =>
        !string.IsNullOrEmpty(expectedRevision)
        && !string.Equals(expectedRevision, ProjectRevision, StringComparison.Ordinal);

    private ProjectRevision CurrentStorageRevision => new(Interlocked.Read(ref _projectRevisionValue));

    private void UpdateStorageRevision(ProjectApplyResult result)
    {
        if (result.Persisted || result.Conflict)
            Interlocked.Exchange(ref _projectRevisionValue, result.Revision.Value);
    }

    private ProjectUpdateResult ToProjectUpdateResult(ProjectApplyResult result)
    {
        if (result.Conflict) return ProjectUpdateResult.RevisionConflict(ProjectRevision);
        return result.RuntimeApplied
            ? ProjectUpdateResult.Success(ProjectRevision)
            : new ProjectUpdateResult(false, false, ProjectRevision,
                result.ErrorCode ?? result.ErrorMessage, Detail: result.ErrorMessage);
    }

    private async Task RetireRealtimeStreamsAsync(string nextProjectRevision)
    {
        var current = RealtimeStreams;
        RealtimeStreams = null;
        if (current is null) return;
        await current.RetireGenerationAsync(nextProjectRevision, CancellationToken.None);
        await current.DisposeAsync();
    }

    private async Task DisposeRealtimeStreamsAsync()
    {
        var current = RealtimeStreams;
        RealtimeStreams = null;
        if (current is not null) await current.DisposeAsync();
    }

    // 驱动是同步 socket IO:每设备循环会阶段性占死一条线程池线程。按设备数预热 MinThreads,
    // 避免大量设备同时重连时线程池饥饿(注入约 1 条/0.5s)拖垮 REST/OPC UA/MES 的 async 续体。
    private void EnsureThreadPoolFor(int devices)
    {
        ThreadPool.GetMinThreads(out int w, out int io);
        int want = Math.Clamp(devices + 16, w, 512);
        if (want > w && ThreadPool.SetMinThreads(want, io))
            _log.LogInformation("线程池 MinThreads {old} → {n}(设备 {d} 台)", w, want, devices);
    }

    // 北向 + 派生:报警/计算/计划/历史/OPC UA/MQTT/MES(都订阅 Hub,可独立于设备重建)
    /// <summary>按组件分别计时启动北向，便于看出是 OPC UA 服务还是 MES 引擎慢。</summary>
    private async Task StartNorthboundTimedAsync(NorthboundComponents components)
    {
        var others = components & ~(NorthboundComponents.OpcUa | NorthboundComponents.Mes);
        if (others != NorthboundComponents.None)
            await TimedAsync("启动报警/计算/计划/历史/MQTT", () => StartNorthboundAsync(others)).ConfigureAwait(false);
        if (components.HasFlag(NorthboundComponents.OpcUa))
            await TimedAsync("启动 OPC UA 服务", () => StartNorthboundAsync(NorthboundComponents.OpcUa)).ConfigureAwait(false);
        if (components.HasFlag(NorthboundComponents.Mes))
            await TimedAsync("启动 MES 引擎", () => StartNorthboundAsync(NorthboundComponents.Mes)).ConfigureAwait(false);
    }

    /// <summary>授权码是否包含这个功能模块(见 LicenseCatalog)。</summary>
    public bool FeatureLicensed(string featureId) => _license.Evaluate().AllowsFeature(featureId);

    private async Task StartNorthboundAsync(NorthboundComponents components = NorthboundComponents.All)
    {
        if (components.HasFlag(NorthboundComponents.Alarms)) Alarms = new AlarmEngine(Hub);
        if (components.HasFlag(NorthboundComponents.Computed)) Computed = new ComputedEngine(Hub, Project.ComputedTags, _lf.CreateLogger("Computed"));
        if (components.HasFlag(NorthboundComponents.Scheduler)) Scheduler = new ScheduleEngine(Engine, Hub, Project.Schedules, _lf.CreateLogger("Scheduler"));
        if (components.HasFlag(NorthboundComponents.History))
        {
            if (Project.History?.Enabled == true && !FeatureLicensed(LicenseCatalog.History))
            {
                _historyState = "unlicensed";
                _log.LogWarning("历史存储未授权,跳过启动");
            }
            else if (Project.History?.Enabled == true)
            {
                try { Logger = new DataLogger(Hub, Project.History, _dataDir, _lf.CreateLogger("DataLogger")); _historyState = "running"; }
                catch (Exception ex) { _log.LogError(ex, "数据记录启动失败(其余功能仍可用)"); Logger = null; _historyState = "failed"; }
            }
            else _historyState = "off";
        }

        if (components.HasFlag(NorthboundComponents.OpcUa))
        {
            if (Project.OpcUa?.Enabled ?? true) await TryStartOpcUaAsync(retry: false);
            else
            {
                _opcuaState = "off";
                _log.LogInformation("OPC UA Server 已在工程中禁用,跳过启动");
            }
        }

        if (components.HasFlag(NorthboundComponents.Mqtt))
        {
            if (Project.MqttPublish?.Enabled == true && !FeatureLicensed(LicenseCatalog.MqttPublish))
            {
                _mqttState = "unlicensed";
                _log.LogWarning("MQTT 发布未授权,跳过启动");
            }
            else if (Project.MqttPublish?.Enabled == true)
            {
                try { _publisher = new MqttPublisher(Hub, Project.MqttPublish, _lf.CreateLogger("MqttPublish")); _mqttState = "running"; }
                catch (Exception ex) { _log.LogError(ex, "IoT 发布启动失败(其余功能仍可用)"); _publisher = null; _mqttState = "failed"; }
            }
            else _mqttState = "off";
        }

        // MES 没有第二套“流程总闸门”：数据块对应任务的 Enabled 就是唯一运行开关。
        // 只要任一数据块开启就创建引擎；全部关闭时才停止。旧 Enabled 字段仅保留
        // JSON 兼容，不再拦截已开启的单独任务。
        if (components.HasFlag(NorthboundComponents.Mes)) await StartMesAsync().ConfigureAwait(false);
    }

    private Task StartMesAsync()
    {
        if (MesRuntimePolicy.ShouldRun(Project.MesUpload) && !FeatureLicensed(LicenseCatalog.Mes))
        {
            _log.LogWarning("MES 采集上报未授权,跳过启动");
            _mesState = "unlicensed";
            PublishMesRuntimeSnapshot();
        }
        else if (MesRuntimePolicy.ShouldRun(Project.MesUpload))
        {
            try
            {
                _mes = new MesUploadEngine(Hub, Project.MesUpload,
                    (id, v, policy, ct) => Engine.WriteAsync(id, v, ct, policy),
                    _lf.CreateLogger("MesUpload"), _dataDir,
                    MesDataRangeCatalog.BuildForTasks(Project), Engine.DeviceState, ScanDemands,
                    saveLogPayload: _saveLogPayload,
                    readNow: Engine.ReadNowAsync);
                _mes.StateChanged += OnMesStateChanged;
                _mes.RuntimeEventRaised += OnMesRuntimeEvent;
                _mesState = "running";
                PublishMesRuntimeSnapshot();
            }
            catch (Exception ex) { _log.LogError(ex, "MES 采集上报启动失败(其余功能仍可用)"); _mes = null; _mesState = "failed"; }
        }
        else
        {
            _mesState = "off";
            PublishMesRuntimeSnapshot();
        }
        return Task.CompletedTask;
    }

    private async Task StopMesAsync()
    {
        var mes = _mes;
        _mes = null;
        _mesState = "off";
        RealtimeStreams?.ClearMesRuntimeStates();
        RealtimeStreams?.PublishMesAvailability(false);
        if (mes is null) return;
        mes.StateChanged -= OnMesStateChanged;
        mes.RuntimeEventRaised -= OnMesRuntimeEvent;
        // MesUploadEngine 自身已分别为执行循环和日志停止设置有界超时；这里必须等它
        // 完成资源释放后再启动新引擎，避免旧、新流程短暂并行而重复上报或重复回写 PLC。
        try { await mes.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // DisposeAsync 在 finally 中释放扫描需求和 HTTP 资源；日志关闭异常不能让
            // 已落盘的新配置停在“旧引擎已停、宿主工程仍是旧版本”的半应用状态。
            _log.LogError(ex, "旧 MES 引擎停止时发生异常，资源已释放，继续启用新配置");
        }
    }

    // 启动 OPC UA;失败不再静默永久下线,而是标记 failed 并安排 60s 后台重试(直到成功或被重载/停机取消)
    private async Task TryStartOpcUaAsync(bool retry)
    {
        if (!FeatureLicensed(LicenseCatalog.OpcUaServer))
        {
            _opcuaState = "unlicensed";
            _log.LogWarning("OPC UA 服务未授权,跳过启动");
            return;
        }
        _pendingOpcUaRecreated.Clear();   // 新服务按当前工程与 Hub 全量建节点，没有需要改绑的旧订阅
        _opcua = new OpcUaServerHost();
        try
        {
            await _opcua.StartAsync(Project, Hub,
                async (id, v) => (await Engine.WriteAsync(id, v, CancellationToken.None)).IsGood,
                (id, slice, value) => Engine.WriteArrayAsync(id, slice, value, CancellationToken.None),
                _log,
                System.IO.Path.Combine(_dataDir, "pki"));
            _opcuaState = "running";
            _opcuaRetry?.Dispose(); _opcuaRetry = null;
            if (retry) _log.LogInformation("OPC UA 后台重试启动成功");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OPC UA 启动失败(其余功能仍可用),60 秒后后台重试");
            try { await _opcua.DisposeAsync(); } catch { }
            _opcua = null; _opcuaState = "failed";
            if (_opcuaRetry == null)
                _opcuaRetry = new System.Threading.Timer(async _ =>
                {
                    try
                    {
                        if (_opcua != null || !(Project.OpcUa?.Enabled ?? true)) return;
                        if (!await _gate.WaitAsync(0)) return;   // 正在重载就跳过本次
                        try { if (_opcua == null) await TryStartOpcUaAsync(retry: true); }
                        finally { _gate.Release(); }
                    }
                    catch { /* 重试自身异常不外抛(Timer 回调),下次再试 */ }
                }, null, 60_000, 60_000);
        }
    }

    // 停北向(不动 Hub/Engine/设备)。全部置空 → 回退路径二次调用是安全的 no-op(否则二次 Dispose 会把回退炸死)
    private async Task StopNorthboundAsync(NorthboundComponents components = NorthboundComponents.All)
    {
        if (components.HasFlag(NorthboundComponents.OpcUa)) { _opcuaRetry?.Dispose(); _opcuaRetry = null; }
        // 先摘除引用并同时发出停止，避免某个外部 I/O 卡住后其它组件连取消信号都收不到。
        var opcua = components.HasFlag(NorthboundComponents.OpcUa) ? _opcua : null;
        var publisher = components.HasFlag(NorthboundComponents.Mqtt) ? _publisher : null;
        var mes = components.HasFlag(NorthboundComponents.Mes) ? _mes : null;
        var logger = components.HasFlag(NorthboundComponents.History) ? Logger : null;
        var scheduler = components.HasFlag(NorthboundComponents.Scheduler) ? Scheduler : null;
        if (components.HasFlag(NorthboundComponents.OpcUa)) { _opcua = null; _opcuaState = "off"; }
        if (components.HasFlag(NorthboundComponents.Mqtt)) { _publisher = null; _mqttState = "off"; }
        if (components.HasFlag(NorthboundComponents.History)) { Logger = null; _historyState = "off"; }
        if (components.HasFlag(NorthboundComponents.Scheduler)) Scheduler = null!;
        if (components.HasFlag(NorthboundComponents.Computed)) { Computed?.Dispose(); Computed = null!; }
        if (components.HasFlag(NorthboundComponents.Alarms)) { Alarms?.Dispose(); Alarms = null!; }
        if (components.HasFlag(NorthboundComponents.Mes))
        {
            _mes = null; _mesState = "off";
            RealtimeStreams?.ClearMesRuntimeStates();
            RealtimeStreams?.PublishMesAvailability(false);
            if (mes is not null) { mes.StateChanged -= OnMesStateChanged; mes.RuntimeEventRaised -= OnMesRuntimeEvent; }
        }

        var stops = new List<Task>(5);
        if (opcua != null) stops.Add(opcua.DisposeAsync().AsTask());
        if (publisher != null) stops.Add(publisher.DisposeAsync().AsTask());
        if (mes != null) stops.Add(mes.DisposeAsync().AsTask());
        if (logger != null) stops.Add(logger.DisposeAsync().AsTask());
        if (scheduler != null) stops.Add(scheduler.DisposeAsync().AsTask());
        if (stops.Count == 0) return;

        var allStops = Task.WhenAll(stops);
        try { await allStops.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            // 保存已在调用本方法前原子落盘。旧组件都已收到取消，不再让异常驱动/HTTP 阻塞配置保存。
            _log.LogError("北向组件停止超过 8 秒，配置已保存；继续重建运行组件");
            _ = allStops.ContinueWith(t =>
            {
                if (t.Exception != null) _log.LogError(t.Exception, "超时后的北向组件停止仍有异常");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public async Task ReloadAsync(IgwProject newProject)
    {
        ProjectUpdateResult result = await ApplyProjectAsync(
            newProject, expectedRevision: null, CancellationToken.None);
        if (!result.Applied) throw new InvalidOperationException(result.Error ?? "配置应用失败。");
    }

    /// <summary>选择外部主数据库时的同步状态；SQLite 时为 null。</summary>
    public IDatabaseSync? DatabaseSync { get; set; }

    /// <summary>主库断开等情况下暂停修改配置(返回给用户看的原因)。</summary>
    private ProjectUpdateResult? ConfigurationWriteBlocked() =>
        DatabaseSync?.ConfigurationWriteBlockedReason is { } reason
            ? new ProjectUpdateResult(false, false, ProjectRevision, reason)
            : null;

    private ProjectUpdateResult AfterConfigurationSaved(ProjectUpdateResult result)
    {
        if (result.Applied) DatabaseSync?.Nudge();   // 保存成功后立即同步到主库，不等下一个周期
        return result;
    }

    public async Task<ProjectUpdateResult> ApplyProjectAsync(
        IgwProject newProject, string? expectedRevision, CancellationToken ct)
    {
        if (ConfigurationWriteBlocked() is { } blocked) return blocked;
        if (DriverLicenseGuard.CheckSnapshot(Project, newProject, _registry.Allows) is { } unlicensed)
            return new ProjectUpdateResult(false, false, ProjectRevision, unlicensed);
        MesTaskTopology.Reconcile(newProject);
        if (RevisionChanged(expectedRevision))
            return ProjectUpdateResult.RevisionConflict(ProjectRevision);

        var total = System.Diagnostics.Stopwatch.StartNew();
        lock (_applySteps) _applySteps.Clear();
        ProjectApplyResult applied = await _configuration.ApplySnapshotAsync(
            newProject, CurrentStorageRevision, ProjectApplyScope.Full, ct).ConfigureAwait(false);
        UpdateStorageRevision(applied);
        PublishApplyTiming(total.ElapsedMilliseconds);
        return AfterConfigurationSaved(ToProjectUpdateResult(applied));
    }

    public async Task<ProjectUpdateResult> ApplyProjectMutationAsync(
        ProjectMutation mutation,
        string expectedRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (ConfigurationWriteBlocked() is { } blocked) return blocked;
        if (DriverLicenseGuard.CheckMutation(Project, mutation, _registry.Allows) is { } unlicensed)
            return new ProjectUpdateResult(false, false, ProjectRevision, unlicensed);
        if (!IGW.Abstractions.ProjectRevision.TryParse(expectedRevision, out ProjectRevision expected)
            || expected != CurrentStorageRevision)
            return ProjectUpdateResult.RevisionConflict(ProjectRevision);

        ProjectApplyScope scope = mutation.Kind is ProjectMutationKind.ReplaceMesTask or ProjectMutationKind.SaveMesTasks
            ? ProjectApplyScope.MesOnly
            : ProjectApplyScope.Full;
        var total = System.Diagnostics.Stopwatch.StartNew();
        lock (_applySteps) _applySteps.Clear();
        ProjectApplyResult applied = await _configuration.ExecuteAsync(
            mutation, expected, scope, ct).ConfigureAwait(false);
        UpdateStorageRevision(applied);
        PublishApplyTiming(total.ElapsedMilliseconds);
        ProjectUpdateResult result = AfterConfigurationSaved(ToProjectUpdateResult(applied));
        return scope == ProjectApplyScope.Full ? await ReconcileMesTopologyAsync(result, ct).ConfigureAwait(false) : result;
    }

    /// <summary>
    /// 与整包保存(PUT /api/v1/project)一致：局部修改删掉或挪走了 MES 任务绑定的组/数据范围后，
    /// 清理失效的任务与上传内容(<see cref="MesTaskTopology"/>)，并只保存 MES 配置。
    /// 没有需要清理的内容时不做任何写入(常见情况)。
    /// </summary>
    private async Task<ProjectUpdateResult> ReconcileMesTopologyAsync(ProjectUpdateResult result, CancellationToken ct)
    {
        if (!result.Applied || Project.MesUpload?.Tasks is not { Count: > 0 }) return result;
        var mes = System.Text.Json.JsonSerializer.Deserialize<MesUploadSettings>(
            System.Text.Json.JsonSerializer.Serialize(Project.MesUpload, ProjectStore.Options), ProjectStore.Options)!;
        IgwProject probe = ProjectStore.WithMesUpload(Project, mes);
        MesTaskTopologyResult outcome = MesTaskTopology.Reconcile(probe);
        if (outcome.RemovedTasks == 0 && outcome.ClearedPayloads == 0 && outcome.ClearedRanges == 0) return result;
        _log.LogInformation("局部保存后清理 MES 任务：删除 {removed} 个失效任务，清理 {payloads} 个上传内容/{ranges} 个数据范围",
            outcome.RemovedTasks, outcome.ClearedPayloads, outcome.ClearedRanges);
        ProjectUpdateResult reconciled = AfterConfigurationSaved(
            await ApplyMesUploadAsync(probe.MesUpload, expectedRevision: null, ct).ConfigureAwait(false));
        return reconciled with { MesTasksReconciled = reconciled.Applied };
    }

    public async Task<ProjectRevision> ApplyImportedProjectAsync(
        IgwProject newProject,
        ProjectRevision expected,
        CancellationToken ct)
    {
        ProjectUpdateResult result = await ApplyProjectAsync(newProject, expected.ETag, ct).ConfigureAwait(false);
        if (result.Conflict) throw new ProjectRevisionConflictException(CurrentStorageRevision);
        if (!result.Applied) throw new InvalidOperationException(result.Error ?? "project_import_runtime_apply_failed");
        return CurrentStorageRevision;
    }

    /// <summary>记一个应用步骤的耗时；保存数据库 = 总耗时减去各运行步骤之和。</summary>
    private void RecordApplyStep(string step, long milliseconds)
    {
        lock (_applySteps) _applySteps.Add($"{step} {milliseconds}ms");
    }

    private async Task TimedAsync(string step, Func<Task> work)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { await work().ConfigureAwait(false); }
        finally { RecordApplyStep(step, watch.ElapsedMilliseconds); }
    }

    private string PublishApplyTiming(long totalMs)
    {
        string[] steps;
        lock (_applySteps) steps = [.. _applySteps];
        long runtime = steps.Sum(step => long.TryParse(step[(step.LastIndexOf(' ') + 1)..^2], out long ms) ? ms : 0);
        LastApplyTiming = $"总计 {totalMs}ms：保存数据库 {Math.Max(0, totalMs - runtime)}ms" +
            (steps.Length > 0 ? "；" + string.Join("；", steps) : "");
        _log.LogInformation("工程保存耗时：{timing}", LastApplyTiming);
        return LastApplyTiming!;
    }

    private async Task ApplyPersistedRuntimeAsync(
        IgwProject newProject,
        ProjectApplyScope scope,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 新工程交给采集/OPC UA 等组件之前压缩重复内容(此时还没有其他线程读取它)。
            if (scope != ProjectApplyScope.MesOnly && scope != ProjectApplyScope.SettingsOnly)
                ProjectMemoryCompactor.Compact(newProject);
            if (scope == ProjectApplyScope.MesOnly)
            {
                await ApplyMesRuntimeAsync(newProject).ConfigureAwait(false);
                return;
            }

            if (scope == ProjectApplyScope.SettingsOnly)
            {
                string nextRealtimeRevision = RealtimeProjectRevision.Compute(newProject);
                bool realtimeChanged = !string.Equals(RealtimeBasis(), nextRealtimeRevision, StringComparison.Ordinal);
                if (realtimeChanged)
                    await RetireRealtimeStreamsAsync(nextRealtimeRevision).ConfigureAwait(false);
                Project = newProject;
                AdvanceRealtimeRevision(nextRealtimeRevision, realtimeChanged);
                StartRealtimeStreams();
                return;
            }

            ResetBulkApplySteps();
            await ApplyFullRuntimeAsync(newProject).ConfigureAwait(false);
            // 批量注册在降级态下由协调器改走全量重载：目标设备随全量应用一并重建
            if (_bulkContext.Value is { } bulk) bulk.DeviceRestarted = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量注册点位：修订号前置检查 → 西门子地址模式混用预检（不动数据库）→ 协调器单事务写库并只读回该设备 →
    /// 按设备应用运行态（<see cref="ApplyScopedRuntimeAsync"/>）。返回写库统计、目标设备是否重启与分段耗时。
    /// </summary>
    public async Task<ProjectBulkTagResponse> ApplyBulkTagsAsync(
        ProjectBulkTagRequest request,
        string? expectedRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 纵深防御：接口层已校验，这里再挡一次（名称规则关系到点位 id 拼接，坏数据绝不能进库）
        string? tooMany = ProjectBulkTagValidator.CheckLimits(request);
        if (tooMany is not null) return BulkRejected(conflict: false, "too_many_items", tooMany);
        IReadOnlyList<ProjectBulkValidationError> invalid = ProjectBulkTagValidator.Validate(request);
        if (invalid.Count > 0)
            return BulkRejected(conflict: false, "invalid_items", string.Join("；", invalid.Take(5).Select(e =>
                e.Index < 0 ? e.Reason : $"第 {e.Index} 项：{e.Reason}")) + (invalid.Count > 5 ? $"（共 {invalid.Count} 处错误）" : ""));
        // 批量注册会按名字自动新建通道：新通道的驱动必须在授权里
        if (request.Channel is { } channel && !_registry.Allows(channel.DriverId)
            && !Project.Channels.Any(item => string.Equals(item.Name, channel.Name, StringComparison.OrdinalIgnoreCase)))
            return BulkRejected(conflict: false, "unlicensed_driver",
                $"驱动 {channel.DriverId} 未授权，不能新建通道 {channel.Name}。请联系供应商更换包含该驱动的授权码。");
        // 外部主数据库断开(或主库已被别处修改)时与其它配置保存一样暂停写入：批量写入的行同样要复制到主库
        if (DatabaseSync?.ConfigurationWriteBlockedReason is { } writeBlocked)
            return BulkRejected(conflict: false, "database_unavailable", writeBlocked) with { Retryable = true };
        ProjectRevision? expected = null;
        if (!string.IsNullOrEmpty(expectedRevision))
        {
            if (!IGW.Abstractions.ProjectRevision.TryParse(expectedRevision, out ProjectRevision parsed)
                || parsed != CurrentStorageRevision)
                return BulkRejected(conflict: true, "revision_conflict", "工程已被其他操作修改，请刷新后重试。");
            expected = parsed;
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var precheck = System.Diagnostics.Stopwatch.StartNew();
        (string Code, string Message)? rejected = PrecheckCandidate(request);
        if (rejected is { } reject)
            return BulkRejected(conflict: false, reject.Code, reject.Message);

        var context = new BulkApplyContext(precheck.ElapsedMilliseconds);
        _bulkContext.Value = context;
        ProjectBulkApplyResult result;
        try
        {
            result = await _configuration.ExecuteBulkAsync(request, expected, ct).ConfigureAwait(false);
        }
        finally
        {
            _bulkContext.Value = null;
        }

        ProjectBulkTagResult persisted = result.Persisted;
        if (persisted.Applied || persisted.Conflict)
            Interlocked.Exchange(ref _projectRevisionValue, persisted.Revision.Value);
        // 已写入本机 SQLite(触发器已把增删改记入待同步队列)：立即推到主库，运行态应用失败也不影响复制
        if (persisted.Applied) DatabaseSync?.Nudge();
        // 分段计入步骤：不这样做的话，写库/读回设备/构造投影会全部落进「保存数据库」
        // 那个差值里，现场只看得到一个上百秒的笼统数字，定位不到慢在哪一段。
        RecordApplyStep("写库", result.WriteMs);
        RecordApplyStep("读回设备", result.LoadMs);
        RecordApplyStep("构造投影", result.ProjectMs);
        string timing = persisted.Applied ? PublishApplyTiming(total.ElapsedMilliseconds) : "";
        bool applied = persisted.Applied && result.RuntimeApplied;
        if (persisted.Applied && !result.RuntimeApplied)
            _log.LogError("批量注册点位已写库，但设备 {device} 运行态应用失败：{error}",
                result.DeviceFullName, result.ErrorMessage);
        return new ProjectBulkTagResponse(
            applied,
            persisted.Conflict,
            ProjectRevision,
            persisted.ChannelId,
            persisted.DeviceId,
            persisted.Created,
            persisted.Updated,
            persisted.Deleted,
            context.DeviceRestarted,
            applied ? null : result.ErrorCode ?? persisted.ErrorCode,
            applied ? null : result.ErrorMessage ?? persisted.Error,
            timing,
            Retryable: !persisted.Applied && persisted.Retryable);
    }

    /// <summary>
    /// 降级态下显式重试运行态应用（按协调器记录的范围：按设备或全量）。
    /// 未降级时直接返回成功、不做任何应用（健康宿主上的全量应用会无谓地置 Reloading、让数据端点短暂 503）。
    /// </summary>
    public async Task<ProjectApplyResult> RetryRuntimeApplyAsync(CancellationToken ct)
    {
        if (!_configuration.Degraded)
            return new ProjectApplyResult(true, true, false, CurrentStorageRevision, null, null);
        ProjectApplyResult result = await _configuration.RetryRuntimeApplyAsync(ct).ConfigureAwait(false);
        UpdateStorageRevision(result);
        return result;
    }

    /// <summary>协调器记录的最后健康工程（测试用于核对与宿主 <see cref="Project"/> 一致）。</summary>
    internal IgwProject ConfigurationHealthyProject => _configuration.HealthyProject;

    private ProjectBulkTagResponse BulkRejected(bool conflict, string code, string message) =>
        new(false, conflict, ProjectRevision, null, null, 0, 0, 0, false, code, message, "");

    /// <summary>
    /// 写库前的候选设备预检（不动数据库）：以内存健康工程为底构造「现有点 - replaceGroups + 请求点」的候选设备，
    /// 按工程校验中的设备级规则（<see cref="ProjectStore.CheckDevice"/>：纯写点确认方式、确认点引用、数组定义）、
    /// 同级组/点重名、西门子地址模式混用逐项检查，保证本次写入不会让整个工程无法通过全量校验。
    /// 目标设备上已有的违规（经其它入口遗留）不拦，只拦本次请求新引入的。
    /// 模板实例设备不支持批量注册（点位来自模板，写入的点会与模板展开结果冲突）。
    /// 与写库之间不持锁：并发的其它保存可能让预检基于稍旧的工程（尽力而为；驱动/全量校验仍是最终兜底）。
    /// </summary>
    private (string Code, string Message)? PrecheckCandidate(ProjectBulkTagRequest request)
    {
        IgwProject project = Project;
        ChannelDef? existingChannel = project.Channels.FirstOrDefault(c =>
            string.Equals(c.Name, request.Channel.Name, StringComparison.OrdinalIgnoreCase));
        if (existingChannel is not null
            && !string.Equals(existingChannel.DriverId, request.Channel.DriverId, StringComparison.OrdinalIgnoreCase))
            return ("channel_driver_mismatch",
                $"通道 {existingChannel.Name} 已存在且驱动为 {existingChannel.DriverId}，与请求的 {request.Channel.DriverId} 不一致。");
        DeviceDef? existingDevice = existingChannel?.Devices.FirstOrDefault(d =>
            string.Equals(d.Name, request.Device.Name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(existingDevice?.TemplateRef))
            return ("template_instance_not_supported",
                $"设备 {existingChannel!.Name}.{existingDevice.Name} 是模板 {existingDevice.TemplateRef} 的实例，不支持批量注册点位；请改用普通设备或在模板上维护点位。");

        var conflicts = new List<string>();
        var (channel, device) = BulkTagCandidateBuilder.Build(project, request, conflicts);
        if (conflicts.Count > 0) return ("invalid_items", Summarize(conflicts));

        string? mixed = SiemensAddressModeGuard.Check(channel, device);
        if (mixed is not null) return ("mixed_siemens_address_mode", mixed);

        // 只拦本次请求引入的问题：目标设备上经其它入口遗留的违规（历史数据）不应让每个批量请求都失败。
        // 有错误时再对「不做任何改动」的现有设备跑同一套检查，扣掉两边都有的错误（错误文本带点位路径，可直接按文本比较）。
        IReadOnlyList<string> errors = ProjectStore.CheckDevice(channel, device);
        if (errors.Count > 0 && existingDevice is not null)
        {
            var (baseChannel, baseDevice) = BulkTagCandidateBuilder.Build(project, new ProjectBulkTagRequest
            {
                Channel = request.Channel,
                Device = new ProjectBulkDeviceSpec(existingDevice.Name, "", null),
            });
            var legacy = new HashSet<string>(ProjectStore.CheckDevice(baseChannel, baseDevice), StringComparer.Ordinal);
            errors = errors.Where(error => !legacy.Contains(error)).ToList();
        }
        if (errors.Count > 0) return ("invalid_items", Summarize(errors));
        return null;

        static string Summarize(IReadOnlyList<string> errors) =>
            string.Join("；", errors.Take(5)) + (errors.Count > 5 ? $"（共 {errors.Count} 处错误）" : "");
    }

    /// <summary>
    /// 局部修改的运行态应用——协调器唯一的 applyPatch 回调，界面局部保存(Batch 等)、批量注册点位与降级重试共用。
    /// 持有宿主 _gate，与全量应用/重连/授权调和串行；不置 <see cref="Reloading"/>（其它设备的数据端点照常服务）。
    /// <list type="bullet">
    /// <item>采集：只重启内容有变化的设备、停掉已删除/改名的旧设备（<see cref="RuntimeEngine.ApplyDevicesAsync(IgwProject, IReadOnlyCollection{string}, IReadOnlyCollection{string}, Func{Task}?)"/>）；</item>
    /// <item>北向组件：按 <see cref="ScopedApplyPolicy"/> 只重建受影响的组件（报警/历史/计算/计划/MQTT/MES/OPC UA）；
    /// 模板实例设备才退回整体应用；</item>
    /// <item>OPC UA：未重建时按设备增量同步节点（释放被删节点的钉点/推送，Hub 条目被重建的点在新条目上重新注册推送）；</item>
    /// <item>实时流：仅当点表参数变化或被删除的点正被实时流订阅时才退役重建。</item>
    /// </list>
    /// 仓储已提交后才调用：不响应请求取消。失败时记警告并抛出：协调器在同一次调用内回退为整体重读 + 整体应用
    /// （只有整体应用也失败才进入降级）。未完成的组件重建、OPC UA 同步/改绑、实时流补建记在待办里，
    /// 由这次回退的整体应用（或之后的局部应用）一并补做。
    /// </summary>
    private async Task ApplyPatchedRuntimeAsync(ProjectPatch patch, CancellationToken ct)
    {
        _ = ct;
        IgwProject next = patch.Project;
        ProjectPatchSummary summary = patch.Summary;
        // 通道增删/改名也在这里增量处理：设备按新旧全名重启/移除，OPC UA 增删通道文件夹。
        bool structural = summary.SouthboundChanged || summary.ChannelsAddedOrRemoved;
        bool pendingWork = Volatile.Read(ref _pendingNorthboundRestart) != 0
            || Volatile.Read(ref _realtimeStreamsPendingRestart) != 0;
        if (!structural && !summary.OpcUaChanged && !summary.HistoryChanged && !summary.TagStreamChanged && !pendingWork)
        {
            if (summary.MesChanged)
            {
                await ApplyPersistedRuntimeAsync(next, ProjectApplyScope.MesOnly, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            // 端口、日志保留天数等设置：运行中的组件按需读取当前工程(端口类需重启网关才生效)。
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { Project = next; }
            finally { _gate.Release(); }
            return;
        }

        BeforeScopedApplyGate?.Invoke();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        bool opcUaInSync = !structural;
        try
        {
            ResetBulkApplySteps();
            BulkApplyContext? bulk = _bulkContext.Value;
            if (Hub is null || Engine is null || _license.Evaluate().Blocked)
            {
                // 与 ApplyFullRuntimeCoreAsync 开头一致：运行态未启动/未授权时只切换工程，启动或激活后按新工程起
                Project = next;
                SetRealtimeRevision(RealtimeProjectRevision.Compute(next));
                return;
            }

            IgwProject current = Project;
            if (ScopedApplyPolicy.RequiresFullApply(current, next, summary))
            {
                _log.LogInformation("局部修改涉及模板实例设备，改为完整应用");
                await ApplyFullRuntimeAsync(next).ConfigureAwait(false);
                Interlocked.Exchange(ref _pendingNorthboundRestart, 0);
                if (bulk is not null) bulk.DeviceRestarted = true;
                return;
            }

            NorthboundComponents restart = (NorthboundComponents)Volatile.Read(ref _pendingNorthboundRestart)
                | ScopedApplyPolicy.ComponentsToRestart(current, next, summary);
            Interlocked.Exchange(ref _pendingNorthboundRestart, (int)restart);
            _log.LogInformation("局部应用配置:重启设备 {changed} 台，移除 {removed} 台，重建组件={components}",
                summary.ChangedDevices.Count, summary.RemovedDevices.Count, restart);
            if (restart != NorthboundComponents.None)
                await TimedAsync("停止受影响组件", () => StopNorthboundAsync(restart)).ConfigureAwait(false);

            ScopedDeviceApplyResult result = ScopedDeviceApplyResult.Empty;
            if (structural)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                EnsureThreadPoolFor(next.Channels.Sum(c => c.Devices.Count));
                result = await Engine.ApplyDevicesAsync(next, summary.ChangedDevices, summary.RemovedDevices).ConfigureAwait(false);
                RecordApplyStep("重启受影响设备", watch.ElapsedMilliseconds);
                if (bulk is not null)
                    bulk.DeviceRestarted = summary.ChangedDevices.Any(name =>
                        result.Restarted.Contains(name, StringComparer.OrdinalIgnoreCase));
                // Hub 条目被注销/新登记(含排空超时重建)的点若被组件按名引用，这些组件按句柄建立的订阅已失效或还没建：补充重建
                NorthboundComponents extra = ScopedApplyPolicy.ComponentsReferencingTags(
                    next, result.RemovedTagIds, result.AddedTagIds, summary.ChangedDevices) & ~restart;
                if (extra != NorthboundComponents.None)
                {
                    restart |= extra;
                    Interlocked.Exchange(ref _pendingNorthboundRestart, (int)restart);
                    await TimedAsync("停止受影响组件", () => StopNorthboundAsync(extra)).ConfigureAwait(false);
                }
            }

            // 必须紧跟引擎应用、在同一次 _gate 持有内判断：被删点的路由（含被槽位复用摘掉的）此刻仍可见
            var streams = RealtimeStreams;
            bool resync = summary.TagStreamChanged
                || (streams is not null && result.RemovedTagIds.Count > 0 && streams.HasRoutesForTags(result.RemovedTagIds));
            Project = next;
            // 上次局部应用已退役实时流、但重建前失败(协调器已降级)：本次(含重试)补建
            bool pendingRestart = Volatile.Read(ref _realtimeStreamsPendingRestart) != 0 && RealtimeStreams is null;
            if (resync || pendingRestart)
            {
                Volatile.Write(ref _realtimeStreamsPendingRestart, 1);
                // 代际版本只作准入令牌：用序号版本而非 O(N) 指纹，比较基准标记为滞后(需要时按当前工程重算)
                string nextRealtimeRevision = NextPatchedRealtimeRevision();
                if (RealtimeStreams is not null)
                    await TimedAsync("重建实时流", () => RetireRealtimeStreamsAsync(nextRealtimeRevision)).ConfigureAwait(false);
                SetRealtimeRevision(nextRealtimeRevision, basisStale: true);
                StartRealtimeStreams();   // 成功创建后清除补建标记
            }
            else if (structural)
            {
                // 不退役：在用协调器仍按旧版本号准入，只标记比较基准滞后（不在此处做 O(N) 指纹计算）
                Volatile.Write(ref _realtimeBasisStale, 1);
            }

            var opcua = _opcua;
            if (structural && !restart.HasFlag(NorthboundComponents.OpcUa) && opcua is not null)
            {
                // 删除后又新增的点（引擎排空超时退回全新 Hub 条目时）条目已重建，OPC UA 需要改绑推送。
                // 先并入待办再同步：同步失败时重试的引擎签名已不变、不会再报告这些点，只能靠这里记下的集合改绑。
                foreach (string id in result.RemovedTagIds.Intersect(result.AddedTagIds, StringComparer.Ordinal))
                    _pendingOpcUaRecreated.Add(id);
                if (BeforeOpcUaReplaceDevice is { } hook)
                    foreach (var channel in next.Channels)
                        foreach (var device in channel.Devices)
                            if (summary.ChangedDevices.Contains(channel.Name + "." + device.Name)) hook(channel, device);
                bool synced = false;
                await TimedAsync("同步 OPC UA 节点", () =>
                {
                    synced = opcua.SyncDevices(next, summary.ChangedDevices, summary.RemovedDevices, summary.RemovedChannels,
                        _pendingOpcUaRecreated.Count > 0 ? _pendingOpcUaRecreated.ToArray() : null);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                if (synced) { _pendingOpcUaRecreated.Clear(); opcUaInSync = true; }
                else
                {
                    // 服务未运行(启动失败/后台重试中)：整体重建 OPC UA，新服务按当前工程全量建节点
                    await StopNorthboundAsync(NorthboundComponents.OpcUa).ConfigureAwait(false);
                    restart |= NorthboundComponents.OpcUa;
                    Interlocked.Exchange(ref _pendingNorthboundRestart, (int)restart);
                }
            }
            if (restart.HasFlag(NorthboundComponents.OpcUa) && (next.OpcUa?.Enabled ?? true))
                await WaitPortFreeAsync(next.OpcUa?.Port ?? 4840, 5000).ConfigureAwait(false);
            if (restart != NorthboundComponents.None)
                await StartNorthboundTimedAsync(restart).ConfigureAwait(false);
            Interlocked.Exchange(ref _pendingNorthboundRestart, 0);
            opcUaInSync = true;
            _log.LogInformation("局部配置已应用并生效：重启设备 {restarted} 台，新增点 {added}，删除点 {removed}，实时流重同步={resync}，重建组件={components}",
                result.Restarted.Count, result.AddedTagIds.Count, result.RemovedTagIds.Count, resync || pendingRestart, restart);
        }
        catch (Exception error)
        {
            // OPC UA 地址空间没同步到新工程：记为待办，协调器随后的整体应用会整体重建 OPC UA
            if (!opcUaInSync && _opcua is not null)
                Interlocked.Or(ref _pendingNorthboundRestart, (int)NorthboundComponents.OpcUa);
            _log.LogWarning(error, "局部应用失败，改为整体重读并整体应用(累计回退 {count} 次)",
                _configuration.ScopedApplyFallbacks + 1);
            RecordApplyStep("局部应用失败，改为整体应用", 0);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量注册的分段耗时在拿到宿主闸门之后才重置（须在 _gate 内调用）：
    /// 预检在闸门外进行，若在那时清空会冲掉并发全量应用正在记录的分段。每次批量只重置一次。
    /// </summary>
    private void ResetBulkApplySteps()
    {
        if (_bulkContext.Value is not { StepsReset: false } bulk) return;
        bulk.StepsReset = true;
        lock (_applySteps) _applySteps.Clear();
        RecordApplyStep("候选设备预检", bulk.PrecheckMs);
    }

    private long _patchedRealtimeSequence;
    private string NextPatchedRealtimeRevision() =>
        $"{RealtimeProjectRevision.Compute(new IgwProject())[..8]}-patch-{Interlocked.Increment(ref _patchedRealtimeSequence)}-{Environment.TickCount64}";

    private async Task ApplyMesRuntimeAsync(IgwProject newProject)
    {
        IgwProject healthy = Project;
        if (Hub is null || Engine is null)
        {
            Project = newProject;
            return;
        }
        try
        {
            await TimedAsync("停止旧 MES 引擎", StopMesAsync).ConfigureAwait(false);
            Project = newProject;
            if (!_license.Evaluate().Blocked) await TimedAsync("启动新 MES 引擎", StartMesAsync).ConfigureAwait(false);
            _log.LogInformation("MES 配置已定向应用，PLC、实时点值流、OPC UA、MQTT 与历史组件保持运行");
        }
        catch
        {
            await StopMesAsync().ConfigureAwait(false);
            Project = healthy;
            if (!_license.Evaluate().Blocked) await StartMesAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ApplyFullRuntimeAsync(IgwProject newProject)
    {
        IgwProject healthy = Project;
        try
        {
            await ApplyFullRuntimeCoreAsync(newProject).ConfigureAwait(false);
        }
        catch (Exception applyError)
        {
            _log.LogError(applyError, "新配置运行态应用失败，恢复最后健康配置");
            try
            {
                await RestoreHealthyRuntimeAsync(healthy).ConfigureAwait(false);
            }
            catch (Exception restoreError)
            {
                _log.LogCritical(restoreError, "恢复最后健康配置失败");
                throw new AggregateException(applyError, restoreError);
            }
            throw;
        }
        finally
        {
            Reloading = false;
        }
    }

    private async Task ApplyFullRuntimeCoreAsync(IgwProject newProject)
    {
        if (Hub is null || Engine is null)
        {
            Project = newProject;
            SetRealtimeRevision(RealtimeProjectRevision.Compute(newProject));
            return;
        }
        if (_license.Evaluate().Blocked)
        {
            Project = newProject;
            SetRealtimeRevision(RealtimeProjectRevision.Compute(newProject));
            _log.LogWarning("未授权:新配置已保存,激活授权码后生效(当前不采集、不北向)。");
            return;
        }

        var changes = ProjectRuntimeChanges.Between(Project, newProject);
        // 之前失败的局部应用留下的待办：已停未起的组件一并重建；OPC UA 有待改绑的重建条目时整体重建 OPC UA
        NorthboundComponents northbound = changes.Northbound
            | (NorthboundComponents)Volatile.Read(ref _pendingNorthboundRestart)
            | (_pendingOpcUaRecreated.Count > 0 ? NorthboundComponents.OpcUa : NorthboundComponents.None);
        bool southSame = !changes.Southbound;
        string nextRealtimeRevision = RealtimeProjectRevision.Compute(newProject);
        bool realtimeChanged = !string.Equals(RealtimeBasis(), nextRealtimeRevision, StringComparison.Ordinal);
        _log.LogInformation("应用新配置:{n} 个通道，南向变化={south}，需要重载组件={components}",
            newProject.Channels.Count, changes.Southbound, northbound);

        async Task BeginRuntimeCutoverAsync()
        {
            Reloading = changes.Southbound || realtimeChanged || northbound != NorthboundComponents.None;
            if (realtimeChanged) await TimedAsync("重建实时流", () => RetireRealtimeStreamsAsync(nextRealtimeRevision)).ConfigureAwait(false);
            await TimedAsync("停止北向组件", () => StopNorthboundAsync(northbound)).ConfigureAwait(false);
        }

        var southWatch = System.Diagnostics.Stopwatch.StartNew();
        if (!southSame)
        {
            EnsureThreadPoolFor(newProject.Channels.Sum(c => c.Devices.Count));
            await Engine.ApplyAsync(newProject, BeginRuntimeCutoverAsync).ConfigureAwait(false);
        }
        else
        {
            await BeginRuntimeCutoverAsync().ConfigureAwait(false);
            Engine.UpdateProjectMetadata(newProject);
        }
        // 引擎应用里包含了“停北向/重建实时流”，这里记的是扣除它们之后 PLC 采集侧自身的耗时。
        long cutover;
        lock (_applySteps) cutover = _applySteps.Sum(step => long.TryParse(step[(step.LastIndexOf(' ') + 1)..^2], out long ms) ? ms : 0);
        RecordApplyStep(southSame ? "更新点位元数据" : "重建 PLC 采集", Math.Max(0, southWatch.ElapsedMilliseconds - cutover));
        Project = newProject;
        AdvanceRealtimeRevision(nextRealtimeRevision, realtimeChanged);
        StartRealtimeStreams();
        if (northbound.HasFlag(NorthboundComponents.OpcUa) && (newProject.OpcUa?.Enabled ?? true))
            await TimedAsync("等待 OPC UA 端口释放", async () =>
            {
                if (!await WaitPortFreeAsync(newProject.OpcUa?.Port ?? 4840, 5000).ConfigureAwait(false))
                    _log.LogError("opc.tcp 端口 {p} 5 秒内未释放,OPC UA 首次绑定可能失败(将进入 60s 后台重试)", newProject.OpcUa?.Port ?? 4840);
            }).ConfigureAwait(false);
        await StartNorthboundTimedAsync(northbound).ConfigureAwait(false);
        Interlocked.Exchange(ref _pendingNorthboundRestart, 0);
        if (!northbound.HasFlag(NorthboundComponents.OpcUa)) _opcua?.RefreshDescriptions();
        _log.LogInformation("配置已应用并生效");
        ScheduleHeapCompaction("整体应用配置");
    }

    private async Task RestoreHealthyRuntimeAsync(IgwProject healthy)
    {
        await StopNorthboundAsync().ConfigureAwait(false);
        await DisposeRealtimeStreamsAsync().ConfigureAwait(false);
        if (Engine is not null)
        {
            try { await Engine.StopAsync().ConfigureAwait(false); } catch { }
        }
        if (Hub is not null)
        {
            try { await Hub.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        Project = healthy;
        SetRealtimeRevision(RealtimeProjectRevision.Compute(healthy));
        if (_license.Evaluate().Blocked)
        {
            Engine = null!;
            Hub = null!;
            return;
        }
        await StartSouthAsync().ConfigureAwait(false);
        await StartNorthboundAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _pendingNorthboundRestart, 0);   // 全部组件已按健康配置重建
    }

    // ---- 授权:激活 + 复查 + 状态快照 ----
    /// <summary>接口用:激活并返回"本次尝试"的结果快照(机器不符/无效等原因如实反映,便于 UI 提示)。</summary>
    public async Task<object> ActivateLicenseAsync(string code) => Snapshot(await ActivateAsync(code));

    /// <summary>
    /// 激活授权码:验签+本机机器码通过(且非机器不符/无效)才写 license.key。
    /// 随后按"落盘后的真实生效状态"调和运行:恢复→起采集与北向;失效→停。
    /// 返回值是"本次尝试"的状态(拒绝的坏码返回 MachineMismatch/Invalid,而当前生效授权不变)。
    /// </summary>
    public async Task<LicenseState> ActivateAsync(string code)
    {
        await _gate.WaitAsync();
        try
        {
            var attempted = _license.Activate(code ?? "");   // 机器不符/无效 → 不落盘,当前授权不变
            await ReconcileRunAsync(_license.Evaluate());     // 按落盘后的真实状态起/停
            return attempted;
        }
        finally { _gate.Release(); }
    }

    // 按当前生效授权调和运行(调用方须持 _gate):未阻断却没在跑→起;已阻断却在跑→停;
    // 在跑但授权的驱动/功能变了(换了授权码)→整体重启，让新授权的驱动开始采集、取消授权的停下。
    private async Task ReconcileRunAsync(LicenseState effective)
    {
        string entitlements = EntitlementKey(effective);
        bool entitlementsChanged = entitlements != _appliedEntitlements;
        _appliedEntitlements = entitlements;
        if (!effective.Blocked && Hub != null && entitlementsChanged)
        {
            _log.LogInformation("授权的驱动/功能有变化,重新启动采集与北向。");
            await StopRunAsync();
            await StartSouthAsync();
            await StartNorthboundAsync();
        }
        else if (!effective.Blocked && Hub == null)
        {
            _log.LogInformation("授权生效({c}),启动采集与北向。", effective.Customer);
            await StartSouthAsync();
            await StartNorthboundAsync();
        }
        else if (effective.Blocked && Hub != null)
        {
            _log.LogError("授权已失效({s}):{m} 停止采集与北向,仅保留 REST 以便重新激活。", effective.Status, effective.Message);
            await StopRunAsync();
        }
    }

    private async Task StopRunAsync()
    {
        await StopNorthboundAsync();
        await DisposeRealtimeStreamsAsync();
        try { await Engine.StopAsync(); } catch { }
        try { await Hub.DisposeAsync(); } catch { }
        Engine = null!; Hub = null!;
    }

    // 授权的驱动/功能清单(null=不限制)拼成一个键，用来判断换授权码后是否需要重启运行
    private string _appliedEntitlements;
    private static string EntitlementKey(LicenseState state) =>
        state.Blocked ? "" : Key(state.Payload?.Drivers) + "|" + Key(state.Payload?.Features);
    private static string Key(List<string>? items) =>
        items is null ? "*" : string.Join(",", items.Order(StringComparer.OrdinalIgnoreCase));

    // 每 10min 复查(GatewayHost 构造里的定时器):长跑中跨过到期/宽限边界则起停;仅临期则告警(去重:状态变了或距上次超 24h 才打,避免 10min 一条刷屏)。
    private LicenseStatus? _lastWarnStatus;
    private DateTime _lastWarnUtc;
    private async Task LicenseRecheckAsync()
    {
        try
        {
            var lic = _license.Evaluate();
            if ((lic.Blocked && Hub != null) || (!lic.Blocked && Hub == null)
                || (!lic.Blocked && EntitlementKey(lic) != _appliedEntitlements))
            {
                await _gate.WaitAsync();
                try { await ReconcileRunAsync(_license.Evaluate()); }
                finally { _gate.Release(); }
            }
            else if (lic.NeedsAttention && (lic.Status != _lastWarnStatus || (DateTime.UtcNow - _lastWarnUtc).TotalHours >= 24))
            {
                _lastWarnStatus = lic.Status;
                _lastWarnUtc = DateTime.UtcNow;
                _log.LogWarning("授权提醒:{m}", lic.Message);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "授权复查异常"); }
    }

    /// <summary>授权状态快照(供 /health 与 /license 端点、UI)。</summary>
    public object LicenseSnapshot() => Snapshot(_license.Evaluate());

    private static object Snapshot(LicenseState s) => new
    {
        status = s.Status.ToString(),
        blocked = s.Blocked,
        customer = s.Customer,
        expiry = s.ExpiryUtc?.ToLocalTime().ToString("yyyy-MM-dd"),
        daysLeft = s.DaysLeft,
        machineId = s.MachineId,
        message = s.Message,
        edition = s.Payload?.Edition ?? "",
        // 授权的驱动/功能：limited=false 表示授权码不限制(试用或旧授权码)
        driversLimited = s.Payload?.Drivers is not null,
        drivers = LicenseCatalog.Drivers.Select(item => new { id = item.Id, name = item.Name, allowed = !s.Blocked && s.AllowsDriver(item.Id) }),
        featuresLimited = s.Payload?.Features is not null,
        features = LicenseCatalog.Features.Select(item => new { id = item.Id, name = item.Name, allowed = !s.Blocked && s.AllowsFeature(item.Id) }),
    };

    // 轮询等某 TCP 端口可绑定(= 已释放),最多等 timeoutMs;返回 false=超时仍被占
    private static async Task<bool> WaitPortFreeAsync(int port, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
                l.Start(); l.Stop();
                return true;   // 绑定成功 = 端口已释放
            }
            catch { await Task.Delay(100); }
        }
        return false;
    }

    // 各北向组件运行状态(off=未启用 / running / failed),供 /health 一眼看出"哪个面静默下线了"
    public object ComponentsSnapshot() => new
    {
        opcUa = _opcuaState,
        mqtt = _mqttState,
        mes = _mesState,
        history = _historyState,
        configuration = _configuration.Degraded ? "degraded" : "running",
        configurationError = _configuration.DegradedMessage,
        // 数据存储：sqlite / syncing(首次同步或补传中) / online / offline(主库断开，用本机缓存运行)
        databaseState = DatabaseSync?.State ?? "sqlite",
    };

    public object MesStatusSnapshot()
    {
        var mes = Volatile.Read(ref _mes);
        if (mes == null)
            return new { enabled = false, uploadsOk = 0L, uploadsErr = 0L, lastError = "", tasks = Array.Empty<object>(), recent = Array.Empty<string>() };
        return new
        {
            enabled = true,
            uploadsOk = mes.UploadsOk,
            uploadsErr = mes.UploadsErr,
            lastError = mes.LastError,
            tasks = mes.Snapshot().Select(s => new { s.Id, s.Name, s.Group, s.Ok, s.Err, s.LastError, s.LastAt, s.Nodes }).ToList(),   // Nodes=流程节点运行状态(画布高亮/卡住/归因)
            recent = mes.Recent.Reverse().Take(30).ToList(),
        };
    }

    public object MesLogSnapshot(int limit)
        => _mes?.QueryLog(limit).Select(r => new { r.Ts, r.Task, r.Step, r.Ok, r.Http, r.Msg }).ToList() ?? (object)Array.Empty<object>();

    public IReadOnlyList<MesTaskStatus> MesTaskStatuses() => _mes?.Snapshot() ?? Array.Empty<MesTaskStatus>();

    public IReadOnlyList<MesFlowEvent> MesTimeline(string taskName) => _mes?.Timeline(taskName) ?? Array.Empty<MesFlowEvent>();

    public async Task<object> MesTestAsync(MesTask task, CancellationToken ct)
    {
        if (_mes != null) return await _mes.TestRunAsync(task, ct);
        // 引擎未运行(还在配置中):建一个「无任务」的临时引擎(不起循环),只跑一次测试
        var cfg = Project.MesUpload ?? new MesUploadSettings();
        var tmp = new MesUploadSettings { DefaultMesUrl = cfg.DefaultMesUrl, AuthBearerToken = cfg.AuthBearerToken, HttpTimeoutMs = cfg.HttpTimeoutMs, LogDbPath = "" };
        await using var tester = new MesUploadEngine(Hub, tmp, (_, _, _) => Task.FromResult(true), _log,
            catalog: MesDataRangeCatalog.BuildForTasks(Project), deviceState: Engine.DeviceState, scanDemands: ScanDemands,
            saveLogPayload: _saveLogPayload, readNow: Engine.ReadNowAsync);
        return await tester.TestRunAsync(task, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _previewSessions.DisposeAsync();
        _certTimer?.Dispose();
        _licTimer?.Dispose();
        _opcuaRetry?.Dispose();
        if (_opcua != null) await _opcua.DisposeAsync();
        if (_publisher != null) await _publisher.DisposeAsync();
        if (_mes != null) await _mes.DisposeAsync();
        if (Logger != null) await Logger.DisposeAsync();
        if (Scheduler != null) await Scheduler.DisposeAsync();
        Computed?.Dispose();
        Alarms?.Dispose();
        await DisposeRealtimeStreamsAsync();
        if (Engine != null) await Engine.StopAsync();
        if (Hub != null) await Hub.DisposeAsync();
        await _configuration.DisposeAsync();
    }
}

/// <summary>
/// MES 运行开关规则。流程功能开放后，数据块的运行开关才允许启动任务；
/// 工程/通道“全开全关”只负责批量改运行开关。
/// </summary>
public static class MesRuntimePolicy
{
    public static bool ShouldRun(MesUploadSettings? settings) =>
        settings?.Tasks?.Any(task => task.FlowAvailable && task.Enabled) == true;
}
