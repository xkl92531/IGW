using IGW.Abstractions;
using IGW.Northbound.RestApi;
using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>日志中心的查询条件：协议、通道、设备下拉，标记组输入提示(开启了采集流程的组)，按工程实际配置给出。</summary>
public sealed class LogFilterOptionsTests
{
    [Fact]
    public void Options_list_used_protocols_channels_devices_and_enabled_flow_groups()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef { Name = "PLC8", DriverId = "Siemens.S7", Devices = [new DeviceDef { Name = "OP205" }, new DeviceDef { Name = "OP210" }] },
                new ChannelDef { Name = "Line2", DriverId = "Modbus.Tcp", Devices = [new DeviceDef { Name = "Pump" }] },
                new ChannelDef { Name = "Odd", DriverId = "Unknown.Driver" },
            ],
            MesUpload = new MesUploadSettings
            {
                Tasks =
                [
                    new MesTask { Id = "t16", Name = "StationEntrance-16", Enabled = true, GroupRef = "PLC8.OP205.StationEntrance-16" },
                    new MesTask { Id = "off", Name = "关掉的", Enabled = false, GroupRef = "PLC8.OP205.Stopped" },
                    new MesTask { Id = "loose", Name = "没绑组", Enabled = true },
                    new MesTask { Id = "deep", Name = "Heart", Enabled = true, GroupRef = "Line2.Pump.Status.Heart" },
                ]
            }
        };
        DriverDescriptor[] drivers =
        [
            new() { DriverId = "Siemens.S7", DisplayName = "西门子 S7" },
            new() { DriverId = "Modbus.Tcp", DisplayName = "Modbus TCP" },
            new() { DriverId = "Mitsubishi.Mc", DisplayName = "三菱 MC" },   // 工程里没用到：不列
        ];

        LogFilterOptions options = LogFilterOptions.Build(project, drivers);

        Assert.Equal([("Modbus.Tcp", "Modbus TCP"), ("Siemens.S7", "西门子 S7"), ("Unknown.Driver", "Unknown.Driver")],
            options.Protocols.Select(item => (item.Id, item.Name)));
        Assert.Equal([("PLC8", "Siemens.S7"), ("Line2", "Modbus.Tcp"), ("Odd", "Unknown.Driver")],
            options.Channels.Select(item => (item.Name, item.Protocol)));
        Assert.Equal(["PLC8.OP205", "PLC8.OP210", "Line2.Pump"], options.Devices.Select(item => item.Path));
        Assert.Equal("OP205", options.Devices[0].Name);
        Assert.Equal("PLC8", options.Devices[0].Channel);

        Assert.Equal(["StationEntrance-16", "Heart"], options.Groups.Select(item => item.Name));   // 只列开启的、绑了组的
        LogFilterGroupOption group = options.Groups[0];
        Assert.Equal("PLC8.OP205.StationEntrance-16", group.Path);
        Assert.Equal("PLC8", group.Channel);
        Assert.Equal("PLC8.OP205", group.Device);
        Assert.Equal("t16", group.TaskId);
        Assert.Equal("StationEntrance-16", group.TaskName);
        Assert.Equal("Line2.Pump", options.Groups[1].Device);   // 子组：设备仍是前两段
    }

    [Fact]
    public async Task Protocol_filter_matches_the_protocol_field_exactly()
    {
        string root = Path.Combine(Path.GetTempPath(), "igw-log-protocol", Guid.NewGuid().ToString("N"));
        try
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-5);
            await using (var store = new LogSegmentStore(root, 30))
            {
                store.Append(Record(at, "S7 采集", "Siemens.S7"));
                store.Append(Record(at.AddSeconds(1), "Modbus 采集", "Modbus.Tcp"));
                store.Append(Record(at.AddSeconds(2), "没有协议的系统日志", null));
                await store.FlushAsync();
                var request = new LogCenterRequest { From = at.AddMinutes(-1), To = at.AddMinutes(1), Protocol = "Siemens.S7" };
                Assert.Equal(["S7 采集"], LogQueryEngine.Query(store, request).Items.Select(item => item.Message));
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        static LogRecord Record(DateTimeOffset at, string message, string? protocol)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (protocol is not null) fields["igw_protocol"] = protocol;
            return new LogRecord(LogRecordCodec.ToMicros(at), 2, "collection", message, fields);
        }
    }
}
