using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceTimingMetricsTests
{
    [Fact]
    public void Snapshot_uses_only_the_recent_sixty_second_window()
    {
        long now = 1_000;
        var metrics = new DeviceTimingMetrics(() => now, 60);
        var block = CompiledScanBlock.ForTest(1, 200);

        metrics.RecordCompletion(block, now, 10, 0);
        now += 1_500;
        metrics.RecordCompletion(block, now, 10, 1);

        var slow = metrics.Snapshot();
        Assert.Equal(2_048, slow.P95PeriodMs);
        Assert.True(slow.AveragePeriodMs >= 1_500);

        now += 61_000;
        var recovered = metrics.Snapshot();
        Assert.Equal(0, recovered.P95PeriodMs);
        Assert.Equal(0, recovered.P99PeriodMs);
        Assert.Equal(0, recovered.AveragePeriodMs);
        Assert.Equal(0, recovered.SamplesPerSecond);
        Assert.Equal(1, recovered.OverduePeriods);
    }

    [Fact]
    public void Snapshot_exposes_aggregate_write_pipeline_timings_and_retry_counts()
    {
        long now = 1_000;
        var metrics = new DeviceTimingMetrics(() => now, 60);

        metrics.ObserveWriteQueueDepth(7);
        metrics.RecordWritePipeline(queueDelayMs: 5, responseMs: 8, verificationMs: 13, retryRounds: 2);
        metrics.RecordBatchDownshift();

        var snapshot = metrics.Snapshot();
        Assert.Equal(8, snapshot.WriteQueueP95Ms);
        Assert.Equal(8, snapshot.WriteResponseP95Ms);
        Assert.Equal(16, snapshot.WriteVerificationP95Ms);
        Assert.Equal(7, snapshot.PeakWriteQueueDepth);
        Assert.Equal(2, snapshot.SameSessionRetries);
        Assert.Equal(1, snapshot.BatchDownshifts);
    }
}
