using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectSaveRuntimeTests
{
    [Fact]
    public void Notes_do_not_restart_devices_or_retire_realtime_streams()
    {
        var project = Project();
        var channel = project.Channels[0];
        var device = channel.Devices[0];
        string deviceBefore = RuntimeEngine.DeviceSig(channel, device, new Dictionary<string, string>());
        string realtimeBefore = RealtimeProjectRevision.Compute(project);
        channel.Description = "channel note";
        device.Description = "device note";
        device.Groups[0].Description = "group note";
        device.Groups[0].Tags[0].Description = "point note";

        Assert.Equal(deviceBefore, RuntimeEngine.DeviceSig(channel, device, new Dictionary<string, string>()));
        Assert.Equal(realtimeBefore, RealtimeProjectRevision.Compute(project));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("period")]
    [InlineData("confirmation")]
    [InlineData("unit")]
    [InlineData("template")]
    public void Operational_changes_still_require_runtime_apply(string change)
    {
        var before = Project();
        var after = ProjectStore.Clone(before);
        var tag = after.Channels[0].Devices[0].Groups[0].Tags[0];
        switch (change)
        {
            case "address": tag.Address = "B"; break;
            case "period": tag.ScanRateMs = 700; break;
            case "confirmation": tag.WriteConfirmation = new() { Mode = WriteConfirmationMode.ProtocolAcknowledged }; break;
            case "unit": tag.EngUnit = "mm"; break;
            case "template": after.Templates.Add(new() { Name = "T", DriverId = "Simulator" }); break;
        }
        Assert.True(ProjectRuntimeChanges.Between(before, after).Southbound);
        Assert.NotEqual(RealtimeProjectRevision.Compute(before), RealtimeProjectRevision.Compute(after));
    }

    [Theory]
    [InlineData("mes", NorthboundComponents.Mes)]
    [InlineData("opcua", NorthboundComponents.OpcUa)]
    [InlineData("history", NorthboundComponents.History)]
    [InlineData("mqtt", NorthboundComponents.Mqtt)]
    [InlineData("schedule", NorthboundComponents.Scheduler)]
    [InlineData("notes", NorthboundComponents.None)]
    public void Independent_configuration_changes_only_select_the_affected_component(string change, NorthboundComponents expected)
    {
        var before = Project();
        var after = ProjectStore.Clone(before);
        switch (change)
        {
            case "mes": after.MesUpload.DefaultMesUrl = "http://localhost:1234"; break;
            case "opcua": after.OpcUa.Port = 4841; break;
            case "history": after.History.Enabled = !after.History.Enabled; break;
            case "mqtt": after.MqttPublish.Enabled = !after.MqttPublish.Enabled; break;
            case "schedule": after.Schedules.Add(new()); break;
            case "notes": after.Channels[0].Devices[0].Groups[0].Tags[0].Description = "changed"; break;
        }
        Assert.Equal(new ProjectRuntimeChanges(false, expected), ProjectRuntimeChanges.Between(before, after));
    }

    internal static IgwProject Project() => new()
    {
        Channels = [new ChannelDef { Name = "Line", DriverId = "Simulator", Devices = [new DeviceDef
        { Name = "PLC", Groups = [new TagGroupDef { Name = "Points", Tags = [new TagDef
        { Name = "Value", Address = "A", DataType = IgwDataType.Int16 }] }] }] }]
    };
}
