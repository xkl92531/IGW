using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeProjectRevisionTests
{
    [Fact]
    public void Mes_log_and_unrelated_northbound_changes_keep_the_realtime_revision()
    {
        var baseline = Project();
        string revision = RealtimeProjectRevision.Compute(baseline);

        var mes = ProjectStore.Clone(baseline);
        mes.MesUpload.DefaultMesUrl = "http://mes/new";
        mes.MesUpload.Tasks.Add(new MesTask { Name = "Task", GroupRef = "Line.Plc.DB" });
        Assert.Equal(revision, RealtimeProjectRevision.Compute(mes));

        var log = ProjectStore.Clone(baseline);
        log.Settings.LogRetentionDays = 365;
        log.Settings.LogStorageDirectory = Path.Combine(Path.GetTempPath(), "igw-logs");
        Assert.Equal(revision, RealtimeProjectRevision.Compute(log));

        var opc = ProjectStore.Clone(baseline);
        opc.OpcUa.Port = 49320;
        Assert.Equal(revision, RealtimeProjectRevision.Compute(opc));
    }

    [Fact]
    public void Point_stream_policy_and_read_authorization_changes_replace_the_realtime_revision()
    {
        var baseline = Project();
        string revision = RealtimeProjectRevision.Compute(baseline);

        var point = ProjectStore.Clone(baseline);
        point.Channels[0].Devices[0].Groups[0].Tags[0].Address = "DB1.DBW2";
        Assert.NotEqual(revision, RealtimeProjectRevision.Compute(point));

        var stream = ProjectStore.Clone(baseline);
        stream.Settings.TagStream.MaxStreams = 50;
        Assert.NotEqual(revision, RealtimeProjectRevision.Compute(stream));

        var security = ProjectStore.Clone(baseline);
        security.Security.Enabled = true;
        security.Security.Users.Add(new UserDef { Username = "operator", Password = "secret", ReadBranches = ["Line.Plc.DB"] });
        Assert.NotEqual(revision, RealtimeProjectRevision.Compute(security));
    }

    private static IgwProject Project() => new()
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
                                Tags = [new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 }],
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
