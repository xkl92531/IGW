using System.Collections.Concurrent;
using System.Threading.Channels;
using IGW.Abstractions;

namespace IGW.Core;

public readonly struct DataHubUpdate
{
    public object Handle { get; }
    public DataValue Value { get; }
    public bool Notify { get; }

    public DataHubUpdate(object handle, DataValue value, bool notify = true)
    {
        Handle = handle;
        Value = value;
        Notify = notify;
    }
}

/// <summary>
/// 内存数据中枢(= KEP 那层缓存),面向单机 1M 点。
/// - 点表:名字字典(仅边界解析)+ 分段槽位数组(整型句柄→条目);值为 DataValue 变体整体替换(seqlock:读不撕裂、零分配)。
/// - 变化总线:按句柄哈希分 N 片,每片独立有界队列(DropOldest,丢弃计数)+ 独立 pump——同点恒同片,单点顺序保留。
/// - 每个订阅有独立队列 + 独立 pump,慢消费者不拖累快消费者。
/// </summary>
public sealed class DataHub : IDataHub, IAsyncDisposable
{
    private sealed class Entry
    {
        public required TagDef Def;
        public required string TagId;  // 反向输出用(变化事件零分配携带)
        public required string DeviceName; // 通道.设备,供按需连接 O(1) 聚合
        public int Handle;             // 槽位号:总线路由/订阅注册寻址
        public DataValue Val;          // 内联当前值(seqlock 保护:读不撕裂且零分配)
        public int Seq;                // 版本号:偶=稳定可读,奇=写入中(单写者:每点只被所属设备循环写)
        public long LastAccessTick;    // 最近被客户端引用的时刻(active-tag)
        public volatile Subscription[] PointSubs = Array.Empty<Subscription>();   // 订阅倒转:本点的点注册订阅者(COW,注册罕见)
        public int DemandSubs;         // 其中构成采集需求的订阅数；被动观察者只投递变化、不计入
    }

    private sealed class DeviceDemand
    {
        public long LastTouchTick;
        public int Pins;
        public int PointSubscriptions;
        public long Version;
    }

    // seqlock 写(单写者):置奇 → 写值 → 置偶
    private static void WriteVal(Entry e, in DataValue v)
    {
        int s = e.Seq | 1;                     // 当前偶数 n → n+1(奇)
        Interlocked.Exchange(ref e.Seq, s);    // 全栅栏:Val 的写不会被重排到"置奇"之前(volatile write 只是 release,挡不住后续写前移)
        e.Val = v;
        Volatile.Write(ref e.Seq, s + 1);      // release:Val 的写不会下沉到"置偶"之后
    }

    // seqlock 读:偶版本下复制,复制后版本未变才有效,否则重试
    private static DataValue ReadVal(Entry e)
    {
        var sw = new SpinWait();
        while (true)
        {
            int s1 = Volatile.Read(ref e.Seq);
            if ((s1 & 1) == 0)
            {
                DataValue v = e.Val;
                Interlocked.MemoryBarrier();   // 复制完成后才做版本校验(防 Val 的读被延后越过校验,读到撕裂值还判有效)
                if (Volatile.Read(ref e.Seq) == s1) return v;
            }
            sw.SpinOnce();
        }
    }

    // ---- 点表:名字字典(边界)+ 分段槽位(句柄) ----
    private readonly ConcurrentDictionary<string, Entry> _tags = new();
    private readonly ConcurrentDictionary<string, DeviceDemand> _deviceDemand = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _tagPins = new(StringComparer.OrdinalIgnoreCase);
    private int _wildcardDemand;
    private const int ChunkShift = 16;                       // 每段 65536 槽
    private const int ChunkSize = 1 << ChunkShift;
    private readonly object _slotLock = new();               // 注册/注销是边界操作,锁开销可忽略
    private volatile Entry?[][] _chunks = new Entry?[4][];   // 外层扩容走 COW,读侧无锁
    private int _slotTop;                                    // 已用槽位上界(含空洞)
    private readonly Stack<int> _freeSlots = new();          // 注销回收
    private volatile string[]? _sortedIds;                   // 已排序点位快照(注册/注销时失效)

    // ---- 变化总线:分片 ----
    private readonly record struct Change(Entry E, DataValue V);

    private sealed class Shard
    {
        public required Channel<Change> Bus;
        public Task Pump = Task.CompletedTask;
        public long Dropped;   // 总线满被 DropOldest 丢弃的条数(Interlocked)
    }

    private readonly Shard[] _shards;
    private long _publishedChangeCount;
    private readonly List<Subscription> _subs = new();                             // 全部订阅(退订/统计)
    private readonly object _subLock = new();
    private volatile Subscription[] _wildcard = Array.Empty<Subscription>();       // 全量订阅快照(copy-on-write:分发热路径零锁零分配)
    private readonly CancellationTokenSource _cts = new();

    /// <summary>默认分片数:随处理器数,上限 8(基准后可调)。</summary>
    public static int DefaultShards => Math.Clamp(Environment.ProcessorCount, 1, 8);

    public DataHub(int? shards = null, int? busCapacityPerShard = null) : this(shards, busCapacityPerShard, startPumps: true) { }

    // startPumps=false 仅测试用(InternalsVisibleTo):冻结 pump 以确定性地验证总线满丢弃计数
    internal DataHub(int? shards, int? busCapacityPerShard, bool startPumps)
    {
        int n = Math.Clamp(shards ?? DefaultShards, 1, 64);
        int cap = Math.Max(64, busCapacityPerShard ?? 100_000 / n);
        _shards = new Shard[n];
        for (int i = 0; i < n; i++)
        {
            var sh = new Shard { Bus = null! };
            sh.Bus = Channel.CreateBounded<Change>(
                new BoundedChannelOptions(cap) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                _ => Interlocked.Increment(ref sh.Dropped));   // 丢弃不静默:计数
            _shards[i] = sh;
        }
        if (startPumps)
            foreach (var sh in _shards) sh.Pump = Task.Run(() => PumpShardAsync(sh));
    }

    public void Register(string tagId, TagDef def, string? deviceName = null)
    {
        // 设备名含点号(如 S1500_127.0.0.1)时两段式启发式会截断,导致按需需求
        // 记录在错误键下、设备循环永远查不到 → 按需扫描不激活。调用方有真实
        // 设备全路径时必须显式传入。
        string name = string.IsNullOrWhiteSpace(deviceName) ? DeviceNameOf(tagId) : deviceName;
        var e = new Entry { Def = def, TagId = tagId, DeviceName = name, Val = DataValue.BadAt(StatusCode.BadNotConnected, DateTime.UtcNow) };
        _deviceDemand.TryAdd(name, new DeviceDemand());
        Entry? replaced = null;
        lock (_slotLock)
        {
            int h = _freeSlots.Count > 0 ? _freeSlots.Pop() : _slotTop++;
            e.Handle = h;
            PlaceSlot(h, e);
            if (_tags.TryGetValue(tagId, out var old)) { FreeSlotLocked(old); replaced = old; }
            _tags[tagId] = e;
        }
        _sortedIds = null;
        if (replaced is not null) DetachPointSubscriptions(replaced);   // 同名重注册:旧条目上的点注册作废
    }

    /// <summary>
    /// 保留句柄、订阅和最后值，只替换点位定义(按设备应用复用既有条目时用)。语义同 <see cref="ReplaceDef"/>:
    /// 原子发布新定义；值类型/数组形状变化时置「等待初值」，不再以 Good 质量提供按旧类型编码的旧值。
    /// </summary>
    public void UpdateDefinition(string tagId, TagDef def) => ReplaceDef(tagId, def);

    /// <summary>注销一个点(增量热重载移除设备时用):字典与槽位同时移除;残留旧句柄写入孤儿条目,无害。</summary>
    public void Unregister(string tagId)
    {
        Entry? removed = null;
        lock (_slotLock)
            if (_tags.TryRemove(tagId, out var e)) { FreeSlotLocked(e); removed = e; }
        _sortedIds = null;
        if (removed is not null) DetachPointSubscriptions(removed);
    }

    /// <summary>
    /// 条目注销/被同名重注册时:摘掉所有点注册订阅在该条目上的注册并释放其设备需求。
    /// 否则 LIFO 复用槽位的新点拿到同一句柄,Add 只会给死注册计数++(新点推送冻结),
    /// 且死条目按名再也 Remove 不到 → 设备需求永久泄漏(设备一直被轮询)。之后按名 Remove 已删点为无害 no-op。
    /// 在 _slotLock 之外调用(Add 路径持 _subLock 再读槽位,避免锁序反转)。
    /// </summary>
    private void DetachPointSubscriptions(Entry e)
    {
        lock (_subLock)
        {
            var subs = e.PointSubs;
            if (subs.Length == 0) return;
            foreach (var s in subs) s.DropEntry(e);
            e.PointSubs = Array.Empty<Subscription>();
        }
    }

    // 须持 _slotLock:清槽 + 回收
    private void FreeSlotLocked(Entry e)
    {
        int h = e.Handle;
        var chunks = _chunks;
        int ci = h >> ChunkShift;
        if (ci < chunks.Length && chunks[ci] is { } chunk && ReferenceEquals(chunk[h & (ChunkSize - 1)], e))
        {
            chunk[h & (ChunkSize - 1)] = null;
            _freeSlots.Push(h);
        }
    }

    // 须持 _slotLock:放入槽位,必要时扩容(外层 COW,读侧无锁)
    private void PlaceSlot(int handle, Entry e)
    {
        int ci = handle >> ChunkShift;
        var chunks = _chunks;
        if (ci >= chunks.Length)
        {
            var bigger = new Entry?[Math.Max(chunks.Length * 2, ci + 1)][];
            Array.Copy(chunks, bigger, chunks.Length);
            _chunks = chunks = bigger;
        }
        chunks[ci] ??= new Entry?[ChunkSize];
        chunks[ci]![handle & (ChunkSize - 1)] = e;
    }

    /// <summary>句柄→条目(读侧无锁);槽位空/越界返回 null。订阅注册等低频路径用。</summary>
    private Entry? EntryAt(int handle)
    {
        if (handle < 0) return null;
        var chunks = _chunks;
        int ci = handle >> ChunkShift;
        return ci < chunks.Length ? chunks[ci]?[handle & (ChunkSize - 1)] : null;
    }

    /// <summary>点位总数(替代 ListTagIds().Count 的全量拷贝)。</summary>
    public int Count => _tags.Count;

    /// <summary>Authoritative owner, including device/channel names containing dots.</summary>
    public bool TryGetDeviceName(string tagId, out string deviceName)
    {
        if (_tags.TryGetValue(tagId, out var entry)) { deviceName = entry.DeviceName; return true; }
        deviceName = "";
        return false;
    }

    /// <summary>已排序的点位清单(缓存,注册/注销时失效重建;/tags 等端点用)。</summary>
    public IReadOnlyList<string> SortedTagIds
    {
        get
        {
            var s = _sortedIds;
            if (s == null)
            {
                s = _tags.Keys.ToArray();
                Array.Sort(s, StringComparer.OrdinalIgnoreCase);
                _sortedIds = s;
            }
            return s;
        }
    }

    // ===== 句柄 API(采集热路径):绑定期解析一次句柄,之后读旧值/更新零全局字典查找 =====
    /// <summary>取点位内部句柄;点不存在返回 null。句柄身份 = 条目对象(GC 兜底,槽位复用无 ABA)。</summary>
    public object? GetHandle(string tagId) => _tags.TryGetValue(tagId, out var e) ? e : null;

    /// <summary>取点位整型句柄(订阅注册用);点不存在返回 false。</summary>
    public bool TryGetHandleId(string tagId, out int handle)
    {
        if (_tags.TryGetValue(tagId, out var e)) { handle = e.Handle; return true; }
        handle = -1; return false;
    }

    /// <summary>经句柄读当前值(热路径,无字典查找)。</summary>
    public static bool TryReadHandle(object handle, out DataValue value)
    {
        if (handle is Entry e) { value = ReadVal(e); return true; }
        value = default; return false;
    }

    /// <summary>经句柄更新值并按真实订阅需求进入变化总线。</summary>
    public void UpdateByHandle(object handle, in DataValue value)
    {
        if (handle is not Entry e) return;
        UpdateEntry(e, in value, notify: true);
    }

    /// <summary>驱动批量写入最新值:无全局字典查找,无每点事件对象分配。</summary>
    public void UpdateBatch(ReadOnlySpan<DataHubUpdate> updates)
    {
        foreach (ref readonly var update in updates)
        {
            if (update.Handle is not Entry entry) continue;
            DataValue value = update.Value;
            UpdateEntry(entry, in value, update.Notify);
        }
    }

    internal long PublishedChangeCount => Interlocked.Read(ref _publishedChangeCount);

    /// <summary>该点是否存在真实订阅者，供采集热路径跳过无意义的旧值比较。</summary>
    public bool HasSubscribers(object handle) => handle is Entry e &&
        (Volatile.Read(ref _wildcardDemand) > 0 || e.PointSubs.Length > 0);

    private void UpdateEntry(Entry e, in DataValue value, bool notify)
    {
        bool interested = notify &&
            (Volatile.Read(ref _wildcardDemand) > 0 || e.PointSubs.Length > 0);
        if (!interested)
        {
            WriteVal(e, value);
            return;
        }

        DataValue previous = ReadVal(e);
        bool changed = previous.Quality != value.Quality || !previous.ValueEquals(in value);
        WriteVal(e, value);
        if (!changed) return;
        var sh = _shards[(int)((uint)e.Handle % (uint)_shards.Length)];
        if (sh.Bus.Writer.TryWrite(new Change(e, value)))
            Interlocked.Increment(ref _publishedChangeCount);
    }

    /// <summary>该点是否被钉住(存在 OPC UA 监控项)。</summary>
    public bool IsPinned(string tagId) => _tagPins.TryGetValue(tagId, out int pins) && pins > 0;

    public bool TryGet(string tagId, out DataValue value)
    {
        if (_tags.TryGetValue(tagId, out var e)) { value = ReadVal(e); return true; }
        value = default;
        return false;
    }

    public bool TryGetDef(string tagId, out TagDef def)
    {
        if (_tags.TryGetValue(tagId, out var e)) { def = e.Def; return true; }
        def = null!;
        return false;
    }

    /// <summary>
    /// 就地替换点位定义(按设备应用时的「更新点」):槽位/句柄、订阅与最后值均保留,
    /// 只让后续读写/展示看到新定义。点不存在返回 false(调用方应改走 Register)。
    /// </summary>
    internal bool ReplaceDef(string tagId, TagDef def)
    {
        if (!_tags.TryGetValue(tagId, out var e)) return false;
        bool shapeChanged = ValueShapeChanged(e.Def, def);
        Volatile.Write(ref e.Def, def);   // 引用赋值原子+发布屏障;读者要么看到旧定义要么看到新定义
        // 值类型/数组形状变了:旧值按旧类型编码,不能再以 Good 质量供 OPC UA/REST/实时读 → 置等待初值,直到新定义下首次采集
        if (shapeChanged)
            UpdateEntry(e, DataValue.BadAt(StatusCode.BadWaitingForInitialData, DateTime.UtcNow), notify: true);
        return true;
    }

    // 定义变化是否改变了值的运行时类型/形状(数据类型、缩放启用、数组与否、元素类型、维度/下界)
    private static bool ValueShapeChanged(TagDef old, TagDef now)
    {
        if (old.DataType != now.DataType || old.IsArray != now.IsArray) return true;
        if ((old.Scaling is { Enabled: true }) != (now.Scaling is { Enabled: true })) return true;
        if (old.Array is null || now.Array is null) return old.Array is null != now.Array is null;
        return old.Array.ElementType != now.Array.ElementType
            || !old.Array.Dimensions.AsSpan().SequenceEqual(now.Array.Dimensions)
            || !old.Array.LowerBounds.AsSpan().SequenceEqual(now.Array.LowerBounds);
    }

    public void Update(string tagId, in DataValue value)
    {
        if (!_tags.TryGetValue(tagId, out var e)) return;
        UpdateByHandle(e, in value);
    }

    public IReadOnlyCollection<string> ListTagIds() => _tags.Keys.ToArray();

    public void Touch(string tagId)
    {
        if (!_tags.TryGetValue(tagId, out var e)) return;
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref e.LastAccessTick);
        Volatile.Write(ref e.LastAccessTick, now);
        var demand = DemandFor(e);
        Volatile.Write(ref demand.LastTouchTick, now);
        // 首次访问或沉默较久后重新访问才改变版本，避免页面轮询触发百万点计划反复编译。
        if (previous == 0 || now - previous >= 30_000)
            Interlocked.Increment(ref demand.Version);
    }

    public void Pin(string tagId)
    {
        if (!_tags.TryGetValue(tagId, out var e)) return;
        _tagPins.AddOrUpdate(tagId, 1, static (_, count) => count + 1);
        var demand = DemandFor(e);
        Interlocked.Increment(ref demand.Pins);
        Interlocked.Increment(ref demand.Version);
    }

    public void Unpin(string tagId)
    {
        while (true)
        {
            if (!_tagPins.TryGetValue(tagId, out int before)) return;
            if (before <= 0) return;
            if (!_tagPins.TryUpdate(tagId, before - 1, before)) continue;
            string deviceName = DeviceNameOf(tagId);
            if (_deviceDemand.TryGetValue(deviceName, out var demand))
            {
                Interlocked.Decrement(ref demand.Pins);
                Interlocked.Increment(ref demand.Version);
            }
            return;
        }
    }

    public bool IsActive(string tagId, int windowMs)
    {
        if (!_tags.TryGetValue(tagId, out var e)) return false;
        if (Volatile.Read(ref _wildcardDemand) > 0 || Volatile.Read(ref e.DemandSubs) > 0) return true;
        if (_tagPins.TryGetValue(tagId, out int pins) && pins > 0) return true;
        long last = e.LastAccessTick;
        return last != 0 && (Environment.TickCount64 - last) <= windowMs;     // 近期被读过
    }

    /// <summary>设备是否存在任一数据消费者。O(1),供设备循环在连接前查询。</summary>
    public bool HasDeviceDemand(string deviceName, int windowMs)
    {
        if (Volatile.Read(ref _wildcardDemand) > 0) return true;
        if (!_deviceDemand.TryGetValue(deviceName, out var d)) return false;
        if (Volatile.Read(ref d.Pins) > 0 || Volatile.Read(ref d.PointSubscriptions) > 0) return true;
        long last = Volatile.Read(ref d.LastTouchTick);
        return last != 0 && Environment.TickCount64 - last <= Math.Max(1, windowMs);
    }

    /// <summary>设备活动需求版本；固定点、订阅和首次访问发生变化时递增。</summary>
    public long DeviceDemandVersion(string deviceName) =>
        _deviceDemand.TryGetValue(deviceName, out var demand)
            ? Volatile.Read(ref demand.Version)
            : 0;

    private DeviceDemand DemandFor(Entry e) => _deviceDemand.GetOrAdd(e.DeviceName, _ => new DeviceDemand());

    private static string DeviceNameOf(string tagId)
    {
        int first = tagId.IndexOf('.');
        if (first < 0) return tagId;
        int second = tagId.IndexOf('.', first + 1);
        return second < 0 ? tagId : tagId[..second];
    }

    /// <summary>全量订阅:收到所有点的全部变化(历史/MQTT 等真全量消费方)。</summary>
    /// <param name="createsDemand">
    /// true 表示这个消费方本身构成采集需求，active 模式下会让全部点位保持轮询。
    /// 被动记录方(如历史库)应传 false：它只记录已经被采集到的变化，不应把几十万点
    /// 全部拉成活动点，否则分层采集和按需扫描都会失效。
    /// </param>
    public IDisposable SubscribeAll(Func<TagChange, ValueTask> onChange, string name = "",
        bool createsDemand = true)
    {
        var sub = new Subscription(this, onChange, wildcard: true, name, options: null,
            createsDemand: createsDemand);
        lock (_subLock) { _subs.Add(sub); _wildcard = _subs.Where(s => s.IsWildcard).ToArray(); }
        if (!createsDemand) return sub;
        Interlocked.Increment(ref _wildcardDemand);
        BumpAllDeviceDemandVersions();
        return sub;
    }

    private void BumpAllDeviceDemandVersions()
    {
        foreach (var demand in _deviceDemand.Values)
            Interlocked.Increment(ref demand.Version);
    }

    /// <summary>点注册订阅:仅收到已 Add 的点(告警/计算/OPC UA 推送等子集消费方);分发成本 ∝ 该点的订阅者数。</summary>
    public IPointSubscription SubscribePoints(
        Func<TagChange, ValueTask> onChange,
        string name = "",
        PointSubscriptionOptions? options = null)
    {
        var sub = new Subscription(this, onChange, wildcard: false, name, options,
            createsDemand: options?.CreatesDemand ?? true);
        lock (_subLock) _subs.Add(sub);
        return sub;
    }

    /// <summary>条目快照(句柄/TagId/定义)——消费方启动时按定义挑点注册用(边界操作)。</summary>
    public IEnumerable<(int Handle, string TagId, TagDef Def)> Snapshot()
    {
        foreach (var kv in _tags) yield return (kv.Value.Handle, kv.Key, kv.Value.Def);
    }

    /// <summary>单分片背压统计。</summary>
    public sealed record ShardStat(int Id, int Depth, long Dropped);
    /// <summary>单订阅背压统计(kind: all=全量 / points=点注册)。</summary>
    public sealed record SubStat(
        string Name,
        string Kind,
        int Points,
        int Depth,
        int Capacity,
        long Dropped);
    /// <summary>数据面背压快照:丢弃永不静默,/health 与监控中心据此展示。</summary>
    public sealed record DataplaneStats(int Shards, long BusDropped, long SubDropped,
        IReadOnlyList<ShardStat> ShardList, IReadOnlyList<SubStat> Subs);

    /// <summary>背压快照(近似读,无热路径开销):每片 深度/丢弃,每订阅 名称/类别/注册点数/滞后/丢弃。</summary>
    public DataplaneStats Stats()
    {
        var shardList = new ShardStat[_shards.Length];
        long busDropped = 0;
        for (int i = 0; i < _shards.Length; i++)
        {
            long d = Interlocked.Read(ref _shards[i].Dropped);
            busDropped += d;
            shardList[i] = new ShardStat(i, _shards[i].Bus.Reader.Count, d);
        }
        Subscription[] subs;
        lock (_subLock) subs = _subs.ToArray();
        var subStats = new SubStat[subs.Length];
        long subDropped = 0;
        for (int i = 0; i < subs.Length; i++)
        {
            var s = subs[i];
            long d = Interlocked.Read(ref s.Dropped);
            subDropped += d;
            subStats[i] = new SubStat(
                s.Name,
                s.IsWildcard ? "all" : "points",
                s.IsWildcard ? 0 : s.RegisteredCount,
                s.QueueDepth,
                s.QueueCapacity,
                d);
        }
        return new DataplaneStats(_shards.Length, busDropped, subDropped, shardList, subStats);
    }

    // 分片 pump:构造一次 TagChange(句柄+TagId 引用+值,零分配),投递给 本点注册订阅 ∪ 全量订阅——无过滤检查、无字符串哈希
    private async Task PumpShardAsync(Shard sh)
    {
        try
        {
            await foreach (var c in sh.Bus.Reader.ReadAllAsync(_cts.Token))
            {
                var tc = new TagChange(c.E.Handle, c.E.TagId, c.V);
                var pointSubs = c.E.PointSubs;   // volatile 快照
                for (int i = 0; i < pointSubs.Length; i++) pointSubs[i].Offer(tc);
                var wild = _wildcard;
                for (int i = 0; i < wild.Length; i++) wild[i].Offer(tc);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var sh in _shards) sh.Bus.Writer.TryComplete();
        foreach (var sh in _shards) { try { await sh.Pump; } catch { } }
        Subscription[] snapshot;
        lock (_subLock) snapshot = _subs.ToArray();
        foreach (var s in snapshot) s.Dispose();
        _cts.Dispose();
    }

    private sealed class Subscription : IPointSubscription
    {
        private readonly DataHub _hub;
        private readonly Func<TagChange, ValueTask> _cb;
        public bool IsWildcard { get; }
        public string Name { get; }
        public long Dropped;                                            // 本订阅队列满丢弃计数(itemDropped 回调)
        private readonly Dictionary<int, (Entry E, int Count)> _regs = new();   // 计数注册:句柄→(注册时的条目, 次数);同点多监控项场景

        private readonly Channel<TagChange> _q;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Action<TagChange>? _itemDropped;
        private int _disposed;

        /// <summary>通配订阅是否登记过采集需求；未登记的释放时也不能去减计数。</summary>
        public bool CreatesDemand { get; }

        public Subscription(
            DataHub hub,
            Func<TagChange, ValueTask> cb,
            bool wildcard,
            string name,
            PointSubscriptionOptions? options,
            bool createsDemand = true)
        {
            _hub = hub; _cb = cb; IsWildcard = wildcard; Name = name; CreatesDemand = createsDemand;
            _itemDropped = options?.ItemDropped;
            QueueCapacity = Math.Clamp(options?.QueueCapacity ?? 10_000, 1_024, 1_048_576);
            _q = Channel.CreateBounded<TagChange>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,   // 分片后多 pump 并发投递同一订阅
                },
                dropped =>
                {
                    Interlocked.Increment(ref Dropped);
                    _itemDropped?.Invoke(dropped);
                });   // 丢弃不静默，精确订阅可据此要求客户端重取快照
            _pump = Task.Run(PumpAsync);
        }

        public int RegisteredCount { get { lock (_hub._subLock) return _regs.Count; } }
        public int QueueDepth => _q.Reader.Count;
        public int QueueCapacity { get; }

        public bool Add(string tagId) => _hub.TryGetHandleId(tagId, out int h) && Add(h);

        public bool Add(int handle)
        {
            lock (_hub._subLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                if (_regs.TryGetValue(handle, out var r)) { _regs[handle] = (r.E, r.Count + 1); return true; }   // 计数++,已在投递面
                var e = _hub.EntryAt(handle);
                if (e is null) return false;                       // 点不存在:不注册(与旧"回调内过滤永不命中"语义一致)
                _regs[handle] = (e, 1);
                var arr = e.PointSubs;
                var bigger = new Subscription[arr.Length + 1];
                Array.Copy(arr, bigger, arr.Length);
                bigger[arr.Length] = this;
                e.PointSubs = bigger;                              // COW:分发侧无锁读
                AddDemand(e);
                return true;
            }
        }

        public void Remove(string tagId) { if (_hub.TryGetHandleId(tagId, out int h)) Remove(h); }

        // 须持 _hub._subLock:条目已注销,丢弃本订阅指向该条目的注册(仅当注册仍指向它)并释放需求
        internal void DropEntry(Entry e)
        {
            if (!_regs.TryGetValue(e.Handle, out var r) || !ReferenceEquals(r.E, e)) return;
            _regs.Remove(e.Handle);
            ReleaseDemand(e);
        }

        public void Remove(int handle)
        {
            lock (_hub._subLock)
            {
                if (!_regs.TryGetValue(handle, out var r)) return;
                if (r.Count > 1) { _regs[handle] = (r.E, r.Count - 1); return; }
                _regs.Remove(handle);
                RemoveFrom(r.E);   // 从"注册时的条目"上摘除:槽位被复用也不会误伤新条目
                ReleaseDemand(r.E);
            }
        }

        private void AddDemand(Entry e)
        {
            if (!CreatesDemand) return;
            Interlocked.Increment(ref e.DemandSubs);
            var demand = _hub.DemandFor(e);
            Interlocked.Increment(ref demand.PointSubscriptions);
            Interlocked.Increment(ref demand.Version);
        }

        private void ReleaseDemand(Entry e)
        {
            if (!CreatesDemand) return;
            Interlocked.Decrement(ref e.DemandSubs);
            var demand = _hub.DemandFor(e);
            Interlocked.Decrement(ref demand.PointSubscriptions);
            Interlocked.Increment(ref demand.Version);
        }

        private void RemoveFrom(Entry e)
        {
            var arr = e.PointSubs;
            int idx = Array.IndexOf(arr, this);
            if (idx < 0) return;
            var smaller = new Subscription[arr.Length - 1];
            Array.Copy(arr, smaller, idx);
            Array.Copy(arr, idx + 1, smaller, idx, arr.Length - idx - 1);
            e.PointSubs = smaller;
        }

        public void Offer(in TagChange c) => _q.Writer.TryWrite(c);

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var c in _q.Reader.ReadAllAsync(_cts.Token))
                {
                    try { await _cb(c); } catch { /* 单订阅故障隔离 */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            _q.Writer.TryComplete();
            lock (_hub._subLock)
            {
                foreach (var r in _regs.Values)
                {
                    RemoveFrom(r.E);   // 从所有已注册条目摘除
                    ReleaseDemand(r.E);
                }
                _regs.Clear();
                _hub._subs.Remove(this);
                if (IsWildcard) _hub._wildcard = _hub._subs.Where(s => s.IsWildcard).ToArray();
            }
            if (IsWildcard && CreatesDemand)
            {
                Interlocked.Decrement(ref _hub._wildcardDemand);
                _hub.BumpAllDeviceDemandVersions();
            }
        }
    }
}
