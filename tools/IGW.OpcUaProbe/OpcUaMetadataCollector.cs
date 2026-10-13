using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace IGW.OpcUaProbe;

public sealed class OpcUaMetadataCollector
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });
    private const int BrowseBatchSize = 64;
    private const int AttributeBatchSize = 32;

    public async Task<OpcUaProbeReport> CollectAsync(
        ProbeOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Validate(options);
        Session? session = null;
        SourceBoundTcpRelay? relay = null;
        try
        {
            string connectionUrl = options.Url;
            if (!string.IsNullOrWhiteSpace(options.LocalIp))
            {
                relay = SourceBoundTcpRelay.Start(
                    options.Url, options.LocalIp, TimeSpan.FromMilliseconds(options.TimeoutMs),
                    message => Log(log, message));
                connectionUrl = relay.ConnectionUrl();
                Log(log, $"已建立源地址绑定转发；本机IPv4={relay.LocalIp}；目标={relay.Target}；SDK连接={connectionUrl}");
            }
            else
            {
                Log(log, "未指定本机IPv4，由 Windows 路由选择源网卡。");
            }
            Log(log, $"开始连接 {options.Url}；安全策略={options.SecurityPolicy}；身份={(options.Anonymous ? "Anonymous" : "UserName")}");
            var configuration = await BuildConfigurationAsync(options, log, cancellationToken).ConfigureAwait(false);
            bool useSecurity = !string.Equals(options.SecurityPolicy, "None", StringComparison.OrdinalIgnoreCase);
            var endpoint = await CoreClientUtils.SelectEndpointAsync(
                    configuration, connectionUrl, useSecurity, options.TimeoutMs, Telemetry, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("服务器未返回可用 OPC UA Endpoint。");
            string discoveredEndpoint = endpoint.EndpointUrl;
            Log(log, $"发现端点 {discoveredEndpoint}；SecurityMode={endpoint.SecurityMode}；SecurityPolicy={endpoint.SecurityPolicyUri}");
            endpoint.EndpointUrl = connectionUrl;
            var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
            IUserIdentity identity = options.Anonymous
                ? new UserIdentity()
                : new UserIdentity(new UserNameIdentityToken
                {
                    UserName = options.Username,
                    DecryptedPassword = System.Text.Encoding.UTF8.GetBytes(options.Password ?? string.Empty),
                });
            session = (Session)await new DefaultSessionFactory(Telemetry).CreateAsync(
                    configuration, configuredEndpoint, false, "IGW OPC UA Read-Only Probe", 60_000,
                    identity, null, cancellationToken)
                .ConfigureAwait(false);
            string sessionEndpoint = session.ConfiguredEndpoint?.EndpointUrl?.ToString() ?? connectionUrl;
            Log(log, $"会话建立；SessionId={session.SessionId}；NamespaceCount={session.NamespaceUris.Count}；最终端点={sessionEndpoint}");

            var namespaces = Enumerable.Range(0, session.NamespaceUris.Count)
                .Select(index => new ProbeNamespaceReport(
                    checked((ushort)index), session.NamespaceUris.GetString((uint)index) ?? string.Empty))
                .ToArray();
            var crawl = await CrawlAsync(session, options, namespaces, log, cancellationToken).ConfigureAwait(false);
            var dataTypes = await ReadReferencedDataTypesAsync(
                session, crawl.Nodes, namespaces, log, cancellationToken).ConfigureAwait(false);
            var serverCapabilities = await ReadServerCapabilitiesAsync(session, log, cancellationToken).ConfigureAwait(false);
            var writeDiagnostics = BuildWriteDiagnostics(crawl.Nodes, serverCapabilities, dataTypes);
            var summary = BuildSummary(crawl.Nodes, crawl.Truncated);
            var certificate = Certificate(endpoint.ServerCertificate);
            var connection = new ProbeConnectionReport(
                options.Url,
                discoveredEndpoint,
                sessionEndpoint,
                endpoint.Server?.ApplicationName?.Text ?? string.Empty,
                endpoint.Server?.ApplicationUri ?? string.Empty,
                endpoint.Server?.ProductUri ?? string.Empty,
                endpoint.SecurityMode.ToString(),
                endpoint.SecurityPolicyUri ?? string.Empty,
                options.Anonymous ? "Anonymous" : $"UserName:{options.Username}",
                certificate?.Subject,
                certificate?.Issuer,
                certificate?.GetCertHashString(HashAlgorithmName.SHA256),
                certificate?.NotBefore,
                certificate?.NotAfter,
                string.IsNullOrWhiteSpace(options.LocalIp) ? null : options.LocalIp,
                connectionUrl);
            Log(log, $"采集完成；节点={summary.TotalNodes}；变量={summary.Variables}；Siemens候选={summary.SiemensSymbolCandidates}；截断={summary.Truncated}");
            return new OpcUaProbeReport
            {
                Connection = connection,
                Namespaces = namespaces,
                Nodes = crawl.Nodes,
                DataTypes = dataTypes,
                Summary = summary,
                WriteDiagnostics = writeDiagnostics,
            };
        }
        finally
        {
            if (session is not null)
            {
                try { await session.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                try { session.Dispose(); } catch { }
            }
            if (relay is not null)
            {
                try { await relay.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private static void Validate(ProbeOptions options)
    {
        if (!Uri.TryCreate(options.Url?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            throw new ArgumentException("必须提供有效的 opc.tcp:// 地址。", nameof(options));
        if (options.TimeoutMs is < 1_000 or > 120_000)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeoutMs 必须在 1000 到 120000 之间。");
        if (options.MaxNodes is < 1 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxNodes 必须在 1 到 2000000 之间。");
        if (options.MaxDepth is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth 必须在 1 到 1024 之间。");
        if (!string.IsNullOrWhiteSpace(options.LocalIp)
            && (!System.Net.IPAddress.TryParse(options.LocalIp, out var localAddress)
                || localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new ArgumentException("LocalIp 必须是有效的本机 IPv4 地址。", nameof(options));
    }

    private static async Task<ApplicationConfiguration> BuildConfigurationAsync(
        ProbeOptions options,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        string pki = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IGW", "OpcUaProbe", "pki");
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "IGW OPC UA Read-Only Probe",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:localhost:IGW:OpcUaReadOnlyProbe",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = "CN=IGW OPC UA Read-Only Probe",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "issuer"),
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "rejected"),
                },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = options.TimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);
        configuration.CertificateValidator.CertificateValidation += (_, args) =>
        {
            if (!options.AcceptUntrustedCertificate) return;
            args.Accept = true;
            Log(log, $"只读探针临时接受服务器证书；Subject={args.Certificate?.Subject ?? "(无)"}；Thumbprint={args.Certificate?.Thumbprint ?? "(无)"}");
        };
        var application = new ApplicationInstance(Telemetry) { ApplicationConfiguration = configuration };
        await application.CheckApplicationInstanceCertificatesAsync(true).ConfigureAwait(false);
        return configuration;
    }

    private static async Task<CrawlResult> CrawlAsync(
        Session session,
        ProbeOptions options,
        IReadOnlyList<ProbeNamespaceReport> namespaces,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        string root = ObjectIds.ObjectsFolder.ToString();
        var nodes = new Dictionary<string, NodeAccumulator>(StringComparer.Ordinal)
        {
            [root] = new(root, NodeClass.Object, "Objects", "Objects", ["Objects"]),
        };
        var queue = new Queue<PendingNode>();
        queue.Enqueue(new PendingNode(root, ["Objects"], 0));
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var continuationRetries = new Dictionary<string, int>(StringComparer.Ordinal);
        var browseWindow = new ProbeBrowseCapacityWindow(BrowseBatchSize);
        bool truncated = false;

        while (queue.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<PendingNode>(browseWindow.Current);
            while (batch.Count < browseWindow.Current && queue.TryDequeue(out var pending))
            {
                if (pending.Depth >= options.MaxDepth || !expanded.Add(pending.NodeId)) continue;
                batch.Add(pending);
            }
            if (batch.Count == 0) continue;

            var descriptions = new BrowseDescriptionCollection(batch.Select(item => new BrowseDescription
            {
                NodeId = NodeId.Parse(item.NodeId),
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            }));
            BrowseResponse response;
            try
            {
                response = await session.BrowseAsync(null, null, 0u, descriptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceResultException ex) when (browseWindow.TryReduce(ex.StatusCode))
            {
                RequeueAfterContinuationExhaustion(
                    batch, queue, expanded, continuationRetries, ex.StatusCode, browseWindow.Current);
                Log(log, $"服务器 Browse continuation point 已耗尽；整批 {batch.Count} 个节点稍后重试，浏览窗口降为 {browseWindow.Current}");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (response.Results.Count != batch.Count)
                throw new InvalidOperationException($"Browse 响应数量不匹配：期望 {batch.Count}，实际 {response.Results.Count}。");

            var active = new List<Continuation>();
            var retry = new List<PendingNode>();
            for (int index = 0; index < batch.Count; index++)
            {
                var result = response.Results[index];
                if (result.StatusCode.Code == StatusCodes.BadNoContinuationPoints)
                {
                    retry.Add(batch[index]);
                    continue;
                }
                EnsureGood(result.StatusCode, batch[index].NodeId, "Browse");
                continuationRetries.Remove(batch[index].NodeId);
                AddReferences(session, options, batch[index], result.References, nodes, queue, ref truncated);
                if (result.ContinuationPoint is { Length: > 0 } continuation)
                    active.Add(new Continuation(batch[index], continuation));
            }

            while (active.Count > 0 && !truncated)
            {
                var next = await session.BrowseNextAsync(
                        null, false, new ByteStringCollection(active.Select(item => item.Token)), cancellationToken)
                    .ConfigureAwait(false);
                if (next.Results.Count != active.Count)
                    throw new InvalidOperationException($"BrowseNext 响应数量不匹配：期望 {active.Count}，实际 {next.Results.Count}。");
                var remaining = new List<Continuation>();
                for (int index = 0; index < active.Count; index++)
                {
                    var result = next.Results[index];
                    EnsureGood(result.StatusCode, active[index].Parent.NodeId, "BrowseNext");
                    AddReferences(session, options, active[index].Parent, result.References, nodes, queue, ref truncated);
                    if (result.ContinuationPoint is { Length: > 0 } continuation)
                        remaining.Add(new Continuation(active[index].Parent, continuation));
                }
                active = remaining;
            }
            if (retry.Count > 0 && !truncated)
            {
                browseWindow.TryReduce(StatusCodes.BadNoContinuationPoints);
                RequeueAfterContinuationExhaustion(
                    retry, queue, expanded, continuationRetries,
                    StatusCodes.BadNoContinuationPoints, browseWindow.Current);
                Log(log, $"服务器拒绝为 {retry.Count} 个节点分配 Browse continuation point；已释放本批续传点并重试，浏览窗口降为 {browseWindow.Current}");
            }
            if (expanded.Count <= 20 || expanded.Count % 1000 < browseWindow.Current)
                Log(log, $"浏览进度：已展开={expanded.Count}；已发现={nodes.Count}；待处理={queue.Count}");
        }

        await ReadAttributesAsync(session, nodes.Values.ToArray(), log, cancellationToken).ConfigureAwait(false);
        var reports = nodes.Values
            .OrderBy(node => node.PrimaryPath, StringComparer.Ordinal)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .Select(node => node.ToReport(NamespaceUri(namespaces, node.ParsedNodeId.NamespaceIndex)))
            .ToArray();
        return new CrawlResult(reports, truncated);
    }

    private static void RequeueAfterContinuationExhaustion(
        IReadOnlyList<PendingNode> items,
        Queue<PendingNode> queue,
        HashSet<string> expanded,
        Dictionary<string, int> retries,
        uint statusCode,
        int nextWindow)
    {
        foreach (var item in items)
        {
            int attempt = retries.GetValueOrDefault(item.NodeId) + 1;
            if (attempt > 8)
                throw new ServiceResultException(statusCode,
                    $"Browse 节点 {item.NodeId} 在窗口降至 {nextWindow} 后仍无法取得 continuation point，已停止重试。");
            retries[item.NodeId] = attempt;
            expanded.Remove(item.NodeId);
            queue.Enqueue(item);
        }
    }

    private static void AddReferences(
        Session session,
        ProbeOptions options,
        PendingNode parent,
        ReferenceDescriptionCollection? references,
        Dictionary<string, NodeAccumulator> nodes,
        Queue<PendingNode> queue,
        ref bool truncated)
    {
        if (references is null) return;
        foreach (var reference in references)
        {
            var child = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (child is null) continue;
            if (child.NamespaceIndex == 0 && !options.IncludeNamespaceZero) continue;
            string childId = child.ToString();
            string segment = reference.BrowseName?.Name
                ?? reference.DisplayName?.Text
                ?? childId;
            string[] path = parent.Path.Append(segment).ToArray();
            if (!nodes.TryGetValue(childId, out var accumulator))
            {
                if (nodes.Count >= options.MaxNodes)
                {
                    truncated = true;
                    return;
                }
                accumulator = new NodeAccumulator(
                    childId,
                    reference.NodeClass,
                    reference.BrowseName?.ToString() ?? segment,
                    reference.DisplayName?.Text ?? segment,
                    path);
                nodes.Add(childId, accumulator);
                queue.Enqueue(new PendingNode(childId, path, parent.Depth + 1));
            }
            accumulator.AddPath(path);
            var referenceType = ExpandedNodeId.ToNodeId(reference.ReferenceTypeId, session.NamespaceUris);
            accumulator.AddReference(new ProbeIncomingReference(
                parent.NodeId,
                referenceType?.ToString() ?? reference.ReferenceTypeId.ToString(),
                ReferenceName(referenceType),
                string.Join('.', path)));
        }
    }

    private static async Task ReadAttributesAsync(
        Session session,
        IReadOnlyList<NodeAccumulator> nodes,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var ids = ProbeAttributeCatalog.ReadOnlyAttributeIds();
        int completed = 0;
        foreach (var batch in nodes.Chunk(AttributeBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requests = new ReadValueIdCollection();
            foreach (var node in batch)
                foreach (uint attributeId in ids)
                    requests.Add(new ReadValueId { NodeId = node.ParsedNodeId, AttributeId = attributeId });
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, requests, cancellationToken)
                .ConfigureAwait(false);
            int expected = batch.Length * ids.Count;
            if (response.Results.Count != expected)
                throw new InvalidOperationException($"属性读取响应数量不匹配：期望 {expected}，实际 {response.Results.Count}。");
            for (int nodeIndex = 0; nodeIndex < batch.Length; nodeIndex++)
            {
                for (int attributeIndex = 0; attributeIndex < ids.Count; attributeIndex++)
                {
                    uint attributeId = ids[attributeIndex];
                    var value = response.Results[nodeIndex * ids.Count + attributeIndex];
                    string status = StatusCodes.LookupSymbolicId(value.StatusCode.Code) ?? value.StatusCode.ToString();
                    batch[nodeIndex].Attributes[AttributeName(attributeId)] = new ProbeAttributeReport(
                        attributeId,
                        status,
                        $"0x{value.StatusCode.Code:X8}",
                        value.Value?.GetType().FullName,
                        Opc.Ua.StatusCode.IsBad(value.StatusCode) ? null : OpcUaAttributeValueConverter.ConvertValue(value.Value),
                        Opc.Ua.StatusCode.IsBad(value.StatusCode) ? null : OpcUaAttributeValueConverter.DecodeFlags(attributeId, value.Value));
                }
            }
            completed += batch.Length;
            if (completed <= 64 || completed % 1000 < AttributeBatchSize)
                Log(log, $"属性进度：{completed}/{nodes.Count}；每节点属性={ids.Count}；已明确排除 Value 属性");
        }
    }

    private static async Task<IReadOnlyList<ProbeNodeReport>> ReadReferencedDataTypesAsync(
        Session session,
        IReadOnlyList<ProbeNodeReport> nodes,
        IReadOnlyList<ProbeNamespaceReport> namespaces,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        string[] ids = nodes
            .Where(node => node.NodeClass == nameof(NodeClass.Variable))
            .Select(node => node.Attributes.TryGetValue("DataType", out var value) && value.StatusCode == "0x00000000"
                ? value.Value?.ToString()
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var accumulators = new List<NodeAccumulator>(ids.Length);
        foreach (string id in ids)
        {
            try
            {
                var parsed = NodeId.Parse(id);
                accumulators.Add(new NodeAccumulator(
                    parsed.ToString(), NodeClass.DataType, id, id, ["ReferencedDataTypes", id]));
            }
            catch { }
        }
        if (accumulators.Count == 0) return [];
        await ReadAttributesAsync(session, accumulators, log, cancellationToken).ConfigureAwait(false);
        Log(log, $"已补充读取 {accumulators.Count} 个变量引用的数据类型定义；仍未读取业务值");
        return accumulators
            .Select(item => item.ToReport(NamespaceUri(namespaces, item.ParsedNodeId.NamespaceIndex)))
            .OrderBy(item => item.NodeId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, ProbeAttributeReport>> ReadServerCapabilitiesAsync(
        Session session,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var requested = new List<(string Name, NodeId NodeId)>();
        foreach (string fieldName in ProbeServerObservationCatalog.FieldNames())
        {
            var field = typeof(VariableIds).GetField(fieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field?.GetValue(null) is uint numeric)
                requested.Add((ObservationName(fieldName), new NodeId(numeric)));
        }
        if (requested.Count == 0) return new Dictionary<string, ProbeAttributeReport>();
        var reads = new ReadValueIdCollection(requested.Select(item => new ReadValueId
        {
            NodeId = item.NodeId,
            AttributeId = Attributes.Value,
        }));
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, reads, cancellationToken)
            .ConfigureAwait(false);
        var result = new Dictionary<string, ProbeAttributeReport>(StringComparer.Ordinal);
        for (int index = 0; index < requested.Count; index++)
        {
            var value = response.Results[index];
            string status = StatusCodes.LookupSymbolicId(value.StatusCode.Code) ?? value.StatusCode.ToString();
            result[requested[index].Name] = new ProbeAttributeReport(
                Attributes.Value,
                status,
                $"0x{value.StatusCode.Code:X8}",
                value.Value?.GetType().FullName,
                Opc.Ua.StatusCode.IsBad(value.StatusCode) ? null : OpcUaAttributeValueConverter.ConvertValue(value.Value));
        }
        Log(log, $"已读取 {result.Count} 个 OPC UA Server 标准状态/诊断/能力值；拒绝计数用于分析当前服务异常，MaxNodesPerWrite 用于分析批量写入限制");
        return result;
    }

    private static string ObservationName(string fieldName)
    {
        const string capabilityPrefix = "Server_ServerCapabilities_";
        const string serverPrefix = "Server_";
        if (fieldName.StartsWith(capabilityPrefix, StringComparison.Ordinal))
            return fieldName[capabilityPrefix.Length..];
        return fieldName.StartsWith(serverPrefix, StringComparison.Ordinal)
            ? fieldName[serverPrefix.Length..]
            : fieldName;
    }

    private static ProbeWriteDiagnostics BuildWriteDiagnostics(
        IReadOnlyList<ProbeNodeReport> nodes,
        IReadOnlyDictionary<string, ProbeAttributeReport> serverCapabilities,
        IReadOnlyList<ProbeNodeReport> dataTypes)
    {
        var definitions = dataTypes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var variables = nodes.Where(node => node.NodeClass == nameof(NodeClass.Variable)).ToArray();
        var items = variables.Select(node => ProbeWriteDiagnosticAnalyzer.Analyze(node, definitions)).ToArray();
        int unknown = variables.Count(node => !node.Attributes.TryGetValue("UserAccessLevel", out var value)
            || value.StatusCode != "0x00000000");
        return new ProbeWriteDiagnostics(
            variables.Length,
            items.Count(item => item.WritableByCurrentUser),
            items.Count(item => !item.WritableByCurrentUser) - unknown,
            unknown,
            serverCapabilities,
            items,
            "本报告仅依据只读元数据和 OPC UA Server 标准能力值分析。实际 Write StatusCode、写入值范围、PLC 程序覆盖等只能通过现有运行日志或经明确授权的受控写入确认。");
    }

    private static ProbeSummary BuildSummary(IReadOnlyList<ProbeNodeReport> nodes, bool truncated)
    {
        int readable = nodes.Count(node => node.NodeClass == nameof(NodeClass.Variable)
            && Flags(node, "UserAccessLevel").Contains("CurrentRead", StringComparer.Ordinal));
        int writable = nodes.Count(node => node.NodeClass == nameof(NodeClass.Variable)
            && Flags(node, "UserAccessLevel").Contains("CurrentWrite", StringComparer.Ordinal));
        int candidates = nodes.Count(node => node.SiemensAddressAnalysis.SiemensSymbolCandidate is not null);
        return new ProbeSummary(
            nodes.Count,
            nodes.Count(node => node.NodeClass == nameof(NodeClass.Object)),
            nodes.Count(node => node.NodeClass == nameof(NodeClass.Variable)),
            nodes.Count(node => node.NodeClass == nameof(NodeClass.Method)),
            nodes.Count(node => node.IdentifierType == IdType.String.ToString()),
            nodes.Count(node => node.IdentifierType == IdType.Numeric.ToString()),
            candidates,
            readable,
            writable,
            nodes.Sum(node => node.Attributes.Values.Count(attribute => attribute.StatusCode != "0x00000000")),
            truncated,
            candidates == 0
                ? "未发现具有 SIMATIC 符号路径形态的字符串 NodeId；仅靠本报告无法转换为西门子直连地址。"
                : $"发现 {candidates} 个未验证的 SIMATIC 符号候选。OPC UA 信息可以提供映射线索，但在没有 S7/TIA 原生符号表验证时不能认定为精确西门子地址。");
    }

    private static IReadOnlyList<string> Flags(ProbeNodeReport node, string attribute) =>
        node.Attributes.TryGetValue(attribute, out var value) && value.DecodedFlags is not null
            ? value.DecodedFlags
            : [];

    private static string NamespaceUri(IReadOnlyList<ProbeNamespaceReport> namespaces, ushort index) =>
        namespaces.FirstOrDefault(item => item.Index == index)?.Uri ?? string.Empty;

    private static string ReferenceName(NodeId? id)
    {
        if (id == ReferenceTypeIds.HasComponent) return BrowseNames.HasComponent;
        if (id == ReferenceTypeIds.HasProperty) return BrowseNames.HasProperty;
        if (id == ReferenceTypeIds.Organizes) return BrowseNames.Organizes;
        if (id == ReferenceTypeIds.HasOrderedComponent) return BrowseNames.HasOrderedComponent;
        return id?.ToString() ?? string.Empty;
    }

    private static string AttributeName(uint id) => id switch
    {
        Attributes.NodeClass => "NodeClass",
        Attributes.BrowseName => "BrowseName",
        Attributes.DisplayName => "DisplayName",
        Attributes.Description => "Description",
        Attributes.WriteMask => "WriteMask",
        Attributes.UserWriteMask => "UserWriteMask",
        Attributes.EventNotifier => "EventNotifier",
        Attributes.DataType => "DataType",
        Attributes.ValueRank => "ValueRank",
        Attributes.ArrayDimensions => "ArrayDimensions",
        Attributes.AccessLevel => "AccessLevel",
        Attributes.UserAccessLevel => "UserAccessLevel",
        Attributes.MinimumSamplingInterval => "MinimumSamplingInterval",
        Attributes.Historizing => "Historizing",
        Attributes.Executable => "Executable",
        Attributes.UserExecutable => "UserExecutable",
        Attributes.DataTypeDefinition => "DataTypeDefinition",
        Attributes.RolePermissions => "RolePermissions",
        Attributes.UserRolePermissions => "UserRolePermissions",
        Attributes.AccessRestrictions => "AccessRestrictions",
        Attributes.AccessLevelEx => "AccessLevelEx",
        _ => $"Attribute_{id}",
    };

    private static X509Certificate2? Certificate(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        try { return X509CertificateLoader.LoadCertificate(raw); }
        catch { return null; }
    }

    private static void EnsureGood(Opc.Ua.StatusCode status, string nodeId, string operation)
    {
        if (Opc.Ua.StatusCode.IsGood(status)) return;
        throw new ServiceResultException(status, $"{operation} 节点 {nodeId} 失败。");
    }

    private static void Log(Action<string>? target, string message) =>
        target?.Invoke($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}");

    private sealed record PendingNode(string NodeId, string[] Path, int Depth);
    private sealed record Continuation(PendingNode Parent, byte[] Token);
    private sealed record CrawlResult(IReadOnlyList<ProbeNodeReport> Nodes, bool Truncated);

    private sealed class NodeAccumulator
    {
        private readonly Dictionary<string, string[]> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProbeIncomingReference> _references = new(StringComparer.Ordinal);

        public NodeAccumulator(string nodeId, NodeClass nodeClass, string browseName, string displayName, string[] path)
        {
            NodeId = nodeId;
            ParsedNodeId = Opc.Ua.NodeId.Parse(nodeId);
            NodeClass = nodeClass;
            BrowseName = browseName;
            DisplayName = displayName;
            AddPath(path);
        }

        public string NodeId { get; }
        public NodeId ParsedNodeId { get; }
        public NodeClass NodeClass { get; }
        public string BrowseName { get; }
        public string DisplayName { get; }
        public Dictionary<string, ProbeAttributeReport> Attributes { get; } = new(StringComparer.Ordinal);
        public string PrimaryPath => _paths.Keys.Order(StringComparer.Ordinal).First();

        public void AddPath(string[] path) => _paths.TryAdd(string.Join('.', path), path);

        public void AddReference(ProbeIncomingReference reference) =>
            _references.TryAdd($"{reference.ParentNodeId}|{reference.ReferenceTypeId}|{reference.Path}", reference);

        public ProbeNodeReport ToReport(string namespaceUri)
        {
            var analysis = OpcUaNodeIdentifierAnalyzer.Analyze(NodeId);
            var orderedPaths = _paths.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            return new ProbeNodeReport
            {
                NodeId = NodeId,
                NamespaceIndex = ParsedNodeId.NamespaceIndex,
                NamespaceUri = namespaceUri,
                IdentifierType = analysis.IdentifierType,
                Identifier = analysis.RawIdentifier,
                NodeClass = NodeClass.ToString(),
                BrowseName = BrowseName,
                DisplayName = DisplayName,
                Paths = orderedPaths.Select(item => item.Key).ToArray(),
                PathSegments = orderedPaths.Select(item => (IReadOnlyList<string>)item.Value).ToArray(),
                IncomingReferences = _references.Values
                    .OrderBy(item => item.Path, StringComparer.Ordinal)
                    .ToArray(),
                Attributes = new Dictionary<string, ProbeAttributeReport>(Attributes, StringComparer.Ordinal),
                SiemensAddressAnalysis = analysis,
            };
        }
    }
}
