#nullable enable
using System.IO;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

#pragma warning disable CS0618 // OPC Foundation 1.5 keeps these compatibility APIs for net8 hosts.

public sealed record OpcUaNodeDescriptor(string NodeId, string DisplayName, bool IsFolder, SimulationDataType DataType = SimulationDataType.Unknown);

/// <summary>一个模拟 PLC 的 OPC UA 端点：来源通道、监听地址和其中的设备/标记数量。</summary>
public sealed record OpcUaSimulationEndpoint(string ChannelName, string Url, int DeviceCount, int TagCount, string Profile = "");

public sealed class OpcUaSimulationServer : IAsyncDisposable
{
    private readonly List<(ApplicationInstance Application, SimulationStandardServer Server)> _servers = new();
    private readonly List<OpcUaSimulationEndpoint> _endpoints = new();

    /// <summary>已启动的端点。导入的工程包含多个 PLC(通道)时，每个 PLC 独立一个端点，端口从起始端口依次递增。</summary>
    public IReadOnlyList<OpcUaSimulationEndpoint> Endpoints => _endpoints;

    public static IReadOnlyList<OpcUaNodeDescriptor> Describe(SimulationProject project)
    {
        var result = new List<OpcUaNodeDescriptor>();
        foreach (var device in project.Devices)
        {
            result.Add(new(device.Name, device.Name, true));
            foreach (var group in device.Groups) AddGroup(group, result);
        }
        return result;
    }

    private static void AddGroup(SimulationGroup group, List<OpcUaNodeDescriptor> result)
    {
        result.Add(new(group.SymbolPath, group.Name, true));
        result.AddRange(group.Tags.Select(x => new OpcUaNodeDescriptor(OpcUaSimulationAddress.DisplayNodeId(x), x.Name, false, x.DataType)));
        foreach (var child in group.Groups) AddGroup(child, result);
    }

    /// <summary>
    /// 按来源通道拆分端点：现场每个通道是一台独立 PLC，不同 PLC 常有完全相同的 DB 地址(如心跳)，
    /// 放进同一个服务器会互相覆盖。只有一个通道(或手工建的工程)时保持单端点。
    /// </summary>
    public static IReadOnlyList<(string ChannelName, IReadOnlyList<SimulationDevice> Devices)> PlanEndpoints(SimulationProject project)
    {
        var channels = project.Devices
            .GroupBy(device => device.SourceChannelName ?? string.Empty, StringComparer.Ordinal)
            .Select(group => (group.Key, (IReadOnlyList<SimulationDevice>)group.ToList()))
            .ToList();
        return channels.Count <= 1
            ? new[] { (channels.FirstOrDefault().Key ?? string.Empty, (IReadOnlyList<SimulationDevice>)project.Devices.ToList()) }
            : channels;
    }

    /// <summary>各端点的运行统计；未设置容量档位时仍会统计调用次数。</summary>
    public IReadOnlyList<OpcUaSimulationMetrics> SnapshotMetrics() =>
        _servers.Select((entry, index) => entry.Server.Snapshot(_endpoints[index].ChannelName)).ToList();

    public Task StartAsync(SimulationProject project, SimulationValueStore store, int port, string pkiDirectory, CancellationToken cancellationToken = default) =>
        StartAsync(project, store, port, pkiDirectory, (OpcUaSimulationLimits?)null, cancellationToken);

    public Task StartAsync(SimulationProject project, SimulationValueStore store, int port, string pkiDirectory, OpcUaSimulationLimits? limits, CancellationToken cancellationToken = default) =>
        StartAsync(project, store, port, pkiDirectory, _ => limits, cancellationToken);

    /// <summary>按来源通道(PLC)分别指定性能档位；现场不同 PLC 型号可混合模拟。</summary>
    public async Task StartAsync(SimulationProject project, SimulationValueStore store, int port, string pkiDirectory, Func<string, OpcUaSimulationLimits?> limitsFor, CancellationToken cancellationToken = default)
    {
        if (_servers.Count > 0) throw new InvalidOperationException("OPC UA 服务已启动");
        var plan = PlanEndpoints(project);
        if (port + plan.Count - 1 > 65535) throw new ArgumentOutOfRangeException(nameof(port), $"需要 {plan.Count} 个连续端口");
        try
        {
            for (int index = 0; index < plan.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int endpointPort = port + index;
                var limits = limitsFor(plan[index].ChannelName);
                var config = BuildConfiguration(endpointPort, pkiDirectory, limits);
                await config.Validate(ApplicationType.Server).ConfigureAwait(false);
                config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;
                var application = new ApplicationInstance { ApplicationName = "PLC Simulator OPC UA", ApplicationType = ApplicationType.Server, ApplicationConfiguration = config };
                await application.CheckApplicationInstanceCertificatesAsync(true, 240).ConfigureAwait(false);
                var server = new SimulationStandardServer(plan[index].Devices, store, limits);
                await application.StartAsync(server).ConfigureAwait(false);
                _servers.Add((application, server));
                _endpoints.Add(new OpcUaSimulationEndpoint(
                    plan[index].ChannelName,
                    $"opc.tcp://localhost:{endpointPort}",
                    plan[index].Devices.Count,
                    plan[index].Devices.Sum(device => device.EnumerateTags().Count()),
                    limits?.Name ?? "不限"));
            }
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ApplicationConfiguration BuildConfiguration(int port, string pki, OpcUaSimulationLimits? limits) => new()
    {
        ApplicationName = "PLC Simulator OPC UA",
        ApplicationUri = "urn:localhost:PLC:Simulator",
        ApplicationType = ApplicationType.Server,
        ServerConfiguration = new ServerConfiguration
        {
            BaseAddresses = { $"opc.tcp://localhost:{port}" },
            SecurityPolicies = new ServerSecurityPolicyCollection { new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None } },
            UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) },
            DiagnosticsEnabled = true,
            MaxSessionCount = limits?.MaxSessions ?? 100,
            MinSessionTimeout = 1000,
            MaxSessionTimeout = 3600000,
            // 现场一个工位的点位可以上千，网关按 1000 个一批建监视项，服务器限额不能比这更小。
            MaxSubscriptionCount = limits is null ? 1000 : (int)Math.Min(1000L, (long)limits.MaxLargeSubscriptions + limits.MaxSmallSubscriptions),
            MinPublishingInterval = limits?.MinPublishingIntervalMs ?? 0,
            MaxPublishRequestCount = 100,
            MaxMessageQueueSize = 10000,
            MaxNotificationQueueSize = 10000,
        },
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=PLC Simulator OPC UA, O=PlcSimulator" },
            TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
            TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
            RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
            AutoAcceptUntrustedCertificates = true,
            AddAppCertToTrustedStore = true,
            RejectSHA1SignedCertificates = false,
            MinimumCertificateKeySize = 2048,
        },
        TransportConfigurations = new TransportConfigurationCollection(),
        TransportQuotas = new TransportQuotas { OperationTimeout = 120000, MaxMessageSize = 64 * 1024 * 1024, MaxArrayLength = 1_000_000, MaxStringLength = 16 * 1024 * 1024, MaxByteStringLength = 16 * 1024 * 1024 },
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        TraceConfiguration = new TraceConfiguration(),
    };

    public ValueTask DisposeAsync()
    {
        foreach (var (_, server) in _servers)
        {
            try { server.Stop(); } catch { }
            try { server.Dispose(); } catch { }
        }
        _servers.Clear();
        _endpoints.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class SimulationStandardServer : StandardServer
    {
        private readonly IReadOnlyList<SimulationDevice> _devices;
        private readonly SimulationValueStore _store;
        private readonly OpcUaSimulationLimits? _limits;
        private readonly SemaphoreSlim? _serviceGate;
        private readonly object _statsGate = new();
        // 订阅在首次创建监视项时定型：一次超过小订阅容量才算大订阅，之后不能再转换(与西门子说明一致，取最严格的理解)。
        private readonly Dictionary<uint, bool> _subscriptionIsLarge = new();
        private long _maxReadNodes, _readCalls, _readNodes, _writeCalls, _writeNodes, _createCalls, _createdItems, _rejectedItems, _rejectedSubscriptions, _queuedCalls;
        private int _peakItems;
        private double _maxQueueMs, _totalQueueMs;

        public SimulationStandardServer(IReadOnlyList<SimulationDevice> devices, SimulationValueStore store, OpcUaSimulationLimits? limits)
        {
            _devices = devices; _store = store; _limits = limits;
            if (limits is { ServiceConcurrency: > 0 and < int.MaxValue }) _serviceGate = new SemaphoreSlim(limits.ServiceConcurrency);
        }

        private OpcUaSimulationNodeManager? _nodes;

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            _nodes = new OpcUaSimulationNodeManager(server, configuration, _devices, _store, _limits?.MinSamplingIntervalMs ?? 0);
            return new(server, configuration, null, new INodeManager[] { _nodes });
        }

        /// <summary>整读 UDT 数组的耗时按成员数估算，而不是按“一个节点”。</summary>
        private double ReadCostMs(ReadValueIdCollection? nodesToRead)
        {
            if (_limits is null || nodesToRead is null) return 0;
            double cost = _limits.ReadBaseMs;
            foreach (var request in nodesToRead)
            {
                if (_nodes is not null && _nodes.TryGetStructArray(request.NodeId, out int elements, out int members))
                {
                    cost += CountElements(request.IndexRange, elements) * members * _limits.StructArrayPerMemberMs;
                    continue;
                }
                cost += _limits.ReadPerNodeMs;
            }
            return cost;
        }

        private static int CountElements(string? indexRange, int total)
        {
            if (string.IsNullOrWhiteSpace(indexRange)) return total;
            string[] parts = indexRange.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int first) && int.TryParse(parts[1], out int last))
                return Math.Max(1, last - first + 1);
            return int.TryParse(indexRange, out _) ? 1 : total;
        }

        public OpcUaSimulationMetrics Snapshot(string channel)
        {
            var (count, large, items) = SubscriptionState();
            lock (_statsGate)
                return new(channel, _readCalls, _readNodes, _writeCalls, _writeNodes, _createCalls, _createdItems, _rejectedItems, _rejectedSubscriptions,
                    count, large, items, _peakItems, _maxQueueMs, _totalQueueMs, _queuedCalls, _maxReadNodes);
        }

        private (int Count, int Large, int Items) SubscriptionState()
        {
            var live = CurrentInstance?.SubscriptionManager?.GetSubscriptions();
            if (live is null) return (0, 0, 0);
            int items = 0, large = 0;
            lock (_subscriptionIsLarge)
            {
                var ids = new HashSet<uint>();
                foreach (var subscription in live)
                {
                    ids.Add(subscription.Id);
                    items += subscription.MonitoredItemCount;
                    if (_subscriptionIsLarge.TryGetValue(subscription.Id, out bool isLarge) && isLarge) large++;
                }
                // 会话关闭时服务器直接删除订阅，不经过 DeleteSubscriptions，这里顺带清理。
                foreach (uint stale in _subscriptionIsLarge.Keys.Where(id => !ids.Contains(id)).ToList()) _subscriptionIsLarge.Remove(stale);
            }
            return (live.Count, large, items);
        }

        private async Task<T> ServeAsync<T>(double costMs, Func<Task<T>> call, CancellationToken ct)
        {
            if (_serviceGate is null && costMs <= 0) return await call().ConfigureAwait(false);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            if (_serviceGate is not null) await _serviceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                double queueMs = waited.Elapsed.TotalMilliseconds;
                lock (_statsGate)
                {
                    _queuedCalls++; _totalQueueMs += queueMs;
                    if (queueMs > _maxQueueMs) _maxQueueMs = queueMs;
                }
                if (costMs >= 1) await Task.Delay(TimeSpan.FromMilliseconds(costMs), ct).ConfigureAwait(false);
                return await call().ConfigureAwait(false);
            }
            finally { _serviceGate?.Release(); }
        }

        public override Task<ReadResponse> ReadAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader, double maxAge,
            TimestampsToReturn timestampsToReturn, ReadValueIdCollection nodesToRead, CancellationToken ct)
        {
            int count = nodesToRead?.Count ?? 0;
            // 服务器能力、命名空间等内部节点不计入 PLC 读取负载。
            bool plcRead = nodesToRead?.Any(node => node.NodeId.NamespaceIndex > 0) == true;
            if (plcRead) lock (_statsGate) { _readCalls++; _readNodes += count; _maxReadNodes = Math.Max(_maxReadNodes, count); }
            double cost = plcRead ? ReadCostMs(nodesToRead) : 0;
            return ServeAsync(cost, () => base.ReadAsync(secureChannelContext, requestHeader, maxAge, timestampsToReturn, nodesToRead, ct), ct);
        }

        public override Task<WriteResponse> WriteAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader,
            WriteValueCollection nodesToWrite, CancellationToken ct)
        {
            int count = nodesToWrite?.Count ?? 0;
            lock (_statsGate) { _writeCalls++; _writeNodes += count; }
            double cost = _limits is null ? 0 : _limits.WriteBaseMs + _limits.WritePerNodeMs * count;
            return ServeAsync(cost, () => base.WriteAsync(secureChannelContext, requestHeader, nodesToWrite, ct), ct);
        }

        public override Task<RegisterNodesResponse> RegisterNodesAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader,
            NodeIdCollection nodesToRegister, CancellationToken ct)
        {
            double cost = _limits is null ? 0 : _limits.RegisterBaseMs + _limits.RegisterPerNodeMs * (nodesToRegister?.Count ?? 0);
            return ServeAsync(cost, () => base.RegisterNodesAsync(secureChannelContext, requestHeader, nodesToRegister, ct), ct);
        }

        public override Task<CreateSubscriptionResponse> CreateSubscriptionAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader,
            double requestedPublishingInterval, uint requestedLifetimeCount, uint requestedMaxKeepAliveCount, uint maxNotificationsPerPublish,
            bool publishingEnabled, byte priority, CancellationToken ct)
        {
            if (_limits is not null)
            {
                var (count, large, _) = SubscriptionState();
                // 新订阅在建监视项之前都按小订阅占位。
                if (count - large >= _limits.MaxSmallSubscriptions)
                {
                    lock (_statsGate) _rejectedSubscriptions++;
                    throw new ServiceResultException(StatusCodes.BadTooManySubscriptions);
                }
            }
            return base.CreateSubscriptionAsync(secureChannelContext, requestHeader, requestedPublishingInterval, requestedLifetimeCount,
                requestedMaxKeepAliveCount, maxNotificationsPerPublish, publishingEnabled, priority, ct);
        }

        public override async Task<CreateMonitoredItemsResponse> CreateMonitoredItemsAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader,
            uint subscriptionId, TimestampsToReturn timestampsToReturn, MonitoredItemCreateRequestCollection itemsToCreate, CancellationToken ct)
        {
            int requested = itemsToCreate?.Count ?? 0;
            lock (_statsGate) _createCalls++;
            if (_limits is null || requested == 0)
            {
                var plain = await base.CreateMonitoredItemsAsync(secureChannelContext, requestHeader, subscriptionId, timestampsToReturn, itemsToCreate, ct).ConfigureAwait(false);
                lock (_statsGate) _createdItems += requested;
                return plain;
            }

            var (_, largeCount, totalItems) = SubscriptionState();
            int current = CurrentInstance?.SubscriptionManager?.GetSubscriptions()
                .FirstOrDefault(subscription => subscription.Id == subscriptionId)?.MonitoredItemCount ?? 0;
            bool isLarge;
            lock (_subscriptionIsLarge)
            {
                if (!_subscriptionIsLarge.TryGetValue(subscriptionId, out isLarge))
                {
                    isLarge = requested > _limits.SmallSubscriptionItems && largeCount < _limits.MaxLargeSubscriptions;
                    _subscriptionIsLarge[subscriptionId] = isLarge;
                }
            }
            int capacity = isLarge ? _limits.MaxItemsPerSubscription : Math.Min(_limits.SmallSubscriptionItems, _limits.MaxItemsPerSubscription);
            int allowed = Math.Clamp(capacity - current, 0, requested);
            var accepted = new MonitoredItemCreateRequestCollection(itemsToCreate!.Take(allowed));
            double cost = _limits.MonitoredItemsBaseMs + _limits.MonitoredItemsPerItemMs * allowed;
            CreateMonitoredItemsResponse response = allowed > 0
                ? await ServeAsync(cost, () => base.CreateMonitoredItemsAsync(secureChannelContext, requestHeader, subscriptionId, timestampsToReturn, accepted, ct), ct).ConfigureAwait(false)
                : new CreateMonitoredItemsResponse { ResponseHeader = new ResponseHeader { Timestamp = DateTime.UtcNow, RequestHandle = requestHeader.RequestHandle }, Results = new MonitoredItemCreateResultCollection(), DiagnosticInfos = new DiagnosticInfoCollection() };
            for (int index = allowed; index < requested; index++)
            {
                response.Results.Add(new MonitoredItemCreateResult { StatusCode = StatusCodes.BadTooManyMonitoredItems });
                if (response.DiagnosticInfos is { Count: > 0 }) response.DiagnosticInfos.Add(null);
            }
            lock (_statsGate)
            {
                _createdItems += allowed;
                _rejectedItems += requested - allowed;
                _peakItems = Math.Max(_peakItems, totalItems + allowed);
            }
            return response;
        }

        public override async Task<DeleteSubscriptionsResponse> DeleteSubscriptionsAsync(SecureChannelContext secureChannelContext, RequestHeader requestHeader,
            UInt32Collection subscriptionIds, CancellationToken ct)
        {
            var response = await base.DeleteSubscriptionsAsync(secureChannelContext, requestHeader, subscriptionIds, ct).ConfigureAwait(false);
            lock (_subscriptionIsLarge)
                foreach (uint id in subscriptionIds ?? new UInt32Collection()) _subscriptionIsLarge.Remove(id);
            return response;
        }
    }
}
