using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectTagScopeApiTests
{
    [Fact]
    public void Exact_scope_is_lazy_stably_paged_and_keeps_duplicate_names_distinct_by_path()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Name = "DB",
                                    Description = "target",
                                    Tags = Enumerable.Range(0, 1_501).Select(index => new TagDef
                                    {
                                        Name = $"Value{index:D4}",
                                        Address = $"DB1.DBW{index * 2}",
                                        DataType = IgwDataType.Int16,
                                        Access = TagAccess.ReadWrite,
                                    }).ToList(),
                                    SubGroups = [new TagGroupDef { Name = "Nested", Description = "first" }],
                                },
                                new TagGroupDef { Name = "Other", SubGroups = [new TagGroupDef { Name = "Nested", Description = "second" }] },
                            ],
                        },
                    ],
                },
            ],
        };

        var first = ProjectTagScopeApi.Search(project, "project-rev-a", "realtime-rev-a", "Line.Plc.DB", null, 1_000);
        var second = ProjectTagScopeApi.Search(project, "project-rev-a", "realtime-rev-a", "Line.Plc.DB", first.NextCursor, 1_000);

        Assert.Equal("project-rev-a", first.ProjectRevision);
        Assert.Equal("realtime-rev-a", first.RealtimeRevision);
        Assert.Equal(1_501, first.Total);
        Assert.Equal(1_000, first.Definitions.Count);
        Assert.Equal(501, second.Definitions.Count);
        Assert.Equal("Line.Plc.DB.Nested", Assert.Single(first.Groups).Path);
        Assert.Equal("Line.Plc.DB.Value0000", first.Definitions[0].Id);
        Assert.Equal("DB1.DBW0", first.Definitions[0].Address);
        Assert.Equal("ReadWrite", first.Definitions[0].Access);
        Assert.DoesNotContain(second.Definitions, current => first.Definitions.Any(previous => previous.Id == current.Id));
        var filtered = ProjectTagScopeApi.Search(
            project, "project-rev-a", "realtime-rev-a", "Line.Plc.DB", "Value1499", null, 100, default);
        Assert.Equal(1, filtered.Total);
        Assert.Equal("Line.Plc.DB.Value1499", Assert.Single(filtered.Definitions).Id);
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "project-rev-a", "realtime-rev-b", "Line.Plc.DB", first.NextCursor, 100));
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "project-rev-a", "realtime-rev-a", "Line.Plc.db", null, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectTagScopeApi.Search(project, "project-rev-a", "realtime-rev-a", "Line.Plc.DB", null, 1_001));
    }

    [Fact]
    public void Dotted_device_and_group_names_resolve_by_greedy_longest_match()
    {
        // 现场常见:设备名带 IP(S1500_10.11.38.10),组名也可能含点 —— 路径解析不能按点切分。
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "通用", DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "S1500_10.11.38.10",
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Name = "OP010",
                                    Tags = [new TagDef { Name = "S_NO", Address = "DB160_MainGraph.S_NO", DataType = IgwDataType.Int16, Access = TagAccess.Read }],
                                    SubGroups = [new TagGroupDef { Name = "DB160_MainGraph.RT_DATA", Tags = [new TagDef { Name = "EXEC_BITS", Address = "DB160_MainGraph.RT_DATA.EXEC_BITS", DataType = IgwDataType.Bool, Access = TagAccess.ReadWrite }] }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var device = ProjectTagScopeApi.Search(project, "rev", "rev", "通用.S1500_10.11.38.10", null, 100);
        Assert.Equal("OP010", Assert.Single(device.Groups).Name);

        var group = ProjectTagScopeApi.Search(project, "rev", "rev", "通用.S1500_10.11.38.10.OP010", null, 100);
        Assert.Equal(1, group.Total);
        Assert.Equal("通用.S1500_10.11.38.10.OP010.S_NO", group.Definitions[0].Id);

        var nested = ProjectTagScopeApi.Search(project, "rev", "rev", "通用.S1500_10.11.38.10.OP010.DB160_MainGraph.RT_DATA", null, 100);
        Assert.Equal("通用.S1500_10.11.38.10.OP010.DB160_MainGraph.RT_DATA.EXEC_BITS", nested.Definitions[0].Id);

        // 拼错的路径依旧精确报错,不做模糊兜底。
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "rev", "rev", "通用.S1500_10.11.38.11", null, 100));
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "rev", "rev", "通用..S1500_10.11.38.10", null, 100));
    }
}
