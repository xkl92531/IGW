using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeProjectGenerationTests
{
    [Fact]
    public async Task Retiring_generation_releases_demand_and_delivers_project_changed_to_old_lease()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 16_384);
        hub.Register("Line.Plc.Old", new TagDef
        {
            Name = "Old",
            Address = "R0",
            DataType = IgwDataType.Int32,
        });
        await using var coordinator = new RealtimeStreamCoordinator(
            hub,
            "rev-old",
            new TagStreamSettings { Enabled = true, CoordinatorQueueCapacity = 16_384 },
            NullLogger<RealtimeStreamCoordinator>.Instance);
        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "client", "operator", "rev-old", ["Line.Plc.Old"], [], false, 5), default);

        await coordinator.RetireGenerationAsync("rev-new", default);

        Assert.Equal(0, coordinator.SnapshotMetrics().SubscriptionRelationships);
        Assert.Equal(0, hub.Stats().Subs.Single(s => s.Name == "realtime-streams").Points);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var message in lease.ReadAllAsync(timeout.Token))
        {
            var changed = Assert.IsType<RealtimeProjectChanged>(message);
            Assert.Equal("rev-new", changed.ProjectRevision);
            return;
        }
        throw new InvalidOperationException("Old realtime lease ended without project-changed.");
    }
}
