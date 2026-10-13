using IGW.Abstractions;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

/// <summary>批量注册前的候选设备构造（仅供协议混用预检）：现有点 - replaceGroups 子树 + 请求点（按组路径与点名覆盖）。</summary>
public sealed class BulkTagCandidateBuilderTests
{
    [Fact]
    public void Device_check_on_the_candidate_does_not_mutate_the_input_project_array_tags()
    {
        var array = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [4], LowerBounds = [] };
        var legacy = new TagDef { Name = "Arr", Address = "DB1.DBW0", DataType = IgwDataType.Int16, Array = array, IsArray = false };
        var grouped = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [2], LowerBounds = [] };
        var groupedTag = new TagDef { Name = "GArr", Address = "DB1.DBW8", DataType = IgwDataType.Int16, Array = grouped };
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "Siemens.S7",
                    Devices = [new DeviceDef { Name = "PLC", Tags = [legacy], Groups = [new TagGroupDef { Name = "G", Tags = [groupedTag] }] }],
                },
            ],
        };

        var (channel, device) = BulkTagCandidateBuilder.Build(project, Request("Line", "Siemens.S7", "PLC",
            Item(["G2"], "New", "DB1.DBW20")));
        Assert.Empty(IGW.Core.ProjectStore.CheckDevice(channel, device));

        Assert.Same(array, legacy.Array);
        Assert.Empty(array.LowerBounds);
        Assert.False(legacy.IsArray);
        Assert.Same(grouped, groupedTag.Array);
        Assert.Empty(grouped.LowerBounds);
        Assert.False(groupedTag.IsArray);
        Assert.NotSame(legacy, device.Tags.Single(t => t.Name == "Arr"));
        Assert.Equal([0], device.Tags.Single(t => t.Name == "Arr").Array!.LowerBounds);
    }

    [Fact]
    public void New_channel_and_device_contain_only_request_items_in_group_tree()
    {
        var project = new IgwProject();
        var request = Request("Line", "Siemens.S7", "PLC",
            Item(["G1"], "A", "DB1.DBX0.0"),
            Item(["G1", "Sub"], "B", "DB1.DBW2"),
            Item([], "Root", "DB1.DBW4"));

        var (channel, device) = BulkTagCandidateBuilder.Build(project, request);

        Assert.Equal("Line", channel.Name);
        Assert.Equal("Siemens.S7", channel.DriverId);
        Assert.Equal("PLC", device.Name);
        Assert.Equal("Root", Assert.Single(device.Tags).Name);
        var g1 = Assert.Single(device.Groups);
        Assert.Equal("G1", g1.Name);
        Assert.Equal("A", Assert.Single(g1.Tags).Name);
        var sub = Assert.Single(g1.SubGroups);
        Assert.Equal("DB1.DBW2", Assert.Single(sub.Tags).Address);
    }

    [Fact]
    public void Existing_device_is_overlaid_case_insensitively_minus_replaced_groups_without_mutating_project()
    {
        var device = new DeviceDef
        {
            Name = "PLC",
            Tags = [Tag("Root", "DB1.DBW0")],
            Groups =
            [
                new TagGroupDef { Name = "Keep", Tags = [Tag("A", "DB1.DBX0.0"), Tag("B", "DB1.DBX0.1")] },
                new TagGroupDef
                {
                    Name = "Old",
                    Tags = [Tag("Gone", "DB2.DBX0.0")],
                    SubGroups = [new TagGroupDef { Name = "Deep", Tags = [Tag("Gone2", "DB2.DBX0.1")] }],
                },
            ],
        };
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "Line", DriverId = "Siemens.S7", Devices = [device] }],
        };
        var request = Request("line", "Siemens.S7", "plc", Item(["keep"], "a", "DB9.DBX1.0"), Item(["New"], "C", "DB3.DBW0"));
        request.ReplaceGroups = [["OLD"]];

        var (channel, candidate) = BulkTagCandidateBuilder.Build(project, request);

        Assert.Same(project.Channels[0], channel);
        Assert.Equal(["Keep", "New"], candidate.Groups.Select(g => g.Name));
        var keep = candidate.Groups[0];
        Assert.Equal(2, keep.Tags.Count);
        Assert.Equal("DB9.DBX1.0", keep.Tags.Single(t => t.Name.Equals("a", StringComparison.OrdinalIgnoreCase)).Address);
        Assert.Equal("DB1.DBX0.1", keep.Tags.Single(t => t.Name == "B").Address);
        Assert.Equal("Root", Assert.Single(candidate.Tags).Name);

        // 内存工程不能被候选构造改动
        Assert.Equal(2, device.Groups.Count);
        Assert.Equal("DB1.DBX0.0", device.Groups[0].Tags[0].Address);
        Assert.Equal(2, device.Groups[0].Tags.Count);
    }

    [Fact]
    public void Replace_group_path_can_target_a_nested_group()
    {
        var device = new DeviceDef
        {
            Name = "PLC",
            Groups =
            [
                new TagGroupDef
                {
                    Name = "Top",
                    Tags = [Tag("Stay", "DB1.DBX0.0")],
                    SubGroups = [new TagGroupDef { Name = "Inner", Tags = [Tag("Gone", "DB1.DBX0.1")] }],
                },
            ],
        };
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "Line", DriverId = "Siemens.S7", Devices = [device] }],
        };
        var request = Request("Line", "Siemens.S7", "PLC");
        request.ReplaceGroups = [["Top", "Inner"]];

        var (_, candidate) = BulkTagCandidateBuilder.Build(project, request);

        var top = Assert.Single(candidate.Groups);
        Assert.Empty(top.SubGroups);
        Assert.Equal("Stay", Assert.Single(top.Tags).Name);
        Assert.Single(device.Groups[0].SubGroups);
    }

    [Fact]
    public void Mixed_candidate_is_detected_by_the_siemens_guard()
    {
        var device = new DeviceDef { Name = "PLC", Tags = [Tag("Classic", "DB1.DBX0.0")] };
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "Line", DriverId = "Siemens.S7", Devices = [device] }],
        };
        var (channel, candidate) = BulkTagCandidateBuilder.Build(
            project, Request("Line", "Siemens.S7", "PLC", Item(["G"], "Sym", "DB490.Type.Error1")));
        Assert.NotNull(SiemensAddressModeGuard.Check(channel, candidate));

        // 同名覆盖掉唯一的 Classic 点后不再混用
        var (channel2, candidate2) = BulkTagCandidateBuilder.Build(
            project, Request("Line", "Siemens.S7", "PLC", Item([], "classic", "DB490.Type.Error1")));
        Assert.Null(SiemensAddressModeGuard.Check(channel2, candidate2));
    }

    private static ProjectBulkTagRequest Request(string channel, string driver, string device, params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec(channel, driver, null),
        Device = new ProjectBulkDeviceSpec(device, "", null),
        Items = [.. items],
    };

    private static ProjectBulkTagItem Item(List<string> path, string name, string address) => new()
    {
        GroupPath = path,
        Name = name,
        Address = address,
        DataType = IgwDataType.Int16,
    };

    private static TagDef Tag(string name, string address) => new() { Name = name, Address = address };
}
