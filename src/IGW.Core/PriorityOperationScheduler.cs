using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// Bounded admission with write priority and a finite priority burst so reads cannot starve.
/// 读取再分两条：小请求(判定点轮询、小工位的按需拉取)先于大块读取，大块读取有限次让路后必得一次。
/// 可选为优先请求保留一个执行槽：串行处理请求的 PLC 上，写入最多等 PLC 正在处理的那一个读取块。
/// </summary>
internal sealed class PriorityOperationScheduler(
    int concurrency,
    int maxQueued = 512,
    int maxPriorityBurst = 8,
    bool reservePriorityLane = false)
{
    /// <summary>不超过该点数的读取算小请求。</summary>
    internal const int SmallRequestCost = 64;
    private const int MaxSmallBurst = 8;

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _priority = [], _small = [], _bulk = [];
    private int _active, _activeNormal, _priorityBurst, _smallBurst;
    private int _concurrency = Math.Max(1, concurrency);
    private bool _exclusive;
    public int Queued { get { lock (_sync) return _priority.Count + _small.Count + _bulk.Count; } }

    /// <summary>
    /// 允许的在途请求数。共享会话上的逻辑设备数变化时调整：调大立即放行等待中的请求，
    /// 调小只阻止新的准入，不打断已在执行的请求。
    /// </summary>
    public int Concurrency
    {
        get { lock (_sync) return _concurrency; }
        set
        {
            lock (_sync)
            {
                int updated = Math.Max(1, value);
                if (updated == _concurrency) return;
                _concurrency = updated;
                Drain();
            }
        }
    }

    // 保留一个槽给写入/回读后，读取最多占用的槽数。
    private int NormalCapacity => reservePriorityLane && _concurrency >= 2 ? _concurrency - 1 : _concurrency;

    public Task<Permit> EnterAsync(bool priority, CancellationToken ct, bool exclusive = false, int cost = 0)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            if (!_exclusive && Queued_NoLock() == 0 && _active < _concurrency &&
                (priority || exclusive || _activeNormal < NormalCapacity) && (!exclusive || _active == 0))
            {
                _active++;
                if (!priority) _activeNormal++;
                _exclusive = exclusive;
                return Task.FromResult(new Permit(this, exclusive, !priority, 0));
            }
            // Exclusive lifecycle cleanup must remain possible when the bounded data queue is full.
            if (!exclusive && Queued_NoLock() >= maxQueued)
                throw new DriverOperationException("同一 PLC 的请求等待队列已满", DriverFailureScope.Capacity,
                    DriverRetryDisposition.RetrySameConnection, StatusCode.BadTooManyOperations);
            waiter = new Waiter(exclusive, !priority);
            var lane = priority ? _priority : cost > SmallRequestCost ? _bulk : _small;
            waiter.Node = lane.AddLast(waiter);
            // 读取可能因只剩保留槽而排队；新到的写入要立即用上保留槽，不能等别的请求结束才被调度。
            Drain();
        }
        return AwaitAsync(waiter, ct);
    }

    private int Queued_NoLock() => _priority.Count + _small.Count + _bulk.Count;

    private async Task<Permit> AwaitAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            lock (_sync)
            {
                if (waiter.Node is null) return;
                waiter.Node.List!.Remove(waiter.Node);
                waiter.Node = null;
                waiter.Ready.TrySetCanceled(ct);
                Drain();
            }
        });
        return await waiter.Ready.Task.ConfigureAwait(false);
    }

    private void Exit(bool exclusive, bool normal)
    {
        lock (_sync)
        {
            _active--;
            if (normal) _activeNormal--;
            if (exclusive) _exclusive = false;
            Drain();
        }
    }

    private LinkedList<Waiter>? NextLane()
    {
        bool readSlot = _activeNormal < NormalCapacity;
        LinkedList<Waiter>? read = null;
        if (readSlot)
        {
            if (_small.Count > 0 && (_smallBurst < MaxSmallBurst || _bulk.Count == 0)) read = _small;
            else if (_bulk.Count > 0) read = _bulk;
            else if (_small.Count > 0) read = _small;
        }
        if (_priority.Count > 0 && (_priorityBurst < Math.Max(1, maxPriorityBurst) || read is null)) return _priority;
        return read;
    }

    private void Drain()
    {
        while (!_exclusive && _active < _concurrency)
        {
            var queue = NextLane();
            if (queue?.First is not { } node) return;
            var waiter = node.Value;
            if (waiter.Exclusive && _active > 0) return;
            queue.RemoveFirst();
            waiter.Node = null;
            bool isPriority = ReferenceEquals(queue, _priority);
            _priorityBurst = isPriority ? _priorityBurst + 1 : 0;
            if (ReferenceEquals(queue, _small)) _smallBurst++;
            else if (ReferenceEquals(queue, _bulk)) _smallBurst = 0;
            _active++;
            if (waiter.Normal) _activeNormal++;
            _exclusive = waiter.Exclusive;
            waiter.Ready.SetResult(new Permit(this, waiter.Exclusive, waiter.Normal,
                Math.Max(0, Environment.TickCount64 - waiter.Enqueued)));
        }
    }

    private sealed class Waiter(bool exclusive, bool normal)
    {
        public readonly bool Exclusive = exclusive;
        public readonly bool Normal = normal;
        public readonly long Enqueued = Environment.TickCount64;
        public readonly TaskCompletionSource<Permit> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    internal sealed class Permit(PriorityOperationScheduler owner, bool exclusive, bool normal, long queueMs) : IDisposable
    {
        private int _released;
        public long QueueMs { get; } = queueMs;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Exit(exclusive, normal); }
    }
}
