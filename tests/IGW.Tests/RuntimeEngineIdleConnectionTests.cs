using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeEngineIdleConnectionTests
{
    [Fact]
    public async Task OpcUa_driver_requests_idle_session_keep_alive()
    {
        await using var driver = new OpcUaClientDeviceDriver(
            "opc.tcp://127.0.0.1:49320", 1_000, true, "", "", "None");

        var policy = Assert.IsAssignableFrom<IIdleConnectionPolicyProvider>(driver);

        Assert.Equal(IdleConnectionPolicy.KeepConnectedWhenIdle, policy.IdleConnectionPolicy);
    }

    [Fact]
    public async Task Configured_device_connects_without_demand_but_does_not_read()
    {
        var factory = new KeepAliveRecordingFactory();
        await using var engine = CreateEngine(factory, Project(Channel("Ua", Device("Server",
            new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 }))));
        await engine.StartAsync();

        await WaitUntilAsync(() => factory.Driver.ConnectCalls == 1, TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(0, factory.Driver.ReadCalls);
        var status = Assert.Single(engine.DeviceStatuses());
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.False(status.DemandActive);
        Assert.Equal(0, status.ActiveTags);

        engine.Touch("Ua.Server.Value");
        await WaitUntilAsync(() => factory.Driver.ReadCalls > 0, TimeSpan.FromMilliseconds(500));
    }

    private static RuntimeEngine CreateEngine(IDriverFactory factory, IgwProject project)
    {
        var registry = new DriverRegistry();
        registry.Register(factory);
        return new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
    }

    private static IgwProject Project(params ChannelDef[] channels) => new() { Channels = [.. channels] };

    private static ChannelDef Channel(string name, params DeviceDef[] devices) => new()
    {
        Name = name,
        DriverId = KeepAliveRecordingFactory.Id,
        Devices = [.. devices],
    };

    private static DeviceDef Device(string name, params TagDef[] tags) => new() { Name = name, Tags = [.. tags] };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待条件超时");
            await Task.Delay(20);
        }
    }

    private sealed class KeepAliveRecordingFactory : IDriverFactory
    {
        public const string Id = "KeepAliveRecording";
        public string DriverId => Id;
        public KeepAliveRecordingDriver Driver { get; } = new();
        public DriverDescriptor Describe() => new() { DriverId = Id, DisplayName = Id };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => Driver;
    }

    private sealed class KeepAliveRecordingDriver : IDeviceDriver
    {
        public string DriverId => KeepAliveRecordingFactory.Id;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new EmptyAddressParser();
        public DriverLimits Limits { get; } = new();
        public int ConnectCalls { get; private set; }
        public int ReadCalls { get; private set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCalls++;
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult<IReadOnlyList<TagSample>>([]);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }

    private sealed class EmptyAddressParser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 1);
            error = null;
            return true;
        }
    }
}
