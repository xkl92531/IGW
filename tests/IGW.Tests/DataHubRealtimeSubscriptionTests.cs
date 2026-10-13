using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class DataHubRealtimeSubscriptionTests
{
    [Fact]
    public async Task Configured_point_subscription_drops_in_its_own_bounded_queue_without_blocking_DataHub()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 16_384);
        const string tagId = "Line.Plc.Group.Value";
        hub.Register(tagId, new TagDef
        {
            Name = "Value",
            Address = "DB1.DBW0",
            DataType = IgwDataType.Int32,
        });

        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = hub.SubscribePoints(async _ =>
        {
            callbackEntered.TrySetResult();
            await releaseCallback.Task;
        }, "realtime-test", new PointSubscriptionOptions { QueueCapacity = 1_024 });
        Assert.True(subscription.Add(tagId));

        hub.Update(tagId, DataValue.GoodAt(1, DateTime.UtcNow));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int value = 2; value <= 4_096; value++)
            hub.Update(tagId, DataValue.GoodAt(value, DateTime.UtcNow));

        await WaitUntilAsync(
            () => hub.Stats().Subs.Single(s => s.Name == "realtime-test").Dropped > 0,
            TimeSpan.FromSeconds(5));

        var stats = hub.Stats().Subs.Single(s => s.Name == "realtime-test");
        Assert.Equal(1_024, stats.Capacity);
        Assert.True(stats.Dropped > 0);
        releaseCallback.TrySetResult();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("DataHub point subscription did not report bounded-queue drops.");
            await Task.Delay(10);
        }
    }
}
