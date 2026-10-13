using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 结构体数组整读的端到端验收：本地起一个像 S7-1500 那样暴露 UDT 数组的 OPC UA 服务器，
/// 驱动必须一次读回整个数组，并把每个成员的值正确分发到对应点位。
/// </summary>
public sealed class OpcUaStructArrayAcceptanceTests
{
    private const string ArrayIdentifier = "\"DB8217\".\"Weld\".\"EquipmentData\"";
    private const int Elements = 8;

    [Fact]
    public async Task Driver_reads_the_whole_udt_array_once_and_maps_every_member()
    {
        int port = FreePort();
        string pki = Path.Combine(Path.GetTempPath(), "igw-structarray-" + Guid.NewGuid().ToString("N"));
        await using var server = new TestStructArrayServer();
        await server.StartAsync(port, pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var driver = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await driver.ConnectAsync(timeout.Token);

        var bindings = new List<TagBinding>();
        for (int index = 0; index < Elements; index++)
        {
            bindings.Add(Binding(index, "Position", IgwDataType.Int16));
            bindings.Add(Binding(index, "Flow", IgwDataType.Float));
            bindings.Add(Binding(index, "Code", IgwDataType.String));
        }

        var samples = await driver.ReadAsync(bindings, timeout.Token);

        Assert.Equal(bindings.Count, samples.Count);
        Assert.All(samples, sample => Assert.True(sample.Value.Quality.IsGood, sample.TagId + ":" + sample.Value.Quality));
        var byTag = samples.ToDictionary(sample => sample.TagId, sample => sample.Value.Value);
        for (int index = 0; index < Elements; index++)
        {
            Assert.Equal((short)(100 + index), byTag[TagId(index, "Position")]);
            Assert.Equal(1.5f * index, byTag[TagId(index, "Flow")]);
            Assert.Equal("SN-" + index, byTag[TagId(index, "Code")]);
        }
        // 整个数组一次读回：服务器只收到一次 Value 读取请求，而不是 24 次。
        Assert.Equal(1, server.ValueReads);
        Assert.Contains("结构体数组整读", driver.ReadWindowSummary);
    }

    /// <summary>
    /// UDT 里有嵌套结构体成员，且有点位直接指向这个成员时：该点退回逐点读取(读不到就是坏值)，
    /// 同一次读取里的其它成员照常返回好值——不能因为一个成员装不进 Variant 就让整台设备这次读取全变坏值。
    /// </summary>
    [Fact]
    public async Task A_nested_struct_member_only_affects_its_own_tag()
    {
        int port = FreePort();
        string pki = Path.Combine(Path.GetTempPath(), "igw-structarray-" + Guid.NewGuid().ToString("N"));
        await using var server = new TestStructArrayServer { WithNested = true };
        await server.StartAsync(port, pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var driver = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await driver.ConnectAsync(timeout.Token);

        var bindings = new List<TagBinding>();
        for (int index = 0; index < Elements; index++)
        {
            bindings.Add(Binding(index, "Position", IgwDataType.Int16));
            bindings.Add(Binding(index, "Flow", IgwDataType.Float));
            bindings.Add(Binding(index, "Code", IgwDataType.String));
            bindings.Add(Binding(index, "Inner", IgwDataType.Int16));   // 直接指向嵌套结构体成员
        }

        var samples = await driver.ReadAsync(bindings, timeout.Token);

        Assert.Equal(bindings.Count, samples.Count);
        var byTag = samples.ToDictionary(sample => sample.TagId, sample => sample.Value);
        for (int index = 0; index < Elements; index++)
        {
            Assert.True(byTag[TagId(index, "Position")].Quality.IsGood, TagId(index, "Position"));
            Assert.Equal((short)(100 + index), byTag[TagId(index, "Position")].Value);
            Assert.Equal("SN-" + index, byTag[TagId(index, "Code")].Value);
            Assert.False(byTag[TagId(index, "Inner")].Quality.IsGood);   // 嵌套结构体成员自身读不成标量
        }
    }

    [Fact]
    public async Task Server_without_a_structure_definition_falls_back_to_per_tag_reads()
    {
        int port = FreePort();
        string pki = Path.Combine(Path.GetTempPath(), "igw-structarray-" + Guid.NewGuid().ToString("N"));
        await using var server = new TestStructArrayServer { PublishDefinition = false };
        await server.StartAsync(port, pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var driver = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await driver.ConnectAsync(timeout.Token);
        var bindings = Enumerable.Range(0, (int)Elements)
            .SelectMany(index => new[] { Binding(index, "Position", IgwDataType.Int16), Binding(index, "Flow", IgwDataType.Float), Binding(index, "Code", IgwDataType.String) })
            .ToArray();

        var samples = await driver.ReadAsync(bindings, timeout.Token);

        Assert.Equal(bindings.Length, samples.Count);
        for (int index = 0; index < Elements; index++)
            Assert.Equal((short)(100 + index), samples.First(s => s.TagId == TagId(index, "Position")).Value.Value);
        Assert.DoesNotContain("结构体数组整读", driver.ReadWindowSummary);
    }

    private static string TagId(int index, string member) => $"Line.Plc.{member}{index}";

    private static TagBinding Binding(int index, string member, IgwDataType type) => new()
    {
        TagId = TagId(index, member),
        Def = new TagDef
        {
            Name = member,
            Address = $"ns=3;s={ArrayIdentifier}[{index}].\"{member}\"",
            DataType = type,
        },
        Address = new TagAddress(0, 0, 2),
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>最小的“西门子式”服务器：UDT 数组节点 + 每个成员的叶子节点 + 数据类型定义。</summary>
    private sealed class TestStructArrayServer : IAsyncDisposable
    {
        private ApplicationInstance? _application;
        private StructArrayServer? _server;
        public bool PublishDefinition { get; init; } = true;
        public bool WithNested { get; init; }
        public int ValueReads => _server?.NodeManager?.ValueReads ?? 0;

        public async Task StartAsync(int port, string pki)
        {
            var configuration = new ApplicationConfiguration
            {
                ApplicationName = "IGW struct array test server",
                ApplicationUri = "urn:localhost:igw:structarray",
                ApplicationType = ApplicationType.Server,
                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
                    SecurityPolicies = [new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }],
                    UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                    MaxSessionCount = 8,
                    MinSessionTimeout = 1000,
                    MaxSessionTimeout = 600000,
                },
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=IGW struct array test server" },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                    RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                    AutoAcceptUntrustedCertificates = true,
                    AddAppCertToTrustedStore = true,
                    MinimumCertificateKeySize = 2048,
                },
                TransportConfigurations = [],
                TransportQuotas = new TransportQuotas { OperationTimeout = 30000 },
                ClientConfiguration = new ClientConfiguration(),
                TraceConfiguration = new TraceConfiguration(),
            };
            await configuration.ValidateAsync(ApplicationType.Server, CancellationToken.None);
            configuration.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;
            _application = new ApplicationInstance(DefaultTelemetry.Create(_ => { })) { ApplicationName = configuration.ApplicationName, ApplicationType = ApplicationType.Server, ApplicationConfiguration = configuration };
            await _application.CheckApplicationInstanceCertificatesAsync(true, 240);
            _server = new StructArrayServer(PublishDefinition, WithNested);
            await _application.StartAsync(_server);
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_server is not null) await _server.StopAsync(CancellationToken.None); } catch { }
            try { _server?.Dispose(); } catch { }
        }
    }

    private sealed class StructArrayServer(bool publishDefinition, bool withNested = false) : StandardServer
    {
        public StructArrayNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new StructArrayNodeManager(server, configuration, publishDefinition, withNested);
            return new MasterNodeManager(server, configuration, null, [NodeManager]);
        }
    }

    private sealed class StructArrayNodeManager : CustomNodeManager2
    {
        private const string Siemens = "http://www.siemens.com/simatic-s7-opcua";
        private readonly bool _publishDefinition;
        private readonly bool _withNested;
        private static bool s_withNested;
        private int _valueReads;
        public int ValueReads => Volatile.Read(ref _valueReads);

        public StructArrayNodeManager(IServerInternal server, ApplicationConfiguration configuration, bool publishDefinition, bool withNested)
            : base(server, configuration, false, NullLogger.Instance, ["urn:igw:test", Siemens])
        {
            _publishDefinition = publishDefinition;
            _withNested = withNested;
            s_withNested = withNested;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                    externalReferences[ObjectIds.ObjectsFolder] = references = [];
                ushort siemens = NamespaceIndexes[1];

                var innerType = new DataTypeState
                {
                    NodeId = new NodeId(ArrayIdentifier + "::INNER", siemens),
                    BrowseName = new QualifiedName("Inner_UDT", siemens),
                    DisplayName = "Inner_UDT",
                    SuperTypeId = DataTypeIds.Structure,
                    DataTypeDefinition = new ExtensionObject(new StructureDefinition
                    {
                        BaseDataType = DataTypeIds.Structure,
                        StructureType = StructureType.Structure,
                        Fields = [new StructureField { Name = "Value", DataType = DataTypeIds.Int16, ValueRank = ValueRanks.Scalar }],
                    }),
                };
                if (_withNested) AddPredefinedNode(SystemContext, innerType);

                var dataType = new DataTypeState
                {
                    NodeId = new NodeId(ArrayIdentifier + "::UDT", siemens),
                    BrowseName = new QualifiedName("EquipmentData_UDT", siemens),
                    DisplayName = "EquipmentData_UDT",
                    SuperTypeId = DataTypeIds.Structure,
                };
                if (_publishDefinition)
                    dataType.DataTypeDefinition = new ExtensionObject(new StructureDefinition
                    {
                        BaseDataType = DataTypeIds.Structure,
                        StructureType = StructureType.Structure,
                        Fields =
                        [
                            new StructureField { Name = "Position", DataType = DataTypeIds.Int16, ValueRank = ValueRanks.Scalar },
                            new StructureField { Name = "Flow", DataType = DataTypeIds.Float, ValueRank = ValueRanks.Scalar },
                            new StructureField { Name = "Code", DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar },
                            .. _withNested
                                ? new[] { new StructureField { Name = "Inner", DataType = innerType.NodeId, ValueRank = ValueRanks.Scalar } }
                                : [],
                        ],
                    });
                AddPredefinedNode(SystemContext, dataType);

                var array = new BaseDataVariableState(null)
                {
                    SymbolicName = "EquipmentData",
                    NodeId = new NodeId(ArrayIdentifier, siemens),
                    BrowseName = new QualifiedName("EquipmentData", siemens),
                    DisplayName = "EquipmentData",
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                    DataType = dataType.NodeId,
                    ValueRank = ValueRanks.OneDimension,
                    ArrayDimensions = new uint[] { Elements },
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow,
                };
                array.OnSimpleReadValue = (ISystemContext context, NodeState node, ref object value) =>
                {
                    Interlocked.Increment(ref _valueReads);
                    value = Encode();
                    return ServiceResult.Good;
                };
                array.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, array.NodeId));
                AddPredefinedNode(SystemContext, array);

                // 叶子节点：整读不可用时驱动退回逐点读取。
                for (int index = 0; index < Elements; index++)
                {
                    AddLeaf(siemens, index, "Position", DataTypeIds.Int16, (short)(100 + index));
                    AddLeaf(siemens, index, "Flow", DataTypeIds.Float, 1.5f * index);
                    AddLeaf(siemens, index, "Code", DataTypeIds.String, "SN-" + index);
                }
            }
        }

        private void AddLeaf(ushort siemens, int index, string member, NodeId type, object value)
        {
            var leaf = new BaseDataVariableState(null)
            {
                SymbolicName = member,
                NodeId = new NodeId($"{ArrayIdentifier}[{index}].\"{member}\"", siemens),
                BrowseName = new QualifiedName(member, siemens),
                DisplayName = member,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = type,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
                Value = value,
            };
            AddPredefinedNode(SystemContext, leaf);
        }

        private static ExtensionObject[] Encode()
        {
            var result = new ExtensionObject[Elements];
            for (int index = 0; index < Elements; index++)
            {
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes((short)(100 + index)));
                body.AddRange(BitConverter.GetBytes(1.5f * index));
                byte[] text = System.Text.Encoding.UTF8.GetBytes("SN-" + index);
                body.AddRange(BitConverter.GetBytes(text.Length));
                body.AddRange(text);
                if (s_withNested) body.AddRange(BitConverter.GetBytes((short)(index + 1)));   // 嵌套结构体成员 Inner.Value
                result[index] = new ExtensionObject(new ExpandedNodeId(ArrayIdentifier + "::UDT", Siemens), body.ToArray());
            }
            return result;
        }
    }
}
