using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using System.Reflection;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaClientEndpointTests
{
    [Fact]
    public void Channel_endpoint_is_used_even_when_device_contains_a_legacy_endpoint()
    {
        var channel = new ChannelDef();
        channel.Settings["url"] = "opc.tcp://10.217.152.80:4845";
        var device = new DeviceDef { Endpoint = "opc.tcp://localhost:4840" };

        string actual = OpcUaClientDriverFactory.ResolveUrl(channel, device);

        Assert.Equal("opc.tcp://10.217.152.80:4845", actual);
    }

    [Fact]
    public void Missing_endpoint_is_rejected_instead_of_connecting_to_the_local_gateway_server()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            OpcUaClientDriverFactory.ResolveUrl(new ChannelDef(), new DeviceDef()));

        Assert.Contains("未配置", error.Message);
        Assert.DoesNotContain("localhost", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_device_endpoint_is_kept_for_existing_projects()
    {
        string actual = OpcUaClientDriverFactory.ResolveUrl(
            new ChannelDef(),
            new DeviceDef { Endpoint = "10.217.152.80:4840" });

        Assert.Equal("opc.tcp://10.217.152.80:4840", actual);
    }

    [Fact]
    public async Task Driver_exposes_the_configured_upstream_endpoint_instead_of_the_gateway_server()
    {
        var factory = new OpcUaClientDriverFactory();
        var channel = new ChannelDef();
        channel.Settings["url"] = "opc.tcp://10.217.152.80:4840";
        await using var driver = factory.CreateDevice(channel, new DeviceDef
        {
            Endpoint = "opc.tcp://127.0.0.1:4845",
        });

        var endpoint = Assert.IsAssignableFrom<IRemoteEndpointDiagnostics>(driver).RemoteEndpoint();

        Assert.Equal("opc.tcp://10.217.152.80:4840", endpoint.ConfiguredEndpoint);
        Assert.Null(endpoint.DiscoveredEndpoint);
        Assert.Null(endpoint.SessionEndpoint);
    }

    [Theory]
    [InlineData("opc.tcp://10.217.152.80:4840", "opc.tcp://10.217.152.80:4840/", true)]
    [InlineData("opc.tcp://10.217.152.80:4840/UA/Server", "opc.tcp://10.217.152.80:4840/UA/Server", true)]
    [InlineData("opc.tcp://10.217.152.80:4840", "opc.tcp://127.0.0.1:4845", false)]
    [InlineData("opc.tcp://10.217.152.80:4840", "opc.tcp://10.217.152.80:4845", false)]
    public void Session_endpoint_must_stay_pinned_to_the_explicit_client_endpoint(
        string configured,
        string session,
        bool expected)
    {
        Assert.Equal(expected, OpcUaClientDeviceDriver.EndpointMatchesConfigured(configured, session));
    }

    [Fact]
    public void Opcua_failure_diagnostic_contains_symbolic_hex_status_and_inner_cause()
    {
        var inner = new IOException("socket timed out");
        var error = new ServiceResultException(
            new Opc.Ua.StatusCode(StatusCodes.BadNodeIdUnknown), "server rejected node", inner);

        string diagnostic = OpcUaClientDeviceDriver.FormatDiagnosticException(error);

        Assert.Contains("BadNodeIdUnknown", diagnostic, StringComparison.Ordinal);
        Assert.Contains("0x80340000", diagnostic, StringComparison.Ordinal);
        Assert.Contains("server rejected node", diagnostic, StringComparison.Ordinal);
        Assert.Contains("socket timed out", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout, true)]
    [InlineData(StatusCodes.BadNotWritable, true)]
    [InlineData(StatusCodes.BadConnectionClosed, false)]
    public void Point_level_write_service_failures_are_returned_without_closing_the_session(
        uint uaStatus,
        bool expectedMapped)
    {
        MethodInfo? method = typeof(OpcUaClientDeviceDriver).GetMethod(
            "TryMapNonTransportWriteFailure",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [new ServiceResultException(uaStatus), null];

        bool mapped = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.Equal(expectedMapped, mapped);
        if (expectedMapped)
            Assert.Equal(new IGW.Abstractions.StatusCode(uaStatus), Assert.IsType<IGW.Abstractions.StatusCode>(arguments[1]));
    }
}
