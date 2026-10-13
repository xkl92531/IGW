namespace IGW.Northbound.Mes;

public sealed record HeartbeatSuccessSummary(long Count, long ElapsedMilliseconds, bool First, bool Recovered);

/// <summary>
/// One constant-size window per system heartbeat loop. Counts successful write callbacks,
/// not distinct MES transactions. No timers, key dictionaries, or external resources.
/// </summary>
public sealed class HeartbeatSuccessLogWindow
{
    private readonly object _gate = new();
    private readonly long _intervalMilliseconds;
    private bool _seenSuccess, _recovering;
    private long _startedAt, _pending;

    public HeartbeatSuccessLogWindow(long intervalMilliseconds = 30_000)
    {
        if (intervalMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        _intervalMilliseconds = intervalMilliseconds;
    }

    public HeartbeatSuccessSummary? Success(long now)
    {
        lock (_gate)
        {
            if (!_seenSuccess || _recovering)
            {
                var result = new HeartbeatSuccessSummary(1, 0, !_seenSuccess && !_recovering, _recovering);
                _seenSuccess = true;
                _recovering = false;
                _startedAt = now;
                return result;
            }
            _pending++;
            return now - _startedAt >= _intervalMilliseconds ? DrainCore(now) : null;
        }
    }

    public HeartbeatSuccessSummary? Failure(long now)
    {
        lock (_gate)
        {
            var pending = DrainCore(now);
            _recovering = true;
            return pending;
        }
    }

    public HeartbeatSuccessSummary? Drain(long now)
    {
        lock (_gate) return DrainCore(now);
    }

    private HeartbeatSuccessSummary? DrainCore(long now)
    {
        if (_pending == 0) return null;
        var result = new HeartbeatSuccessSummary(_pending, Math.Max(0, now - _startedAt), false, false);
        _pending = 0;
        _startedAt = now;
        return result;
    }
}
