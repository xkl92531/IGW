using IGW.Abstractions;

namespace IGW.Core;

internal sealed class ReadFailureLogGate
{
    private string? _signature;
    private long _next;
    private int _suppressed;

    public bool TryEmit(IReadOnlyList<TagSample> samples, string context, long now, out int suppressed)
    {
        // Values and timestamps change each scan; only address/quality changes are a new incident.
        string signature = context + "|" + string.Join('|', samples.Where(s => !s.Value.Quality.IsGood)
            .Select(s => $"{s.TagId}:{s.Value.Quality}"));
        suppressed = 0;
        if (signature == _signature && now < _next)
        {
            _suppressed++;
            return false;
        }
        suppressed = _suppressed;
        _suppressed = 0;
        _signature = signature;
        _next = now + 30_000;
        return true;
    }

    public bool Recover(out int suppressed)
    {
        suppressed = _suppressed;
        bool active = _signature is not null;
        _signature = null;
        _suppressed = 0;
        return active;
    }
}
