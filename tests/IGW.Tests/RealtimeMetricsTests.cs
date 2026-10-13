using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeMetricsTests
{
    [Fact]
    public async Task Heartbeat_metrics_reuse_one_aggregate_snapshot_inside_the_cache_window()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = Coordinator(hub);
        await using var lease = await coordinator.OpenAsync(Request("status", ["devices"]), default);

        var first = coordinator.SnapshotMetricsCached();
        var second = coordinator.SnapshotMetricsCached();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Runtime_event_backpressure_resets_only_the_slow_session()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = Coordinator(hub);
        await using var slow = await coordinator.OpenAsync(Request("slow", ["slow-topic"]), default);
        await using var fast = await coordinator.OpenAsync(Request("fast", ["fast-topic"]), default);

        for (int index = 0; index < 5_000; index++)
            coordinator.PublishRuntimeEvent(new RealtimeRuntimeEvent(
                "slow-topic", index, "ordered", index, DateTime.UtcNow));

        var metrics = coordinator.SnapshotMetrics();
        var slowMetrics = Assert.Single(metrics.Sessions, session => session.ClientId == "slow");
        var fastMetrics = Assert.Single(metrics.Sessions, session => session.ClientId == "fast");
        Assert.True(slowMetrics.ResetCount > 0);
        Assert.True(slowMetrics.DroppedEvents > 0);
        Assert.Equal(0, fastMetrics.ResetCount);
        Assert.Equal(0, fastMetrics.DroppedEvents);
    }

    [Fact]
    public async Task Sent_delta_records_bounded_bytes_values_and_latency()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        hub.Register("Line.Plc.Value", new TagDef
        {
            Name = "Value", Address = "D0", DataType = IgwDataType.Int32,
        });
        await using var coordinator = Coordinator(hub);
        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "fast", "operator", "rev-1", ["Line.Plc.Value"], [], false, 10), default);

        hub.Update("Line.Plc.Value", DataValue.GoodAt(7, DateTime.UtcNow));
        var batch = Assert.IsType<RealtimeStateBatch>(await ReadNextAsync(lease));
        await Task.Delay(2);
        coordinator.RecordSent(lease.StreamId, batch, 321);

        var metrics = coordinator.SnapshotMetrics();
        Assert.Equal(1, metrics.SentMessages);
        Assert.Equal(1, metrics.SentValues);
        Assert.Equal(321, metrics.SentBytes);
        Assert.Equal(1, metrics.Latency.Count);
        Assert.True(metrics.Latency.P50Microseconds > 0);
        Assert.Equal(1, Assert.Single(metrics.Sessions).SentMessages);
    }

    private static RealtimeStreamCoordinator Coordinator(DataHub hub) => new(
        hub, "rev-1", new TagStreamSettings
        {
            Enabled = true,
            MaxStreams = 100,
            MaxTagsPerStream = 10_000,
            CoordinatorQueueCapacity = 1_024,
            DeltaBatchWindowMs = 10,
        }, NullLogger<RealtimeStreamCoordinator>.Instance);

    private static RealtimeStreamOpenRequest Request(string clientId, IReadOnlyList<string> topics) =>
        new(clientId, "operator", "rev-1", [], topics, false, 10);

    private static async Task<RealtimeOutboundMessage> ReadNextAsync(RealtimeStreamLease lease)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var message in lease.ReadAllAsync(timeout.Token)) return message;
        throw new InvalidOperationException("Realtime lease completed without a message.");
    }
}
