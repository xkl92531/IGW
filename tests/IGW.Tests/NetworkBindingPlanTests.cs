using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class NetworkBindingPlanTests
{
    [Theory]
    [InlineData("192.168.1.2", 103)]
    [InlineData("192.168.1.2:104", 104)]
    public async Task Siemens_relay_uses_configured_port_when_endpoint_has_no_explicit_port(string endpoint, int expected)
    {
        var channel = BoundChannel("Siemens.S7");
        var device = new DeviceDef { Endpoint = endpoint, Settings = { ["port"] = "103" } };
        await using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());
        Assert.Equal(expected, prepared.Lease!.Target.Port);
    }

    [Theory]
    [InlineData("Siemens.S7", "192.168.1.2:102")]
    [InlineData("Modbus.Tcp", "192.168.1.3:502")]
    [InlineData("Omron.Fins", "192.168.1.4:9600")]
    [InlineData("Omron.Cip", "192.168.1.5:44818")]
    [InlineData("AB.Eip", "192.168.1.6:44818")]
    [InlineData("Mitsubishi.Mc", "192.168.1.7:5000")]
    [InlineData("Mqtt.Client", "192.168.1.8:1883")]
    public async Task Explicit_binding_rewrites_runtime_device_endpoint_only(string driverId, string endpoint)
    {
        var channel = BoundChannel(driverId);
        var device = new DeviceDef { Name = "D1", Endpoint = endpoint };
        await using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());

        Assert.StartsWith("127.", prepared.Device.Endpoint);
        Assert.Equal(endpoint, device.Endpoint);
        Assert.Same(device.Tags, prepared.Device.Tags);
        Assert.Same(device.Groups, prepared.Device.Groups);
        Assert.NotNull(prepared.Lease);
    }

    [Fact]
    public async Task Opcua_rewrites_only_runtime_url_and_preserves_real_project_url()
    {
        var channel = BoundChannel("OpcUa.Client");
        channel.Settings["url"] = "opc.tcp://192.168.1.9:49320/IGW";

        await using var prepared = NetworkBindingPlan.Prepare(channel, new DeviceDef { Name = "UA" }, Catalog());

        Assert.StartsWith("opc.tcp://127.0.0.1:", prepared.Channel.Settings["url"]);
        Assert.EndsWith("/IGW", prepared.Channel.Settings["url"]);
        Assert.Equal("opc.tcp://192.168.1.9:49320/IGW", channel.Settings["url"]);
    }

    [Fact]
    public void Opcua_without_an_upstream_url_never_creates_a_relay_to_localhost()
    {
        var channel = BoundChannel("OpcUa.Client");

        var error = Assert.Throws<InvalidOperationException>(() =>
            NetworkBindingPlan.Prepare(channel, new DeviceDef { Name = "UA" }, Catalog()));

        Assert.Contains("未配置", error.Message);
        Assert.DoesNotContain("localhost", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Default_adapter_returns_original_definitions_without_a_relay()
    {
        var channel = new ChannelDef { DriverId = "Siemens.S7", Settings = { ["networkAdapter"] = "默认值" } };
        var device = new DeviceDef { Endpoint = "192.168.1.2:102" };

        await using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());

        Assert.Same(channel, prepared.Channel);
        Assert.Same(device, prepared.Device);
        Assert.Null(prepared.Lease);
    }

    [Fact]
    public void Unsupported_non_tcp_driver_is_left_unchanged()
    {
        var channel = BoundChannel("Simulator");
        var device = new DeviceDef { Endpoint = "ignored" };

        using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());

        Assert.Same(channel, prepared.Channel);
        Assert.Same(device, prepared.Device);
        Assert.Null(prepared.Lease);
    }

    private static ChannelDef BoundChannel(string driverId) => new()
    {
        Name = "C1",
        DriverId = driverId,
        Settings =
        {
            ["networkAdapter"] = "Loop Test",
            ["networkAdapterId"] = "loop",
            ["networkAdapterIpv4"] = "127.0.0.1"
        }
    };

    private static INetworkAdapterCatalog Catalog() => new StaticCatalog([
        new NetworkAdapterInfo("loop", "Loop Test", "127.0.0.1", 8)
    ]);

    private sealed class StaticCatalog(IReadOnlyList<NetworkAdapterInfo> rows) : INetworkAdapterCatalog
    {
        public IReadOnlyList<NetworkAdapterInfo> Available() => rows;
    }
}
