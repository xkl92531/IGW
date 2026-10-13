using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ScanTimingApiTests
{
    [Fact]
    public async Task Seventy_one_point_API_keeps_legacy_members_and_exposes_non_opc_physical_requests()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver { Instrument = true };
        var host = new TimingHost(hub, driver);
        var device = host.Project.Channels[0].Devices[0];
        device.Endpoint = "192.168.1.20:102";
        device.Tags = Enumerable.Range(0, 71).Select(i => new TagDef { Name = $"T{i}", Address = "1", DataType = IgwDataType.Int16, ScanRateMs = 100 }).ToList();
        await using var engine = host.Engine;
        await engine.StartAsync();
        foreach (var tag in device.Tags) engine.Touch($"Line.Plc.{tag.Name}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.ScanTimings().Records.FirstOrDefault()?.TagCount != 71) await Task.Delay(10, timeout.Token);
        await using var app = await StartApp(host);
        using var json = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/v1/scan-timings"));
        var summary = json.RootElement.GetProperty("devices")[0];
        Assert.Equal(71, summary.GetProperty("pollingTags").GetInt32());
        Assert.Equal(0, summary.GetProperty("subscribedTags").GetInt32());
        Assert.Equal(1, summary.GetProperty("plannedBlocks").GetInt32());
        Assert.Equal(1, summary.GetProperty("logicalBatchCount").GetInt32());
        Assert.Equal(5, summary.GetProperty("latestProtocolRequestCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("sameEndpointDeviceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("sameEndpointSessionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("knownSessionLimit").ValueKind);
        var batch = json.RootElement.GetProperty("records")[0];
        Assert.Equal(71, batch.GetProperty("tagCount").GetInt32());
        Assert.Equal(5, batch.GetProperty("protocolRequestSamples").GetArrayLength());
        Assert.Equal(1, batch.GetProperty("sessionGeneration").GetInt64());
        Assert.Matches("^[a-f0-9]{16}-p$", batch.GetProperty("deviceSessionFingerprint").GetString()!);
    }
    [Fact]
    public async Task Qc_batch_query_is_bounded_deduplicated_and_does_not_activate_or_read_PLC()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var host = new TimingHost(hub, driver);
        await using var engine = host.Engine;
        await engine.StartAsync();
        await using var app = await StartApp(host);
        var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/v1/scan-timings/tags",
            new { tagIds = new[] { "Line.Plc.Value", "Line.Plc.Value", "Line.Other.Value" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(data.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Line.Plc.Value", item.GetProperty("tagId").GetString());
        Assert.Equal("standby", item.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("batch").ValueKind);
        Assert.Equal(0, driver.Calls);
        using var tooMany = await client.PostAsJsonAsync("/api/v1/scan-timings/tags",
            new { tagIds = Enumerable.Repeat("Line.Plc.Value", 201).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/scan-timings/tags", new { tagIds = new string?[] { null } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Qc_batch_returns_actual_latest_tag_batch_without_transferring_all_members()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var host = new TimingHost(hub, driver);
        await using var engine = host.Engine;
        await engine.StartAsync();
        engine.Touch("Line.Plc.Value");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.ScanTimings().Records.Count < 4) await Task.Delay(10, timeout.Token);
        await using var app = await StartApp(host);
        using var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/scan-timings/tags",
            new { tagIds = new[] { "Line.Plc.Value", "Line.Plc.Value.More" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(data.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("polling", item.GetProperty("mode").GetString());
        var batch = item.GetProperty("batch");
        Assert.True(batch.GetProperty("driverMs").GetInt32() >= 10);
        Assert.Equal(batch.GetProperty("totalMs").GetInt32(),
            batch.GetProperty("driverMs").GetInt32() + batch.GetProperty("localMs").GetInt32());
        Assert.Equal(100, batch.GetProperty("configuredPeriodMs").GetInt32());
        Assert.Equal(1, batch.GetProperty("tagCount").GetInt32());
        Assert.False(batch.TryGetProperty("tagIds", out _));
        Assert.True(batch.GetProperty("sequence").GetInt64() >= 4);
    }

    [Fact]
    public async Task Query_does_not_activate_PLC_and_reports_unknown_device_explicitly()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var host = new TimingHost(hub, driver);
        await using var engine = host.Engine;
        await engine.StartAsync();
        await using var app = await StartApp(host);
        var client = app.GetTestClient();
        using var data = JsonDocument.Parse(await client.GetStringAsync("/api/v1/scan-timings"));
        Assert.Equal(200, data.RootElement.GetProperty("capacity").GetInt32());
        Assert.Equal("Line.Plc", data.RootElement.GetProperty("device").GetString());
        Assert.Equal("standby", data.RootElement.GetProperty("devices")[0].GetProperty("mode").GetString());
        Assert.Empty(data.RootElement.GetProperty("records").EnumerateArray());
        await Task.Delay(100);
        Assert.Equal(0, driver.Calls);
        using var missing = await client.GetAsync("/api/v1/scan-timings?device=missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Real_loop_exposes_timed_success_bad_data_and_exception_without_extra_reads()
    {
        await using var hub = new DataHub();
        var driver = new TimedDriver();
        var host = new TimingHost(hub, driver);
        await using var engine = host.Engine;
        await engine.StartAsync();
        engine.Touch("Line.Plc.Value");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.ScanTimings().Records.Count < 4) await Task.Delay(10, timeout.Token);
        var snapshot = engine.ScanTimings();
        Assert.Contains(snapshot.Records, record => record.Status == "good");
        Assert.Contains(snapshot.Records, record => record.Status == "bad");
        Assert.Contains(snapshot.Records, record => record.Status == "error" && record.Error == "read timed out");
        Assert.All(snapshot.Records, record =>
        {
            Assert.True(record.DriverMs >= 10);
            Assert.Equal(record.TotalMs, record.DriverMs + record.LocalMs);
            Assert.Equal(100, record.ConfiguredPeriodMs);
            Assert.Equal("Line.Plc.Value", Assert.Single(record.TagIds));
        });
        Assert.Contains(snapshot.Records, record => record.ActualIntervalMs >= 100);
        await using var app = await StartApp(host);
        using var data = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(
            "/api/v1/scan-timings?device=Line.Plc&tag=value&limit=1"));
        Assert.Single(data.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal("polling", data.RootElement.GetProperty("devices")[0].GetProperty("mode").GetString());
        using var filtered = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(
            "/api/v1/scan-timings?device=Line.Plc&tag=not-here"));
        Assert.Empty(filtered.RootElement.GetProperty("records").EnumerateArray());
    }

    private static async Task<WebApplication> StartApp(IGatewayHost host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
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
        public bool Instrument;
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref Calls);
            await Task.Delay(20, ct);
            if (Instrument)
                for (int i = 0; i < 5; i++) ProtocolOperationTiming.Measure(() => true, new("read", $"DB1 bytes {i * 100}-{i * 100 + 99}", 14, 100, 0));
            if (call == 3 && !Instrument) throw new InvalidDataException("read timed out");
            return tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(1, call == 2 ? StatusCode.BadCommFailure : StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow))).ToArray();
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new TagAddress(0, 0, 2); error = null; return true; }
    }
    private sealed class Factory(TimedDriver driver) : IDriverFactory
    {
        public string DriverId => "timing-test";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => driver;
    }

    private sealed class TimingHost : IGatewayHost
    {
        public TimingHost(DataHub hub, TimedDriver driver)
        {
            Hub = hub;
            var registry = new DriverRegistry();
            registry.Register(new Factory(driver));
            Engine = new RuntimeEngine(Project, registry, hub, NullLoggerFactory.Instance);
        }
        public DataHub Hub { get; }
        public RuntimeEngine Engine { get; }
        public IgwProject Project { get; } = new() { Channels = [new ChannelDef { Name = "Line", DriverId = "timing-test",
            Devices = [new DeviceDef { Name = "Plc", Tags = [new TagDef { Name = "Value", Address = "1", DataType = IgwDataType.Int16, ScanRateMs = 100 }] }] }] };
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters { get; } = new NetworkAdapterResolver();
        public int OpcUaSessions => 0;
        public bool Reloading => false;
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
