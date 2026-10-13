using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectTagScopeRenameTests
{
    [Fact]
    public void Renamed_channel_requires_the_new_exact_path_and_preserves_slash_group_names()
    {
        var channel = new ChannelDef
        {
            Name = "PLC10", DriverId = "Simulator",
            Devices = [new DeviceDef
            {
                Name = "PLC10", Groups = [new TagGroupDef
                {
                    Name = "DB2050_HeatBeat/Time",
                    Tags = [new TagDef { Name = "HeatBeat", Address = "D0" }, new TagDef { Name = "Time", Address = "D1" }],
                }],
            }],
        };
        var project = new IgwProject { Channels = [channel] };
        const string oldPath = "PLC10.PLC10.DB2050_HeatBeat/Time";
        Assert.Equal(2, ProjectTagScopeApi.Search(project, "r1", "rt1", oldPath, null, 100).Total);
        channel.Name = "PLC_Test";
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "r2", "rt2", oldPath, null, 100));
        var scope = ProjectTagScopeApi.Search(project, "r2", "rt2", "PLC_Test.PLC10.DB2050_HeatBeat/Time", null, 100);
        Assert.Equal(2, scope.Total);
        Assert.All(scope.Definitions, tag => Assert.StartsWith("PLC_Test.PLC10.DB2050_HeatBeat/Time.", tag.Id));
        Assert.Throws<ArgumentException>(() => ProjectTagScopeApi.Search(project, "r2", "rt2", "PLC_Test.PLC10.DB2050_HeatBeat.Time", null, 100));
    }
}
