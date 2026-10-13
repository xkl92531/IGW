using IGW.Abstractions;

namespace IGW.Core;

internal sealed class SharedOpcUaDriverLease(OpcUaConnectionPool pool, string key, OpcUaConnectionPool.Entry connection)
    : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider, IArrayCapableDriver,
      IIdleConnectionPolicyProvider, IRemoteEndpointDiagnostics, IDeviceConnectionDiagnostics, IConnectionResettableDriver,
      IWriteVerificationReader
{
    private readonly OpcUaConnectionPool.Entry entry = connection;
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(connection.StopToken);
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private readonly HashSet<SubscriptionLease> _subscriptions = [];
    private long _generation = -1;
    private int _disposed;
    public NetworkBindingLease? Binding => entry.Binding;
    internal long? DiagnosticGeneration => entry.Generation > 0 ? entry.Generation : null;
    internal string PhysicalConnectionFingerprint => key;
    public bool PhysicalConnectionHealthy => !entry.Faulted && entry.Driver.State == ConnectionState.Connected;
    public bool SharesConnectionWith(SharedOpcUaDriverLease other) => ReferenceEquals(entry, other.entry);
    public string DriverId => entry.Driver.DriverId;
    public ConnectionState State => Volatile.Read(ref _disposed) != 0 || Interlocked.Read(ref _generation) < 0
        ? ConnectionState.Disconnected : entry.Faulted ? ConnectionState.Faulted
        : Interlocked.Read(ref _generation) != entry.Generation ? ConnectionState.Disconnected : entry.Driver.State;
    public event Action<ConnectionState>? StateChanged;
    public IAddressParser AddressParser => entry.Driver.AddressParser;
    public DriverLimits Limits => entry.Driver.Limits;
    public DriverCapabilitySnapshot Capabilities => ((entry.Driver as IDriverCapabilityProvider)?.Capabilities
        ?? DriverCapabilitySnapshot.FromLimits(Limits)) with { MaxInFlight = 2 };
    public IdleConnectionPolicy IdleConnectionPolicy => IdleConnectionPolicy.KeepConnectedWhenIdle;
    public ArrayDriverCapabilities ArrayCapabilities => (entry.Driver as IArrayCapableDriver)?.ArrayCapabilities
        ?? new(false, false, false, false, false, false, 0, 0);
    public RemoteEndpointSnapshot RemoteEndpoint() => (entry.Driver as IRemoteEndpointDiagnostics)?.RemoteEndpoint()
        ?? new("", null, null);
    public DeviceConnectionDetail ConnectionDetail() => new(State == ConnectionState.Connected ? "Connected" : "SharedOpcUaConnection",
        $"共享 OPC UA 会话；逻辑设备租约={Math.Max(0, Volatile.Read(ref entry.Users))}；待处理请求={entry.Scheduler.Queued}；连接复探等待={entry.Recovery.Remaining}ms{(entry.Driver is IReadChunkHint hint && hint.ReadWindowSummary.Length > 0 ? "；" + hint.ReadWindowSummary : "")}");

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        if (State != ConnectionState.Connected) await CloseSubscriptionsAsync().ConfigureAwait(false);
        long generation = await entry.ConnectAsync().WaitAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _generation, generation);
        StateChanged?.Invoke(State);
    }

    public async Task DisconnectAsync()
    {
        Interlocked.Exchange(ref _generation, -1);
        await CloseSubscriptionsAsync().ConfigureAwait(false);
        StateChanged?.Invoke(ConnectionState.Disconnected);
    }
    public Task ResetConnectionAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return DisconnectAsync(); }

    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
        ReadAdmittedAsync(false, tags, ct);

    /// <summary>写回读和分层采集的按需拉取走优先通道；同样逐块准入，避免一个大拉取长期占住执行槽。</summary>
    public Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
        ReadAdmittedAsync(true, tags, ct);

    private Task<IReadOnlyList<TagSample>> ReadAdmittedAsync(bool priority, IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (entry.Driver is not IReadChunkHint hint || tags.Count <= Math.Max(1, hint.ReadChunkSize))
            return RunAsync(priority, token => entry.Driver.ReadAsync(tags, token), ct, samples => samples.Any(s => s.Value.Quality.IsGood), tags.Count);
        // 驱动决定怎么分：同一个结构体数组的成员放一个单元(一次读回)，其余按读批量切分。
        return ReadInUnitsAsync(priority, hint.PlanReadUnits(tags), ct);
    }

    /// <summary>
    /// 大读取逐块准入：同一 PLC 的写入和其他工位的小读取可以插在两块之间，不必等整次读取结束。
    /// 串行处理请求的 PLC 上，它们最多等几个分块，而不是等上千点的整批读取。
    /// </summary>
    private async Task<IReadOnlyList<TagSample>> ReadInUnitsAsync(
        bool priority, IReadOnlyList<IReadOnlyList<TagBinding>> units, CancellationToken ct)
    {
        var samples = new List<TagSample>(units.Sum(unit => unit.Count));
        foreach (var unit in units)
            samples.AddRange(await RunAsync(priority, token => entry.Driver.ReadAsync(unit, token), ct,
                result => result.Any(s => s.Value.Quality.IsGood), unit.Count).ConfigureAwait(false));
        return samples;
    }
    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
        RunAsync(true, token => entry.Driver.WriteAsync(writes, token), ct, results => results.Any(s => s.Status.IsGood));
    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        RunAsync(true, token => entry.Driver is IArrayCapableDriver arrays ? arrays.WriteArrayAsync(request, token)
            : Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, "驱动不支持数组写入")), ct, result => result.Status.IsGood);

    private async Task<T> RunAsync<T>(bool priority, Func<CancellationToken, Task<T>> operation,
        CancellationToken ct, Func<T, bool>? healthy = null, int cost = 0)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        using var permit = await entry.Scheduler.EnterAsync(priority, linked.Token, cost: cost).ConfigureAwait(false);
        ProtocolOperationTiming.AddQueue(permit.QueueMs);
        linked.Token.ThrowIfCancellationRequested();
        long generation = Interlocked.Read(ref _generation);
        if (State != ConnectionState.Connected)
            throw new DriverOperationException("共享 OPC UA 会话等待恢复", DriverFailureScope.Session,
                DriverRetryDisposition.RebuildSession, StatusCode.BadNotConnected);
        try
        {
            T result = await operation(linked.Token).ConfigureAwait(false);
            if (healthy?.Invoke(result) == true) entry.Healthy(generation);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { entry.Failed(ex, generation); throw; }
    }

    public async Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags,
        Action<IReadOnlyList<TagSample>> onSamples, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _subscriptionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var callbacks = new SubscriptionCallbacks();
        try
        {
            long generation = Interlocked.Read(ref _generation);
            var inner = await RunAsync(true, token => entry.Driver is IDriverSubscriptionProvider provider
                ? provider.SubscribeAsync(tags, samples => callbacks.Invoke(() =>
                {
                    if (State == ConnectionState.Connected && generation == entry.Generation &&
                        generation == Interlocked.Read(ref _generation)) onSamples(samples);
                }), token)
                : throw new NotSupportedException("驱动不支持变化订阅"), linked.Token).ConfigureAwait(false);
            var lease = new SubscriptionLease(this, inner, callbacks);
            _subscriptions.Add(lease);
            return lease;
        }
        catch { callbacks.Close(); throw; }
        finally { _subscriptionGate.Release(); }
    }

    private async Task CloseSubscriptionsAsync()
    {
        await _subscriptionGate.WaitAsync().ConfigureAwait(false);
        SubscriptionLease[] subscriptions;
        try { subscriptions = _subscriptions.ToArray(); _subscriptions.Clear(); }
        finally { _subscriptionGate.Release(); }
        Exception? failure = null;
        foreach (var subscription in subscriptions)
        {
            try { await subscription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null) throw failure;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await pool.ReleaseAsync(key, entry).ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }

    private sealed class SubscriptionCallbacks
    {
        private readonly object _sync = new();
        private bool _closed;
        public void Invoke(Action callback) { lock (_sync) if (!_closed) callback(); }
        public void Close() { lock (_sync) _closed = true; }
    }

    private sealed class SubscriptionLease(SharedOpcUaDriverLease owner, IDriverSubscription inner,
        SubscriptionCallbacks callbacks) : IIncrementalDriverSubscription
    {
        /// <summary>等待清理串行位和调度准入的上限；超时后仍然执行删除，只是不再占用调度名额。</summary>
        private const int CleanupAdmissionMs = 5_000;
        private int _disposed;
        public IReadOnlyCollection<string> TagIds => inner.TagIds;

        /// <summary>增量摘除同样走优先通道，不独占共享会话。驱动不支持时返回 0，由上层回退整组重建。</summary>
        public Task<int> RemoveAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct) =>
            Volatile.Read(ref _disposed) != 0 || inner is not IIncrementalDriverSubscription incremental
                ? Task.FromResult(0)
                : owner.RunAsync(true, token => incremental.RemoveAsync(tagIds, token), ct);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            callbacks.Close();
            await owner._subscriptionGate.WaitAsync().ConfigureAwait(false);
            try { owner._subscriptions.Remove(this); }
            finally { owner._subscriptionGate.Release(); }
            // 删除一个订阅只是会话上的一次服务调用，不需要独占整条共享会话。独占会让调度器
            // 停止派发后续请求，同一 PLC 上其它逻辑设备的采集和 QC 写入要等最慢的在途读取
            // 返回才能继续；按优先请求排队即可，清理之间仍由 CleanupGate 串行。
            if (!await owner.entry.CleanupGate.WaitAsync(CleanupAdmissionMs).ConfigureAwait(false))
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                return;
            }
            try
            {
                PriorityOperationScheduler.Permit? permit = null;
                using var admission = new CancellationTokenSource(CleanupAdmissionMs);
                // 准入超时或队列已满都不能放弃删除：否则服务端订阅泄漏，继续累积发布负载。
                try { permit = await owner.entry.Scheduler.EnterAsync(true, admission.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (DriverOperationException) { }
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { permit?.Dispose(); }
            }
            finally { owner.entry.CleanupGate.Release(); }
        }
    }
}
