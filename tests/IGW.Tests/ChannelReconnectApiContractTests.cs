using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ChannelReconnectApiContractTests
{
    [Fact]
    public async Task Post_reconnect_calls_host_and_returns_restarted_count()
    {
        var host = new ReconnectHost
        {
            Handler = (name, _) => Task.FromResult<object>(new { channel = name, restarted = 3 }),
        };
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsync("/api/v1/channels/Line%20A/reconnect", null);
        var body = await response.Content.ReadFromJsonAsync<ReconnectResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Line A", host.RequestedChannel);
        Assert.Equal("Line A", body!.Channel);
        Assert.Equal(3, body.Restarted);
    }

    [Fact]
    public async Task Post_reconnect_maps_unknown_channel_to_404()
    {
        var host = new ReconnectHost
        {
            Handler = (_, _) => throw new KeyNotFoundException("missing"),
        };
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsync("/api/v1/channels/Missing/reconnect", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_reconnect_maps_unavailable_runtime_to_409()
    {
        var host = new ReconnectHost
        {
            Handler = (_, _) => throw new InvalidOperationException("Runtime 尚未启动"),
        };
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsync("/api/v1/channels/LineA/reconnect", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Post_reconnect_rejects_reload_in_progress()
    {
        var host = new ReconnectHost { Reloading = true };
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsync("/api/v1/channels/LineA/reconnect", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(host.RequestedChannel);
    }

    [Fact]
    public async Task Get_devices_keeps_protocol_connection_state_available_during_management_reload()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line",
                    DriverId = SimulatorDriverFactory.Id,
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "PLC",
                            Tags =
                            [
                                new TagDef { Name = "Value", Address = "R0", DataType = IgwDataType.Int16 },
                            ],
                        },
                    ],
                },
            ],
        };
        await using var hub = new DataHub();
        var registry = new DriverRegistry();
        registry.Register(new SimulatorDriverFactory());
        await using var engine = new RuntimeEngine(project, registry, hub, NullLoggerFactory.Instance);
        await engine.StartAsync();
        await WaitUntilAsync(
            () => engine.DeviceStatuses().SingleOrDefault()?.State == ConnectionState.Connected,
            TimeSpan.FromSeconds(2));
        var host = new ReconnectHost
        {
            Project = project,
            Hub = hub,
            Engine = engine,
            Reloading = true,
        };
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().GetAsync("/api/v1/devices");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var device = Assert.Single(body.EnumerateArray());
        Assert.Equal("Line.PLC", device.GetProperty("name").GetString());
        Assert.Equal("Connected", device.GetProperty("state").GetString());
    }

    private static async Task<WebApplication> StartAppAsync(IGatewayHost host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapIgwApi(host);
        await app.StartAsync();
        return app;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待模拟设备连接超时");
            await Task.Delay(20);
        }
    }

    private sealed record ReconnectResponse(string Channel, int Restarted);

    private sealed class ReconnectHost : IGatewayHost
    {
        public Func<string, CancellationToken, Task<object>> Handler { get; set; } =
            (name, _) => Task.FromResult<object>(new { channel = name, restarted = 0 });
        public string? RequestedChannel { get; private set; }
        public bool Reloading { get; set; }

        // This becomes the IGatewayHost member implemented by the production route.
        public Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct)
        {
            RequestedChannel = channelName;
            return Handler(channelName, ct);
        }

        public DataHub Hub { get; init; } = null!;
        public RuntimeEngine Engine { get; init; } = null!;
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IgwProject Project { get; init; } = new();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters { get; } = new NetworkAdapterResolver();
        public int OpcUaSessions => 0;
        public Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowseResult> BrowseOpcUaBranchAsync(BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct) => throw new NotSupportedException();
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
