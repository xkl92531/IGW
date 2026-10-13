using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>注销点时释放点注册订阅(告警/计算/OPC UA 推送共用机制):复用槽位的新点不被死注册吞掉,需求不泄漏。</summary>
public sealed class DataHubUnregisterTests
{
    private const string Device = "C.A";

    [Fact]
    public async Task Unregister_releases_point_registrations_so_reused_slot_delivers_to_new_tag()
    {
        await using var hub = new DataHub();
        var def = new TagDef { Name = "w", DataType = IgwDataType.Int16 };
        hub.Register("C.A.w", def, Device);
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = hub.SubscribePoints(c =>
        {
            if (c.TagId == "C.A.z") got.TrySetResult(c.TagId);
            return ValueTask.CompletedTask;
        }, "test");
        Assert.True(sub.Add("C.A.w"));
        Assert.True(sub.Add("C.A.w"));
        Assert.True(hub.HasDeviceDemand(Device, 1));
        Assert.True(hub.TryGetHandleId("C.A.w", out int hw));

        hub.Unregister("C.A.w");
        Assert.False(hub.HasDeviceDemand(Device, 1));
        sub.Remove("C.A.w");   // 已删点按名移除:无害 no-op
        hub.Register("C.A.z", def, Device);
        Assert.True(hub.TryGetHandleId("C.A.z", out int hz));
        Assert.Equal(hw, hz);

        Assert.True(sub.Add("C.A.z"));
        hub.Update("C.A.z", DataValue.GoodAt((short)1, DateTime.UtcNow));
        Assert.Equal("C.A.z", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        sub.Remove("C.A.z");
        Assert.False(hub.HasDeviceDemand(Device, 1));
    }
}
