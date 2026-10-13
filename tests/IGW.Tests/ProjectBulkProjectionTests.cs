using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkProjectionTests
{
    private static IgwProject Project() => new()
    {
        Name = "p",
        Channels =
        [
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7",
                Devices = [new DeviceDef { Id = "d1", Name = "D1" }, new DeviceDef { Id = "d2", Name = "D2" }] },
            new ChannelDef { Id = "c2", Name = "B", DriverId = "Siemens.S7", Devices = [new DeviceDef { Id = "d3", Name = "D3" }] },
        ],
    };

    [Fact]
    public void Replaces_only_the_target_device_and_reuses_other_objects()
    {
        var current = Project();
        var originalChannels = current.Channels;
        var originalDevices = current.Channels[0].Devices;
        var originalD1 = current.Channels[0].Devices[0];
        var replacement = new DeviceDef { Id = "d1", Name = "D1", Tags = [new TagDef { Name = "T" }] };
        var next = ProjectBulkProjection.WithDevice(current,
            new ProjectDeviceSnapshot(new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, replacement), out var full);

        Assert.Equal("A.D1", full);
        Assert.Same(replacement, next.Channels[0].Devices[0]);
        Assert.Same(current.Channels[0].Devices[1], next.Channels[0].Devices[1]);
        Assert.Same(current.Channels[1], next.Channels[1]);
        Assert.NotSame(current.Channels[0], next.Channels[0]);
        Assert.Empty(current.Channels[0].Devices[0].Tags);          // 原工程不被修改
        Assert.Equal(current.Name, next.Name);
        // 原工程的列表与对象引用均未被改动
        Assert.Same(originalChannels, current.Channels);
        Assert.Same(originalDevices, current.Channels[0].Devices);
        Assert.Same(originalD1, current.Channels[0].Devices[0]);
        Assert.NotSame(current.Channels, next.Channels);
        Assert.NotSame(current.Channels[0].Devices, next.Channels[0].Devices);
        // 非拓扑属性引用复用
        Assert.Same(current.Settings, next.Settings);
        Assert.Same(current.Templates, next.Templates);
    }

    [Fact]
    public void Adds_new_device_and_new_channel()
    {
        var current = Project();
        var next = ProjectBulkProjection.WithDevice(current,
            new ProjectDeviceSnapshot(new ChannelDef { Id = "c9", Name = "N", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d9", Name = "D9" }), out var full);
        Assert.Equal("N.D9", full);
        Assert.Equal(3, next.Channels.Count);
        Assert.Equal(2, current.Channels.Count);
        Assert.Equal("d9", Assert.Single(next.Channels[2].Devices).Id);
    }

    [Fact]
    public void Adds_new_device_to_existing_channel_without_touching_original()
    {
        var current = Project();
        var next = ProjectBulkProjection.WithDevice(current,
            new ProjectDeviceSnapshot(new ChannelDef { Id = "c2", Name = "B", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d4", Name = "D4" }), out var full);
        Assert.Equal("B.D4", full);
        Assert.Equal(2, next.Channels[1].Devices.Count);
        Assert.Single(current.Channels[1].Devices);
        Assert.Same(current.Channels[0], next.Channels[0]);
    }
}
