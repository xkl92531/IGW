using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeEngineNetworkBindingTests
{
    [Fact]
    public async Task Explicit_adapter_rewrites_only_factory_endpoint_and_exposes_binding()
    {
        var project = Project("loop", "127.0.0.1");
        var factory = new CaptureFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var engine = new RuntimeEngine(project, registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: new Catalog());

        await engine.StartAsync();

        Assert.StartsWith("127.0.0.1:", factory.Endpoint);
        Assert.Equal("192.0.2.10:102", project.Channels[0].Devices[0].Endpoint);
        Assert.NotNull(engine.NetworkBindingSnapshot("C.D"));
    }

    [Fact]
    public async Task Stopping_engine_releases_its_loopback_listener()
    {
        var factory = new CaptureFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var engine = new RuntimeEngine(Project("loop", "127.0.0.1"), registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: new Catalog());
        await engine.StartAsync();
        var endpoint = engine.NetworkBindingSnapshot("C.D")!.LoopbackEndpoint;

        await engine.StopAsync();

        using var client = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(endpoint));
    }

    [Fact]
    public async Task Channel_reconnect_recreates_adapter_resolution_relay_and_driver()
    {
        var factory = new CaptureFactory();
        var catalog = new Catalog();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var engine = new RuntimeEngine(Project("loop", "127.0.0.1"), registry, new DataHub(),
            NullLoggerFactory.Instance, networkAdapters: catalog);
        await engine.StartAsync();

        await engine.ReconnectChannelAsync("C", CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(catalog.AvailableCalls >= 2);
        Assert.NotNull(engine.NetworkBindingSnapshot("C.D"));
    }

    private static IgwProject Project(string id, string ipv4) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "C", DriverId = "Siemens.S7",
                Settings =
                {
                    ["networkAdapter"] = "Loop",
                    ["networkAdapterId"] = id,
                    ["networkAdapterIpv4"] = ipv4
                },
                Devices = [new DeviceDef { Name = "D", Endpoint = "192.0.2.10:102", Settings = { ["dataCollection"] = "禁用" } }]
            }
        ]
    };

    private sealed class Catalog : INetworkAdapterCatalog
    {
        public int AvailableCalls { get; private set; }
        public IReadOnlyList<NetworkAdapterInfo> Available() =>
            AvailableCore();

        private IReadOnlyList<NetworkAdapterInfo> AvailableCore()
        {
            AvailableCalls++;
            return [new("loop", "Loop", "127.0.0.1", 8)];
        }
    }

    private sealed class CaptureFactory : IDriverFactory
    {
        public string Endpoint { get; private set; } = "";
        public int CreateCount { get; private set; }
        public string DriverId => "Siemens.S7";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
        {
            CreateCount++;
            Endpoint = device.Endpoint;
            return new Driver();
        }
    }

    private sealed class Driver : IDeviceDriver
    {
        public string DriverId => "Siemens.S7";
        public ConnectionState State => ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 1);
            error = null;
            return true;
        }
    }
}
