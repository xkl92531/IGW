using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using Xunit;
using IgwWrite = IGW.Abstractions.WriteRequest;
using IgwStatus = IGW.Abstractions.StatusCode;

namespace IGW.Tests;

/// <summary>
/// 时间类型的端到端验收：本地起一个像 S7-1500 那样发布 DTL/DT/DATE/TOD/TIME… 的 OPC UA 服务器，
/// 驱动必须按点位配置的语义类型解出可读文本，写回时必须还原成服务器原本的物理表示。
/// 只有真的经过一次 OPC UA 会话，才算验证了这些类型「在 OPC UA 上支持」。
/// </summary>
public sealed class OpcUaTimeTypeAcceptanceTests
{
    // 基准时刻：2026-09-20 10:11:12.123456789（DT 只到毫秒，LDT 受 OPC UA DateTime 的 100ns 刻度限制）
    private const string Dtl = "2026-09-20 10:11:12.123456789";
    private const string Dt = "2026-09-20 10:11:12.123";
    private const string Ldt = "2026-09-20 10:11:12.123456700";
    private static readonly string[] DtlArray =
    [
        "2026-01-01 00:00:00.000000000",
        "2026-06-15 12:30:45.500000000",
        "2262-04-11 23:47:16.854775807",   // DTL 上界
    ];

    [Fact]
    public async Task Every_siemens_time_type_reads_back_as_readable_text()
    {
        await using var fixture = await TimeTypeFixture.StartAsync();

        var samples = await fixture.Driver.ReadAsync(fixture.AllBindings(), fixture.Token);
        var byTag = samples.ToDictionary(sample => sample.TagId, sample => sample.Value);

        foreach (var (tag, expected) in new (string Tag, object Expected)[]
                 {
                     ("DTL", Dtl),
                     ("DT", Dt),
                     ("LDT", Ldt),
                     ("DATE", "2026-09-20"),
                     ("TOD", "10:11:12.123"),
                     ("LTOD", "10:11:12.123456789"),
                     ("TIME", "T#3723000ms"),              // 1h2m3s
                     ("LTIME", "LT#3723000000000ns"),
                     ("S5TIME", "S5T#99900ms"),
                     ("DATETIME", "2026-09-20T10:11:12.1234567Z"),
                 })
        {
            Assert.True(byTag[tag].Quality.IsGood, tag + " 质量应为 Good，实际 " + byTag[tag].Quality.Class);
            Assert.Equal(expected, byTag[tag].Value);
        }
    }

    [Fact]
    public async Task Every_siemens_time_type_writes_back_in_the_servers_own_physical_form()
    {
        await using var fixture = await TimeTypeFixture.StartAsync();

        // 先读一轮：DTL 的写入需要服务器原值里的 BinaryEncodingId，驱动会自己取（这里验证不用人工预热也行）。
        var writes = new (string Tag, IgwDataType Type, string Value)[]
        {
            ("DTL", IgwDataType.Dtl, "2030-01-02 03:04:05.987654321"),
            ("DT", IgwDataType.Dt, "2030-01-02 03:04:05.987"),
            ("LDT", IgwDataType.Ldt, "2030-01-02 03:04:05.987654300"),
            ("DATE", IgwDataType.Date, "2030-01-02"),
            ("TOD", IgwDataType.Tod, "03:04:05.987"),
            ("LTOD", IgwDataType.Ltod, "03:04:05.987654321"),
            ("TIME", IgwDataType.Time, "T#2h3m4s"),
            ("LTIME", IgwDataType.Ltime, "LT#7384000000000ns"),
            ("S5TIME", IgwDataType.S5Time, "S5T#5000ms"),
            ("DATETIME", IgwDataType.DateTime, "2030-01-02T03:04:05.9876543Z"),
        };

        var results = await fixture.Driver.WriteAsync(
            [.. writes.Select(item => new IgwWrite
            {
                TagId = item.Tag,
                Def = TagDefOf(item.Tag, item.Type),
                Address = new TagAddress(0, 0, 2),
                Value = item.Value,
            })], fixture.Token);

        foreach (var result in results)
            Assert.True(result.Status.IsGood, result.TagId + " 写入失败：" + result.Status + " " + result.Error);

        // 服务器端拿到的必须是物理表示，而不是文本
        Assert.IsType<ExtensionObject>(fixture.ServerValue("DTL"));
        Assert.Equal(12, ((byte[])((ExtensionObject)fixture.ServerValue("DTL")!).Body!).Length);
        Assert.Equal(8, ((byte[])fixture.ServerValue("DT")!).Length);
        Assert.Equal((ushort)(new DateTime(2030, 1, 2) - new DateTime(1990, 1, 1)).Days, fixture.ServerValue("DATE"));
        Assert.Equal(3u * 3600_000 + 4 * 60_000 + 5987, fixture.ServerValue("TOD"));
        Assert.Equal((ulong)((3 * 3600 + 4 * 60 + 5) * 1_000_000_000L + 987_654_321), fixture.ServerValue("LTOD"));
        Assert.Equal(7_384_000, fixture.ServerValue("TIME"));                  // 2h3m4s 的毫秒数
        Assert.Equal(7_384_000_000_000L, fixture.ServerValue("LTIME"));
        Assert.Equal((ushort)0x0500, fixture.ServerValue("S5TIME"));           // 编码器取最小可精确表示的时基：10ms × 500
        Assert.IsType<DateTime>(fixture.ServerValue("LDT"));
        Assert.IsType<DateTime>(fixture.ServerValue("DATETIME"));

        // 再读一轮：写进去什么，读回来就是什么
        var samples = await fixture.Driver.ReadAsync(fixture.AllBindings(), fixture.Token);
        var byTag = samples.ToDictionary(sample => sample.TagId, sample => sample.Value);
        foreach (var item in writes)
        {
            Assert.True(byTag[item.Tag].Quality.IsGood, item.Tag + " 回读质量应为 Good");
            string expected = item.Tag switch
            {
                "TIME" => "T#7384000ms",                   // 写入用 T#2h3m4s，规范化后是毫秒
                _ => item.Value,
            };
            Assert.Equal(expected, byTag[item.Tag].Value);
        }
    }

    [Fact]
    public async Task Out_of_range_values_are_refused_instead_of_silently_truncated()
    {
        await using var fixture = await TimeTypeFixture.StartAsync();

        var results = await fixture.Driver.WriteAsync(
            [
                // DT 只到毫秒，且范围 1990–2089
                new IgwWrite { TagId = "DT", Def = TagDefOf("DT", IgwDataType.Dt), Address = new TagAddress(0, 0, 2), Value = "2030-01-02 03:04:05.987654" },
                // TOD 只到毫秒
                new IgwWrite { TagId = "TOD", Def = TagDefOf("TOD", IgwDataType.Tod), Address = new TagAddress(0, 0, 2), Value = "03:04:05.987654" },
                // DATE 下界是 1990-01-01
                new IgwWrite { TagId = "DATE", Def = TagDefOf("DATE", IgwDataType.Date), Address = new TagAddress(0, 0, 2), Value = "1989-12-31" },
                // S5TIME 必须能用 0–999 × 时基精确表示
                new IgwWrite { TagId = "S5TIME", Def = TagDefOf("S5TIME", IgwDataType.S5Time), Address = new TagAddress(0, 0, 2), Value = "S5T#12345ms" },
            ], fixture.Token);

        foreach (var result in results)
            Assert.False(result.Status.IsGood, result.TagId + " 越界值不应写入成功");
    }

    /// <summary>
    /// 服务器给出的 DTL 结构体损坏时，只有这个点变坏值(BadDecodeError)，
    /// 同一次读取里的其它时间点位必须照常返回——一个坏点不能拖垮整批。
    /// </summary>
    [Fact]
    public async Task A_corrupt_dtl_only_spoils_its_own_tag()
    {
        await using var fixture = await TimeTypeFixture.StartAsync(corruptDtl: true);

        var samples = await fixture.Driver.ReadAsync(fixture.AllBindings(), fixture.Token);
        var byTag = samples.ToDictionary(sample => sample.TagId, sample => sample.Value);

        Assert.False(byTag["DTL"].Quality.IsGood);
        Assert.Equal(IgwStatus.BadDecodeError, byTag["DTL"].Quality);
        foreach (string tag in new[] { "DT", "LDT", "DATE", "TOD", "LTOD", "TIME", "LTIME", "S5TIME", "DATETIME" })
            Assert.True(byTag[tag].Quality.IsGood, tag + " 不应受坏 DTL 影响");
        Assert.Equal(Dt, byTag["DT"].Value);
    }

    /// <summary>元素类型就是 DTL 的数组：一次读回整段，每个元素都解成日历文本(含上界值)。</summary>
    [Fact]
    public async Task A_dtl_array_reads_back_every_element()
    {
        await using var fixture = await TimeTypeFixture.StartAsync();

        var binding = new TagBinding
        {
            TagId = "DTLARRAY",
            Def = new TagDef
            {
                Name = "DTLARRAY",
                Address = "ns=3;s=DTLARRAY",
                DataType = IgwDataType.Dtl,
                Array = new ArrayDef { ElementType = IgwDataType.Dtl, Dimensions = [3], LowerBounds = [0] },
            },
            Address = new TagAddress(0, 0, 2),
        };

        var sample = Assert.Single(await fixture.Driver.ReadAsync([binding], fixture.Token));
        Assert.True(sample.Value.Quality.IsGood, "DTL 数组读取应为 Good，实际 " + sample.Value.Quality.Class);
        var array = Assert.IsAssignableFrom<ArrayRuntimeValue>(sample.Value.Value);
        Assert.Equal(IgwDataType.Dtl, array.ElementType);
        Assert.Equal(DtlArray, ArrayValueCodec.ToWireValues(array));
    }

    private static TagDef TagDefOf(string tag, IgwDataType type) =>
        new() { Name = tag, Address = $"ns=3;s={tag}", DataType = type };

    private static TagBinding BindingOf(string tag, IgwDataType type) =>
        new() { TagId = tag, Def = TagDefOf(tag, type), Address = new TagAddress(0, 0, 2) };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TimeTypeFixture : IAsyncDisposable
    {
        private ApplicationInstance? _application;
        private TimeTypeServer? _server;
        private CancellationTokenSource? _timeout;
        public OpcUaClientDeviceDriver Driver { get; private set; } = null!;
        public CancellationToken Token => _timeout!.Token;

        public static async Task<TimeTypeFixture> StartAsync(bool corruptDtl = false)
        {
            var fixture = new TimeTypeFixture();
            int port = FreePort();
            string pki = Path.Combine(Path.GetTempPath(), "igw-timetypes-" + Guid.NewGuid().ToString("N"));
            var configuration = new ApplicationConfiguration
            {
                ApplicationName = "IGW time type test server",
                ApplicationUri = "urn:localhost:igw:timetypes",
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
                    ApplicationCertificate = new CertificateIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "own"), SubjectName = "CN=IGW time type test server" },
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
            fixture._application = new ApplicationInstance(DefaultTelemetry.Create(_ => { }))
            {
                ApplicationName = configuration.ApplicationName,
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = configuration,
            };
            await fixture._application.CheckApplicationInstanceCertificatesAsync(true, 240);
            fixture._server = new TimeTypeServer(corruptDtl);
            await fixture._application.StartAsync(fixture._server);

            fixture._timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            fixture.Driver = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
            await fixture.Driver.ConnectAsync(fixture.Token);
            return fixture;
        }

        public IReadOnlyList<TagBinding> AllBindings() =>
        [
            BindingOf("DTL", IgwDataType.Dtl),
            BindingOf("DT", IgwDataType.Dt),
            BindingOf("LDT", IgwDataType.Ldt),
            BindingOf("DATE", IgwDataType.Date),
            BindingOf("TOD", IgwDataType.Tod),
            BindingOf("LTOD", IgwDataType.Ltod),
            BindingOf("TIME", IgwDataType.Time),
            BindingOf("LTIME", IgwDataType.Ltime),
            BindingOf("S5TIME", IgwDataType.S5Time),
            BindingOf("DATETIME", IgwDataType.DateTime),
        ];

        public object? ServerValue(string tag) => _server?.NodeManager?.ValueOf(tag);

        public async ValueTask DisposeAsync()
        {
            try { await Driver.DisposeAsync(); } catch { }
            try { if (_server is not null) await _server.StopAsync(CancellationToken.None); } catch { }
            try { _server?.Dispose(); } catch { }
            _timeout?.Dispose();
        }
    }

    private sealed class TimeTypeServer(bool corruptDtl = false) : StandardServer
    {
        public TimeTypeNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new TimeTypeNodeManager(server, configuration, corruptDtl);
            return new MasterNodeManager(server, configuration, null, [NodeManager]);
        }
    }

    private sealed class TimeTypeNodeManager(IServerInternal server, ApplicationConfiguration configuration, bool corruptDtl)
        : CustomNodeManager2(server, configuration, false, NullLogger.Instance, ["urn:igw:test", Siemens])
    {
        private const string Siemens = "http://www.siemens.com/simatic-s7-opcua";
        private readonly Dictionary<string, BaseDataVariableState> _nodes = new(StringComparer.Ordinal);

        public object? ValueOf(string tag)
        {
            lock (Lock) { return _nodes.TryGetValue(tag, out var node) ? node.Value : null; }
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                    externalReferences[ObjectIds.ObjectsFolder] = references = [];
                ushort siemens = NamespaceIndexes[1];

                // DTL 在 OPC UA 上没有对应类型：西门子发布成自定义结构体，值是 ExtensionObject + 12 字节小端结构体编码。
                var dtlType = new DataTypeState
                {
                    NodeId = new NodeId("DTL::UDT", siemens),
                    BrowseName = new QualifiedName("DTL", siemens),
                    DisplayName = "DTL",
                    SuperTypeId = DataTypeIds.Structure,
                };
                AddPredefinedNode(SystemContext, dtlType);

                byte[] dtlBody = SiemensValueCodec.EncodeDtl(Dtl, littleEndian: true);
                if (corruptDtl) dtlBody[4] = 0;   // 星期字节非法(合法为 1..7)，模拟服务器给出坏结构体
                Add(references, siemens, "DTL", DataTypeIds.Structure, ValueRanks.Scalar,
                    new ExtensionObject(new ExpandedNodeId("DTL::UDT", Siemens), dtlBody));
                // DT/DATE_AND_TIME：8 字节 BCD
                Add(references, siemens, "DT", DataTypeIds.Byte, ValueRanks.OneDimension, DtBytes(Dt));
                // LDT / OPC UA DateTime：100ns 刻度的 UtcTime
                Add(references, siemens, "LDT", DataTypeIds.DateTime, ValueRanks.Scalar, UtcTicks(2026, 9, 20, 10, 11, 12, 1234567));
                Add(references, siemens, "DATETIME", DataTypeIds.DateTime, ValueRanks.Scalar, UtcTicks(2026, 9, 20, 10, 11, 12, 1234567));
                // DATE：自 1990-01-01 起的天数
                Add(references, siemens, "DATE", DataTypeIds.UInt16, ValueRanks.Scalar,
                    (ushort)(new DateTime(2026, 9, 20) - new DateTime(1990, 1, 1)).Days);
                // TOD：当天毫秒数；LTOD：当天纳秒数
                Add(references, siemens, "TOD", DataTypeIds.UInt32, ValueRanks.Scalar, 10u * 3600_000 + 11 * 60_000 + 12_123);
                Add(references, siemens, "LTOD", DataTypeIds.UInt64, ValueRanks.Scalar,
                    (ulong)((10 * 3600 + 11 * 60 + 12) * 1_000_000_000L + 123_456_789));
                // TIME：毫秒；LTIME：纳秒
                Add(references, siemens, "TIME", DataTypeIds.Int32, ValueRanks.Scalar, 3_723_000);
                Add(references, siemens, "LTIME", DataTypeIds.Int64, ValueRanks.Scalar, 3_723_000_000_000L);
                // S5TIME：高 2 位是时基索引(10/100/1000/10000ms)，低 12 位是 3 位 BCD
                Add(references, siemens, "S5TIME", DataTypeIds.UInt16, ValueRanks.Scalar, (ushort)0x1999);
                // Array[0..2] of DTL：值是 ExtensionObject 数组，驱动要一次读回整段
                Add(references, siemens, "DTLARRAY", DataTypeIds.Structure, ValueRanks.OneDimension,
                    (object)DtlArray.Select(text => new ExtensionObject(new ExpandedNodeId("DTL::UDT", Siemens),
                        SiemensValueCodec.EncodeDtl(text, littleEndian: true))).ToArray(), dimension: 3);   // 时基 100ms × 999 = 99900ms
            }
        }

        private static DateTime UtcTicks(int year, int month, int day, int hour, int minute, int second, long fractionTicks) =>
            DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, second).AddTicks(fractionTicks), DateTimeKind.Utc);

        private static byte[] DtBytes(string text) => (byte[])SiemensValueCodec.ToWire(IgwDataType.Dt, text);

        private void Add(IList<IReference> references, ushort ns, string name, NodeId dataType, int valueRank, object value, uint dimension = 8)
        {
            var node = new BaseDataVariableState(null)
            {
                SymbolicName = name,
                NodeId = new NodeId(name, ns),
                BrowseName = new QualifiedName(name, ns),
                DisplayName = name,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = dataType,
                ValueRank = valueRank,
                ArrayDimensions = valueRank == ValueRanks.OneDimension ? new uint[] { dimension } : null,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
                Value = value,
            };
            node.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, node.NodeId));
            AddPredefinedNode(SystemContext, node);
            _nodes[name] = node;
        }
    }
}
