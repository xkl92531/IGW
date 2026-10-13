using System.Diagnostics;

namespace IGW.Core;

/// <summary>固定桶延迟直方图。记录热路径无分配，供实时流指标计算 P50/P95/P99。</summary>
public sealed class RealtimeLatencyHistogram
{
    private static readonly long[] UpperBoundsMicroseconds =
        [100, 250, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000, 250_000, 500_000, 1_000_000];
    private readonly long[] _counts = new long[UpperBoundsMicroseconds.Length + 1];

    public void RecordElapsed(long startedTimestamp)
    {
        RecordElapsed(startedTimestamp, Stopwatch.GetTimestamp());
    }

    public void RecordElapsed(long startedTimestamp, long completedTimestamp)
    {
        long elapsed = completedTimestamp - startedTimestamp;
        long microseconds = elapsed <= 0
            ? 0
            : (long)(elapsed * (1_000_000d / Stopwatch.Frequency));
        RecordMicroseconds(microseconds);
    }

    public void RecordMicroseconds(long microseconds)
    {
        int bucket = Array.BinarySearch(UpperBoundsMicroseconds, Math.Max(0, microseconds));
        if (bucket < 0) bucket = ~bucket;
        Interlocked.Increment(ref _counts[bucket]);
    }

    public RealtimeLatencySnapshot Snapshot()
    {
        var counts = new long[_counts.Length];
        long total = 0;
        for (int i = 0; i < counts.Length; i++) total += counts[i] = Interlocked.Read(ref _counts[i]);
        return new RealtimeLatencySnapshot(total, Percentile(counts, total, 0.50),
            Percentile(counts, total, 0.95), Percentile(counts, total, 0.99));
    }

    private static long Percentile(long[] counts, long total, double percentile)
    {
        if (total == 0) return 0;
        long target = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long cumulative = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            cumulative += counts[i];
            if (cumulative >= target)
                return i < UpperBoundsMicroseconds.Length ? UpperBoundsMicroseconds[i] : UpperBoundsMicroseconds[^1] + 1;
        }
        return UpperBoundsMicroseconds[^1] + 1;
    }
}

public sealed record RealtimeLatencySnapshot(long Count, long P50Microseconds, long P95Microseconds, long P99Microseconds);
