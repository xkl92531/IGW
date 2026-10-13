using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>Open Protocol 已下线：旧工程里的这类通道读入时直接忽略，其它内容原样保留。</summary>
public sealed class RetiredProjectContentTests
{
    [Fact]
    public void Open_protocol_channels_are_removed_and_other_channels_kept()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef { Name = "Gun", DriverId = "OpenProtocol.Tcp" },
                new ChannelDef { Name = "Line", DriverId = "Simulator" },
                new ChannelDef { Name = "Gun2", DriverId = "openprotocol.tcp" },
            ],
        };

        Assert.Equal(["Gun", "Gun2"], RetiredProjectContent.RemoveOpenProtocolChannels(project));
        Assert.Equal("Line", Assert.Single(project.Channels).Name);
        Assert.Empty(RetiredProjectContent.RemoveOpenProtocolChannels(project));
    }

    [Fact]
    public void Legacy_project_file_loads_without_its_open_protocol_channel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-retired", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "project.igwproj");
            File.WriteAllText(path, """
                {"channels":[
                  {"name":"Gun","driverId":"OpenProtocol.Tcp","devices":[{"name":"Tool","endpoint":"127.0.0.1:4545",
                    "settings":{"keepAliveMs":"5000","customResultFields":"[]"},
                    "tags":[{"name":"SetVin","address":"SetVin","dataType":"String","access":"Write","stringLength":25,
                      "rejectStringOverflow":true,"writeConfirmation":{"mode":"ProtocolAcknowledged"}}]}]},
                  {"name":"Line","driverId":"Simulator","devices":[{"name":"Plc",
                    "tags":[{"name":"Counter","address":"RAMP","dataType":"Int32"}]}]}]}
                """);

            IgwProject project = ProjectStore.Load(path);

            ChannelDef line = Assert.Single(project.Channels);
            Assert.Equal("Line", line.Name);
            Assert.Equal("Counter", Assert.Single(Assert.Single(line.Devices).Tags).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
