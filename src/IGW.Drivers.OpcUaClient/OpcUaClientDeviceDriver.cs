using IGW.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using IgwStatusCode = IGW.Abstractions.StatusCode;
using IgwDataValue = IGW.Abstractions.DataValue;
using IgwWriteRequest = IGW.Abstractions.WriteRequest;
using IgwBrowseResult = IGW.Abstractions.BrowseResult;
using UaDataValue = Opc.Ua.DataValue;

namespace IGW.Drivers.OpcUaClient;

/// <summary>OPC UA Client 物理连接驱动:同连接配置共享 Session,按 NodeId 批量读/写;支持浏览自动建点。</summary>
public sealed class OpcUaClientDeviceDriver : IDeviceDriver, IBrowsableDriver, IHierarchicalBrowsableDriver, IIdleConnectionPolicyProvider, IArrayCapableDriver, IRemoteEndpointDiagnostics, IBrowseDiagnosticsSource, IDriverCapabilityProvider, IDriverSubscriptionProvider, IReadChunkHint
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });
    private const int UndeclaredInitialBatch = 512;
    private const int UndeclaredBatchCeiling = 100_000;
    /// <summary>
    /// 单个 Read/Write 请求的耗时预算(通道设置 requestBudgetMs)。S7-1515 现场实测约 15 ms + 1.6 ms/点，
    /// 且一次只处理一个请求：1000 点的读取要占住 PLC 约 1.6 秒，期间同一 PLC 的握手回写全部排队。
    /// 默认自动按实测开销推算，响应快的服务器不受影响。
    /// </summary>
    private readonly RequestBudget _requestBudget;
    private readonly string _url;
    private readonly int _timeoutMs;
    private readonly bool _anonymous;
    private readonly string _user;
    private readonly string _pass;
    private readonly bool _secure;
    private readonly string _securityPolicy;
    private readonly OpcUaCertificateTrustMode _certificateTrustMode;
    private readonly OpcUaCapabilityCache _capabilityCache;
    private readonly OpcUaTofuStore _tofuStore;
    private string _capabilityKey;
    private ApplicationConfiguration? _config;
    private Session? _session;
    private string? _discoveredEndpoint;
    private string? _sessionEndpoint;
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?[]> _arrayTemplates = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _stringLengthCache = new(StringComparer.Ordinal);
    private AdaptiveRequestWindow _readWindow =
        AdaptiveRequestWindow.Undeclared(UndeclaredInitialBatch, UndeclaredBatchCeiling);
    private AdaptiveRequestWindow _writeWindow =
        AdaptiveRequestWindow.Undeclared(UndeclaredInitialBatch, UndeclaredBatchCeiling);
    private readonly OpcUaNodeIdCache _nodeIds = new();
    private readonly bool _structArrays;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpcUaStructDecoder?> _structDecoders = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _structArrayFallbacks = new(StringComparer.Ordinal);
    private long _structArrayReads, _structArrayTags;
    /// <summary>当前会话的 RegisterNodes 句柄表；重连后换新实例，旧句柄不得跨会话使用。</summary>
    private OpcUaNodeRegistry? _registeredNodes;
    /// <summary>服务器未声明单次创建监控项上限时的保守值。</summary>
    private const int UndeclaredMonitoredItemsPerCall = 1_000;
    private int _maxMonitoredItemsPerCall = UndeclaredMonitoredItemsPerCall;
    private long _capabilityVersion;

    public string DriverId => OpcUaClientDriverFactory.Id;
    public IAddressParser AddressParser { get; } = new OpcUaNodeAddressParser();
    public DriverLimits Limits => new()
    {
        MaxReadUnits = int.MaxValue,
        MaxGap = 0,
        MaxTagsPerRead = _readWindow.Current,
        MaxTagsPerWrite = _writeWindow.Current,
    };
    public DriverCapabilitySnapshot Capabilities => new(
        _readWindow.Current,
        _writeWindow.Current,
        64 * 1024 * 1024,
        64 * 1024 * 1024,
        4,
        true,
        true,
        true,
        true,
        Interlocked.Read(ref _capabilityVersion),
        1,
        // Bound collection blocks to the physical window so each completed batch is
        // published immediately, instead of withholding every station behind the last read.
        PartitionsReadRequests: false);
    public IdleConnectionPolicy IdleConnectionPolicy => IdleConnectionPolicy.KeepConnectedWhenIdle;
    public ConnectionState State => _state;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
        SliceRead: true, SliceWrite: true, AtomicWholeWrite: true,
        MaxElementsPerRequest: 1_000_000, MaxBytesPerRequest: 64 * 1024 * 1024);
    public event Action<ConnectionState>? StateChanged;
    public event Action<BrowseDiagnosticEvent>? BrowseDiagnostic;

    public RemoteEndpointSnapshot RemoteEndpoint() => new(_url, _discoveredEndpoint, _sessionEndpoint);

    public OpcUaClientDeviceDriver(
        string url,
        int timeoutMs,
        bool anonymous,
        string user,
        string pass,
        string securityPolicy,
        string certificateTrust = "TrustOnFirstUse",
        RequestBudget? requestBudget = null,
        bool structArrayReads = true)
    {
        _requestBudget = requestBudget ?? RequestBudget.Auto;
        _structArrays = structArrayReads;
        _url = url;
        _timeoutMs = timeoutMs <= 0 ? 10000 : timeoutMs;
        _anonymous = anonymous;
        _user = user;
        _pass = pass;
        _securityPolicy = string.IsNullOrWhiteSpace(securityPolicy) ? "None" : securityPolicy;
        _secure = !string.Equals(securityPolicy, "None", StringComparison.OrdinalIgnoreCase);
        _certificateTrustMode = OpcUaCertificatePolicy.Parse(certificateTrust);
        _capabilityCache = new OpcUaCapabilityCache();
        _tofuStore = new OpcUaTofuStore();
        _capabilityKey = _url;
    }

    private void Report(BrowseDiagnosticEvent item)
    {
        try { BrowseDiagnostic?.Invoke(item); }
        catch { /* 日志订阅者不能中断协议操作。 */ }
    }

    private string Redact(string value) =>
        string.IsNullOrEmpty(_pass) ? value : value.Replace(_pass, "***", StringComparison.Ordinal);

    internal static string FormatDiagnosticException(Exception error)
    {
        var details = new List<string>();
        Exception? current = error;
        for (int depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            string status = "";
            if (current is ServiceResultException serviceError)
            {
                uint code = serviceError.StatusCode;
                string symbolic = StatusCodes.LookupSymbolicId(code) ?? "UnknownStatus";
                status = $" OPC-UA={symbolic}(0x{code:X8})";
            }
            details.Add($"{current.GetType().Name}{status}: {current.Message}");
        }
        return string.Join(" -> ", details);
    }

    private void SetState(ConnectionState s)
    {
        if (_state == s) return;
        _state = s;
        StateChanged?.Invoke(s);
    }

    internal static bool EndpointMatchesConfigured(string configured, string? actual)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var expected) ||
            !Uri.TryCreate(actual, UriKind.Absolute, out var observed))
            return false;
        return string.Equals(expected.Scheme, observed.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Host, observed.Host, StringComparison.OrdinalIgnoreCase)
            && expected.Port == observed.Port
            && string.Equals(expected.AbsolutePath.TrimEnd('/'), observed.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await CloseSessionAsync(CancellationToken.None);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "客户端配置",
                $"准备 OPC UA 客户端，配置端点={_url}，连接超时={_timeoutMs}ms，安全策略={_securityPolicy}，登录={(_anonymous ? "匿名" : "用户名")}"));

            if (Uri.TryCreate(_url, UriKind.Absolute, out var configuredUri))
            {
                try
                {
                    var dnsWatch = System.Diagnostics.Stopwatch.StartNew();
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(configuredUri.Host, ct);
                    Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "DNS/网络",
                        $"主机 {configuredUri.Host} 解析为 [{string.Join(", ", addresses.Select(x => x.ToString()))}]，端口={configuredUri.Port}，耗时={dnsWatch.ElapsedMilliseconds}ms"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "DNS/网络",
                        $"主机 {configuredUri.Host} 解析诊断失败，但仍继续由 OPC UA SDK 连接：{Redact(FormatDiagnosticException(ex))}"));
                }
            }

            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "应用配置",
                $"加载客户端证书与传输配置，OperationTimeout={_timeoutMs}ms", ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
            _config ??= await BuildConfigAsync(ct);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "端点发现",
                $"开始发现远端端点 {_url}，要求安全端点={_secure}", ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
            var epDesc = await CoreClientUtils.SelectEndpointAsync(
                _config, _url, _secure, _timeoutMs, Telemetry, ct)
                ?? throw new InvalidOperationException($"OPC UA 服务器 {_url} 未返回可用端点");
            _discoveredEndpoint = epDesc.EndpointUrl;
            _capabilityKey = string.Join('|', _url,
                epDesc.Server?.ApplicationUri ?? "", epDesc.Server?.ProductUri ?? "");
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "端点发现",
                $"发现端点={epDesc.EndpointUrl}，SecurityMode={epDesc.SecurityMode}，SecurityPolicy={epDesc.SecurityPolicyUri}，Server={epDesc.Server?.ApplicationName?.Text ?? "(未返回)"}",
                ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
            // 服务器发现结果可能返回其真实主机名/IP。显式网卡绑定时 _url 指向
            // 本地源绑定转发端口，必须继续使用该 URL 建 Session，不能绕过转发层。
            epDesc.EndpointUrl = _url;
            var ep = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(_config));
            IUserIdentity identity = _anonymous
                ? new UserIdentity()
                : new UserIdentity(new UserNameIdentityToken { UserName = _user, DecryptedPassword = System.Text.Encoding.UTF8.GetBytes(_pass ?? "") });
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "会话创建",
                $"使用发现结果创建远端会话，最终请求端点固定为 {_url}，身份={(_anonymous ? "Anonymous" : "UserName")}",
                ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
            var session = (Session)await new DefaultSessionFactory(Telemetry).CreateAsync(
                _config, ep, false, "IGW OPC UA Client", 60000, identity, null, ct);
            _sessionEndpoint = session.ConfiguredEndpoint?.EndpointUrl?.ToString() ?? ep.EndpointUrl?.ToString();
            if (!EndpointMatchesConfigured(_url, _sessionEndpoint))
            {
                try { await session.CloseAsync(CancellationToken.None); } catch { }
                try { session.Dispose(); } catch { }
                throw new InvalidOperationException(
                    $"OPC UA SDK 会话端点发生偏移：配置={_url}，实际={_sessionEndpoint ?? "(空)"}。已拒绝连接，避免误连 IGW 本机北向服务。");
            }
            session.KeepAliveInterval = Math.Clamp(_timeoutMs, 1_000, 10_000);
            session.KeepAlive += OnKeepAlive;
            _session = session;
            await LoadOperationLimitsAsync(session, ct);
            SetState(ConnectionState.Connected);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "会话建立",
                $"远端会话已建立，SessionId={session.SessionId}，NamespaceCount={session.NamespaceUris.Count}，最终端点={_sessionEndpoint}",
                ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Error, "会话失败",
                Redact(FormatDiagnosticException(ex)), ElapsedMilliseconds: elapsed.ElapsedMilliseconds));
            throw;
        }
    }

    private async Task<ApplicationConfiguration> BuildConfigAsync(CancellationToken ct)
    {
        string pki = Path.Combine(AppContext.BaseDirectory, "pki");
        var config = new ApplicationConfiguration
        {
            ApplicationName = "IGW OPC UA Client",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:localhost:IGW:UaClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=IGW OPC UA Client" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = _timeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await config.ValidateAsync(ApplicationType.Client, ct);
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            string thumbprint = e.Certificate?.Thumbprint ?? "";
            string? remembered = _tofuStore.Get(_url);
            uint status = e.Error?.StatusCode.Code ?? StatusCodes.BadCertificateInvalid;
            e.Accept = OpcUaCertificatePolicy.ShouldAccept(
                _certificateTrustMode, status, thumbprint, remembered, out bool remember);
            if (e.Accept && remember && thumbprint.Length > 0)
            {
                try { _tofuStore.Remember(_url, thumbprint); }
                catch { e.Accept = false; }
            }
        };
        var appInst = new ApplicationInstance(Telemetry) { ApplicationConfiguration = config };
        await appInst.CheckApplicationInstanceCertificatesAsync(true);
        return config;
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ReferenceEquals(_session, session) || e.Status is null || !Opc.Ua.StatusCode.IsBad(e.Status.StatusCode)) return;
        SetState(ConnectionState.Faulted);
    }

    private async Task CloseSessionAsync(CancellationToken ct)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        try { session.KeepAlive -= OnKeepAlive; } catch { }
        try { await session.CloseAsync(ct); } catch { }
        try { session.Dispose(); } catch { }
        _registeredNodes = null;
        _arrayTemplates.Clear();
        _stringLengthCache.Clear();
        _sessionEndpoint = null;
    }

    private async Task LoadOperationLimitsAsync(Session session, CancellationToken ct)
    {
        try
        {
            var requests = new ReadValueIdCollection
            {
                new() { NodeId = new NodeId(VariableIds.Server_ServerCapabilities_OperationLimits_MaxNodesPerRead), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId(VariableIds.Server_ServerCapabilities_OperationLimits_MaxNodesPerWrite), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId(VariableIds.Server_ServerCapabilities_OperationLimits_MaxMonitoredItemsPerCall), AttributeId = Attributes.Value },
                new() { NodeId = new NodeId(VariableIds.Server_ServerCapabilities_OperationLimits_MaxNodesPerRegisterNodes), AttributeId = Attributes.Value },
            };
            var response = await ProtocolOperationTiming.MeasureAsync(() => session.ReadAsync(null, 0, TimestampsToReturn.Neither, requests, ct))
                .ConfigureAwait(false);
            DataValueCollection values = response.Results;
            var read = OperationLimit(values is { Count: > 0 } ? values[0] : null, UndeclaredInitialBatch);
            var write = OperationLimit(values is { Count: > 1 } ? values[1] : null, UndeclaredInitialBatch);
            var monitored = OperationLimit(values is { Count: > 2 } ? values[2] : null, UndeclaredMonitoredItemsPerCall);
            var register = OperationLimit(values is { Count: > 3 } ? values[3] : null, OpcUaNodeRegistry.UndeclaredRegisterBatch);
            _maxMonitoredItemsPerCall = monitored.Initial;
            _registeredNodes = CreateNodeRegistry(session, register.Initial);
            var cached = await _capabilityCache.LoadAsync(_capabilityKey, ct).ConfigureAwait(false);
            int readInitial = read.Declared ? read.Initial : ClampLearned(cached?.Read, read.Initial, read.Ceiling);
            int writeInitial = write.Declared ? write.Initial : ClampLearned(cached?.Write, write.Initial, write.Ceiling);
            _readWindow = read.Declared
                ? AdaptiveRequestWindow.ForDeclaredLimit(read.Initial, _requestBudget)
                : AdaptiveRequestWindow.Undeclared(readInitial, read.Ceiling, _requestBudget);
            _writeWindow = write.Declared
                ? AdaptiveRequestWindow.ForDeclaredLimit(write.Initial, _requestBudget)
                : AdaptiveRequestWindow.Undeclared(writeInitial, write.Ceiling, _requestBudget);
            Interlocked.Increment(ref _capabilityVersion);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "服务器能力",
                $"OperationLimits：MaxNodesPerRead={(read.Declared ? read.Initial : 0)}，MaxNodesPerWrite={(write.Declared ? write.Initial : 0)}，" +
                $"MaxMonitoredItemsPerCall={(monitored.Declared ? monitored.Initial : 0)}，MaxNodesPerRegisterNodes={(register.Declared ? register.Initial : 0)}；" +
                $"单次创建监控项 {monitored.Initial}，单次注册节点 {register.Initial}；" +
                $"当前批量 Read={readInitial}/Write={writeInitial}，未声明时从已验证缓存恢复并允许自适应增长到 100000；" +
                $"单请求耗时预算={_requestBudget}"));
        }
        catch (Exception ex)
        {
            var cached = await _capabilityCache.LoadAsync(_capabilityKey, ct).ConfigureAwait(false);
            _readWindow = AdaptiveRequestWindow.Undeclared(
                ClampLearned(cached?.Read, UndeclaredInitialBatch, UndeclaredBatchCeiling),
                UndeclaredBatchCeiling, _requestBudget);
            _writeWindow = AdaptiveRequestWindow.Undeclared(
                ClampLearned(cached?.Write, UndeclaredInitialBatch, UndeclaredBatchCeiling),
                UndeclaredBatchCeiling, _requestBudget);
            _maxMonitoredItemsPerCall = UndeclaredMonitoredItemsPerCall;
            _registeredNodes = CreateNodeRegistry(session, OpcUaNodeRegistry.UndeclaredRegisterBatch);
            Interlocked.Increment(ref _capabilityVersion);
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "服务器能力",
                $"无法读取 OperationLimits，使用安全初值 {UndeclaredInitialBatch} 并在容量错误时自动降批：{Redact(FormatDiagnosticException(ex))}"));
        }
    }

    private OpcUaNodeRegistry CreateNodeRegistry(Session session, int batchSize) => new(
        async (nodes, token) => (await ProtocolOperationTiming.MeasureAsync(
                () => session.RegisterNodesAsync(null, nodes, token),
                new("RegisterNodes", "OPC UA node handles", nodes.Count, -1, 0))
            .ConfigureAwait(false)).RegisteredNodeIds,
        batchSize,
        message => Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "节点注册", message)));

    /// <summary>把请求里的 NodeId 换成本会话的注册句柄；未注册或注册不可用时原样返回。</summary>
    private static Task<NodeId[]> WireNodesAsync(OpcUaNodeRegistry? registry, IReadOnlyList<NodeId> nodes, CancellationToken ct) =>
        registry is null ? Task.FromResult(nodes.ToArray()) : registry.MapAsync(nodes, ct);

    private static int ClampLearned(int? learned, int fallback, int ceiling) =>
        learned is > 0 ? Math.Clamp(learned.Value, 1, ceiling) : fallback;

    private void QueueCapabilityPersist()
    {
        int read = _readWindow.Current;
        int write = _writeWindow.Current;
        _ = PersistCapabilitiesAsync(read, write);
    }

    private async Task PersistCapabilitiesAsync(int read, int write)
    {
        try { await _capabilityCache.SaveAsync(_capabilityKey, read, write, CancellationToken.None).ConfigureAwait(false); }
        catch { /* 能力缓存失败不得影响实时协议会话。 */ }
    }

    private static (int Initial, int Ceiling, bool Declared) OperationLimit(UaDataValue? value, int fallback)
    {
        if (value is null || Opc.Ua.StatusCode.IsBad(value.StatusCode)) return (fallback, UndeclaredBatchCeiling, false);
        long parsed = value.Value switch
        {
            uint item => item,
            int item => item,
            ushort item => item,
            long item => item,
            _ => 0,
        };
        if (parsed <= 0) return (fallback, UndeclaredBatchCeiling, false);
        int declared = checked((int)Math.Min(parsed, UndeclaredBatchCeiling));
        return (declared, declared, true);
    }

    public async Task DisconnectAsync()
    {
        await CloseSessionAsync(CancellationToken.None);
        SetState(ConnectionState.Disconnected);
    }

    public async Task<IDriverSubscription> SubscribeAsync(
        IReadOnlyList<TagBinding> tags,
        Action<IReadOnlyList<TagSample>> onSamples,
        CancellationToken ct)
    {
        var session = _session;
        if (session is null || !session.Connected)
            throw new DriverOperationException(
                "OPC UA 会话未连接",
                DriverFailureScope.Session,
                DriverRetryDisposition.RebuildSession,
                IgwStatusCode.BadNotConnected);
        if (tags.Count == 0) return new OpcUaSubscriptionLease(this, session, [], []);

        int itemsPerCall = Math.Max(1, _maxMonitoredItemsPerCall);
        var subscriptions = new List<Subscription>();
        var acceptedTagIds = new List<string>(tags.Count);
        try
        {
            foreach (var planned in PlanSubscriptions(tags, MaxItemsPerSubscription))
            {
                ct.ThrowIfCancellationRequested();
                var slice = planned.Tags;
                var subscription = new Subscription(session.DefaultSubscription)
                {
                    DisplayName = $"IGW realtime {planned.PeriodMs}ms #{subscriptions.Count + 1}",
                    PublishingInterval = PublishingIntervalFor(planned.PeriodMs),
                    KeepAliveCount = 10,
                    LifetimeCount = 100,
                    PublishingEnabled = true,
                };
                var items = new List<MonitoredItem>(slice.Count);
                foreach (var binding in slice)
                {
                    if (!TryNode(binding.Def.Address, out var node)) continue;
                    var context = new SubscriptionTagContext(binding, onSamples, this);
                    var item = new MonitoredItem(subscription.DefaultItem)
                    {
                        DisplayName = binding.TagId,
                        StartNodeId = node,
                        AttributeId = Attributes.Value,
                        SamplingInterval = SubscriptionPeriod(binding),
                        QueueSize = 1,
                        DiscardOldest = true,
                        Handle = context,
                    };
                    item.Notification += OnMonitoredItemNotification;
                    items.Add(item);
                }
                if (items.Count == 0) continue;
                session.AddSubscription(subscription);
                // Own the subscription before either SDK call can fail; peers may keep this session alive.
                subscriptions.Add(subscription);
                await subscription.CreateAsync(ct).ConfigureAwait(false);
                // 单次 CreateMonitoredItems 不超过服务器声明的上限：超出时服务器会整批拒绝，
                // 这一组点全部退回轮询，是最慢的结果。
                for (int offset = 0; offset < items.Count; offset += itemsPerCall)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var item in items.Skip(offset).Take(itemsPerCall)) subscription.AddItem(item);
                    await subscription.ApplyChangesAsync(ct).ConfigureAwait(false);
                }
                acceptedTagIds.AddRange(subscription.MonitoredItems
                    .Where(item => item.Handle is SubscriptionTagContext &&
                        (item.Status?.Error is null || !Opc.Ua.StatusCode.IsBad(item.Status.Error.StatusCode)))
                    .Select(item => ((SubscriptionTagContext)item.Handle!).Binding.TagId));
            }
            return new OpcUaSubscriptionLease(this, session, subscriptions, acceptedTagIds);
        }
        catch (Exception ex)
        {
            // 建立失败的回滚同样按订阅数给预算：部分创建成功的订阅必须真正删掉，否则重试会叠加。
            await OpcUaSubscriptionLease.DisposeSubscriptionsAsync(
                this, session, subscriptions, perSubscriptionMs: 250).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("OPC UA 订阅建立已取消", ex, ct);
            throw ClassifyReadFailure(ex, Math.Max(1, tags.Count));
        }
    }

    internal const int MaxItemsPerSubscription = 5_000;

    internal sealed record PlannedSubscription(int PeriodMs, IReadOnlyList<TagBinding> Tags);

    /// <summary>
    /// 按采样周期分组后再按单订阅容量切分。一个订阅的发布周期取组内最快的点，
    /// 若 200ms 的点与 5s 的点混在一起，5s 的点也会按 200ms 发布，白白增加 PLC 与网关负载。
    /// </summary>
    internal static IReadOnlyList<PlannedSubscription> PlanSubscriptions(
        IReadOnlyList<TagBinding> tags, int maxItemsPerSubscription)
    {
        int capacity = Math.Max(1, maxItemsPerSubscription);
        var planned = new List<PlannedSubscription>();
        foreach (var group in tags.GroupBy(SubscriptionPeriod).OrderBy(group => group.Key))
        {
            var members = group.ToArray();
            for (int cursor = 0; cursor < members.Length; cursor += capacity)
                planned.Add(new(group.Key, members.Skip(cursor).Take(capacity).ToArray()));
        }
        return planned;
    }

    internal static int PublishingIntervalFor(int periodMs) => Math.Clamp(periodMs, 10, 1000);

    private static int SubscriptionPeriod(TagBinding binding) =>
        Math.Max(1, binding.RequestedSamplingIntervalMs ??
            (binding.Def.ScanRateMs <= 0 ? 1000 : binding.Def.ScanRateMs));

    private static void OnMonitoredItemNotification(
        MonitoredItem item,
        MonitoredItemNotificationEventArgs args)
    {
        if (item.Handle is not SubscriptionTagContext context) return;
        try
        {
            var values = item.DequeueValues();
            if (values.Count == 0 && args.NotificationValue is MonitoredItemNotification notification)
                values = [notification.Value];
            foreach (var value in values)
            {
                DateTime now = DateTime.UtcNow;
                var quality = new IgwStatusCode(value.StatusCode.Code);
                DateTime source = value.SourceTimestamp == DateTime.MinValue ? now : value.SourceTimestamp;
                IgwDataValue decoded;
                if (context.Binding.Def.Array is not null && !Opc.Ua.StatusCode.IsBad(value.StatusCode))
                {
                    try
                    {
                        var array = OpcUaArrayCodec.Decode(value.Value, context.Binding.Def, quality);
                        if (context.Binding.Def.Array.ElementType is IgwDataType.Struct or IgwDataType.Dtl)
                            context.Driver._arrayTemplates[context.Binding.TagId] = OpcUaArrayCodec.CaptureTemplates(value.Value);
                        decoded = new IgwDataValue(array, array.OverallQuality, source, now);
                    }
                    catch { decoded = IgwDataValue.BadAt(IgwStatusCode.BadDecodeError, now); }
                }
                else decoded = context.Driver.DecodeScalar(context.Binding, value, quality, source, now);
                context.Publish([new TagSample(context.Binding.TagId, decoded)]);
            }
        }
        catch { /* 单个通知解码或消费者异常不能终止 SDK 发布线程。 */ }
    }

    private sealed record SubscriptionTagContext(
        TagBinding Binding,
        Action<IReadOnlyList<TagSample>> Publish, OpcUaClientDeviceDriver Driver);

    private sealed class OpcUaSubscriptionLease(
        OpcUaClientDeviceDriver driver,
        Session session,
        IReadOnlyList<Subscription> subscriptions,
        IReadOnlyCollection<string> tagIds) : IIncrementalDriverSubscription
    {
        private readonly object _sync = new();
        private HashSet<string> _tagIds = tagIds.ToHashSet(StringComparer.Ordinal);
        private int _disposed;
        public IReadOnlyCollection<string> TagIds { get { lock (_sync) return _tagIds.ToArray(); } }

        /// <summary>
        /// 只摘除这些点的监控项，其余点继续推送。监控项清空的订阅整条删除，避免在 PLC 上
        /// 留下空订阅继续占用 S7-1500 有限的订阅配额。
        /// </summary>
        public async Task<int> RemoveAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0 || ids.Count == 0) return 0;
            var target = ids.ToHashSet(StringComparer.Ordinal);
            int removed = 0;
            foreach (var subscription in subscriptions)
            {
                var doomed = subscription.MonitoredItems
                    .Where(item => item.Handle is SubscriptionTagContext context &&
                        target.Contains(context.Binding.TagId))
                    .ToArray();
                if (doomed.Length == 0) continue;
                foreach (var item in doomed)
                {
                    item.Notification -= OnMonitoredItemNotification;
                    subscription.RemoveItem(item);
                }
                removed += doomed.Length;
                if (subscription.MonitoredItemCount == 0)
                {
                    try { await subscription.DeleteAsync(true, ct).ConfigureAwait(false); }
                    catch { }
                    try { await session.RemoveSubscriptionAsync(subscription, ct).ConfigureAwait(false); }
                    catch { }
                    continue;
                }
                await subscription.ApplyChangesAsync(ct).ConfigureAwait(false);
            }
            if (removed > 0)
                lock (_sync)
                {
                    var updated = new HashSet<string>(_tagIds, StringComparer.Ordinal);
                    updated.ExceptWith(target);
                    _tagIds = updated;
                }
            return removed;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await DisposeSubscriptionsAsync(driver, session, subscriptions).ConfigureAwait(false);
        }

        internal static async Task DisposeSubscriptionsAsync(
            OpcUaClientDeviceDriver driver,
            Session session,
            IReadOnlyList<Subscription> subscriptions,
            int perSubscriptionMs = 500)
        {
            if (subscriptions.Count == 0) return;
            // 总预算随订阅数增长。几十万点会被切成几十个订阅，固定 500ms 会让靠后的删除全部被取消，
            // 在 PLC 上留下没有监控项却仍在发布的订阅，持续消耗 S7-1500 有限的订阅资源。
            // 仍设 30 秒硬上限，避免停机或重连被一条坏会话无限拖住。
            var budget = TimeSpan.FromMilliseconds(
                Math.Min(30_000L, subscriptions.Count * (long)Math.Max(1, perSubscriptionMs)));
            long startedAt = Environment.TickCount64;
            int unconfirmed = 0;
            foreach (var subscription in subscriptions)
            {
                foreach (var item in subscription.MonitoredItems)
                    item.Notification -= OnMonitoredItemNotification;
                var remaining = budget - TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
                using var cleanup = new CancellationTokenSource(
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                bool deleted = false;
                try { await subscription.DeleteAsync(true, cleanup.Token).ConfigureAwait(false); deleted = true; }
                catch { }
                try { await session.RemoveSubscriptionAsync(subscription, cleanup.Token).ConfigureAwait(false); }
                catch { }
                try { subscription.Dispose(); } catch { }
                if (!deleted) unconfirmed++;
            }
            if (unconfirmed > 0)
                driver.Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "订阅删除",
                    $"{subscriptions.Count} 个订阅中有 {unconfirmed} 个未确认删除(总预算 {budget.TotalMilliseconds:F0}ms)；" +
                    "服务器可能仍保留并继续发布，重建会话时会一并释放"));
        }
    }

    /// <summary>当前单个读请求的点数；共享会话据此把大读取拆成多次准入，让写入可以插在分块之间。</summary>
    public int ReadChunkSize => Math.Max(1, _readWindow.Current);
    public string ReadWindowSummary => $"读{_readWindow}；写{_writeWindow}" +
        (_structArrayReads > 0 ? $"；结构体数组整读 {_structArrayReads} 次/{_structArrayTags} 点" : "");

    /// <summary>
    /// 把结构体数组的成员点归到同一个准入单元：整个数组一次读回，不再按批量切开。
    /// 其余点位按读批量切分。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TagBinding>> PlanReadUnits(IReadOnlyList<TagBinding> tags)
    {
        int chunk = Math.Max(1, ReadChunkSize);
        var plan = _structArrays
            ? OpcUaStructArrayPlan.Plan(tags)
            : ((IReadOnlyList<OpcUaStructArrayGroup>)[], tags.ToList());
        var (groups, rest) = plan;
        var units = new List<IReadOnlyList<TagBinding>>(groups.Count + rest.Count / chunk + 1);
        foreach (var group in groups) units.Add(group.Members.Select(item => item.Binding).ToArray());
        for (int offset = 0; offset < rest.Count; offset += chunk)
            units.Add(rest.Skip(offset).Take(chunk).ToArray());
        return units.Count == 0 ? [tags] : units;
    }

    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        var session = _session;
        var registry = _registeredNodes;
        if (session is null || !session.Connected)
            throw new InvalidOperationException("OPC UA 会话未连接");
        return ReadCoreAsync(tags, async (requests, token) =>
            (await session.ReadAsync(null, 0, TimestampsToReturn.Both, requests, token).ConfigureAwait(false)).Results,
            () => session.Connected, ct, (nodes, token) => WireNodesAsync(registry, nodes, token));
    }

    internal async Task<IReadOnlyList<TagSample>> ReadCoreAsync(IReadOnlyList<TagBinding> tags,
        Func<ReadValueIdCollection, CancellationToken, Task<DataValueCollection>> read,
        Func<bool> connected, CancellationToken ct,
        Func<IReadOnlyList<NodeId>, CancellationToken, Task<NodeId[]>>? wireNodes = null)
    {
        var now = DateTime.UtcNow;
        var samples = new List<TagSample>(tags.Count);
        if (!connected())
            throw new InvalidOperationException("OPC UA 会话未连接"); // → DeviceLoop 触发重连

        var remaining = tags;
        if (_structArrays && tags.Count >= OpcUaStructArrayPlan.MinimumGroupTags)
        {
            var (groups, rest) = OpcUaStructArrayPlan.Plan(tags);
            if (groups.Count > 0)
            {
                var pending = new List<TagBinding>(rest);
                foreach (var group in groups)
                {
                    var decoded = await TryReadStructArrayAsync(group, read, now, pending, ct).ConfigureAwait(false);
                    if (decoded is null) pending.AddRange(group.Members.Select(item => item.Binding));
                    else samples.AddRange(decoded);
                }
                remaining = pending;
            }
        }

        var valid = new List<(TagBinding Binding, NodeId Node)>(remaining.Count);
        foreach (var t in remaining)
        {
            if (TryNode(t.Def.Address, out var nodeId))
                valid.Add((t, nodeId));
            else samples.Add(new TagSample(t.TagId, IgwDataValue.BadAt(IgwStatusCode.Bad, now)));
        }

        // 在计时之外一次性换成注册句柄：首轮注册的耗时不能算进读取耗时，否则会干扰批量窗口学习。
        NodeId[]? wire = wireNodes is null || valid.Count == 0
            ? null
            : await wireNodes(valid.Select(item => item.Node).ToArray(), ct).ConfigureAwait(false);
        int recoveryEnd = 0;
        int cursor = 0;
        while (cursor < valid.Count)
        {
            ct.ThrowIfCancellationRequested();
            var planWindow = _readWindow.Capture();
            int size = Math.Min(planWindow.Current, valid.Count - cursor);
            if (cursor < recoveryEnd) size = Math.Min(size, recoveryEnd - cursor);
            var requests = new ReadValueIdCollection();
            for (int i = 0; i < size; i++)
                requests.Add(new ReadValueId { NodeId = wire?[cursor + i] ?? valid[cursor + i].Node, AttributeId = Attributes.Value });
            DataValueCollection res;
            double requestMs = 0;
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                res = await ProtocolOperationTiming.MeasureAsync(() => read(requests, ct),
                    new("Read", "OPC UA Value attributes", requests.Count, -1, 0))
                    .ConfigureAwait(false);
                requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("OPC UA 读取已取消", ex, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsServiceTimeout(ex))
            {
                // 超时不证明服务器容量不足：可能只是这一刻 PLC 负载高或网络抖动。缩小批量窗口会让
                // 后续同样的点位需要更多物理请求，反而加重负载并引发更多超时。任何情况下都保留
                // 会话与批量窗口，本次逻辑读取不再拆分重试，交回调度器按周期整批重试。
                _ = TryGetServiceTimeoutStatus(ex, out var timeoutStatus);
                Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "读取超时",
                    $"Read 请求 {size} 点超时，本次逻辑读取不再拆分重试；" +
                    $"批量窗口保持 {_readWindow.Current} 点，连接保持在线，由调度器按周期重试"));
                // 已经成功读到的前缀是这一刻 PLC 的真实值，不能因为后续分片超时而丢弃；
                // 整批第一个请求就超时则没有可用数据，按批次级失败抛出，避免上层把空结果当成一次成功采集。
                if (cursor == 0) throw ClassifyReadFailure(ex, size);
                for (int i = cursor; i < valid.Count; i++)
                    samples.Add(new TagSample(valid[i].Binding.TagId, IgwDataValue.BadAt(timeoutStatus, now)));
                cursor = valid.Count;
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsCapacityError(ex))
            {
                recoveryEnd = Math.Max(recoveryEnd, cursor + size);
                if (size == 1)
                {
                    var binding = valid[cursor++].Binding;
                    var failure = ClassifyReadFailure(ex, size);
                    samples.Add(new TagSample(binding.TagId, IgwDataValue.BadAt(failure.Status, now)));
                    continue;
                }
                int before = _readWindow.Current;
                int reduced = _readWindow.OnCapacityFailure(size);
                if (reduced != before)
                {
                    Interlocked.Increment(ref _capabilityVersion);
                    QueueCapabilityPersist();
                }
                ReportBatchReduction("Read", size, reduced);
                continue; // Preserve successful prefix; retry only this failed physical span.
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw ClassifyReadFailure(ex, size);
            }
            if (res is null || res.Count != size)
                throw new InvalidOperationException($"OPC UA 读响应数量不匹配(期望 {size},实得 {res?.Count ?? 0})");
            for (int i = 0; i < size; i++)
            {
                var dv = res[i];
                var binding = valid[cursor + i].Binding;
                var q = new IgwStatusCode(dv.StatusCode.Code); // 码值与 OPC UA 一致,零转换
                var src = dv.SourceTimestamp == DateTime.MinValue ? now : dv.SourceTimestamp;
                if (binding.Def.Array is not null && !Opc.Ua.StatusCode.IsBad(dv.StatusCode))
                {
                    try
                    {
                        var array = OpcUaArrayCodec.Decode(dv.Value, binding.Def, q);
                        if (binding.Def.Array.ElementType is IgwDataType.Struct or IgwDataType.Dtl)
                            _arrayTemplates[binding.TagId] = OpcUaArrayCodec.CaptureTemplates(dv.Value);
                        samples.Add(new TagSample(binding.TagId,
                            new IgwDataValue(array, array.OverallQuality, src, now)));
                    }
                    catch
                    {
                        samples.Add(new TagSample(binding.TagId,
                            IgwDataValue.BadAt(IgwStatusCode.BadDecodeError, now)));
                    }
                }
                else samples.Add(new TagSample(binding.TagId, DecodeScalar(binding, dv, q, src, now)));
            }
            if (cursor >= recoveryEnd && !ct.IsCancellationRequested && connected() && res.All(dv => Opc.Ua.StatusCode.IsGood(dv.StatusCode)) && _readWindow.OnSuccess(size, requestMs, planWindow))
            {
                Interlocked.Increment(ref _capabilityVersion);
                QueueCapabilityPersist();
            }
            cursor += size;
        }
        var byId = samples.ToDictionary(sample => sample.TagId, StringComparer.Ordinal);
        return tags.Select(tag => byId[tag.TagId]).ToArray();
    }

    /// <summary>
    /// 结构体数组整读：一次读回索引段内的全部元素，再按服务器的结构定义拆回各成员点位。
    /// 任何一步不成立(服务器没给结构定义、有可选字段、元素不是二进制结构体…)都返回 null，调用方退回逐点读取。
    /// </summary>
    /// <param name="perTagFallback">整读解不出来的成员(如成员本身又是结构体)：只把这些点退回逐点读取，其余成员照常返回。</param>
    private async Task<IReadOnlyList<TagSample>?> TryReadStructArrayAsync(
        OpcUaStructArrayGroup group,
        Func<ReadValueIdCollection, CancellationToken, Task<DataValueCollection>> read,
        DateTime now, List<TagBinding> perTagFallback, CancellationToken ct)
    {
        if (!TryNode(group.ArrayAddress, out var arrayNode)) return null;
        OpcUaStructDecoder? decoder;
        try { decoder = await StructDecoderAsync(arrayNode, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ReportStructArrayFallback(group, "读取结构定义失败:" + Redact(FormatDiagnosticException(ex)));
            return null;
        }
        if (decoder is null) return null;

        DataValueCollection response;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            response = await ProtocolOperationTiming.MeasureAsync(
                () => read([OpcUaStructArrayPlan.ReadRequest(group, arrayNode)], ct),
                new("Read", "OPC UA struct array", group.ElementCount, -1, 0)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested) { throw new OperationCanceledException("OPC UA 读取已取消", ex, ct); }
        catch (Exception ex)
        {
            ReportStructArrayFallback(group, "整读失败:" + Redact(FormatDiagnosticException(ex)));
            return null;
        }
        if (response is not { Count: 1 } || Opc.Ua.StatusCode.IsBad(response[0].StatusCode))
        {
            ReportStructArrayFallback(group, $"整读返回 {(response is { Count: > 0 } ? response[0].StatusCode.ToString() : "空响应")}");
            return null;
        }

        object?[] elements = SiemensOpcUaCodec.Elements(response[0].Value, IgwDataType.Struct);
        if (elements.Length < group.LastIndex - group.FirstIndex + 1)
        {
            ReportStructArrayFallback(group, $"整读只返回 {elements.Length} 个元素，少于索引段 {group.FirstIndex}:{group.LastIndex}");
            return null;
        }

        var decodedElements = new Dictionary<string, object?>[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i] is not ExtensionObject { Body: byte[] body }) { ReportStructArrayFallback(group, "元素不是二进制结构体"); return null; }
            var members = decoder.TryDecode(body);
            if (members is null) { ReportStructArrayFallback(group, "结构定义与数据不匹配"); return null; }
            decodedElements[i] = members;
        }

        var samples = new List<TagSample>(group.Members.Count);
        var timestamps = response[0];
        DateTime source = timestamps.SourceTimestamp == DateTime.MinValue ? now : timestamps.SourceTimestamp;
        foreach (var (binding, index, member) in group.Members)
        {
            int offset = index - group.FirstIndex;
            if (offset < 0 || offset >= decodedElements.Length ||
                !decodedElements[offset].TryGetValue(member, out object? value))
            {
                ReportStructArrayFallback(group, $"结构体成员 {member} 不在服务器的定义里");
                return null;
            }
            // 成员本身又是结构体(解出来是成员字典)等无法当标量搬运的值：只让这个点退回逐点读取。
            // 这里若直接抛出，本次读取的所有点都会被标成坏值(现场 66 点整组变 Bad 就是这样来的)。
            if (!TryWrapVariant(value, out Variant wrapped))
            {
                ReportStructArrayFallback(group, $"结构体成员 {member} 不是标量(可能是嵌套结构体)，该点退回逐点读取");
                perTagFallback.Add(binding);
                continue;
            }
            samples.Add(new TagSample(binding.TagId,
                DecodeScalar(binding, new UaDataValue(wrapped) { StatusCode = timestamps.StatusCode, SourceTimestamp = source },
                    new IgwStatusCode(timestamps.StatusCode.Code), source, now)));
        }
        Interlocked.Increment(ref _structArrayReads);
        Interlocked.Add(ref _structArrayTags, group.Members.Count);
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (Interlocked.Read(ref _structArrayReads) == 1)
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "结构体数组整读",
                $"{group.ArrayAddress}[{group.FirstIndex}:{group.LastIndex}] 一次读回 {group.Members.Count} 个成员点，耗时 {elapsedMs:F0}ms；" +
                "逐点读取需要同样数量的请求"));
        return samples;
    }

    /// <summary>把解码出的成员值装进 OPC UA Variant；装不下(嵌套结构体、未支持的类型)时返回 false，不抛出。</summary>
    private static bool TryWrapVariant(object? value, out Variant variant)
    {
        try
        {
            variant = new Variant(value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or ServiceResultException)
        {
            variant = Variant.Null;
            return false;
        }
    }

    private void ReportStructArrayFallback(OpcUaStructArrayGroup group, string reason)
    {
        if (_structArrayFallbacks.TryAdd(group.ArrayAddress, 0))
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "结构体数组整读",
                $"{group.ArrayAddress} 无法整读，退回逐点读取：{reason}"));
    }

    /// <summary>取数组元素类型的结构定义(含嵌套类型)，按会话缓存；不可用时缓存 null，不再反复询问。</summary>
    private async Task<OpcUaStructDecoder?> StructDecoderAsync(NodeId arrayNode, CancellationToken ct)
    {
        string key = arrayNode.ToString();
        if (_structDecoders.TryGetValue(key, out var cached)) return cached;
        var session = _session;
        if (session is null || !session.Connected) return null;

        var dataTypeResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = arrayNode, AttributeId = Attributes.DataType }], ct).ConfigureAwait(false);
        OpcUaStructDecoder? decoder = null;
        if (dataTypeResponse.Results is { Count: 1 } && !Opc.Ua.StatusCode.IsBad(dataTypeResponse.Results[0].StatusCode) &&
            dataTypeResponse.Results[0].Value is NodeId dataType && dataType.NamespaceIndex > 0)
        {
            var definitions = new Dictionary<NodeId, StructureDefinition>();
            await LoadDefinitionsAsync(session, dataType, definitions, depth: 0, ct).ConfigureAwait(false);
            if (definitions.TryGetValue(dataType, out var definition))
                decoder = new OpcUaStructDecoder(definition, definitions);
        }
        _structDecoders[key] = decoder;
        return decoder;
    }

    private static async Task LoadDefinitionsAsync(Session session, NodeId dataType,
        Dictionary<NodeId, StructureDefinition> definitions, int depth, CancellationToken ct)
    {
        if (depth > 4 || definitions.ContainsKey(dataType)) return;
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = dataType, AttributeId = Attributes.DataTypeDefinition }], ct).ConfigureAwait(false);
        if (response.Results is not { Count: 1 } || Opc.Ua.StatusCode.IsBad(response.Results[0].StatusCode)) return;
        StructureDefinition? definition = response.Results[0].Value switch
        {
            StructureDefinition direct => direct,
            ExtensionObject { Body: StructureDefinition body } => body,
            _ => null,
        };
        if (definition is null) return;
        definitions[dataType] = definition;
        foreach (var field in definition.Fields ?? [])
            if (field.DataType.NamespaceIndex > 0)
                await LoadDefinitionsAsync(session, field.DataType, definitions, depth + 1, ct).ConfigureAwait(false);
    }

    private IgwDataValue DecodeScalar(TagBinding binding, UaDataValue value, IgwStatusCode quality, DateTime source, DateTime now)
    {
        if (quality.IsBad || !SiemensValueCodec.IsSemantic(binding.Def.DataType))
            return new IgwDataValue(value.Value, quality, source, now);
        try
        {
            var decoded = SiemensOpcUaCodec.Decode(binding.Def.DataType, value.Value);
            if (binding.Def.DataType == IgwDataType.Dtl) _arrayTemplates[binding.TagId] = [value.Value];
            return new IgwDataValue(decoded, quality, source, now);
        }
        catch { return IgwDataValue.BadAt(IgwStatusCode.BadDecodeError, now); }
    }

    private async Task<object?[]> ReadTemplatesAsync(string tagId, NodeId node, CancellationToken ct)
    {
        if (_arrayTemplates.TryGetValue(tagId, out var cached)) return cached;
        var response = await ProtocolOperationTiming.MeasureAsync(() => _session!.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new ReadValueIdCollection { new() { NodeId = node, AttributeId = Attributes.Value } }, ct)).ConfigureAwait(false);
        if (response.Results.Count != 1 || Opc.Ua.StatusCode.IsBad(response.Results[0].StatusCode))
            throw new InvalidDataException("读取 DTL 原始服务器编码失败");
        var value = response.Results[0].Value;
        var templates = value is ExtensionObject or byte[] ? new object?[] { value } : OpcUaArrayCodec.CaptureTemplates(value);
        _arrayTemplates[tagId] = templates;
        return templates;
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<IgwWriteRequest> writes, CancellationToken ct)
    {
        var results = new List<WriteResult>(writes.Count);
        if (_session is null || !_session.Connected)
        {
            foreach (var w in writes) results.Add(new WriteResult { TagId = w.TagId, Status = IgwStatusCode.BadNotConnected });
            return results;
        }

        var parsed = new List<(IgwWriteRequest Request, NodeId Node)>(writes.Count);
        foreach (var w in writes)
        {
            if (TryNode(w.Def.Address, out var nodeId))
                parsed.Add((w, nodeId));
            else results.Add(new WriteResult { TagId = w.TagId, Status = IgwStatusCode.Bad });
        }

        // PLC/OPC UA 数据类型可能在工程导入后发生变化。每次字符串写批次下发前，
        // 都先批量读取 DataTypeDefinition 中的 MaxStringLength，再按实际字段容量截断。
        // 单次 ResolveManyAsync 会把同一结构体的字段合并读取，避免逐点网络往返。
        await RefreshPublishedStringLengthsAsync(parsed, ct).ConfigureAwait(false);

        var valid = new List<(IgwWriteRequest Request, NodeId Node, object Value, string? Diagnostic)>(parsed.Count);
        foreach (var item in parsed)
        {
            object effectiveValue = item.Request.Value;
            try
            {
                if (SiemensValueCodec.IsSemantic(item.Request.Def.DataType))
                {
                    object? template = item.Request.Def.DataType == IgwDataType.Dtl
                        ? (await ReadTemplatesAsync(item.Request.TagId, item.Node, ct).ConfigureAwait(false))[0] : null;
                    effectiveValue = SiemensOpcUaCodec.Encode(item.Request.Def.DataType, effectiveValue, template);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                results.Add(new WriteResult { TagId = item.Request.TagId, Status = IgwStatusCode.BadDecodeError, Error = ex.Message });
                continue;
            }
            int? capacity = null;
            if (item.Request.Def.DataType is IgwDataType.String or IgwDataType.WString)
            {
                int? verified = _stringLengthCache.TryGetValue(item.Node.ToString(), out int cached) && cached > 0
                    ? cached
                    : null;
                capacity = OpcUaStringWriteFallback.EffectiveCapacity(item.Request.Def.StringLength, verified);
                if (capacity is > 0)
                    effectiveValue = OpcUaStringWriteFallback.TruncateToCapacity(
                        Convert.ToString(item.Request.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                        capacity.Value,
                        item.Request.Def.DataType,
                        item.Request.Def.TextEncoding);
            }
            string? diagnostic = item.Request.Def.DataType is IgwDataType.String or IgwDataType.WString
                ? StringWriteDiagnostic(item.Request, item.Node, effectiveValue, capacity, null, 1)
                : null;
            valid.Add((item.Request, item.Node, effectiveValue, diagnostic));
        }
        NodeId[] wire = await WireNodesAsync(_registeredNodes, valid.Select(item => item.Node).ToArray(), ct)
            .ConfigureAwait(false);
        bool retried = false;
        int cursor = 0;
        while (cursor < valid.Count)
        {
            ct.ThrowIfCancellationRequested();
            int size = Math.Min(_writeWindow.Current, valid.Count - cursor);
            var requests = new WriteValueCollection();
            for (int i = 0; i < size; i++)
            {
                var item = valid[cursor + i];
                requests.Add(new WriteValue
                {
                    NodeId = wire[cursor + i],
                    AttributeId = Attributes.Value,
                    Value = new Opc.Ua.DataValue(SiemensOpcUaCodec.WireVariant(item.Request.Def.DataType, item.Value)),
                });
            }
            bool normalSuccess = false;
            double requestMs = 0;
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var response = await ProtocolOperationTiming.MeasureAsync(() => _session.WriteAsync(null, requests, ct)).ConfigureAwait(false);
                requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                StatusCodeCollection wres = response.Results;
                normalSuccess = wres is not null && wres.Count == size && wres.All(Opc.Ua.StatusCode.IsGood);
                if (wres is null || wres.Count != size)
                    for (int i = 0; i < size; i++)
                        results.Add(new WriteResult { TagId = valid[cursor + i].Request.TagId, Status = IgwStatusCode.Bad });
                else
                {
                    using var retryGate = new SemaphoreSlim(Math.Max(1, Math.Min(size, Capabilities.MaxInFlight)));
                    var batchResults = new Task<WriteResult>[size];
                    for (int i = 0; i < size; i++)
                    {
                        var item = valid[cursor + i];
                        var status = new IgwStatusCode(wres[i].Code);
                        if (status.Code == StatusCodes.BadOutOfRange &&
                            item.Request.Def.DataType is IgwDataType.String or IgwDataType.WString)
                        {
                            batchResults[i] = RetryStringOutOfRangeThrottledAsync(
                                retryGate, item.Request, item.Node, item.Value, status, ct);
                        }
                        else
                        {
                            batchResults[i] = Task.FromResult(new WriteResult
                            {
                                TagId = item.Request.TagId,
                                Status = status,
                                Attempts = 1,
                                ActualValue = status.IsGood ? SiemensOpcUaCodec.WrittenValue(item.Request.Def.DataType, item.Value) : null,
                                Error = item.Diagnostic is null
                                    ? null
                                    : StringWriteDiagnostic(item.Request, item.Node, item.Value,
                                        EffectiveStringCapacity(item.Request, item.Node), status, 1),
                            });
                        }
                    }
                    results.AddRange(await Task.WhenAll(batchResults).ConfigureAwait(false));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && size > 1 && IsCapacityError(ex))
            {
                retried = true;
                int before = _writeWindow.Current;
                int reduced = _writeWindow.OnCapacityFailure(size);
                if (reduced != before)
                {
                    Interlocked.Increment(ref _capabilityVersion);
                    QueueCapabilityPersist();
                }
                ReportBatchReduction("Write", size, reduced);
                continue;
            }
            catch (Exception ex)
            {
                // 点位拒写、类型/权限错误和单次服务超时不等于会话已断。
                // 返回逐点质量码，让上层只标记本批写失败；仅真实连接/会话错误继续抛出并触发硬重连。
                if (!TryMapNonTransportWriteFailure(ex, out var status))
                    throw ClassifyReadFailure(ex, size);
                for (int i = 0; i < size; i++)
                {
                    var item = valid[cursor + i];
                    results.Add(new WriteResult
                    {
                        TagId = item.Request.TagId,
                        Status = status,
                        Attempts = 1,
                        Error = item.Diagnostic is null
                            ? null
                            : StringWriteDiagnostic(item.Request, item.Node, item.Value,
                                EffectiveStringCapacity(item.Request, item.Node), status, 1),
                    });
                }
            }
            cursor += size;
            if (!retried && normalSuccess && !ct.IsCancellationRequested && _session.Connected && _writeWindow.OnSuccess(size, requestMs))
            {
                Interlocked.Increment(ref _capabilityVersion);
                QueueCapabilityPersist();
            }
        }
        return results;
    }

    private async Task RefreshPublishedStringLengthsAsync(
        IReadOnlyList<(IgwWriteRequest Request, NodeId Node)> writes,
        CancellationToken ct)
    {
        if (_session is null || !_session.Connected) return;
        NodeId[] stringNodes = writes
            .Where(item => item.Request.Def.DataType is IgwDataType.String or IgwDataType.WString)
            .Select(item => item.Node)
            .GroupBy(node => node.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (stringNodes.Length == 0) return;

        try
        {
            var published = await OpcUaStringLengthResolver.ResolveManyAsync(_session, stringNodes, ct)
                .ConfigureAwait(false);
            foreach (var pair in published)
            {
                if (pair.Value <= 0) continue;
                _stringLengthCache.AddOrUpdate(pair.Key, pair.Value,
                    (_, current) => current > 0 ? Math.Min(current, pair.Value) : pair.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 元数据不可读不能阻断写入；后续 BadOutOfRange 仍会使用最多 10 次的有界探测。
            Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "写前字符串容量",
                $"写入前无法批量读取 MaxStringLength，将使用已知容量或有界自适应：{Redact(FormatDiagnosticException(ex))}"));
        }
    }

    private int? EffectiveStringCapacity(IgwWriteRequest request, NodeId node)
    {
        int? verified = _stringLengthCache.TryGetValue(node.ToString(), out int cached) && cached > 0
            ? cached
            : null;
        return OpcUaStringWriteFallback.EffectiveCapacity(request.Def.StringLength, verified);
    }

    private static string StringWriteDiagnostic(
        IgwWriteRequest request,
        NodeId node,
        object appliedValue,
        int? plcCapacity,
        IgwStatusCode? status,
        int attempts)
    {
        string original = Convert.ToString(request.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        string applied = Convert.ToString(appliedValue, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        int originalUnits = OpcUaStringWriteFallback.EncodedLength(original, request.Def.DataType, request.Def.TextEncoding);
        int appliedUnits = OpcUaStringWriteFallback.EncodedLength(applied, request.Def.DataType, request.Def.TextEncoding);
        string unit = request.Def.DataType == IgwDataType.WString ? "Unicode字符" : "编码字节";
        return $"OPC UA 字符串写入：NodeId={node}，PLC字段长度={(plcCapacity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未公开")} {unit}，" +
               $"原始写入长度={originalUnits} {unit}，实际下发长度={appliedUnits} {unit}，" +
               $"是否截断={(appliedUnits < originalUnits ? "是" : "否")}，尝试={attempts}，状态={(status?.ToString() ?? "待下发")}";
    }

    private async Task<WriteResult> RetryStringOutOfRangeThrottledAsync(
        SemaphoreSlim gate,
        IgwWriteRequest request,
        NodeId node,
        object attemptedValue,
        IgwStatusCode initialStatus,
        CancellationToken ct)
    {
        long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        ProtocolOperationTiming.AddQueue(System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
        try
        {
            return await RetryStringOutOfRangeAsync(request, node, attemptedValue, initialStatus, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WriteResult> RetryStringOutOfRangeAsync(
        IgwWriteRequest request,
        NodeId node,
        object attemptedValue,
        IgwStatusCode initialStatus,
        CancellationToken ct)
    {
        if (_session is null || !_session.Connected)
            return new WriteResult { TagId = request.TagId, Status = IgwStatusCode.BadNotConnected, Attempts = 1 };

        string original = Convert.ToString(attemptedValue, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        int? capacity = request.Def.StringLength is > 0 ? request.Def.StringLength : null;
        string cacheKey = node.ToString();
        bool hasCachedCapacity = _stringLengthCache.TryGetValue(cacheKey, out int cached);
        capacity = OpcUaStringWriteFallback.EffectiveCapacity(
            capacity,
            hasCachedCapacity && cached > 0 ? cached : null);
        if (!hasCachedCapacity)
        {
            try
            {
                capacity = await OpcUaStringLengthResolver.ResolveAsync(_session, node, ct).ConfigureAwait(false);
                _stringLengthCache[cacheKey] = capacity ?? 0;
            }
            catch (Exception ex)
            {
                _stringLengthCache[cacheKey] = 0;
                Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "字符串容量",
                    $"无法从 DataTypeDefinition 读取字段容量，将使用最多 10 次有界自适应写入：{Redact(FormatDiagnosticException(ex))}",
                    cacheKey));
            }
        }

        bool consumeInitial = true;
        var outcome = await OpcUaStringWriteFallback.WriteAsync(
            original,
            request.Def.DataType,
            request.Def.TextEncoding,
            capacity,
            maxAttempts: 10,
            async (candidate, token) =>
            {
                if (consumeInitial)
                {
                    consumeInitial = false;
                    return initialStatus;
                }
                return await WriteScalarStatusAsync(node, candidate, token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        string requested = Convert.ToString(request.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        int requestedUnits = OpcUaStringWriteFallback.EncodedLength(requested, request.Def.DataType, request.Def.TextEncoding);
        int originalUnits = OpcUaStringWriteFallback.EncodedLength(original, request.Def.DataType, request.Def.TextEncoding);
        int appliedUnits = OpcUaStringWriteFallback.EncodedLength(outcome.AppliedValue, request.Def.DataType, request.Def.TextEncoding);
        if (outcome.Status.IsGood && appliedUnits > 0 && outcome.AppliedValue.Length < original.Length)
            _stringLengthCache[cacheKey] = appliedUnits;
        string capacityUnit = request.Def.DataType == IgwDataType.WString ? "Unicode字符" : "编码字节";
        string detail = $"OPC UA 字符串写入：NodeId={cacheKey}，PLC字段长度={(capacity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未公开")} {capacityUnit}，" +
                        $"原始写入长度={requestedUnits} {capacityUnit}，首次下发长度={originalUnits} {capacityUnit}，" +
                        $"实际下发长度={appliedUnits} {capacityUnit}，是否截断={(appliedUnits < requestedUnits ? "是" : "否")}，" +
                        $"尝试={outcome.Attempts}，状态={outcome.Status}";
        Report(new BrowseDiagnosticEvent(
            outcome.Status.IsGood ? BrowseDiagnosticLevel.Information : BrowseDiagnosticLevel.Warning,
            "字符串写入", detail, cacheKey));
        return new WriteResult
        {
            TagId = request.TagId,
            Status = outcome.Status,
            Attempts = outcome.Attempts,
            ActualValue = outcome.Status.IsGood ? outcome.AppliedValue : null,
            Error = detail,
        };
    }

    private async Task<IgwStatusCode> WriteScalarStatusAsync(NodeId node, string value, CancellationToken ct)
    {
        if (_session is null || !_session.Connected) return IgwStatusCode.BadNotConnected;
        try
        {
            NodeId wire = (await WireNodesAsync(_registeredNodes, [node], ct).ConfigureAwait(false))[0];
            var response = await ProtocolOperationTiming.MeasureAsync(() => _session.WriteAsync(null, new WriteValueCollection
            {
                new()
                {
                    NodeId = wire,
                    AttributeId = Attributes.Value,
                    Value = new Opc.Ua.DataValue(new Variant(value)),
                },
            }, ct)).ConfigureAwait(false);
            return response.Results is { Count: 1 }
                ? new IgwStatusCode(response.Results[0].Code)
                : IgwStatusCode.Bad;
        }
        catch (Exception ex)
        {
            if (TryMapNonTransportWriteFailure(ex, out var status)) return status;
            throw ClassifyReadFailure(ex, 1);
        }
    }

    private void ReportBatchReduction(string operation, int attempted, int reduced)
    {
        Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "自适应批量",
            $"{operation} 请求 {attempted} 点超过服务器容量，当前会话降为 {reduced} 点；连接保持在线"));
    }

    private static bool IsCapacityError(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not ServiceResultException service) continue;
            string symbolic = StatusCodes.LookupSymbolicId(service.StatusCode) ?? "";
            if (symbolic.Contains("TooManyOperations", StringComparison.OrdinalIgnoreCase) ||
                symbolic.Contains("RequestTooLarge", StringComparison.OrdinalIgnoreCase) ||
                symbolic.Contains("ResponseTooLarge", StringComparison.OrdinalIgnoreCase) ||
                symbolic.Contains("EncodingLimitsExceeded", StringComparison.OrdinalIgnoreCase) ||
                symbolic.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsServiceTimeout(Exception error) =>
        TryGetServiceTimeoutStatus(error, out _);

    private static bool TryGetServiceTimeoutStatus(Exception error, out IgwStatusCode status)
    {
        bool clrTimeout = false;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                clrTimeout = true;
                continue;
            }
            if (current is ServiceResultException service &&
                service.StatusCode is StatusCodes.BadRequestTimeout or StatusCodes.BadTimeout)
            {
                status = new IgwStatusCode(service.StatusCode);
                return true;
            }
        }
        status = IgwStatusCode.BadTimeout;
        return clrTimeout;
    }

    internal static DriverOperationException ClassifyReadFailure(Exception error, int requestSize)
    {
        if (TryGetServiceTimeoutStatus(error, out var timeoutStatus))
            return new DriverOperationException(
                error.Message,
                DriverFailureScope.Block,
                DriverRetryDisposition.RetrySameConnection,
                timeoutStatus,
                error);

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not ServiceResultException service) continue;
            uint code = service.StatusCode;
            var status = new IgwStatusCode(code);
            if (code == StatusCodes.BadRequestInterrupted)
                return new DriverOperationException(
                    service.Message,
                    DriverFailureScope.Block,
                    DriverRetryDisposition.RetrySameConnection,
                    status,
                    error);
            if (IsCapacityError(service))
                return new DriverOperationException(
                    service.Message,
                    requestSize > 1 ? DriverFailureScope.Capacity : DriverFailureScope.Block,
                    requestSize > 1
                        ? DriverRetryDisposition.ReduceBatchAndRetry
                        : DriverRetryDisposition.RetrySameConnection,
                    status,
                    error);
            if (code is StatusCodes.BadConnectionClosed or
                StatusCodes.BadSecureChannelClosed or
                StatusCodes.BadSecureChannelIdInvalid or
                StatusCodes.BadSessionClosed or
                StatusCodes.BadSessionIdInvalid or
                StatusCodes.BadServerNotConnected or
                StatusCodes.BadNoCommunication or
                StatusCodes.BadCommunicationError or
                StatusCodes.BadNotConnected or
                StatusCodes.BadShutdown)
                return new DriverOperationException(
                    service.Message,
                    DriverFailureScope.Transport,
                    DriverRetryDisposition.RebuildConnection,
                    status,
                    error);

            return new DriverOperationException(
                service.Message,
                DriverFailureScope.Block,
                DriverRetryDisposition.DoNotRetry,
                status,
                error);
        }

        return new DriverOperationException(
            error.Message,
            DriverFailureScope.Block,
            DriverRetryDisposition.DoNotRetry,
            IgwStatusCode.BadDeviceFailure,
            error);
    }

    internal static bool TryMapNonTransportWriteFailure(Exception error, out IgwStatusCode status)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                status = IgwStatusCode.BadTimeout;
                return true;
            }

            if (current is not ServiceResultException serviceError) continue;
            uint code = serviceError.StatusCode;
            status = new IgwStatusCode(code);
            return code is not (
                StatusCodes.BadConnectionClosed or
                StatusCodes.BadSecureChannelClosed or
                StatusCodes.BadSecureChannelIdInvalid or
                StatusCodes.BadSessionClosed or
                StatusCodes.BadSessionIdInvalid or
                StatusCodes.BadServerNotConnected or
                StatusCodes.BadNoCommunication or
                StatusCodes.BadCommunicationError or
                StatusCodes.BadNotConnected or
                StatusCodes.BadShutdown);
        }

        status = IgwStatusCode.BadCommFailure;
        return false;
    }

    public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        if (_session is null || !_session.Connected)
            return ArrayWriteResult.Failed(IgwStatusCode.BadNotConnected, "OPC UA 会话未连接");
        if (request.Binding.Def.Array is not { } definition)
            return ArrayWriteResult.Failed(IgwStatusCode.BadDecodeError, "目标点位不是数组");
        if (!TryNode(request.Binding.Def.Address, out var nodeId))
            return ArrayWriteResult.Failed(IgwStatusCode.BadDecodeError, "OPC UA NodeId 无效");

        try
        {
            var sliceShape = request.Slice.ValidateAndCreateShape(definition.Shape);
            IReadOnlyList<object?>? templates = null;
            if (definition.ElementType is IgwDataType.Struct or IgwDataType.Dtl)
            {
                if (definition.ElementType == IgwDataType.Dtl)
                    await ReadTemplatesAsync(request.Binding.TagId, nodeId, ct).ConfigureAwait(false);
                if (!_arrayTemplates.TryGetValue(request.Binding.TagId, out var wholeTemplates))
                    return ArrayWriteResult.Failed(IgwStatusCode.BadDecodeError,
                        "结构体数组尚未读取，缺少服务器类型编码");
                templates = OpcUaArrayCodec.SliceTemplates(wholeTemplates, definition.Shape, request.Slice);
            }

            var write = new WriteValue
            {
                NodeId = (await WireNodesAsync(_registeredNodes, [nodeId], ct).ConfigureAwait(false))[0],
                AttributeId = Attributes.Value,
                IndexRange = NumericRange(request.Slice, definition.Shape) + (definition.ElementType == IgwDataType.Dt ? ",0:7" : ""),
                Value = new Opc.Ua.DataValue(SiemensOpcUaCodec.WireVariant(definition.ElementType, OpcUaArrayCodec.Encode(request.Value, templates))),
            };
            var response = await ProtocolOperationTiming.MeasureAsync(() => _session.WriteAsync(null, new WriteValueCollection { write }, ct))
                .ConfigureAwait(false);
            StatusCodeCollection statuses = response.Results;
            if (statuses is null || statuses.Count != 1)
                return ArrayWriteResult.Failed(IgwStatusCode.Bad, "OPC UA 数组写响应数量不匹配");
            var status = new IgwStatusCode(statuses[0].Code);
            return status.IsGood
                ? new ArrayWriteResult(status, checked((int)sliceShape.ElementCount), [])
                : ArrayWriteResult.Failed(status, "OPC UA 服务器拒绝数组写入");
        }
        catch (Exception ex)
        {
            if (!TryMapNonTransportWriteFailure(ex, out var status))
                throw ClassifyReadFailure(ex, 1);
            return ArrayWriteResult.Failed(status, ex.Message);
        }
    }

    private static string NumericRange(ArraySlice slice, ArrayShape whole) => string.Join(",",
        Enumerable.Range(0, whole.Rank).Select(i =>
        {
            var start = slice.Start[i] - whole.LowerBounds[i];
            return $"{start}:{start + slice.Lengths[i] - 1}";
        }));

    private bool TryNode(string address, out NodeId nodeId) => _nodeIds.TryGet(address, out nodeId);

    // ---------- 浏览:从 Objects 文件夹递归收集自定义命名空间的变量节点 ----------
    public Task<OpcUaBrowseChildrenResult> BrowseChildrenAsync(
        string nodeId,
        IReadOnlyList<string> parentPathSegments,
        CancellationToken ct)
    {
        if (_session is null || !_session.Connected) throw new IOException("OPC UA 未连接");
        ct.ThrowIfCancellationRequested();
        string root = string.IsNullOrWhiteSpace(nodeId) ? ObjectIds.ObjectsFolder.ToString() : nodeId;
        var source = new SessionBrowseSource(_session, Report, ct);
        Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "按层浏览",
            $"读取直接子节点，父路径={string.Join('.', parentPathSegments)}", root));
        var nodes = OpcUaBrowseWalker.Children(source, root, parentPathSegments);
        Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "按层浏览",
            $"直接子节点读取完成，共 {nodes.Count} 个；未递归后代", root,
            ScannedNodeCount: source.BrowseNodeCount));
        return Task.FromResult(new OpcUaBrowseChildrenResult(nodes));
    }

    public Task<IgwBrowseResult> BrowseBranchAsync(OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct)
    {
        if (_session is null || !_session.Connected) throw new IOException("OPC UA 未连接");
        if (string.IsNullOrWhiteSpace(selectedRoot.NodeId)) throw new ArgumentException("未选择 OPC UA 节点。", nameof(selectedRoot));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var source = new SessionBrowseSource(_session, Report, ct);
        Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "所选分支",
            $"仅扫描用户选择的分支，路径={string.Join('.', selectedRoot.PathSegments)}，上限={limit}", selectedRoot.NodeId));
        var result = OpcUaBrowseWalker.WalkBranch(
            source,
            selectedRoot,
            limit,
            ct,
            ReportWalkProgress,
            progressEvery: 250);
        ReportBrowseSummary(source, result, elapsed.ElapsedMilliseconds, selectedRoot.NodeId);
        return Task.FromResult(result);
    }

    public Task<IGW.Abstractions.BrowseResult> BrowseAsync(CancellationToken ct)
    {
        if (_session is null || !_session.Connected) throw new IOException("OPC UA 未连接");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var namespaces = Enumerable.Range(0, Math.Min(_session.NamespaceUris.Count, 20))
            .Select(index => $"ns={index}:{_session.NamespaceUris.GetString((uint)index)}")
            .ToArray();
        Report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "地址空间",
            $"从 ObjectsFolder({ObjectIds.ObjectsFolder}) 开始浏览，NamespaceCount={_session.NamespaceUris.Count}，Namespaces=[{string.Join(" | ", namespaces)}]"));
        var source = new SessionBrowseSource(_session, Report, ct);
        var result = OpcUaBrowseWalker.Walk(
            source,
            ObjectIds.ObjectsFolder.ToString(),
            10_000,
            ct,
            ReportWalkProgress,
            progressEvery: 250);
        ReportBrowseSummary(source, result, elapsed.ElapsedMilliseconds, ObjectIds.ObjectsFolder.ToString());
        return Task.FromResult(result);
    }

    private void ReportWalkProgress(OpcUaBrowseProgress item)
    {
        var level = item.Stage == OpcUaBrowseProgressStage.Failed
            ? BrowseDiagnosticLevel.Error
            : BrowseDiagnosticLevel.Information;
        string message = item.Stage switch
        {
            OpcUaBrowseProgressStage.Starting => $"浏览开始，根节点={item.CurrentNodeId}",
            OpcUaBrowseProgressStage.Scanning => $"正在扫描节点，路径={item.Path}，深度={item.Depth}",
            OpcUaBrowseProgressStage.Completed => "节点遍历完成",
            OpcUaBrowseProgressStage.Failed => $"节点浏览失败：{item.Error}",
            _ => item.Stage.ToString(),
        };
        Report(new BrowseDiagnosticEvent(level, "节点扫描", message, item.CurrentNodeId,
            item.ScannedNodeCount, item.DiscoveredTagCount, item.ElapsedMilliseconds));
    }

    private void ReportBrowseSummary(SessionBrowseSource source, IgwBrowseResult result, long elapsedMilliseconds, string root)
    {
        Report(new BrowseDiagnosticEvent(
            result.Issues is { Count: > 0 } ? BrowseDiagnosticLevel.Warning : BrowseDiagnosticLevel.Information,
            "浏览汇总",
            $"发现变量={result.Tags.Count}，浏览节点={source.BrowseNodeCount}，Browse服务调用={source.BrowseRequestCount}，类型批次={source.TypeBatchCount}，类型失败={result.Issues?.Count ?? 0}，截断={result.Truncated}",
            root,
            source.BrowseNodeCount,
            result.Tags.Count,
            elapsedMilliseconds));
    }

    internal static DataValueCollection ReadBrowseAttributes(
        ReadValueIdCollection requests, Func<ReadValueIdCollection, DataValueCollection> read)
    {
        var result = new DataValueCollection();
        int window = Math.Min(512, requests.Count);
        while (result.Count < requests.Count)
        {
            int count = Math.Min(window, requests.Count - result.Count);
            var batch = new ReadValueIdCollection(requests.Skip(result.Count).Take(count));
            try
            {
                var values = read(batch);
                if (values is null || values.Count != count)
                    throw new InvalidOperationException("OPC UA 类型属性读取响应数量不匹配");
                if (values.Any(v => v.StatusCode == StatusCodes.BadTooManyOperations))
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                ThrowIfBrowseTransportFailure(values);
                result.AddRange(values);
            }
            catch (Exception ex) when (count > 1 && IsCapacityError(ex))
            {
                window = Math.Max(1, count / 2);
            }
        }
        return result;
    }

    internal static void ThrowIfBrowseTransportFailure(IEnumerable<UaDataValue>? values)
    {
        if (values is null) return;
        foreach (var value in values)
            if (value.StatusCode.Code is StatusCodes.BadCommunicationError or StatusCodes.BadTimeout
                or StatusCodes.BadNotConnected or StatusCodes.BadConnectionClosed
                or StatusCodes.BadSessionIdInvalid or StatusCodes.BadSessionClosed
                or StatusCodes.BadSecureChannelIdInvalid or StatusCodes.BadSecureChannelClosed
                or StatusCodes.BadServerNotConnected or StatusCodes.BadShutdown
                or StatusCodes.BadRequestTimeout or StatusCodes.BadRequestCancelledByClient)
                throw new ServiceResultException(value.StatusCode);
    }

    internal static Dictionary<string, OpcUaBrowseType> ReadBrowseTypes(
        IReadOnlyList<string> nodeIds, Func<ReadValueIdCollection, DataValueCollection> read) =>
        SessionBrowseSource.ReadTypes(nodeIds, read);

    internal static OpcUaBrowseTypeBatch ReadBrowseTypeBatch(
        IReadOnlyList<string> nodeIds, Func<ReadValueIdCollection, DataValueCollection> read)
    {
        var issues = new List<OpcUaBrowseIssue>();
        var types = SessionBrowseSource.ReadTypes(nodeIds, read, issues: issues);
        return new(types, issues);
    }

    internal static Dictionary<string, OpcUaBrowseType> ReadBrowseAtomicTypes(
        IReadOnlyList<string> nodeIds, Func<ReadValueIdCollection, DataValueCollection> read) =>
        SessionBrowseSource.ReadTypes(nodeIds, read, atomicOnly: true);

    private sealed class SessionBrowseSource : IOpcUaBrowseSource, IOpcUaAtomicBrowseSource, IOpcUaPartialBrowseSource
    {
        private readonly Session _session;
        private readonly CancellationToken _ct;
        private readonly Action<BrowseDiagnosticEvent> _report;
        private readonly Dictionary<string, string?> _dataTypeNames = new(StringComparer.Ordinal);
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        private int _browseRequestCount;
        private int _browseNodeCount;
        private int _typeBatchCount;

        public int BrowseRequestCount => _browseRequestCount;
        public int BrowseNodeCount => _browseNodeCount;
        public int TypeBatchCount => _typeBatchCount;

        public SessionBrowseSource(Session session, Action<BrowseDiagnosticEvent> report, CancellationToken ct)
        {
            _session = session;
            _ct = ct;
            _report = report;
        }

        public IReadOnlyList<OpcUaBrowseChild> Children(string nodeId)
        {
            var result = ChildrenOf([nodeId]);
            return result.TryGetValue(nodeId, out var children) ? children : Array.Empty<OpcUaBrowseChild>();
        }

        public IReadOnlyDictionary<string, IReadOnlyList<OpcUaBrowseChild>> ChildrenOf(IReadOnlyList<string> nodeIds)
        {
            var ids = nodeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            var output = ids.ToDictionary(
                id => id,
                _ => (IReadOnlyList<OpcUaBrowseChild>)new List<OpcUaBrowseChild>(),
                StringComparer.Ordinal);
            if (ids.Length == 0) return output;

            try
            {
                foreach (var chunk in ids.Chunk(64))
                {
                    int request = ++_browseRequestCount;
                    _browseNodeCount += chunk.Length;
                    var descriptions = new BrowseDescriptionCollection(chunk.Select(id => new BrowseDescription
                    {
                        NodeId = NodeId.Parse(id),
                        BrowseDirection = BrowseDirection.Forward,
                        ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                        IncludeSubtypes = true,
                        NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                        ResultMask = (uint)BrowseResultMask.All,
                    }));
                    var browseResponse = _session.BrowseAsync(
                            null, null, 0u, descriptions, _ct)
                        .GetAwaiter().GetResult();
                    BrowseResultCollection results = browseResponse.Results;
                    if (results is null || results.Count != chunk.Length)
                        throw new InvalidOperationException($"OPC UA 批量浏览响应数量不匹配(期望 {chunk.Length},实得 {results?.Count ?? 0})");

                    var active = new List<(string NodeId, byte[] ContinuationPoint)>();
                    int references = 0;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        string id = chunk[i];
                        var result = results[i];
                        if (Opc.Ua.StatusCode.IsBad(result.StatusCode))
                            throw new OpcUaBrowseNodeException(id,
                                new ServiceResultException(result.StatusCode));
                        var children = (List<OpcUaBrowseChild>)output[id];
                        AddReferences(result.References, children);
                        references += result.References?.Count ?? 0;
                        if (result.ContinuationPoint is { Length: > 0 } continuationPoint)
                            active.Add((id, continuationPoint));
                    }
                    _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "Browse 批量",
                        $"第 {request} 次服务调用批量浏览 {chunk.Length} 个节点，返回 {references} 个引用，需要续传={active.Count}",
                        chunk[0], _browseNodeCount, ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));

                    int page = 1;
                    while (active.Count > 0)
                    {
                        request = ++_browseRequestCount;
                        var continuationPoints = new ByteStringCollection(active.Select(item => item.ContinuationPoint));
                        var browseNextResponse = _session.BrowseNextAsync(
                                null, false, continuationPoints, _ct)
                            .GetAwaiter().GetResult();
                        results = browseNextResponse.Results;
                        if (results is null || results.Count != active.Count)
                            throw new InvalidOperationException($"OPC UA 批量续传响应数量不匹配(期望 {active.Count},实得 {results?.Count ?? 0})");

                        var next = new List<(string NodeId, byte[] ContinuationPoint)>();
                        references = 0;
                        for (int i = 0; i < active.Count; i++)
                        {
                            string id = active[i].NodeId;
                            var result = results[i];
                            if (Opc.Ua.StatusCode.IsBad(result.StatusCode))
                                throw new OpcUaBrowseNodeException(id,
                                    new ServiceResultException(result.StatusCode));
                            AddReferences(result.References, (List<OpcUaBrowseChild>)output[id]);
                            references += result.References?.Count ?? 0;
                            if (result.ContinuationPoint is { Length: > 0 } continuationPoint)
                                next.Add((id, continuationPoint));
                        }
                        page++;
                        active = next;
                        _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "Browse 分页",
                            $"批量续传第 {page} 页返回 {references} 个引用，仍需续传={active.Count}",
                            chunk[0], _browseNodeCount, ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));
                    }
                }
                return output;
            }
            catch (OpcUaBrowseNodeException ex)
            {
                _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Error, "Browse 请求失败",
                    FormatDiagnosticException(ex.InnerException ?? ex), ex.NodeId, _browseNodeCount,
                    ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));
                throw;
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                string failedNode = ids[0];
                _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Error, "Browse 请求失败",
                    FormatDiagnosticException(ex), failedNode, _browseNodeCount,
                    ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));
                throw new OpcUaBrowseNodeException(failedNode, ex);
            }
        }

        private void AddReferences(ReferenceDescriptionCollection? references, List<OpcUaBrowseChild> children)
        {
            if (references is null) return;
            foreach (var reference in references)
            {
                var child = ExpandedNodeId.ToNodeId(reference.NodeId, _session.NamespaceUris);
                if (child is null || child.NamespaceIndex == 0) continue;
                string name = reference.DisplayName?.Text ?? reference.BrowseName?.Name ?? child.ToString();
                var referenceType = ExpandedNodeId.ToNodeId(reference.ReferenceTypeId, _session.NamespaceUris);
                bool isProperty = referenceType == ReferenceTypeIds.HasProperty;
                children.Add(new OpcUaBrowseChild(child.ToString(), name, reference.NodeClass == NodeClass.Variable, isProperty));
            }
        }

        public IReadOnlyDictionary<string, OpcUaBrowseType> AtomicTypesOf(IReadOnlyList<string> nodeIds) =>
            ReadTypes(nodeIds, batch =>
            {
                _ct.ThrowIfCancellationRequested();
                return _session.ReadAsync(null, 0, TimestampsToReturn.Neither, batch, _ct)
                    .GetAwaiter().GetResult().Results;
            }, _dataTypeNames, atomicOnly: true);

        public OpcUaBrowseType TypeOf(string nodeId)
        {
            var values = TypesOf([nodeId]);
            return values.TryGetValue(nodeId, out var type)
                ? type
                : throw new InvalidOperationException($"缺少 OPC UA 点位 {nodeId} 的数据类型");
        }

        public IReadOnlyDictionary<string, OpcUaBrowseType> TypesOf(IReadOnlyList<string> nodeIds)
            => TypeBatch(nodeIds).Types;

        public OpcUaBrowseTypeBatch TypeBatch(IReadOnlyList<string> nodeIds)
        {
            var result = new Dictionary<string, OpcUaBrowseType>(StringComparer.Ordinal);
            var issues = new List<OpcUaBrowseIssue>();
            if (nodeIds.Count == 0) return new(result, issues);
            _typeBatchCount++;
            {
                int requests = 0, sampleNodes = 0;
                long rpcMs = 0;
                result = ReadTypes(nodeIds, batch =>
                {
                    _ct.ThrowIfCancellationRequested();
                    requests++;
                    sampleNodes += batch.Count(item => item.AttributeId == Attributes.Value);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var response = _session.ReadAsync(null, 0, TimestampsToReturn.Neither, batch, _ct)
                        .GetAwaiter().GetResult();
                    rpcMs += timer.ElapsedMilliseconds;
                    return response.Results;
                }, _dataTypeNames, issues: issues);
                _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "类型读取耗时",
                    $"变量={nodeIds.Count}；属性请求={requests}；按需读取值={sampleNodes}；请求耗时={rpcMs}ms",
                    nodeIds[0], _browseRequestCount, ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));

                string[] stringNodeIds = nodeIds.Where(nodeId =>
                        result.TryGetValue(nodeId, out var type) &&
                        !type.IsArray &&
                        type.DataType is IgwDataType.String or IgwDataType.WString)
                    .ToArray();
                if (stringNodeIds.Length > 0)
                {
                    try
                    {
                        var lengths = OpcUaStringLengthResolver.ResolveManyAsync(
                                _session,
                                stringNodeIds.Select(NodeId.Parse).ToArray(),
                                _ct)
                            .GetAwaiter().GetResult();
                        foreach (string nodeId in stringNodeIds)
                            if (lengths.TryGetValue(nodeId, out int length))
                                result[nodeId] = result[nodeId] with { StringLength = length };
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
                    {
                        _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "字符串容量",
                            $"服务器没有提供可读取的 DataTypeDefinition，字符串点位仍可导入并在首次 BadOutOfRange 时自适应：{FormatDiagnosticException(ex)}",
                            stringNodeIds[0], _browseRequestCount, ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));
                    }
                }

                _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Information, "类型批量读取",
                    $"第 {_typeBatchCount} 批读取 {nodeIds.Count} 个变量类型，成功={result.Count}",
                    nodeIds[0], _browseRequestCount, ElapsedMilliseconds: _elapsed.ElapsedMilliseconds));
            }
            foreach (var issue in issues.Take(20))
                _report(new BrowseDiagnosticEvent(BrowseDiagnosticLevel.Warning, "类型解析失败",
                    issue.Reason, issue.NodeId));
            return new(result, issues);
        }

        internal static Dictionary<string, OpcUaBrowseType> ReadTypes(
            IReadOnlyList<string> nodeIds, Func<ReadValueIdCollection, DataValueCollection> read,
            Dictionary<string, string?>? typeNames = null, bool atomicOnly = false,
            List<OpcUaBrowseIssue>? issues = null)
        {
            typeNames ??= new(StringComparer.Ordinal);
            var result = new Dictionary<string, OpcUaBrowseType>(StringComparer.Ordinal);
            var requests = new ReadValueIdCollection();
            foreach (string nodeId in nodeIds)
            {
                var parsed = NodeId.Parse(nodeId);
                foreach (uint attribute in new[] { Attributes.AccessLevel, Attributes.UserAccessLevel,
                             Attributes.DataType, Attributes.ValueRank, Attributes.ArrayDimensions })
                    requests.Add(new ReadValueId { NodeId = parsed, AttributeId = attribute });
            }
            // Type discovery should not force a fresh PLC value for every ordinary scalar.
            DataValueCollection values = ReadBrowseAttributes(requests, read);
            const int attributesPerNode = 5;
            var customTypes = Enumerable.Range(0, nodeIds.Count)
                .Select(i => AttributeText(values[i * 5 + 2]))
                .Where(id => NodeId.TryParse(id, out var parsed) && parsed.NamespaceIndex != 0)
                .Cast<string>().Distinct(StringComparer.Ordinal).Where(id => !typeNames.ContainsKey(id)).ToArray();
            if (customTypes.Length > 0)
            {
                var names = ReadBrowseAttributes(new ReadValueIdCollection(customTypes.Select(id =>
                    new ReadValueId { NodeId = NodeId.Parse(id), AttributeId = Attributes.BrowseName })), read);
                for (int i = 0; i < customTypes.Length; i++)
                    typeNames[customTypes[i]] = Opc.Ua.StatusCode.IsGood(names[i].StatusCode)
                        ? (names[i].Value as QualifiedName)?.Name : null;
            }

            var sampleRequests = new ReadValueIdCollection();
            for (int i = 0; i < nodeIds.Count; i++)
            {
                int offset = i * attributesPerNode;
                string? typeId = AttributeText(values[offset + 2]);
                int? rank = AttributeInt32(values[offset + 3]);
                int[]? dimensions = AttributeDimensions(values[offset + 4]);
                bool standardPrimitive = NodeId.TryParse(typeId, out var type) && type.NamespaceIndex == 0 &&
                    type.Identifier is uint id && (id is >= 1 and <= 13 || id == 15);
                standardPrimitive |= typeId is not null && typeNames.TryGetValue(typeId, out var name) && SiemensValueCodec.FromTypeName(name ?? "") is not null;
                bool knownShape = rank == ValueRanks.Scalar || (rank >= 1 && dimensions?.Length == rank && dimensions.All(d => d > 0));
                if (!atomicOnly && (!standardPrimitive || !knownShape))
                    sampleRequests.Add(new ReadValueId { NodeId = NodeId.Parse(nodeIds[i]), AttributeId = Attributes.Value });
            }
            var samples = ReadBrowseAttributes(sampleRequests, read);
            var samplesByNode = new Dictionary<string, UaDataValue>(StringComparer.Ordinal);
            for (int i = 0; i < sampleRequests.Count; i++)
                samplesByNode[sampleRequests[i].NodeId.ToString()] = samples[i];
            for (int i = 0; i < nodeIds.Count; i++)
            {
                string nodeId = nodeIds[i];
                try
                {
                int offset = i * attributesPerNode;
                var value = samplesByNode.GetValueOrDefault(NodeId.Parse(nodeId).ToString()) ?? new UaDataValue();
                string? sourceType = AttributeText(values[offset + 2]);
                string? sourceName = sourceType is null ? null : typeNames.GetValueOrDefault(sourceType);
                if (atomicOnly && (sourceName is null || SiemensValueCodec.FromTypeName(sourceName) != IgwDataType.Dtl))
                    continue;
                if (sourceName is not null && SiemensValueCodec.FromTypeName(sourceName) is { } semantic)
                {
                    int rank = AttributeInt32(values[offset + 3]) ?? ValueRanks.Scalar;
                    var dimensions = AttributeDimensions(values[offset + 4]);
                    object? shapeSample = value.Value is Matrix matrix ? matrix.ToArray() : value.Value;
                    if (rank > 0 && (dimensions is null || dimensions.Length != rank || dimensions.Any(d => d <= 0)) && shapeSample is Array sampledArray)
                        dimensions = Enumerable.Range(0, sampledArray.Rank).Select(sampledArray.GetLength).ToArray();
                    if (semantic == IgwDataType.Dt && rank > 0)
                    {
                        if (dimensions is not { Length: > 0 } || dimensions[^1] != 8)
                            throw new InvalidDataException("Siemens DT 的物理数组最后一维必须为 8 字节");
                        dimensions = dimensions[..^1];
                        rank = rank == 1 ? ValueRanks.Scalar : rank - 1;
                    }
                    result[nodeId] = new OpcUaBrowseType(semantic, rank > 0, dimensions,
                        dimensions is null ? null : new int[dimensions.Length], ValueRank: rank,
                        DataTypeNodeId: sourceType, DataTypeName: sourceName,
                        PhysicalValueRank: AttributeInt32(values[offset + 3]), PhysicalDimensions: AttributeDimensions(values[offset + 4]),
                        Access: MapBrowseAccess(AccessLevelValue(values[offset]), AccessLevelValue(values[offset + 1])));
                    continue;
                }
                if (sourceName is not null && new[] { "POINTER", "ANY", "VARIANT", "BLOCK_FB", "BLOCK_FC", "BLOCK_DB", "BLOCK_SDB" }.Contains(sourceName.ToUpperInvariant()))
                    throw new NotSupportedException($"PLC 类型 {sourceName} 不支持 OPC UA 值访问");
                result[nodeId] = DescribeType(value, nodeId, AttributeInt32(values[offset + 3]),
                    AttributeDimensions(values[offset + 4]), AttributeText(values[offset + 2]))
                    with { Access = MapBrowseAccess(AccessLevelValue(values[offset]), AccessLevelValue(values[offset + 1])) };
                }
                catch (Exception ex) when (issues is not null && ex is NotSupportedException or InvalidDataException or InvalidOperationException)
                {
                    issues.Add(new OpcUaBrowseIssue(nodeId, nodeId, Array.Empty<string>(), ex.Message));
                }
            }
            return result;
        }

        private static byte? AccessLevelValue(UaDataValue value)
        {
            if (Opc.Ua.StatusCode.IsBad(value.StatusCode)) return null;
            return value.Value switch
            {
                byte item => item,
                sbyte item => unchecked((byte)item),
                ushort item when item <= byte.MaxValue => (byte)item,
                short item when item is >= byte.MinValue and <= byte.MaxValue => (byte)item,
                uint item when item <= byte.MaxValue => (byte)item,
                int item when item is >= byte.MinValue and <= byte.MaxValue => (byte)item,
                _ => null,
            };
        }

        private static int? AttributeInt32(UaDataValue value)
        {
            if (Opc.Ua.StatusCode.IsBad(value.StatusCode)) return null;
            return value.Value switch
            {
                int item => item,
                short item => item,
                long item when item is >= int.MinValue and <= int.MaxValue => (int)item,
                _ => null,
            };
        }

        private static string? AttributeText(UaDataValue value)
        {
            if (Opc.Ua.StatusCode.IsBad(value.StatusCode) || value.Value is null) return null;
            return value.Value.ToString();
        }

        private static int[]? AttributeDimensions(UaDataValue value)
        {
            if (Opc.Ua.StatusCode.IsBad(value.StatusCode) || value.Value is null) return null;
            return value.Value switch
            {
                uint[] values => values.Select(item => checked((int)item)).ToArray(),
                int[] values => values.ToArray(),
                Array values => values.Cast<object?>()
                    .Select(item => Convert.ToInt32(item, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray(),
                _ => null,
            };
        }

        private static OpcUaBrowseType DescribeType(
            UaDataValue dataValue,
            string nodeId,
            int? declaredValueRank,
            IReadOnlyList<int>? declaredDimensions,
            string? dataTypeNodeId)
        {
            var sample = Opc.Ua.StatusCode.IsBad(dataValue.StatusCode) ? null : dataValue.Value;
            var info = sample is null ? null : dataValue.WrappedValue.TypeInfo;
            var builtIn = info?.BuiltInType ?? BuiltInType.Null;
            // DataType is the contract; Value may be null, stale or unavailable during browsing.
            if (NodeId.TryParse(dataTypeNodeId, out var declaredType) &&
                declaredType.NamespaceIndex == 0 && declaredType.Identifier is uint id && id is >= 1 and <= 25)
                builtIn = (BuiltInType)id;
            var dataType = builtIn == BuiltInType.Null ? MapType(sample) : MapBuiltInType(builtIn);
            int? valueRank = declaredValueRank ?? (info?.ValueRank >= ValueRanks.OneDimension ? info.ValueRank : null);
            // UA ByteString is a scalar byte buffer, despite its CLR representation being byte[].
            bool byteBuffer = builtIn == BuiltInType.ByteString && sample is not byte[][] &&
                (valueRank is null || valueRank < ValueRanks.OneDimension);
            if (byteBuffer)
                return new OpcUaBrowseType(IgwDataType.ByteArray, false, ValueRank: valueRank, DataTypeNodeId: dataTypeNodeId);
            bool isArray = valueRank >= ValueRanks.OneDimension || sample is Array;
            if (sample is Array array)
            {
                var dimensions = declaredDimensions is { Count: > 0 } && declaredDimensions.All(d => d > 0)
                    ? declaredDimensions.ToArray()
                    : Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
                var lowerBounds = Enumerable.Range(0, array.Rank).Select(array.GetLowerBound).ToArray();
                var structure = dataType == IgwDataType.Struct
                    ? OpcUaArrayCodec.TryInferStructure(sample, nodeId)
                    : null;
                if (dataType == IgwDataType.Struct && structure is null)
                    throw new InvalidDataException($"OPC UA 结构体数组 {nodeId} 缺少可解码的服务器类型定义，不能从原始二进制推断成员");
                return new OpcUaBrowseType(dataType, true, dimensions, lowerBounds, structure,
                    ValueRank: valueRank ?? array.Rank, DataTypeNodeId: dataTypeNodeId);
            }
            if (isArray && declaredDimensions is { Count: > 0 })
                return new OpcUaBrowseType(dataType, true, declaredDimensions.ToArray(),
                    new int[declaredDimensions.Count], ValueRank: valueRank, DataTypeNodeId: dataTypeNodeId);
            return new OpcUaBrowseType(dataType, isArray, ValueRank: valueRank, DataTypeNodeId: dataTypeNodeId);
        }

    }

    internal static TagAccess MapBrowseAccess(byte? accessLevel, byte? userAccessLevel)
    {
        byte effective = userAccessLevel ?? accessLevel ?? AccessLevels.CurrentRead;
        bool canRead = (effective & AccessLevels.CurrentRead) != 0;
        bool canWrite = (effective & AccessLevels.CurrentWrite) != 0;
        return (canRead, canWrite) switch
        {
            (true, true) => TagAccess.ReadWrite,
            (false, true) => TagAccess.Write,
            _ => TagAccess.Read,
        };
    }

    private static IgwDataType MapType(object? v) => v switch
    {
        bool => IgwDataType.Bool, sbyte => IgwDataType.SInt, byte => IgwDataType.Byte,
        short => IgwDataType.Int16, ushort => IgwDataType.UInt16, int => IgwDataType.Int32, uint => IgwDataType.UInt32,
        long => IgwDataType.Int64, ulong => IgwDataType.UInt64, float => IgwDataType.Float, double => IgwDataType.Double,
        string => IgwDataType.String, byte[] => IgwDataType.ByteArray,
        _ => throw new InvalidOperationException("服务器未提供可识别的 OPC UA 数据类型或有效样本"),
    };

    private static IgwDataType MapBuiltInType(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => IgwDataType.Bool,
        BuiltInType.SByte => IgwDataType.SInt,
        BuiltInType.Byte => IgwDataType.Byte,
        BuiltInType.Int16 => IgwDataType.Int16,
        BuiltInType.UInt16 => IgwDataType.UInt16,
        BuiltInType.Int32 => IgwDataType.Int32,
        BuiltInType.UInt32 => IgwDataType.UInt32,
        BuiltInType.Int64 => IgwDataType.Int64,
        BuiltInType.UInt64 => IgwDataType.UInt64,
        BuiltInType.Float => IgwDataType.Float,
        BuiltInType.Double => IgwDataType.Double,
        BuiltInType.ByteString => IgwDataType.ByteArray,
        BuiltInType.String => IgwDataType.String,
        BuiltInType.DateTime => IgwDataType.DateTime,
        BuiltInType.ExtensionObject => IgwDataType.Struct,
        _ => throw new InvalidOperationException($"暂不支持 OPC UA 数据类型 {type}"),
    };

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
