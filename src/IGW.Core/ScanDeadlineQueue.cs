namespace IGW.Core;

internal readonly record struct ScanCompletion(long NextDueMs, long MissedPeriods, long LatenessMs);

internal sealed class ScanDeadlineQueue
{
    private readonly PriorityQueue<CompiledScanBlock, (long Due, int Priority, long Sequence)> _queue = new();
    private readonly Dictionary<int, long> _scheduled = new();
    private long _sequence;

    public int Count => _scheduled.Count;

    public void Clear()
    {
        _queue.Clear();
        _scheduled.Clear();
    }

    public void Add(CompiledScanBlock block, long firstDueMs)
    {
        if (!_scheduled.TryAdd(block.Id, firstDueMs))
            throw new InvalidOperationException($"读取块 {block.Id} 已在截止时间队列中。");
        block.DueMs = firstDueMs;
        _queue.Enqueue(block, (firstDueMs, (int)block.Priority, _sequence++));
    }

    public bool TryTakeDue(long nowMs, out CompiledScanBlock block)
    {
        while (_queue.TryPeek(out var candidate, out var priority))
        {
            long due = priority.Due;
            if (!_scheduled.TryGetValue(candidate.Id, out long current) || current != due)
            {
                _queue.Dequeue();
                continue;
            }
            if (due > nowMs) break;
            _queue.Dequeue();
            _scheduled.Remove(candidate.Id);
            block = candidate;
            return true;
        }

        block = null!;
        return false;
    }

    public long DelayUntilNext(long nowMs) =>
        _queue.TryPeek(out _, out var priority) ? Math.Max(0, priority.Due - nowMs) : long.MaxValue;

    public ScanCompletion Complete(CompiledScanBlock block, long completedAtMs)
    {
        long due = block.DueMs;
        long lateness = Math.Max(0, completedAtMs - due);
        long missed = lateness / block.PeriodMs;
        long next = checked(due + (missed + 1) * block.PeriodMs);
        Add(block, next);
        return new ScanCompletion(next, missed, lateness);
    }

    public ScanCompletion CompleteAfter(CompiledScanBlock block, long completedAtMs, int delayMs)
    {
        long due = block.DueMs;
        long lateness = Math.Max(0, completedAtMs - due);
        long missed = lateness / block.PeriodMs;
        long next = checked(completedAtMs + Math.Max(1, delayMs));
        Add(block, next);
        return new ScanCompletion(next, missed, lateness);
    }
}
