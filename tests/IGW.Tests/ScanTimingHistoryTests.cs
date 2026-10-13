using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ScanTimingHistoryTests
{
    [Fact]
    public void Seventy_one_points_are_one_logical_batch_with_five_bounded_physical_requests()
    {
        var history = new ScanTimingHistory();
        var block = Block(1, Enumerable.Range(0, 71).Select(i => $"PLC.T{i}").ToArray());
        using var timing = ProtocolOperationTiming.Begin();
        for (int i = 0; i < 5; i++)
            ProtocolOperationTiming.Measure(() => true, new("read", $"DB1 bytes {i * 100}-{i * 100 + 99}", i == 4 ? 15 : 14, 100, 2));
        var record = history.Record(block, DateTimeOffset.UtcNow, 0, 12, 15, 71, 0, null, 0,
            timing.Snapshot(), deviceSessionFingerprint: "anonymous-p", sessionGeneration: 2);
        Assert.Equal(71, record.TagCount);
        Assert.Equal(1, record.LogicalBatchCount);
        Assert.Equal(5, record.LatestProtocolRequestCount);
        Assert.Equal(5, record.ProtocolRequestSamples!.Count);
        Assert.Equal(0, record.OmittedProtocolRequestSamples);
        Assert.Equal("anonymous-p", record.DeviceSessionFingerprint);
        Assert.Equal(2, record.SessionGeneration);
        ProtocolOperationTiming.MarkOpaqueOperation();
        var opaque = history.Record(block, DateTimeOffset.UtcNow, 100, 112, 115, 71, 0, null, 0, timing.Snapshot());
        Assert.Null(opaque.LatestProtocolRequestCount);
        Assert.Equal(5, opaque.ProtocolRequestSamples!.Count);
        var missing = history.Record(block, DateTimeOffset.UtcNow, 200, 212, 215, 71, 0, null, 0);
        Assert.Null(missing.LatestProtocolRequestCount);
        Assert.Null(missing.ProtocolRequestSamples);
        Assert.Null(missing.OmittedProtocolRequestSamples);
    }
    private static CompiledScanBlock Block(int id, params string[] tags) => new()
    {
        Id = id, PeriodMs = 100, Priority = ScanDemandPriority.Background,
        Area = 0, Start = 0, Count = 0,
        Tags = tags.Select(tag => new TagBinding { TagId = tag, Def = new TagDef { Name = tag }, Address = new TagAddress(0, 0, 1) }).ToArray(),
        Handles = [], HubHandles = [],
    };

    [Fact]
    public void Qc_lookup_keeps_each_tags_latest_actual_batch_after_isolation_and_matches_exact_ID()
    {
        var history = new ScanTimingHistory();
        var together = Block(1, "Line.A", "Line.B");
        history.Record(together, DateTimeOffset.UtcNow, 0, 23, 30, 2, 1, null, 0);
        history.Record(together, DateTimeOffset.UtcNow, 100, 108, 110, 1, 0, null, 0,
            requested: [together.Tags[0]]);
        var latest = history.LatestForTags(new HashSet<string> { "Line.A", "Line.B", "Line.A.Extra" });
        Assert.Equal(2, latest.Count);
        Assert.Equal(2, latest["Line.A"].Sequence);
        Assert.Equal(10, latest["Line.A"].TotalMs);
        Assert.Equal(1, latest["Line.A"].TagCount);
        Assert.Equal(1, latest["Line.B"].Sequence);
        Assert.Equal(30, latest["Line.B"].TotalMs);
        Assert.Equal(2, latest["Line.B"].TagCount);
        Assert.Equal("partial", latest["Line.B"].Status);
        for (int i = 0; i < 200; i++)
            history.Record(Block(2, "Line.A"), DateTimeOffset.UtcNow, 200 + i * 100, 201 + i * 100, 202 + i * 100, 1, 0, null, 0);
        Assert.Empty(history.LatestForTags(new HashSet<string> { "Line.B" }));
    }

    [Fact]
    public void Protocol_snapshot_is_optional_and_preserved_without_changing_wall_clock_fields()
    {
        var history = new ScanTimingHistory();
        var protocol = new ProtocolTimingSnapshot(3, 1, 0, 45.5, 20, 7);
        history.Record(Block(1, "PLC.A"), DateTimeOffset.UtcNow, 0, 12, 15, 1, 0, null, 0, protocol);
        var record = Assert.Single(history.Snapshot(1, null));
        Assert.Equal(protocol, record.Protocol);
        Assert.Equal(15, record.TotalMs);
        history.Record(Block(2, "Other.B"), DateTimeOffset.UtcNow, 20, 24, 25, 1, 0, null, 0);
        Assert.Null(history.Latest!.Protocol);
    }

    [Fact]
    public void Records_start_interval_per_block_and_separates_driver_from_local_time()
    {
        var history = new ScanTimingHistory();
        var first = Block(1, "PLC.A", "PLC.B");
        var second = Block(2, "PLC.C");
        history.Record(first, DateTimeOffset.UtcNow, 0, 23, 30, 2, 0, null, 0);
        history.Record(second, DateTimeOffset.UtcNow, 50, 53, 55, 1, 0, null, 0);
        history.Record(first, DateTimeOffset.UtcNow, 100, 125, 132, 2, 0, null, 0);
        var records = history.Snapshot(200, null);
        Assert.Equal(100, records[0].ActualIntervalMs);
        Assert.Equal(32, records[0].TotalMs);
        Assert.Equal(25, records[0].DriverMs);
        Assert.Equal(7, records[0].LocalMs);
        Assert.Null(records[1].ActualIntervalMs);
        Assert.Null(records[2].ActualIntervalMs);
        Assert.Equal(2, records[0].TagCount);
        Assert.Equal(new[] { "PLC.A", "PLC.B" }, records[0].TagIds);
    }

    [Fact]
    public void History_is_bounded_and_filter_keeps_original_batch_duration()
    {
        var history = new ScanTimingHistory();
        var block = Block(1, "Line.A", "Line.B");
        for (int i = 0; i < 205; i++)
            history.Record(block, DateTimeOffset.UtcNow, i * 100, i * 100 + 12, i * 100 + 15, 2, 0, null, 0);
        Assert.Equal(200, history.Snapshot(999, null).Count);
        var filtered = history.Snapshot(1, "line.b");
        Assert.Single(filtered);
        Assert.Equal(205, filtered[0].Sequence);
        Assert.Equal(15, filtered[0].TotalMs);
        Assert.Equal(2, filtered[0].TagCount);
        Assert.Empty(history.Snapshot(100, "unknown"));
    }

    [Fact]
    public void Records_missing_samples_quality_errors_and_exceptions_without_faking_success()
    {
        var history = new ScanTimingHistory();
        var block = Block(1, "A", "B");
        history.Record(block, DateTimeOffset.UtcNow, 10, 20, 21, 1, 0, null, 0);
        history.Record(block, DateTimeOffset.UtcNow, 110, 120, 121, 2, 2, null, 0);
        history.Record(block, DateTimeOffset.UtcNow, 210, 410, 413, 0, 0, "timeout", 2);
        var records = history.Snapshot(200, null);
        Assert.Equal("error", records[0].Status);
        Assert.Equal("timeout", records[0].Error);
        Assert.Equal(2, records[0].MissedPeriods);
        Assert.Equal(2, records[0].BadCount);
        Assert.Equal("bad", records[1].Status);
        Assert.Equal("partial", records[2].Status);
        Assert.Equal(1, records[2].BadCount);
    }

    [Fact]
    public void Reused_block_number_with_different_members_has_no_previous_interval()
    {
        var history = new ScanTimingHistory();
        history.Record(Block(1, "Old"), DateTimeOffset.UtcNow, 10, 15, 16, 1, 0, null, 0);
        history.Record(Block(1, "New"), DateTimeOffset.UtcNow, 110, 115, 116, 1, 0, null, 0);
        Assert.Null(history.Snapshot(1, null)[0].ActualIntervalMs);
    }
}
