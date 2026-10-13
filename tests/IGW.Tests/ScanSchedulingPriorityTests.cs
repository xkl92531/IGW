using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ScanSchedulingPriorityTests
{
    [Fact]
    public void Explicit_process_demand_keeps_the_highest_priority_for_a_shared_tag()
    {
        var hub = new DataHub();
        hub.Register("Line.Plc.DB.Value", new TagDef { Name = "Value", Address = "D0", DataType = IgwDataType.Int16 });
        var registry = new ScanDemandRegistry(hub);

        using var interactive = registry.Acquire(
            "qc", ["Line.Plc.DB.Value"], 100, ScanDemandPriority.Interactive);
        using var process = registry.Acquire(
            "mes", ["Line.Plc.DB.Value"], 200, ScanDemandPriority.Process);

        var demand = Assert.Single(registry.Snapshot("Line.Plc").EffectivePeriods);
        Assert.Equal(100, demand.PeriodMs);
        Assert.Equal(ScanDemandPriority.Process, demand.Priority);
    }

    [Fact]
    public void Owner_metadata_is_compact_per_registration_and_resolves_shared_tags()
    {
        var hub = new DataHub();
        string[] tags = Enumerable.Range(0, 10_000).Select(index => $"Line.Plc.DB.Value{index}").ToArray();
        foreach (string tag in tags)
            hub.Register(tag, new TagDef { Name = tag, Address = tag, DataType = IgwDataType.Int16 });
        var registry = new ScanDemandRegistry(hub);

        using var first = registry.Acquire(
            "mes:task-a", tags, 100, ownerName: "任务 A", scope: "Line.Plc.DB");
        using var second = registry.Acquire(
            "mes:task-b", tags.Take(5_000), 200, ownerName: "任务 B", scope: "Line.Plc.DB");

        DeviceScanDemandSnapshot snapshot = registry.Snapshot("Line.Plc");
        Assert.Equal(2, snapshot.OwnerRegistrations.Count);
        Assert.Equal(10_000, snapshot.OwnerRegistrations[0].TagIds.Count);
        Assert.Equal(new[] { "task-a", "task-b" },
            snapshot.OwnersFor([tags[0]]).Select(owner => owner.Id));
        Assert.Equal(["task-a"], snapshot.OwnersFor([tags[^1]]).Select(owner => owner.Id));
    }

    [Fact]
    public void Due_blocks_are_taken_by_process_then_interactive_then_background()
    {
        var queue = new ScanDeadlineQueue();
        queue.Add(CompiledScanBlock.ForTest(1, 100, ScanDemandPriority.Background), 10);
        queue.Add(CompiledScanBlock.ForTest(2, 100, ScanDemandPriority.Interactive), 10);
        queue.Add(CompiledScanBlock.ForTest(3, 100, ScanDemandPriority.Process), 10);

        Assert.True(queue.TryTakeDue(10, out var first));
        Assert.True(queue.TryTakeDue(10, out var second));
        Assert.True(queue.TryTakeDue(10, out var third));
        Assert.Equal(new[] { 3, 2, 1 }, new[] { first.Id, second.Id, third.Id });
    }
}
