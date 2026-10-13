using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagAliasResolverTests
{
    [Fact]
    public void Resolves_dcs_alias_across_internal_driver_channel()
    {
        var project = ProjectWithDevice("通用_S1500", "PhysicalPlc", "S1500_10.1.2.3");
        var resolver = new DcsTagAliasResolver(
            project, ["通用_S1500.PhysicalPlc.DB1.Struct.Value"]);

        Assert.True(resolver.TryResolve(
            "通用.S1500_10.1.2.3.DB1.Struct.Value", out var physical, out var error), error);
        Assert.Equal("通用_S1500.PhysicalPlc.DB1.Struct.Value", physical);
    }

    [Fact]
    public void Accepts_native_physical_path_without_alias_metadata()
    {
        var project = ProjectWithDevice("Line", "PLC", logicalDevice: null);
        var resolver = new DcsTagAliasResolver(project, ["Line.PLC.DB1.Value"]);

        Assert.True(resolver.TryResolve("line.plc.db1.value", out var physical, out var error), error);
        Assert.Equal("Line.PLC.DB1.Value", physical);
    }

    [Fact]
    public void Resolves_alias_case_insensitively()
    {
        var project = ProjectWithDevice("通用_S1500", "PhysicalPlc", "S1500_10.1.2.3");
        var resolver = new DcsTagAliasResolver(project, ["通用_S1500.PhysicalPlc.DB1.Value"]);

        Assert.True(resolver.TryResolve(
            "通用.s1500_10.1.2.3.db1.value", out var physical, out var error), error);
        Assert.Equal("通用_S1500.PhysicalPlc.DB1.Value", physical);
    }

    [Fact]
    public void Resolves_alias_when_physical_device_name_contains_ipv4_dots()
    {
        var project = ProjectWithDevice("通用_OpcUa", "OpcUa_10.0.0.9", "OpcUa_10.0.0.9");
        var resolver = new DcsTagAliasResolver(
            project, ["通用_OpcUa.OpcUa_10.0.0.9.Ua.Value"]);

        Assert.True(resolver.TryResolve(
            "通用.OpcUa_10.0.0.9.Ua.Value", out var physical, out var error), error);
        Assert.Equal("通用_OpcUa.OpcUa_10.0.0.9.Ua.Value", physical);
    }

    [Fact]
    public void Rejects_ambiguous_alias_instead_of_guessing()
    {
        var project = new IgwProject
        {
            Channels =
            [
                Channel("通用_S1500", Device("P1", "S1500_10.1.2.3")),
                Channel("通用_S7", Device("P2", "S1500_10.1.2.3")),
            ],
        };
        var resolver = new DcsTagAliasResolver(project,
        [
            "通用_S1500.P1.DB1.Value",
            "通用_S7.P2.DB1.Value",
        ]);

        Assert.False(resolver.TryResolve("通用.S1500_10.1.2.3.DB1.Value", out _, out var error));
        Assert.Contains("不唯一", error);
    }

    [Fact]
    public void Deleted_physical_tag_is_not_resolved_from_stale_project_configuration()
    {
        var project = ProjectWithDevice("通用_S1500", "PhysicalPlc", "S1500_10.1.2.3");
        var resolver = new DcsTagAliasResolver(project, []);

        Assert.False(resolver.TryResolve("通用.S1500_10.1.2.3.DB1.Value", out _, out var error));
        Assert.Contains("不存在", error);
    }

    [Fact]
    public void Rejects_blank_requested_path()
    {
        var resolver = new DcsTagAliasResolver(new IgwProject(), []);

        Assert.False(resolver.TryResolve("  ", out _, out var error));
        Assert.Contains("不能为空", error);
    }

    [Fact]
    public void Callback_resolver_checks_tag_existence_lazily_without_building_a_point_index()
    {
        var project = ProjectWithDevice("通用_S1500", "S1500_10.1.2.3", "S1500_10.1.2.3");
        int existenceChecks = 0;
        var resolver = new DcsTagAliasResolver(project, tagId =>
        {
            existenceChecks++;
            return tagId.Equals(
                "通用_S1500.S1500_10.1.2.3.DB1.Value", StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(0, existenceChecks);
        Assert.True(resolver.TryResolve(
            "通用.S1500_10.1.2.3.DB1.Value", out var physical, out var error), error);
        Assert.Equal("通用_S1500.S1500_10.1.2.3.DB1.Value", physical);
        Assert.Equal(2, existenceChecks); // 先查请求是否为物理路径，再查转换后的候选路径
    }

    private static IgwProject ProjectWithDevice(string channel, string device, string? logicalDevice) => new()
    {
        Channels = [Channel(channel, Device(device, logicalDevice))],
    };

    private static ChannelDef Channel(string name, DeviceDef device) => new()
    {
        Name = name,
        DriverId = "Siemens.S7",
        Devices = [device],
    };

    private static DeviceDef Device(string name, string? logicalDevice)
    {
        var settings = new Dictionary<string, string>();
        if (logicalDevice is not null) settings[DcsTagMetadata.LogicalDevice] = logicalDevice;
        return new DeviceDef { Name = name, Settings = settings };
    }
}
