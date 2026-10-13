using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 监控中心的点位异常只有完整路径，要把配错设备的点位挪走就得先解析出节点 ID 和当前父节点。
/// 现场的点名里本身就带点号（如 DB110_User.Cmd.Flag），所以不能按点号切分路径。
/// </summary>
public sealed class ProjectPathResolverTests
{
    private static IgwProject Project() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Id = "ch-1", Name = "产线1_PLC1", DriverId = "Siemens.S7",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "dev-plus", Name = "S7Plus",
                        Tags = [new TagDef { Id = "tag-loose", Name = "DirectOnDevice" }],
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Id = "grp-db110", Name = "DB110_OP010_User",
                                Tags = [new TagDef { Id = "tag-flag", Name = "Cmd.1#ClearLine" }],
                                SubGroups =
                                [
                                    new TagGroupDef
                                    {
                                        Id = "grp-status", Name = "Status",
                                        Tags = [new TagDef { Id = "tag-nested", Name = "ProcessOn" }],
                                    },
                                ],
                            },
                        ],
                    },
                    new DeviceDef { Id = "dev-classic", Name = "S7" },
                ],
            },
        ],
    };

    [Theory]
    [InlineData("产线1_PLC1.S7Plus.DB110_OP010_User.Cmd.1#ClearLine", "tag", "tag-flag", "grp-db110", "产线1_PLC1.S7Plus.DB110_OP010_User")]
    [InlineData("产线1_PLC1.S7Plus.DB110_OP010_User.Status.ProcessOn", "tag", "tag-nested", "grp-status", "产线1_PLC1.S7Plus.DB110_OP010_User.Status")]
    [InlineData("产线1_PLC1.S7Plus.DirectOnDevice", "tag", "tag-loose", "dev-plus", "产线1_PLC1.S7Plus")]
    [InlineData("产线1_PLC1.S7Plus.DB110_OP010_User", "group", "grp-db110", "dev-plus", "产线1_PLC1.S7Plus")]
    [InlineData("产线1_PLC1.S7Plus.DB110_OP010_User.Status", "group", "grp-status", "grp-db110", "产线1_PLC1.S7Plus.DB110_OP010_User")]
    [InlineData("产线1_PLC1.S7Plus", "device", "dev-plus", "ch-1", "产线1_PLC1")]
    [InlineData("产线1_PLC1", "channel", "ch-1", null, null)]
    public void Resolves_tags_groups_devices_and_channels(
        string path, string kind, string id, string? parentId, string? parentPath)
    {
        var match = ProjectPathResolver.Resolve(Project(), path);

        Assert.True(match.Found, path);
        Assert.Equal(kind, match.Kind);
        Assert.Equal(id, match.Id);
        Assert.Equal(parentId, match.ParentId);
        Assert.Equal(parentPath, match.ParentPath);
    }

    [Theory]
    [InlineData("产线1_PLC1.S7Plus.DB110_OP010_User.Cmd")]          // 只匹配到点名的一部分
    [InlineData("产线1_PLC1.S7Plus.NoSuchGroup.Flag")]
    [InlineData("产线9.S7Plus.DB110_OP010_User")]
    [InlineData("")]
    public void Unknown_paths_report_not_found(string path)
    {
        var match = ProjectPathResolver.Resolve(Project(), path);

        Assert.False(match.Found);
        Assert.Null(match.Id);
    }
}
