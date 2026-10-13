namespace IGW.Core;

/// <summary>Lives outside device loops so rebuilding a driver cannot reset its recovery budget.</summary>
internal sealed class ChannelReconnectBackoff
{
    private readonly object _gate = new();
    private long _notBefore, _completed;
    private int _attempts;
    private bool _running;

    public bool IsReady(long now)
    {
        lock (_gate) return !_running && now >= _notBefore;
    }

    public bool TryBegin(long now, bool manual = false)
    {
        lock (_gate)
        {
            if (_running || (!manual && now < _notBefore)) return false;
            if (manual || now - _completed > 600_000) _attempts = 0;
            _running = true;
            _attempts = Math.Min(5, _attempts + 1);
            return true;
        }
    }

    public int Complete(long now)
    {
        lock (_gate)
        {
            int delay = Math.Min(300_000, 30_000 * (1 << Math.Max(0, _attempts - 1)));
            _completed = now;
            _notBefore = now + delay;
            _running = false;
            return delay;
        }
    }
}
