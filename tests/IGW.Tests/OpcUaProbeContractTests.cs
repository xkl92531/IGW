using System.Reflection;
using System.IO.Compression;
using IGW.OpcUaProbe;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaProbeContractTests
{
    [Fact]
    public void Probe_assembly_exposes_NodeId_analysis_contract()
    {
        var assembly = Assembly.Load("IGW.OpcUaProbe");

        Assert.NotNull(assembly.GetType("IGW.OpcUaProbe.OpcUaNodeIdentifierAnalyzer"));
    }

    [Fact]
    public void Siemens_native_string_NodeId_is_only_an_unverified_symbol_candidate()
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.OpcUaNodeIdentifierAnalyzer");
        var method = type?.GetMethod("Analyze", [typeof(string)]);

        Assert.NotNull(method);
        var result = method!.Invoke(null, ["ns=3;s=\"DB8011_Heart\".\"HeartBeat\""]);
        Assert.NotNull(result);
        Assert.Equal("String", Property(result!, "IdentifierType"));
        Assert.Equal("3", Property(result!, "NamespaceIndex")?.ToString());
        Assert.Equal("\"DB8011_Heart\".\"HeartBeat\"", Property(result!, "SiemensSymbolCandidate"));
        Assert.Equal("Unverified", Property(result!, "MappingConfidence"));
    }

    [Theory]
    [InlineData("ns=3;i=1001")]
    [InlineData("ns=3;s=Channel.Device.Tag")]
    public void Numeric_or_generic_NodeId_is_not_claimed_as_a_Siemens_address(string nodeId)
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.OpcUaNodeIdentifierAnalyzer");
        var method = type?.GetMethod("Analyze", [typeof(string)]);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [nodeId]);
        Assert.NotNull(result);
        Assert.Null(Property(result!, "SiemensSymbolCandidate"));
        Assert.Equal("Unavailable", Property(result!, "MappingConfidence"));
    }

    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

    [Fact]
    public void Archive_contains_the_complete_read_only_evidence_set()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-opcua-probe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string zipPath = Path.Combine(directory, "probe.zip");
        try
        {
            var type = Assembly.Load("IGW.OpcUaProbe")
                .GetType("IGW.OpcUaProbe.ProbeArchiveWriter");
            var method = type?.GetMethod("Create", [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)]);

            Assert.NotNull(method);
            method!.Invoke(null, [zipPath, "{\"nodes\":[]}", "{\"items\":[]}", "summary", "log", "runtime-write"]);

            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Equal(
                new[] { "README.txt", "igw-runtime-write-errors.log", "opcua-nodes.json", "opcua-probe.log", "summary.txt", "write-diagnostics.json" },
                zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("{\"nodes\":[]}", Read(zip, "opcua-nodes.json"));
            Assert.Equal("{\"items\":[]}", Read(zip, "write-diagnostics.json"));
            Assert.Contains("不读取业务变量值", Read(zip, "README.txt"));
            Assert.Equal("runtime-write", Read(zip, "igw-runtime-write-errors.log"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Runtime_log_collector_keeps_write_failures_and_redacts_credentials()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-opcua-probe-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "igw-20260830.log"), """
                2026-08-30 10:00:00.000 INF [RuntimeEngine] 普通采集日志
                2026-08-30 10:00:01.000 WRN [RuntimeEngine] 设备 PLC8 写入失败: BadNotWritable password=secret123
                Opc.Ua.ServiceResultException: BadNotWritable
                2026-08-30 10:00:02.000 INF [RuntimeEngine] 后续普通日志
                """);

            var type = Assembly.Load("IGW.OpcUaProbe")
                .GetType("IGW.OpcUaProbe.RuntimeWriteLogCollector");
            var method = type?.GetMethod("Collect", [typeof(string), typeof(int), typeof(int)]);

            Assert.NotNull(method);
            string result = Assert.IsType<string>(method!.Invoke(null, [directory, 3, 2000]));
            Assert.Contains("BadNotWritable", result);
            Assert.Contains("ServiceResultException", result);
            Assert.DoesNotContain("普通采集日志", result);
            Assert.DoesNotContain("secret123", result);
            Assert.Contains("password=***", result);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Runtime_log_collector_prioritizes_the_newest_write_failure_when_output_is_limited()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-opcua-probe-log-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "igw-20260830.log"), """
                2026-08-30 09:00:00.000 WRN [RuntimeEngine] 写入失败: BadNotWritable OLD
                2026-08-30 10:00:00.000 WRN [RuntimeEngine] 写入失败: BadOutOfRange NEW
                """);
            var type = Assembly.Load("IGW.OpcUaProbe")
                .GetType("IGW.OpcUaProbe.RuntimeWriteLogCollector");
            var method = type?.GetMethod("Collect", [typeof(string), typeof(int), typeof(int)]);

            Assert.NotNull(method);
            string result = Assert.IsType<string>(method!.Invoke(null, [directory, 1, 2]));
            Assert.Contains("BadOutOfRange NEW", result);
            Assert.DoesNotContain("BadNotWritable OLD", result);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Attribute_catalog_never_requests_the_Value_attribute()
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.ProbeAttributeCatalog");
        var method = type?.GetMethod("ReadOnlyAttributeIds", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var ids = Assert.IsAssignableFrom<IEnumerable<uint>>(method!.Invoke(null, null));
        Assert.DoesNotContain(13u, ids); // OPC UA Attributes.Value
        Assert.Contains(14u, ids);       // DataType
        Assert.Contains(17u, ids);       // AccessLevel
        Assert.Contains(23u, ids);       // DataTypeDefinition
    }

    [Fact]
    public void Probe_assembly_exposes_the_read_only_collector()
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.OpcUaMetadataCollector");

        Assert.NotNull(type);
        Assert.NotNull(type!.GetMethod("CollectAsync"));
    }

    [Fact]
    public void Serialized_report_explicitly_proves_that_values_and_writes_were_not_used()
    {
        var report = new OpcUaProbeReport
        {
            Connection = new ProbeConnectionReport(
                "opc.tcp://10.0.0.1:4840", "opc.tcp://10.0.0.1:4840", "opc.tcp://10.0.0.1:4840",
                "SIMATIC", "urn:simatic", "product", "None", "None", "UserName:operator",
                null, null, null, null, null),
            Namespaces = [],
            Nodes = [],
            Summary = new ProbeSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, "不能确认映射"),
        };
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.ProbeReportFormatter");
        var method = type?.GetMethod("ToJson", [typeof(OpcUaProbeReport)]);

        Assert.NotNull(method);
        string json = Assert.IsType<string>(method!.Invoke(null, [report]));
        Assert.Contains("\"variableValuesRead\": false", json);
        Assert.Contains("\"plcWritesPerformed\": false", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Command_line_keeps_the_password_out_of_its_safe_description()
    {
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.ProbeCommandLine");
        var method = type?.GetMethod("Parse", [typeof(string[])]);

        Assert.NotNull(method);
        var result = method!.Invoke(null,
        [
            new[]
            {
                "--url", "opc.tcp://10.0.0.1:4840",
                "--username", "operator",
                "--password", "top-secret",
                "--output", "C:\\Probe",
            },
        ]);
        Assert.NotNull(result);
        Assert.Equal("C:\\Probe", Property(result!, "OutputDirectory"));
        string safe = Assert.IsType<string>(Property(result!, "SafeDescription"));
        Assert.Contains("operator", safe);
        Assert.DoesNotContain("top-secret", safe);
    }

    [Fact]
    public void Command_line_preserves_the_explicit_local_ipv4_for_multi_nic_sites()
    {
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.ProbeCommandLine");
        var method = type?.GetMethod("Parse", [typeof(string[])]);

        Assert.NotNull(method);
        var result = method!.Invoke(null,
        [
            new[]
            {
                "--url", "opc.tcp://10.217.152.80:4840/UA/S7-1500",
                "--local-ip", "10.217.152.221",
            },
        ]);

        Assert.NotNull(result);
        var options = Assert.IsType<ProbeOptions>(Property(result!, "Options"));
        Assert.Equal("10.217.152.221", options.LocalIp);
        string safe = Assert.IsType<string>(Property(result!, "SafeDescription"));
        Assert.Contains("LocalIp=10.217.152.221", safe);
    }

    [Fact]
    public void Source_bound_relay_keeps_the_original_opcua_path_when_it_builds_the_loopback_url()
    {
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.SourceBoundTcpRelay");
        var method = type?.GetMethod("BuildLoopbackUrl", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        string rewritten = Assert.IsType<string>(method!.Invoke(null,
            ["opc.tcp://10.217.152.80:4840/UA/S7-1500", 49123]));
        Assert.Equal("opc.tcp://127.0.0.1:49123/UA/S7-1500", rewritten);
    }

    [Fact]
    public void Passive_server_observations_include_write_limits_and_rejection_counters()
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.ProbeServerObservationCatalog");
        var method = type?.GetMethod("FieldNames", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(method!.Invoke(null, null));
        Assert.Contains("Server_ServerCapabilities_OperationLimits_MaxNodesPerWrite", names);
        Assert.Contains("Server_ServerDiagnostics_ServerDiagnosticsSummary_RejectedRequestsCount", names);
        Assert.Contains("Server_ServerDiagnostics_ServerDiagnosticsSummary_SecurityRejectedRequestsCount", names);
        Assert.Contains("Server_ServerStatus_State", names);
        Assert.Contains("Server_ServiceLevel", names);
    }

    [Fact]
    public void Browse_window_halves_and_retries_when_the_server_runs_out_of_continuation_points()
    {
        var type = Assembly.Load("IGW.OpcUaProbe")
            .GetType("IGW.OpcUaProbe.ProbeBrowseCapacityWindow");

        Assert.NotNull(type);
        object window = Activator.CreateInstance(type!, [64])!;
        var reduce = type!.GetMethod("TryReduce", [typeof(uint)]);
        Assert.NotNull(reduce);

        Assert.Equal(64, Property(window, "Current"));
        Assert.Equal(true, reduce!.Invoke(window, [0x804B0000u]));
        Assert.Equal(32, Property(window, "Current"));
        Assert.Equal(false, reduce.Invoke(window, [0x80340000u]));
        Assert.Equal(32, Property(window, "Current"));
        for (int attempt = 0; attempt < 8; attempt++)
            Assert.Equal(true, reduce.Invoke(window, [0x804B0000u]));
        Assert.Equal(1, Property(window, "Current"));
    }

    [Fact]
    public void Write_diagnostics_identify_user_permission_as_a_read_only_blocker()
    {
        var node = VariableNode(new Dictionary<string, ProbeAttributeReport>
        {
            ["AccessLevel"] = Attribute(17, (byte)3, ["CurrentRead", "CurrentWrite"]),
            ["UserAccessLevel"] = Attribute(18, (byte)1, ["CurrentRead"]),
            ["DataType"] = Attribute(14, "i=12"),
        });
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.ProbeWriteDiagnosticAnalyzer");
        var method = type?.GetMethod("Analyze", [typeof(ProbeNodeReport)]);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [node]);
        Assert.NotNull(result);
        Assert.Equal(false, Property(result!, "WritableByCurrentUser"));
        var reasons = Assert.IsAssignableFrom<IEnumerable<string>>(Property(result!, "LikelyReasons"));
        Assert.Contains(reasons, reason => reason.Contains("当前登录用户", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_diagnostics_do_not_invent_a_string_limit_when_the_server_does_not_publish_one()
    {
        var node = VariableNode(new Dictionary<string, ProbeAttributeReport>
        {
            ["AccessLevel"] = Attribute(17, (byte)3, ["CurrentRead", "CurrentWrite"]),
            ["UserAccessLevel"] = Attribute(18, (byte)3, ["CurrentRead", "CurrentWrite"]),
            ["DataType"] = Attribute(14, "i=12"),
            ["DataTypeDefinition"] = new(23, "BadAttributeIdInvalid", "0x80350000", null, null),
        });
        var type = Assembly.Load("IGW.OpcUaProbe").GetType("IGW.OpcUaProbe.ProbeWriteDiagnosticAnalyzer");
        var method = type?.GetMethod("Analyze", [typeof(ProbeNodeReport)]);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [node]);
        var reasons = Assert.IsAssignableFrom<IEnumerable<string>>(Property(result!, "LikelyReasons"));
        Assert.Contains(reasons, reason => reason.Contains("未公开字符串长度", StringComparison.Ordinal));
    }

    private static ProbeNodeReport VariableNode(IReadOnlyDictionary<string, ProbeAttributeReport> attributes) => new()
    {
        NodeId = "ns=3;s=\"DB1\".\"Message\"",
        NamespaceIndex = 3,
        NamespaceUri = "urn:simatic",
        IdentifierType = "String",
        Identifier = "\"DB1\".\"Message\"",
        NodeClass = "Variable",
        BrowseName = "3:Message",
        DisplayName = "Message",
        Paths = ["Objects.PLC.DataBlocksGlobal.DB1.Message"],
        PathSegments = [new[] { "Objects", "PLC", "DataBlocksGlobal", "DB1", "Message" }],
        IncomingReferences = [],
        Attributes = attributes,
        SiemensAddressAnalysis = OpcUaNodeIdentifierAnalyzer.Analyze("ns=3;s=\"DB1\".\"Message\""),
    };

    private static ProbeAttributeReport Attribute(uint id, object value, IReadOnlyList<string>? flags = null) =>
        new(id, "Good", "0x00000000", value.GetType().FullName, value, flags);
}
