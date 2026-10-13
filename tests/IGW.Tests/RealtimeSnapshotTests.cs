using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeSnapshotTests
{
    [Fact]
    public async Task Initial_snapshot_is_complete_before_a_change_observed_after_open()
    {
        await using var fixture = await Fixture.StartAsync(snapshotIntervalSeconds: 60);
        await using var lease = await fixture.OpenAsync();

        fixture.Hub.Update("Line.Plc.Tag", DataValue.GoodAt(9, DateTime.UtcNow));

        var messages = await ReadThroughDeltaAsync(lease);
        int begin = messages.FindIndex(static message => message is RealtimeSnapshotBegin);
        int complete = messages.FindIndex(static message => message is RealtimeSnapshotComplete);
        int delta = messages.FindIndex(static message => message is RealtimeStateBatch);
        Assert.True(begin >= 0);
        Assert.True(complete > begin);
        Assert.True(delta > complete);
        var batch = Assert.IsType<RealtimeStateBatch>(messages[delta]);
        Assert.Equal(9, Assert.IsType<int>(batch.Values.Single().Value.Value));
    }

    [Fact]
    public async Task Periodic_snapshot_starts_again_without_closing_the_stream()
    {
        await using var fixture = await Fixture.StartAsync(snapshotIntervalSeconds: 1);
        await using var lease = await fixture.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        int begins = 0;

        await foreach (var message in lease.ReadAllAsync(timeout.Token))
        {
            if (message is RealtimeSnapshotBegin && ++begins == 2) break;
        }

        Assert.Equal(2, begins);
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().ActiveStreams);
    }

    private static async Task<List<RealtimeOutboundMessage>> ReadThroughDeltaAsync(RealtimeStreamLease lease)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var messages = new List<RealtimeOutboundMessage>();
        await foreach (var message in lease.ReadAllAsync(timeout.Token))
        {
            messages.Add(message);
            if (message is RealtimeStateBatch) break;
        }
        return messages;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DataHub hub, RealtimeStreamCoordinator coordinator, string revision)
        {
            Hub = hub;
            Coordinator = coordinator;
            Revision = revision;
        }

        public DataHub Hub { get; }
        public RealtimeStreamCoordinator Coordinator { get; }
        public string Revision { get; }

        public static Task<Fixture> StartAsync(int snapshotIntervalSeconds)
        {
            var hub = new DataHub(shards: 1, busCapacityPerShard: 16_384);
            hub.Register("Line.Plc.Tag", new TagDef
            {
                Name = "Tag",
                Address = "R0",
                DataType = IgwDataType.Int32,
            });
            hub.Update("Line.Plc.Tag", DataValue.GoodAt(1, DateTime.UtcNow));
            const string revision = "rev-snapshot";
            var settings = new TagStreamSettings
            {
                Enabled = true,
                MaxStreams = 100,
                MaxTagsPerStream = 10_000,
                DeltaBatchWindowMs = 10,
                SnapshotIntervalSeconds = snapshotIntervalSeconds,
                MaxConcurrentSnapshots = 1,
                CoordinatorQueueCapacity = 16_384,
            };
            var coordinator = new RealtimeStreamCoordinator(
                hub, revision, settings, NullLogger<RealtimeStreamCoordinator>.Instance);
            return Task.FromResult(new Fixture(hub, coordinator, revision));
        }

        public ValueTask<RealtimeStreamLease> OpenAsync() => Coordinator.OpenAsync(
            new RealtimeStreamOpenRequest(
                "snapshot-client", "operator", Revision,
                ["Line.Plc.Tag"], [], true, 5), default);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }
}
