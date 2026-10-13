namespace PlcSimulator;

internal sealed record TrafficLogBatch(IReadOnlyList<string> Lines, int Dropped);

/// <summary>
/// 有界的流量日志队列。生产线程永不等待 UI；积压时优先保留最新日志。
/// </summary>
internal sealed class TrafficLogBuffer
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private int _dropped;

    public TrafficLogBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    public void Enqueue(string line)
    {
        lock (_gate)
        {
            if (_lines.Count == _capacity)
            {
                _lines.Dequeue();
                _dropped++;
            }

            _lines.Enqueue(line);
        }
    }

    public TrafficLogBatch Drain(int maxItems)
    {
        if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));

        lock (_gate)
        {
            int count = Math.Min(maxItems, _lines.Count);
            var lines = new List<string>(count);
            for (int i = 0; i < count; i++) lines.Add(_lines.Dequeue());

            int dropped = _dropped;
            _dropped = 0;
            return new TrafficLogBatch(lines, dropped);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _dropped = 0;
        }
    }
}
