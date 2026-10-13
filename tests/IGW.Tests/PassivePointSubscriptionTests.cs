using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// QC 监控窗只接收已经被采集到的变化，本身不构成采集需求；
/// 只有点击刷新或定时刷新才去 PLC 读取。
/// </summary>
public sealed class PassivePointSubscriptionTests
{
    private const string Tag = "Line.Plc.DB.Value";
    private const string Device = "Line.Plc";

    [Fact]
    public async Task Passive_subscription_receives_changes_without_activating_the_point()
    {
        await using var hub = new DataHub();
        hub.Register(Tag, new TagDef { Name = "Value", DataType = IgwDataType.Int16 }, Device);
        long version = hub.DeviceDemandVersion(Device);
        var received = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var passive = hub.SubscribePoints(change =>
        {
            if (change.Value.Value is short value) received.TrySetResult(value);
            return ValueTask.CompletedTask;
        }, "qc", new PointSubscriptionOptions { CreatesDemand = false });

        Assert.True(passive.Add(Tag));
        Assert.False(hub.IsActive(Tag, 30_000));
        Assert.False(hub.HasDeviceDemand(Device, 30_000));
        Assert.Equal(version, hub.DeviceDemandVersion(Device));

        // 其他消费者触发的采集结果照常送到 QC。
        hub.Update(Tag, DataValue.GoodAt((short)9, DateTime.UtcNow));
        Assert.Equal((short)9, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        passive.Remove(Tag);
        Assert.False(hub.IsActive(Tag, 30_000));
        Assert.False(hub.HasDeviceDemand(Device, 30_000));
    }

    [Fact]
    public async Task Demand_subscription_still_activates_and_is_released_symmetrically()
    {
        await using var hub = new DataHub();
        hub.Register(Tag, new TagDef { Name = "Value", DataType = IgwDataType.Int16 }, Device);
        using var passive = hub.SubscribePoints(_ => ValueTask.CompletedTask, "qc",
            new PointSubscriptionOptions { CreatesDemand = false });
        passive.Add(Tag);
        var active = hub.SubscribePoints(_ => ValueTask.CompletedTask, "alarms");
        active.Add(Tag);

        Assert.True(hub.IsActive(Tag, 30_000));
        Assert.True(hub.HasDeviceDemand(Device, 30_000));

        active.Dispose();
        // 被动订阅仍在，但不能让点位继续被采集。
        Assert.False(hub.IsActive(Tag, 30_000));
        Assert.False(hub.HasDeviceDemand(Device, 30_000));
        Assert.True(hub.HasSubscribers(HandleOf(hub)));
    }

    [Fact]
    public async Task Disposing_a_passive_subscription_does_not_release_someone_elses_demand()
    {
        await using var hub = new DataHub();
        hub.Register(Tag, new TagDef { Name = "Value", DataType = IgwDataType.Int16 }, Device);
        using var active = hub.SubscribePoints(_ => ValueTask.CompletedTask, "alarms");
        active.Add(Tag);
        var passive = hub.SubscribePoints(_ => ValueTask.CompletedTask, "qc",
            new PointSubscriptionOptions { CreatesDemand = false });
        passive.Add(Tag);

        passive.Dispose();

        Assert.True(hub.IsActive(Tag, 30_000));
        Assert.True(hub.HasDeviceDemand(Device, 30_000));
    }

    [Fact]
    public async Task Realtime_stream_coordinator_subscribes_passively()
    {
        await using var hub = new DataHub();
        hub.Register(Tag, new TagDef { Name = "Value", DataType = IgwDataType.Int16 }, Device);
        await using var coordinator = new RealtimeStreamCoordinator(hub, "rev-1", new TagStreamSettings(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RealtimeStreamCoordinator>.Instance);
        await using var lease = await coordinator.OpenAsync(
            new RealtimeStreamOpenRequest("qc-test", "tester", "rev-1", [Tag], [], IncludeSnapshot: false, HeartbeatSeconds: 15),
            default);

        // 打开 QC 不应让点位进入周期采集或订阅。
        Assert.False(hub.IsActive(Tag, 30_000));
        Assert.False(hub.HasDeviceDemand(Device, 30_000));
    }

    private static object HandleOf(DataHub hub) => Assert.IsAssignableFrom<object>(hub.GetHandle(Tag));
}
