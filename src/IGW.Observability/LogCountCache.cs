namespace IGW.Observability;

/// <summary>一个分段在某个查询条件下的匹配条数和趋势图桶(桶键为 UTC Ticks)。缓存里的实例会被多个查询共用，只读。</summary>
internal sealed record SegmentCount(int Matches, IReadOnlyDictionary<long, (int Total, int Warn, int Error)> Buckets);

/// <summary>
/// 已封存分段的计数结果缓存：带关键词、设备等条件的查询每 10 秒自动刷新、翻页时都要重新计数，
/// 已封存分段内容不会再变，算过一次就记下来，之后只需重扫正在写入的分段。
/// 键 = 分段(路径去掉扩展名，.jsonl 压缩成 .seg 后不变) + 条数 + 条件签名 + 有效时间范围 + 桶宽；
/// 条数和时间范围也在键里，万一同名文件被重新写过也不会误中。按最近使用淘汰，条数有上限。
/// </summary>
internal sealed class LogCountCache(int capacity = LogCountCache.DefaultCapacity)
{
    /// <summary>每项通常只有十来个趋势桶，上万项也就几 MB。</summary>
    public const int DefaultCapacity = 16_384;

    internal readonly record struct Key(string Segment, int Count, string Filter, long FromMicros, long ToMicros, long BucketTicks);

    private readonly object _gate = new();
    private readonly Dictionary<Key, LinkedListNode<(Key Key, SegmentCount Value)>> _items = new();
    private readonly LinkedList<(Key Key, SegmentCount Value)> _recent = new();   // 头部最近用过
    private long _hits;
    private long _misses;

    public int Count { get { lock (_gate) return _items.Count; } }
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public static string SegmentId(string path) => Path.ChangeExtension(path, null);

    public bool TryGet(in Key key, out SegmentCount value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                value = node.Value.Value;
                _hits++;
                return true;
            }
            _misses++;
        }
        value = null!;
        return false;
    }

    public void Add(in Key key, SegmentCount value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var existing)) _recent.Remove(existing);
            _items[key] = _recent.AddFirst((key, value));
            while (_items.Count > capacity && _recent.Last is { } oldest)
            {
                _recent.RemoveLast();
                _items.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>分段被删除(保留期清理)时清掉它的所有缓存项。</summary>
    public void Remove(IEnumerable<string> segments)
    {
        var doomed = new HashSet<string>(segments, StringComparer.Ordinal);
        if (doomed.Count == 0) return;
        lock (_gate)
        {
            for (LinkedListNode<(Key Key, SegmentCount Value)>? node = _recent.First; node is not null;)
            {
                LinkedListNode<(Key Key, SegmentCount Value)>? next = node.Next;
                if (doomed.Contains(node.Value.Key.Segment))
                {
                    _recent.Remove(node);
                    _items.Remove(node.Value.Key);
                }
                node = next;
            }
        }
    }
}
