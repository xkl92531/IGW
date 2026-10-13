using System.IO.Compression;
using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class AcquisitionDiagnosticsExportTests
{
    [Fact]
    public async Task Export_has_six_entries_missing_sources_and_never_activates_driver_or_session()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var host = new TimingHost(hub, driver);
        await using var engine = host.Engine;
        await engine.StartAsync();
        await using var app = await StartApp(host);
        using var client = app.GetTestClient();
        var before = (driver.Calls, driver.Connections);
        using var response = await client.GetAsync("/api/v1/diagnostics/acquisition-export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".zip", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "README.txt", "batches.csv", "connection-diagnostics.json", "network.csv", "requests.csv", "summary.json" },
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
        using var summary = JsonDocument.Parse(await Read(zip, "summary.json"));
        Assert.Contains("batches", summary.RootElement.GetProperty("missingSources").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("network", summary.RootElement.GetProperty("missingSources").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("RequestMaxMs", await Read(zip, "README.txt"));
        Assert.Equal(before, (driver.Calls, driver.Connections));
    }

    [Theory]
    [InlineData(true, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, true, HttpStatusCode.Forbidden)]
    public async Task Export_uses_existing_diagnostics_gate(bool reloading, bool unlicensed, HttpStatusCode expected)
    {
        await using var hub = new DataHub();
        var host = new TimingHost(hub, new TimedDriver()) { Reloading = reloading };
        await using var engine = host.Engine;
        if (unlicensed) host.Hub = null!;
        await using var app = await StartApp(host);
        using var response = await app.GetTestClient().GetAsync("/api/v1/diagnostics/acquisition-export");
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<string> Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Request_export_matches_71_points_one_batch_five_requests_and_anonymous_sessions()
    {
        var original = ReviewSnapshot();
        var requests = Enumerable.Range(0, 5).Select(i => new ProtocolRequestSample(
            new("read", $"DB1 bytes {i * 100}-{i * 100 + 99}", i == 4 ? 15 : 14, 100, 2), 3.5, false, false)).ToArray();
        var first = original.Devices[0] with
        {
            Summary = original.Devices[0].Summary with { PollingTags = 71, PlannedBlocks = 1,
                LogicalBatchCount = 1, LatestProtocolRequestCount = 5, ProtocolRequestSamples = requests,
                OmittedProtocolRequestSamples = 0, DeviceSessionFingerprint = "aabbcc-p", SessionGeneration = 2,
                SameEndpointDeviceCount = 2, SameEndpointSessionCount = 2, KnownSessionLimit = 3,
                SessionLimitSource = "fixture server evidence" },
            Records = [original.Devices[0].Records[0] with { TagCount = 71, LogicalBatchCount = 1,
                LatestProtocolRequestCount = 5, ProtocolRequestSamples = requests, OmittedProtocolRequestSamples = 0,
                DeviceSessionFingerprint = "aabbcc-p", SessionGeneration = 2 }]
        };
        var second = first with { Summary = first.Summary with { Name = "Other", DeviceSessionFingerprint = "ddeeff-p", SessionGeneration = 1 }, Records = [] };
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(original with { Devices = [first, second] }, "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        using var json = JsonDocument.Parse(await Read(zip, "summary.json"));
        var summary = json.RootElement.GetProperty("devices")[0];
        Assert.Equal(71, summary.GetProperty("pollingTags").GetInt32());
        Assert.Equal(1, summary.GetProperty("logicalBatchCount").GetInt32());
        Assert.Equal(5, summary.GetProperty("latestProtocolRequestCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("sameEndpointSessionCount").GetInt32());
        Assert.Equal("ddeeff-p", json.RootElement.GetProperty("devices")[1].GetProperty("deviceSessionFingerprint").GetString());
        var batches = CsvRows(await Read(zip, "batches.csv"));
        Assert.Equal("71", batches[1][Array.IndexOf(batches[0], "TagCount")]);
        Assert.Equal("1", batches[1][Array.IndexOf(batches[0], "LogicalBatchCount")]);
        Assert.Equal("5", batches[1][Array.IndexOf(batches[0], "LatestProtocolRequestCount")]);
        var rows = CsvRows(await Read(zip, "requests.csv"));
        Assert.Equal(6, rows.Count);
        Assert.Equal("DB1 bytes 0-99", rows[1][4]);
        Assert.Equal("3.5", rows[1][8]);
        Assert.Equal("100", rows[1][6]);
    }

    [Fact]
    public async Task Request_samples_are_bounded_redacted_and_unknown_sizes_are_empty()
    {
        var original = ReviewSnapshot();
        var samples = Enumerable.Range(0, 70).Select(_ => new ProtocolRequestSample(
            new("read", "DB2 password=SECRET_LITERAL", -1, -1, -1), 1, false, false)).ToArray();
        var device = original.Devices[0] with
        {
            Summary = original.Devices[0].Summary with { ProtocolRequestSamples = samples },
            Records = Enumerable.Range(0, 205).Select(i => original.Devices[0].Records[0] with
            { Sequence = i, ProtocolRequestSamples = samples, OmittedProtocolRequestSamples = 3 }).ToArray()
        };
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(original with { Devices = [device] }, "r", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        string csv = await Read(zip, "requests.csv");
        Assert.DoesNotContain("SECRET_LITERAL", csv);
        Assert.DoesNotContain("SECRET_LITERAL", await Read(zip, "summary.json"));
        var rows = CsvRows(csv);
        Assert.Equal(1 + 200 * 64, rows.Count);
        Assert.Equal("", rows[1][5]); Assert.Equal("", rows[1][6]); Assert.Equal("", rows[1][7]);
        var batches = CsvRows(await Read(zip, "batches.csv"));
        Assert.Equal("9", batches[1][Array.IndexOf(batches[0], "OmittedProtocolRequestSamples")]);
        using var json = JsonDocument.Parse(await Read(zip, "summary.json"));
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Populated_runtime_export_keeps_compiled_NodeIds_bounded_history_and_shared_probe_without_IO()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var echo = new Echo();
        await using var probes = new NetworkPerformanceProbeCoordinator(echo, cadence: TimeSpan.FromHours(1));
        var host = new TimingHost(hub, driver, probes);
        host.Project.Channels[0].DriverId = "OpcUa.Client";
        host.Project.Channels[0].Settings["url"] = "opc.tcp://192.168.6.10:4840";
        host.Project.Channels[0].Settings["password"] = "CONFIG_PASSWORD_LITERAL";
        host.Project.Description = "PROJECT_SECRET_LITERAL";
        host.Project.MqttPublish.Password = "MQTT_PASSWORD_LITERAL";
        host.Project.Channels[0].Devices.Add(new DeviceDef { Name = "Other", Tags = [new TagDef { Name = "OtherValue", Address = "ns=2;s=DB2.Value", DataType = IgwDataType.Int16 }] });
        await using var engine = host.Engine;
        await engine.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.AcquisitionDiagnostics().Network.Count == 0) await Task.Delay(10, timeout.Token);
        var runtimes = (System.Collections.IEnumerable)typeof(RuntimeEngine).GetField("_devices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        var runtime = runtimes.Cast<object>().First();
        var loop = (DeviceLoop)runtime.GetType().GetProperty("Loop")!.GetValue(runtime)!;
        var history = (ScanTimingHistory)typeof(DeviceLoop).GetField("_scanHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(loop)!;
        var block = new CompiledScanBlock { Id = 1, PeriodMs = 100, Priority = ScanDemandPriority.Background, Area = 0, Start = 0, Count = 1,
            Tags = [new TagBinding { TagId = "Line.Plc.Value", Def = new TagDef(), Address = new TagAddress(0, 0, 2) }], Handles = [], HubHandles = [] };
        for (int i = 0; i < 205; i++) history.Record(block, DateTimeOffset.UtcNow, i * 100, i * 100 + 25, i * 100 + 30, 1, 0, null, 0, new(1, 0, 0, 25.5, 25.5, 0));
        var snapshot = engine.AcquisitionDiagnostics();
        Assert.Equal(200, snapshot.Devices[0].Records.Count);
        Assert.Single(snapshot.Network);
        Assert.Equal("ns=2;s=DB1.Value", Assert.Single(snapshot.Devices[0].Addresses).Address);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Devices[0].Records[0].TagIds)[0] = "changed");
        history.Record(block, DateTimeOffset.UtcNow, 21000, 21025, 21030, 1, 0, null, 0);
        Assert.Equal(205, snapshot.Devices[0].Records[0].Sequence);
        Assert.Null(engine.AcquisitionDiagnostics().Devices[0].Summary.NetworkP95RttMs);
        var before = (driver.Calls, driver.Connections, driver.Writes, echo.Calls);
        await using var app = await StartApp(host);
        using var response = await app.GetTestClient().GetAsync("/api/v1/diagnostics/acquisition-export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Contains("ns=2;s=DB1.Value", await Read(zip, "batches.csv"));
        foreach (var entry in zip.Entries)
            foreach (var secret in new[] { "CONFIG_PASSWORD_LITERAL", "PROJECT_SECRET_LITERAL", "MQTT_PASSWORD_LITERAL" })
                Assert.DoesNotContain(secret, await Read(zip, entry.FullName));
        Assert.Equal(before, (driver.Calls, driver.Connections, driver.Writes, echo.Calls));
        var retainedRecords = (ScanTimingRecord?[])typeof(ScanTimingHistory).GetField("_records", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(history)!;
        int latestIndex = Array.FindIndex(retainedRecords, record => record?.Sequence == history.Latest!.Sequence);
        var original = retainedRecords[latestIndex]!;
        retainedRecords[latestIndex] = original with { Attribution = new("evidence-insufficient", "证据不足", "low",
            Enumerable.Range(0, 65).Select(i => "evidence-" + i).ToArray(), Enumerable.Range(0, 65).Select(i => "factor-" + i).ToArray()) };
        var clippedAttribution = engine.AcquisitionDiagnostics();
        Assert.True(clippedAttribution.Truncated);
        Assert.Equal(64, clippedAttribution.Devices[0].Records[0].Attribution!.Evidence.Count);
        Assert.Equal("[truncated]", clippedAttribution.Devices[0].Records[0].Attribution!.Evidence[^1]);
        Assert.Equal("[truncated]", clippedAttribution.Devices[0].Records[0].Attribution!.Factors[^1]);
        retainedRecords[latestIndex] = original;
        Assert.False(engine.AcquisitionDiagnostics().Truncated);
        var oversized = new CompiledScanBlock { Id = 2, PeriodMs = 100, Priority = ScanDemandPriority.Background, Area = 0, Start = 0, Count = 1,
            Tags = Enumerable.Range(0, 4097).Select(i => new TagBinding { TagId = "Unknown." + i, Def = new TagDef(), Address = new TagAddress(0, 0, 2) }).ToArray(), Handles = [], HubHandles = [] };
        history.Record(oversized, DateTimeOffset.UtcNow, 22000, 22025, 22030, 4097, 0, null, 0);
        var bounded = engine.AcquisitionDiagnostics();
        Assert.True(bounded.Truncated);
        Assert.Equal(4096, bounded.Devices[0].Records[0].TagIds.Count);
        Assert.Null(bounded.Devices[0].Addresses.First(address => address.TagId == "Unknown.0").Address);
        await using var boundedStream = await AcquisitionDiagnosticsExport.CreateAsync(bounded, "revision", CancellationToken.None);
        using var boundedZip = new ZipArchive(boundedStream);
        Assert.Contains("[missing]", await Read(boundedZip, "batches.csv"));
        using var boundedSummary = JsonDocument.Parse(await Read(boundedZip, "summary.json"));
        Assert.True(boundedSummary.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("point-addresses", boundedSummary.RootElement.GetProperty("missingSources").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Export_obeys_management_Basic_auth_and_allows_readonly_user_without_exporting_credentials()
    {
        await using var hub = new DataHub();
        var host = new TimingHost(hub, new TimedDriver());
        await using var engine = host.Engine;
        host.Project.Security = new() { Enabled = true, Users = [new() { Username = "reader", Password = "AUTH_PASSWORD_LITERAL", CanWrite = false }] };
        await using var app = await StartApp(host);
        using var client = app.GetTestClient();
        using var denied = await client.GetAsync("/api/v1/diagnostics/acquisition-export");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("reader:AUTH_PASSWORD_LITERAL")));
        client.DefaultRequestHeaders.Add("Cookie", "COOKIE_HEADER_LITERAL");
        using var response = await client.GetAsync("/api/v1/diagnostics/acquisition-export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        foreach (var entry in zip.Entries)
        {
            var text = await Read(zip, entry.FullName);
            Assert.DoesNotContain("AUTH_PASSWORD_LITERAL", text);
            Assert.DoesNotContain("COOKIE_HEADER_LITERAL", text);
            Assert.DoesNotContain(client.DefaultRequestHeaders.Authorization.Parameter!, text);
        }
    }

    private sealed class Echo : INetworkEchoClient
    {
        public int Calls;
        public Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new NetworkEchoResult(true, 2.5, "healthy", null)); }
    }

    [Theory]
    [InlineData("password=PASSWORD_LITERAL")]
    [InlineData("Authorization: Bearer AUTH_LITERAL")]
    [InlineData("Cookie: COOKIE_LITERAL")]
    [InlineData("MES request body={BODY_LITERAL}")]
    [InlineData("response body=RESPONSE_LITERAL")]
    [InlineData("projectSecret=SECRET_LITERAL")]
    [InlineData("{\"arbitraryPayload\":\"BODY_LITERAL\"}")]
    [InlineData("opc.tcp://user:PASSWORD_LITERAL@192.168.6.10:4840")]
    [InlineData("HTTP 401 at https://host/path?token=TOKEN_LITERAL")]
    [InlineData("https://host/?access_token=TOKEN_LITERAL")]
    [InlineData("https://host/?id_token=TOKEN_LITERAL")]
    [InlineData("https://host/?refresh_token=TOKEN_LITERAL")]
    [InlineData("https://host/?api_key=TOKEN_LITERAL")]
    [InlineData("https://host/?key=TOKEN_LITERAL")]
    [InlineData("https://host/?password=TOKEN_LITERAL")]
    [InlineData("https://host/?secret=TOKEN_LITERAL")]
    [InlineData("https://host/?signature=TOKEN_LITERAL")]
    [InlineData("Authorization Basic BASIC_LITERAL")]
    [InlineData("Proxy-Authorization Basic BASIC_LITERAL")]
    [InlineData("Proxy-Authorization: Bearer TOKEN_LITERAL")]
    public async Task Archive_preserves_field_evidence_escapes_csv_and_removes_literal_secrets_from_every_entry(string sensitive)
    {
        var at = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        string unsafeText = "BadRequestTimeout (0x80850000); " + sensitive;
        var batch = new ScanTimingRecord(1, at, 2, 100, null, 150, 100, 50, 1, 1, 1, "bad", unsafeText, 1,
            ["Line.Plc.Value"], new(2, 1, 0, 32.5, 25.5, 12.5),
            new(at.AddSeconds(-30), at, 6, 6, 0, 1.5, 2.5, 2, 0, "healthy", null, true),
            new("igw-local-processing", "IGW本地处理", "confirmed", ["local=50 ms", unsafeText], ["split"]));
        var summary = new ScanTimingDeviceSummary("Line.Plc", "polling", 100, 100, 1, 0, 1, 150, 100, 100, 1, 10, at, 100, 50, 1,
            ProtocolRequestP95Ms: 25.5, NetworkP95RttMs: null);
        var incident = new ConnectionIncident("Line.Plc", "Line", "opcua", ConnectionDiagnosticCategory.ConnectionTimeout,
            "超时", "Timeout", "Check endpoint", unsafeText, ConnectionDiagnosticScope.Session, ConnectionState.Disconnected,
            at, at, null, 1, true, 1000);
        var snapshot = new AcquisitionDiagnosticsSnapshot(at, "1.0", "Test", 1,
            [new(summary, [batch], [new("Line.Plc.Value", "ns=2;s=DB1,\"Value\"\r\nPart")])],
            [new(at, "healthy", 1.5, unsafeText, "192.168.6.2", "192.168.6.10", true)], new([incident], []), false);
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None);
            using var zip = new ZipArchive(stream);
            foreach (var entry in zip.Entries)
            {
                string text = await Read(zip, entry.FullName);
                foreach (var secret in new[] { "PASSWORD_LITERAL", "AUTH_LITERAL", "COOKIE_LITERAL", "BODY_LITERAL", "RESPONSE_LITERAL", "SECRET_LITERAL", "TOKEN_LITERAL", "BASIC_LITERAL" })
                    Assert.DoesNotContain(secret, text);
            }
            string csv = await Read(zip, "batches.csv");
            Assert.Contains("Line.Plc.Value", csv);
            Assert.Contains(@"ns=2;s=DB1,""""Value""""", csv);
            Assert.Contains("25.5", csv);
            Assert.Contains("confirmed", csv);
            Assert.Contains("local=50 ms", csv);
            Assert.Contains("BadRequestTimeout", csv);
            string network = await Read(zip, "network.csv");
            Assert.Contains("192.168.6.2", network);
            Assert.Contains("192.168.6.10", network);
            using var json = JsonDocument.Parse(await Read(zip, "summary.json"));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("devices")[0].GetProperty("networkP95RttMs").ValueKind);
            Assert.Contains("RequestMaxMs", json.RootElement.GetProperty("protocolRequestP95Definition").GetString());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public async Task Archive_cancellation_and_size_limit_fail_before_returning_partial_download()
    {
        await using var hub = new DataHub();
        await using var engine = new RuntimeEngine(new IgwProject(), new DriverRegistry(), hub, NullLoggerFactory.Instance);
        var snapshot = engine.AcquisitionDiagnostics();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None, maximumBytes: 32));
        var stream = Assert.IsType<FileStream>(await AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None));
        string temporaryPath = stream.Name;
        await stream.DisposeAsync();
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Address_with_braces_is_preserved_while_unlabelled_structured_error_body_is_removed()
    {
        var at = DateTimeOffset.UtcNow;
        var batch = new ScanTimingRecord(1, at, 1, 100, null, 1, 1, 0, 1, 1, 0, "good", "[\"BODY_LITERAL\"]", 0, ["Line.Plc.Value"]);
        var summary = new ScanTimingDeviceSummary("Line.Plc", "polling", 100, 100, 1, 0, 1, 1, 0, 0, 0, 1, at, 1, 0, 1);
        var snapshot = new AcquisitionDiagnosticsSnapshot(at, "1", "test", 1,
            [new(summary, [batch], [new("Line.Plc.Value", "ns=2;s=DB1.{Item}<Actual>")])], [], new([], []), false);
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        string csv = await Read(zip, "batches.csv");
        Assert.Contains("ns=2;s=DB1.{Item}<Actual>", csv);
        Assert.DoesNotContain("BODY_LITERAL", csv);
    }

    private static AcquisitionDiagnosticsSnapshot ReviewSnapshot(string identity = "Line.Plc[1]", string? error = null,
        IReadOnlyList<string>? evidence = null, IReadOnlyList<string>? factors = null)
    {
        var at = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        var batch = new ScanTimingRecord(1, at, 1, 100, null, 1, 1, 0, 1, 1, 0, "good", error, 0,
            [identity + ".Value"], new(1, 0, 0, 2.5, 2.5, 0),
            Attribution: new("evidence-insufficient", "证据不足", "low", evidence ?? [], factors ?? []));
        var summary = new ScanTimingDeviceSummary(identity, "polling", 100, 100, 1, 0, 1, 1, 0, 0, 0, 1, at, 1, 0, 1);
        var incident = new ConnectionIncident(identity, identity, "opcua", ConnectionDiagnosticCategory.ConnectionTimeout,
            "超时", "Timeout", "Check endpoint", error ?? "Timeout", ConnectionDiagnosticScope.Session, ConnectionState.Disconnected,
            at, at, null, 1, true, 1000);
        return new(at, "1", "Project[1]{A}<B>", 1,
            [new(summary, [batch], [new(identity + ".Value", identity + ".Node[1]{A}<B>")])],
            [new(at, "healthy", 2.5, error, "192.168.6.2", "192.168.6.10", true)], new([incident], []), false);
    }

    private static List<string[]> CsvRows(string csv)
    {
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv))
        { TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var result = new List<string[]>();
        while (!parser.EndOfData) result.Add(parser.ReadFields()!);
        return result;
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("'=1+1")]
    [InlineData("''=1+1")]
    [InlineData("'literal")]
    [InlineData(" +1+1")]
    [InlineData("\t-1+1")]
    [InlineData(" \t@SUM(1,2)")]
    public async Task Textual_CSV_formula_prefixes_are_neutralized_reversibly_without_changing_numbers(string formula)
    {
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(ReviewSnapshot(formula, formula), "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        var rows = CsvRows(await Read(zip, "batches.csv"));
        Assert.Equal("'" + formula, rows[1][Array.IndexOf(rows[0], "Device")]);
        Assert.Equal("'" + formula, rows[1][Array.IndexOf(rows[0], "Error")]);
        Assert.Equal("'" + formula + ".Value", rows[1][Array.IndexOf(rows[0], "TagIds")]);
        Assert.Equal("'" + formula + ".Node[1]{A}<B>", rows[1][Array.IndexOf(rows[0], "Addresses")]);
        Assert.Equal(formula, rows[1][Array.IndexOf(rows[0], "Device")][1..]);
        using var summary = JsonDocument.Parse(await Read(zip, "summary.json"));
        using var incidents = JsonDocument.Parse(await Read(zip, "connection-diagnostics.json"));
        Assert.Equal(summary.RootElement.GetProperty("devices")[0].GetProperty("name").GetString(), rows[1][0][1..]);
        Assert.Equal(incidents.RootElement.GetProperty("active")[0].GetProperty("device").GetString(), rows[1][0][1..]);
        Assert.Equal("2.5", rows[1][Array.IndexOf(rows[0], "RequestMaxMs")]);
        var network = CsvRows(await Read(zip, "network.csv"));
        Assert.Equal("'" + formula, network[1][Array.IndexOf(network[0], "Error")]);
    }

    [Fact]
    public async Task Typed_identities_remain_verbatim_and_correlate_across_archive_files()
    {
        const string identity = "Line.Plc[1]{A}<B>.password=literal-name";
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(ReviewSnapshot(identity, "Timeout {secret body}"), "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        using var summary = JsonDocument.Parse(await Read(zip, "summary.json"));
        Assert.Equal("Project[1]{A}<B>", summary.RootElement.GetProperty("projectName").GetString());
        Assert.Equal(identity, summary.RootElement.GetProperty("devices")[0].GetProperty("name").GetString());
        using var incidents = JsonDocument.Parse(await Read(zip, "connection-diagnostics.json"));
        Assert.Equal(identity, incidents.RootElement.GetProperty("active")[0].GetProperty("device").GetString());
        Assert.Equal(identity, incidents.RootElement.GetProperty("active")[0].GetProperty("channel").GetString());
        var csv = CsvRows(await Read(zip, "batches.csv"));
        Assert.Equal(identity, csv[1][0]);
        Assert.Equal(identity + ".Value", csv[1][Array.IndexOf(csv[0], "TagIds")]);
        Assert.Equal(identity + ".Node[1]{A}<B>", csv[1][Array.IndexOf(csv[0], "Addresses")]);
        Assert.DoesNotContain("secret body", await Read(zip, "connection-diagnostics.json"));
    }

    [Fact]
    public async Task Identity_length_is_preserved_verbatim_and_governed_by_archive_budget_not_free_text_clipping()
    {
        string identity = "Project[1]." + new string('x', 8200);
        var snapshot = ReviewSnapshot() with { ProjectName = identity };
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        using var summary = JsonDocument.Parse(await Read(zip, "summary.json"));
        Assert.Equal(identity, summary.RootElement.GetProperty("projectName").GetString());
        Assert.False(summary.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("evidence")]
    [InlineData("factors")]
    public async Task Every_bounded_free_text_or_list_clip_is_reported_before_summary_and_keeps_truncated_marker(string source)
    {
        var snapshot = ReviewSnapshot(error: source == "text" ? "Timeout " + new string('x', 8200) : "Timeout",
            evidence: source == "evidence" ? Enumerable.Range(0, 65).Select(i => "evidence-" + i).ToArray() : [],
            factors: source == "factors" ? Enumerable.Range(0, 65).Select(i => "factor-" + i).ToArray() : []);
        await using var stream = await AcquisitionDiagnosticsExport.CreateAsync(snapshot, "revision", CancellationToken.None);
        using var zip = new ZipArchive(stream);
        using var summary = JsonDocument.Parse(await Read(zip, "summary.json"));
        Assert.True(summary.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(64, summary.RootElement.GetProperty("limits").GetProperty("evidenceItems").GetInt32());
        Assert.Equal(64, summary.RootElement.GetProperty("limits").GetProperty("factorItems").GetInt32());
        Assert.Equal(8192, summary.RootElement.GetProperty("limits").GetProperty("freeTextCharacters").GetInt32());
        string csv = await Read(zip, "batches.csv");
        Assert.Contains("[truncated]", csv);
        Assert.DoesNotContain("[redacted]", csv);
        if (source == "text") Assert.Contains("[truncated]", await Read(zip, "connection-diagnostics.json"));
    }

    private static async Task<WebApplication> StartApp(IGatewayHost host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var auth = PublicPlcApiPolicy.Authorize(context, host.Project.Security);
            if (!auth.IsAllowed) { context.Response.StatusCode = auth.StatusCode!.Value; return; }
            if (auth.User is not null) context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] = auth.User;
            await next();
        });
        app.MapIgwApi(host);
        await app.StartAsync();
        return app;
    }

    private sealed class TimedDriver : IDeviceDriver
    {
        public string DriverId => "timing-test";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public int Calls;
        public int Connections;
        public int Writes;
        public Task ConnectAsync(CancellationToken ct) { Connections++; State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref Calls);
            await Task.Delay(20, ct);
            if (call == 3) throw new InvalidDataException("read timed out");
            return tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(1, call == 2 ? StatusCode.BadCommFailure : StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow))).ToArray();
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) { Writes++; return Task.FromResult<IReadOnlyList<WriteResult>>([]); }
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new TagAddress(0, 0, 2); error = null; return true; }
    }
    private sealed class Factory(TimedDriver driver, string id = "timing-test") : IDriverFactory
    {
        public string DriverId => id;
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => driver;
    }

    private sealed class TimingHost : IGatewayHost
    {
        public TimingHost(DataHub hub, TimedDriver driver, NetworkPerformanceProbeCoordinator? probes = null)
        {
            Hub = hub;
            var registry = new DriverRegistry();
            registry.Register(new Factory(driver));
            registry.Register(new Factory(driver, "OpcUa.Client"));
            Engine = new RuntimeEngine(Project, registry, hub, NullLoggerFactory.Instance, networkProbes: probes);
        }
        public DataHub Hub { get; set; }
        public RuntimeEngine Engine { get; }
        public IgwProject Project { get; } = new() { Channels = [new ChannelDef { Name = "Line", DriverId = "timing-test",
            Devices = [new DeviceDef { Name = "Plc", Tags = [new TagDef { Name = "Value", Address = "ns=2;s=DB1.Value", DataType = IgwDataType.Int16, ScanRateMs = 100 }] }] }] };
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters { get; } = new NetworkAdapterResolver();
        public int OpcUaSessions => 0;
        public bool Reloading { get; set; }
        public Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowseResult> BrowseOpcUaBranchAsync(BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public object ComponentsSnapshot() => new { };
        public object LicenseSnapshot() => new { blocked = false };
        public Task<object> ActivateLicenseAsync(string code) => throw new NotSupportedException();
        public Task ReloadAsync(IgwProject newProject) => throw new NotSupportedException();
        public object MesStatusSnapshot() => new { };
        public object MesLogSnapshot(int limit) => Array.Empty<object>();
        public IReadOnlyList<MesTaskStatus> MesTaskStatuses() => [];
        public IReadOnlyList<MesFlowEvent> MesTimeline(string taskName) => [];
        public Task<object> MesTestAsync(MesTask task, CancellationToken ct) => throw new NotSupportedException();
    }
}
