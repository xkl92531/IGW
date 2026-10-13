namespace IGW.Core;

internal readonly record struct DeviceTimingSnapshot(
    double AveragePeriodMs,
    int P95PeriodMs,
    int P99PeriodMs,
    long OverduePeriods,
    long ConsecutiveOverruns,
    double SamplesPerSecond,
    bool RecentOverrun)
{
    public int WriteQueueP95Ms { get; init; }
    public int WriteResponseP95Ms { get; init; }
    public int WriteVerificationP95Ms { get; init; }
    public int PeakWriteQueueDepth { get; init; }
    public long SameSessionRetries { get; init; }
    public long BatchDownshifts { get; init; }
}

/// <summary>
/// 设备周期统计。累计计数保留进程口径；平均值、P95/P99 和吞吐只统计最近窗口，
/// 防止一次历史断网让恢复后的设备长期显示为 2 秒或 8 秒。
/// </summary>
internal sealed class DeviceTimingMetrics
{
    private sealed class WindowSlot
    {
        public long EpochSecond = long.MinValue;
        public long PeriodSum;
        public long PeriodCount;
        public long Samples;
        public readonly long[] PeriodBuckets = new long[32];
        public readonly long[] WriteQueueBuckets = new long[32];
        public readonly long[] WriteResponseBuckets = new long[32];
        public readonly long[] WriteVerificationBuckets = new long[32];

        public void Reset(long epochSecond)
        {
            EpochSecond = epochSecond;
            PeriodSum = PeriodCount = Samples = 0;
            Array.Clear(PeriodBuckets);
            Array.Clear(WriteQueueBuckets);
            Array.Clear(WriteResponseBuckets);
            Array.Clear(WriteVerificationBuckets);
        }
    }

    private readonly object _gate = new();
    private readonly WindowSlot[] _slots;
    private readonly Func<long> _nowMilliseconds;
    private long _overdue;
    private long _consecutive;
    private long _lastOverrunTick;
    private int _peakWriteQueueDepth;
    private long _sameSessionRetries;
    private long _batchDownshifts;

    public DeviceTimingMetrics() : this(() => Environment.TickCount64, 60) { }

    internal DeviceTimingMetrics(Func<long> nowMilliseconds, int windowSeconds)
    {
        _nowMilliseconds = nowMilliseconds;
        _slots = Enumerable.Range(0, Math.Max(5, windowSeconds)).Select(_ => new WindowSlot()).ToArray();
    }

    public void Reset()
    {
        lock (_gate)
            foreach (var slot in _slots) slot.Reset(long.MinValue);
        Interlocked.Exchange(ref _overdue, 0);
        Interlocked.Exchange(ref _consecutive, 0);
        Volatile.Write(ref _lastOverrunTick, 0);
        Interlocked.Exchange(ref _peakWriteQueueDepth, 0);
        Interlocked.Exchange(ref _sameSessionRetries, 0);
        Interlocked.Exchange(ref _batchDownshifts, 0);
    }

    public void ObserveWriteQueueDepth(int depth)
    {
        int current;
        while (depth > (current = Volatile.Read(ref _peakWriteQueueDepth)) &&
               Interlocked.CompareExchange(ref _peakWriteQueueDepth, depth, current) != current) { }
    }

    public void RecordWritePipeline(int queueDelayMs, int responseMs, int verificationMs, int retryRounds)
    {
        long epochSecond = _nowMilliseconds() / 1000;
        lock (_gate)
        {
            var slot = Slot(epochSecond);
            slot.WriteQueueBuckets[BucketOf(Math.Max(0, queueDelayMs))]++;
            slot.WriteResponseBuckets[BucketOf(Math.Max(0, responseMs))]++;
            slot.WriteVerificationBuckets[BucketOf(Math.Max(0, verificationMs))]++;
        }
        if (retryRounds > 0) Interlocked.Add(ref _sameSessionRetries, retryRounds);
    }

    public void RecordSameSessionRetry() => Interlocked.Increment(ref _sameSessionRetries);
    public void RecordBatchDownshift() => Interlocked.Increment(ref _batchDownshifts);

    public void RecordCompletion(CompiledScanBlock block, long nowMs, int samples, long missedPeriods)
    {
        long previous = block.LastCompletedMs;
        block.LastCompletedMs = nowMs;
        long epochSecond = nowMs / 1000;
        lock (_gate)
        {
            var slot = Slot(epochSecond);
            slot.Samples += samples;
            if (previous > 0)
            {
                long period = Math.Max(0, nowMs - previous);
                slot.PeriodSum += period;
                slot.PeriodCount++;
                slot.PeriodBuckets[BucketOf(period)]++;
            }
        }

        if (missedPeriods > 0)
        {
            Interlocked.Add(ref _overdue, missedPeriods);
            Interlocked.Increment(ref _consecutive);
            Volatile.Write(ref _lastOverrunTick, _nowMilliseconds());
        }
        else
        {
            Interlocked.Exchange(ref _consecutive, 0);
        }
    }

    public DeviceTimingSnapshot Snapshot()
    {
        long now = _nowMilliseconds();
        long currentSecond = now / 1000;
        long periodSum = 0, periodCount = 0, samples = 0;
        var buckets = new long[32];
        var writeQueueBuckets = new long[32];
        var writeResponseBuckets = new long[32];
        var writeVerificationBuckets = new long[32];
        long oldest = long.MaxValue;
        lock (_gate)
        {
            foreach (var slot in _slots)
            {
                long age = currentSecond - slot.EpochSecond;
                if (slot.EpochSecond == long.MinValue || age < 0 || age >= _slots.Length) continue;
                oldest = Math.Min(oldest, slot.EpochSecond);
                periodSum += slot.PeriodSum;
                periodCount += slot.PeriodCount;
                samples += slot.Samples;
                for (int i = 0; i < buckets.Length; i++)
                {
                    buckets[i] += slot.PeriodBuckets[i];
                    writeQueueBuckets[i] += slot.WriteQueueBuckets[i];
                    writeResponseBuckets[i] += slot.WriteResponseBuckets[i];
                    writeVerificationBuckets[i] += slot.WriteVerificationBuckets[i];
                }
            }
        }

        double observedSeconds = oldest == long.MaxValue ? 0 : Math.Max(1, Math.Min(_slots.Length, currentSecond - oldest + 1));
        return new DeviceTimingSnapshot(
            periodCount == 0 ? 0 : (double)periodSum / periodCount,
            Percentile(buckets, 0.95),
            Percentile(buckets, 0.99),
            Volatile.Read(ref _overdue),
            Volatile.Read(ref _consecutive),
            observedSeconds == 0 ? 0 : samples / observedSeconds,
            IsRecentOverrun(now))
        {
            WriteQueueP95Ms = Percentile(writeQueueBuckets, 0.95),
            WriteResponseP95Ms = Percentile(writeResponseBuckets, 0.95),
            WriteVerificationP95Ms = Percentile(writeVerificationBuckets, 0.95),
            PeakWriteQueueDepth = Volatile.Read(ref _peakWriteQueueDepth),
            SameSessionRetries = Volatile.Read(ref _sameSessionRetries),
            BatchDownshifts = Volatile.Read(ref _batchDownshifts),
        };
    }

    private WindowSlot Slot(long epochSecond)
    {
        int index = (int)((ulong)epochSecond % (ulong)_slots.Length);
        var slot = _slots[index];
        if (slot.EpochSecond != epochSecond) slot.Reset(epochSecond);
        return slot;
    }

    private bool IsRecentOverrun(long now)
    {
        long tick = Volatile.Read(ref _lastOverrunTick);
        return tick > 0 && now - tick <= 10_000;
    }

    private static int BucketOf(long period)
    {
        int bucket = 0;
        for (long ceiling = 1; ceiling < period && bucket < 31; ceiling <<= 1) bucket++;
        return bucket;
    }

    private static int Percentile(long[] buckets, double percentile)
    {
        long total = buckets.Sum();
        if (total == 0) return 0;
        long target = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long seen = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            seen += buckets[i];
            if (seen >= target) return i == 0 ? 0 : i >= 30 ? int.MaxValue : 1 << i;
        }
        return int.MaxValue;
    }
}
