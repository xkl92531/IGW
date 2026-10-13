using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectParameterSearchTests
{
    [Fact]
    public void Write_destinations_stay_inside_the_current_db_but_read_sources_can_cross_plcs()
    {
        var project = new IgwProject { Channels = [
            new ChannelDef { Name = "PLC9", Devices = [new DeviceDef { Name = "OP025", Groups = [
                new TagGroupDef { Name = "PackGlueCheck", Tags = [new TagDef { Name = "MESFlag", Address = "DB1", Access = TagAccess.ReadWrite }] },
                new TagGroupDef { Name = "PackGlueCheckOther", Tags = [new TagDef { Name = "MESFlag", Address = "DB2", Access = TagAccess.ReadWrite }] }
            ] }] },
            new ChannelDef { Name = "PLC5", Devices = [new DeviceDef { Name = "OP040", Groups = [
                new TagGroupDef { Name = "AGV", Tags = [new TagDef { Name = "MESFlag", Address = "DB3", Access = TagAccess.ReadWrite }] }
            ] }] }
        ] };
        var writes = ProjectParameterSearch.Search(project, "mes", "PLC9.OP025.PackGlueCheck", "tag", "write", 100, default);
        Assert.Equal("PLC9.OP025.PackGlueCheck.MESFlag", Assert.Single(writes.Items).FullPath);
        var sources = ProjectParameterSearch.Search(project, "mes", "PLC9.OP025.PackGlueCheck", "tag", "read", 100, default);
        Assert.Contains(sources.Items, item => item.FullPath == "PLC5.OP040.AGV.MESFlag");
        Assert.Empty(ProjectParameterSearch.Search(project, "mes", "PLC9.OP025.Missing", "tag", "write", 100, default).Items);
    }

    [Fact]
    public void Search_ReturnsTagDescriptionForMesPicker()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Ua",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Device",
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Name = "DB2100",
                                    Tags =
                                    [
                                        new TagDef
                                        {
                                            Name = "MESFlag",
                                            Address = "ns=2;s=S7Sim.DB2100.MESFlag",
                                            Description = "MES 返回状态：2=已响应，4=流程结束",
                                            Access = TagAccess.ReadWrite,
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var result = ProjectParameterSearch.Search(
            project, null, "Ua.Device.DB2100", "tag", "write", 100, CancellationToken.None);

        var option = Assert.Single(result.Items);
        Assert.Equal("MES 返回状态：2=已响应，4=流程结束", option.Description);
        Assert.Equal("Ua.Device.DB2100.MESFlag", option.FullPath);
        Assert.Equal("ns=2;s=S7Sim.DB2100.MESFlag", option.Address);
    }
}
