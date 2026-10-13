using IGW.Abstractions;

namespace IGW.Core;

internal sealed record OpcUaConnectionResource(IDeviceDriver Driver, NetworkBindingLease? Binding);

/// <summary>Runtime-owned OPC UA sessions, including their source-bound relays. Leases never own peer sessions.</summary>
internal sealed class OpcUaConnectionPool(int retryBaseMs = 1_000) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Retirement> _retired = new(StringComparer.Ordinal);
    private bool _disposed;

    public static string ConnectionKey(ChannelDef channel, DeviceDef device, string? runtimeFullName = null)
    {
        string url = channel.Settings.GetValueOrDefault("url", "").Trim();
        if (url.Length == 0) url = device.Endpoint.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "opc.tcp://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp")
            throw new InvalidOperationException("OPC UA 上游地址无效");
        if (uri.Port < 0) uri = new UriBuilder(uri) { Port = 4840 }.Uri;
        if (uri.Port == 0) throw new InvalidOperationException("OPC UA 上游端口无效");
        // Hash only connection-affecting configuration; never log credentials or a raw serialized key.
        string[] keys = ["securityPolicy", "anonymous", "username", "password", "certificateTrust", "timeoutMs",
            "networkAdapter", "networkAdapterId", "networkAdapterIpv4"];
        return JsonHash.Of(new
        {
            endpoint = uri.ToString().TrimEnd('/'),
            settings = keys.Select(key => new { key, value = channel.Settings.GetValueOrDefault(key, "") }).ToArray(),
            relayTimeout = device.Settings.GetValueOrDefault("timeoutMs", "")
        });
    }

    public SharedOpcUaDriverLease Acquire(string key, Func<OpcUaConnectionResource> factory,
        int maxConcurrentRequests = Entry.DefaultMaxConcurrentRequests)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_active.TryGetValue(key, out var entry))
            {
                _retired.TryGetValue(key, out var previous);
                var resource = factory();
                entry = new Entry(resource, previous?.Recovery ?? new Recovery(retryBaseMs), previous?.Closed ?? Task.CompletedTask);
                _active.Add(key, entry);
                _retired.Remove(key);
            }
            entry.ApplyConcurrencyCeiling(maxConcurrentRequests);
            entry.Users++;
            entry.ApplyAdaptiveConcurrency();
            return new SharedOpcUaDriverLease(this, key, entry);
        }
    }

    public void RetryNow(string key)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(key, out var active)) active.Recovery.Healthy();
            if (_retired.TryGetValue(key, out var retired)) retired.Recovery.Healthy();
        }
    }

    internal async ValueTask ReleaseAsync(string key, Entry entry)
    {
        TaskCompletionSource? closed = null;
        lock (_sync)
        {
            if (--entry.Users != 0)
            {
                // 设备退租后收紧在途上限，避免剩余设备继续按旧规模并发压 PLC。
                entry.ApplyAdaptiveConcurrency();
                return;
            }
            if (_active.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _active.Remove(key);
            closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_disposed) _retired[key] = new(entry.Recovery, closed.Task, Environment.TickCount64);
            // Keep failed-endpoint recovery budgets across hard rebuilds, but bound idle history.
            foreach (var old in _retired.Where(pair => pair.Value.Closed.IsCompleted).OrderBy(pair => pair.Value.At)
                .Take(Math.Max(0, _retired.Count - 256)).ToArray()) _retired.Remove(old.Key);
        }
        try { await entry.CloseAsync().ConfigureAwait(false); }
        finally { closed.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        Task[] closing;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _active.Values.ToArray();
            closing = _retired.Values.Select(item => item.Closed).ToArray();
            _active.Clear();
            _retired.Clear();
        }
        await Task.WhenAll(entries.Select(item => item.CloseAsync()).Concat(closing)).ConfigureAwait(false);
    }

    private sealed record Retirement(Recovery Recovery, Task Closed, long At);

    internal sealed class Recovery(int baseMs)
    {
        private readonly object _sync = new();
        private int _failures;
        private long _next;
        public int Remaining { get { lock (_sync) return (int)Math.Clamp(_next - Environment.TickCount64, 0, 30_000); } }
        public void Failed()
        {
            lock (_sync)
            {
                _failures = Math.Min(6, _failures + 1);
                _next = Environment.TickCount64 + Math.Min(30_000L, (long)baseMs * (1 << (_failures - 1)));
            }
        }
        public void Healthy() { lock (_sync) { _failures = 0; _next = 0; } }
    }

    internal sealed class Entry(OpcUaConnectionResource resource, Recovery recovery, Task predecessor)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private Task<long>? _connecting;
        private Task? _closing;
        private int _faulted;
        private long _generation;
        /// <summary>单条物理会话默认允许的最大在途请求数；可由通道设置 maxConcurrentRequests 覆盖。</summary>
        internal const int DefaultMaxConcurrentRequests = 6;
        /// <summary>只有一个逻辑设备时的在途请求数：一条采集在途，另一条留给写入/回读。</summary>
        private const int BaseConcurrency = 2;

        private int? _concurrencyCeiling;
        public int Users;
        public readonly Recovery Recovery = recovery;
        // A queued collection read gets a turn after one write/verification operation.
        // Eight priority operations across busy stations can otherwise add seconds of delay.
        // 保留一个在途额度给写入/回读：读取(含上千点的按需拉取)再多，握手回写也不必排在读取块后面等执行槽。
        public readonly PriorityOperationScheduler Scheduler = new(BaseConcurrency, maxPriorityBurst: 1, reservePriorityLane: true);

        /// <summary>
        /// 收拢在途上限。maxConcurrentRequests 是给能力较弱的 PLC 留的安全阀，因此多个通道共用
        /// 同一物理会话时取最保守的一个：任何一方要求收紧都生效，不会被另一方的宽松配置抵消。
        /// </summary>
        public void ApplyConcurrencyCeiling(int requested)
        {
            int bounded = Math.Clamp(requested, BaseConcurrency, 64);
            _concurrencyCeiling = _concurrencyCeiling is { } current ? Math.Min(current, bounded) : bounded;
        }

        /// <summary>
        /// 按共享这条会话的逻辑设备数调整在途上限。固定为 2 时，同一 PLC 下第二、三个设备的采集
        /// 只能排队等前面那条大读取返回(现场 PLC7 通道三个工位共用一条会话即如此)。每个设备给一条
        /// 在途额度，再留一条给写入/回读；上限由 maxConcurrentRequests 约束，避免压垮 PLC 的 OPC UA 服务。
        /// </summary>
        public void ApplyAdaptiveConcurrency() =>
            Scheduler.Concurrency = Math.Clamp(Math.Max(0, Users) + 1,
                BaseConcurrency, _concurrencyCeiling ?? DefaultMaxConcurrentRequests);
        public readonly SemaphoreSlim CleanupGate = new(1, 1);
        public IDeviceDriver Driver => resource.Driver;
        public NetworkBindingLease? Binding => resource.Binding;
        public CancellationToken StopToken => _stop.Token;
        public long Generation => Interlocked.Read(ref _generation);
        public bool Faulted => Volatile.Read(ref _faulted) != 0;

        public Task<long> ConnectAsync()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closing is not null, this);
                if (_connecting is { IsCompleted: false }) return _connecting;
                if (!Faulted && Driver.State == ConnectionState.Connected) return Task.FromResult(Generation);
                // KeepAlive can detect a loss while every device is idle (there is then no failed Read).
                if (Generation > 0 && Interlocked.Exchange(ref _faulted, 1) == 0) Recovery.Failed();
                return _connecting = ConnectCoreAsync();
            }
        }

        private async Task<long> ConnectCoreAsync()
        {
            var ct = _stop.Token;
            await predecessor.WaitAsync(ct).ConfigureAwait(false);
            int delay = Recovery.Remaining;
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
            using var permit = await Scheduler.EnterAsync(true, ct, exclusive: true).ConfigureAwait(false);
            try
            {
                await Driver.DisconnectAsync().ConfigureAwait(false);
                await Driver.ConnectAsync(ct).ConfigureAwait(false);
                Volatile.Write(ref _faulted, 0);
                return Interlocked.Increment(ref _generation);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                Volatile.Write(ref _faulted, 1);
                Recovery.Failed();
                throw;
            }
        }

        public void Failed(Exception error, long generation)
        {
            var failure = DriverOperationFailure.FromException(error, Driver.State);
            if (!failure.ConnectionLost) return;
            lock (_sync)
            {
                if (generation != Generation) return;
                if (Interlocked.Exchange(ref _faulted, 1) == 0) Recovery.Failed();
            }
        }

        public void Healthy(long generation)
        {
            lock (_sync)
                if (!Faulted && generation == Generation && Driver.State == ConnectionState.Connected) Recovery.Healthy();
        }

        public Task CloseAsync()
        {
            lock (_sync) return _closing ??= CloseCoreAsync();
        }

        private async Task CloseCoreAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            // Even an unused intermediate generation must preserve the complete predecessor chain.
            await predecessor.ConfigureAwait(false);
            Task<long>? connecting;
            lock (_sync) connecting = _connecting;
            if (connecting is not null) { try { await connecting.ConfigureAwait(false); } catch { } }
            using var permit = await Scheduler.EnterAsync(true, CancellationToken.None, exclusive: true).ConfigureAwait(false);
            try { await Driver.DisposeAsync().ConfigureAwait(false); }
            finally { if (Binding is not null) await Binding.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
