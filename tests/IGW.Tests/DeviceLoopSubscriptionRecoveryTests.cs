using System.Collections.Concurrent;
using System.Reflection;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopSubscriptionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_items_do_not_rebuild_unchanged_subscription(bool rejectAll)
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B"), Tag("C") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        hub.Touch(tags[0].TagId); hub.Touch(tags[1].TagId);
        var driver = new RecoveryDriver { Rejected = rejectAll ? [tags[0].TagId, tags[1].TagId] : [tags[1].TagId] };
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P", scanMode: "active");
        await driver.ConnectAsync(default);
        try
        {
            for (int i = 1; i <= 10; i++) await Reconcile(loop, i * 3000);
            // 不重建是这里的验收点：10 次检查只创建一次。全部被拒时那次租约没有任何监控项，
            // 会被立即释放以免占用 PLC 侧订阅资源；部分被拒时健康监控项保持活动不被释放。
            Assert.Equal(1, driver.Creates);
            Assert.Equal(rejectAll ? 1 : 0, driver.Disposes);
            // A real change still reconciles, rather than permanently freezing the first request.
            hub.Touch(tags[2].TagId);
            await Reconcile(loop, 33_000);
            Assert.Equal(2, driver.Creates);
            Assert.Equal(1, driver.Disposes);
            Assert.Contains(tags[2].TagId, driver.Accepted);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Changed_sampling_period_reconciles_even_when_one_item_was_rejected()
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        var demands = new ScanDemandRegistry(hub);
        using var original = demands.Acquire("original", tags.Select(t => t.TagId).ToArray(), 100, ScanDemandPriority.Process);
        var driver = new RecoveryDriver { Rejected = [tags[1].TagId] };
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P", scanMode: "active", scanDemands: demands);
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, 3000);
            using var faster = demands.Acquire("faster", [tags[0].TagId], 50, ScanDemandPriority.Process);
            await Reconcile(loop, 6000);
            Assert.Equal(2, driver.Creates);
            Assert.Equal(50, driver.RequestedPeriods[tags[0].TagId]);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Dropping_points_removes_monitored_items_instead_of_rebuilding_the_group()
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B"), Tag("C") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        var demands = new ScanDemandRegistry(hub);
        var all = demands.Acquire("process", tags.Select(tag => tag.TagId).ToArray(), 100, ScanDemandPriority.Process);
        var driver = new RecoveryDriver();
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P",
            scanMode: "active", scanDemands: demands);
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, 1_000);
            Assert.Equal(1, driver.Creates);
            Assert.Equal(3, driver.Accepted.Count);

            // 需求缩减为两个点：应当只摘除 C 的监控项，而不是拆掉整组重建。
            all.Dispose();
            using var fewer = demands.Acquire("process", [tags[0].TagId, tags[1].TagId], 100, ScanDemandPriority.Process);
            await Reconcile(loop, 4_000);

            Assert.Equal(1, driver.Creates);
            Assert.Equal(0, driver.Disposes);
            Assert.Equal([tags[2].TagId], driver.Removed);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Transient_rest_points_are_retained_without_any_explicit_demand()
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        // 纯 QC/REST 访问：没有任何显式扫描需求，只有 Touch 带来的 30 秒活动窗口。
        hub.Touch(tags[0].TagId);
        hub.Touch(tags[1].TagId);
        var clock = new MutableClock { NowMilliseconds = 1_000 };
        var driver = new RecoveryDriver();
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P",
            scanMode: "active", clock: clock, activeWindowMs: 1_000);
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, clock.NowMilliseconds);
            Assert.Equal(1, driver.Creates);

            // B 的活动窗口过期。保留期内不得因此拆掉整组订阅重建。
            clock.NowMilliseconds += 5_000;
            hub.Touch(tags[0].TagId);
            await Reconcile(loop, clock.NowMilliseconds);
            Assert.Equal(1, driver.Creates);
            Assert.Equal(0, driver.Disposes);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Interrupted_retry_can_be_cancelled_before_next_protocol_call()
    {
        await using var hub = new DataHub();
        var tag = Tag("A"); hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId);
        var driver = new RecoveryDriver { Interruptions = int.MaxValue };
        var loop = new DeviceLoop(driver, [tag], hub, NullLogger.Instance, "L.P", scanMode: "active");
        await driver.ConnectAsync(default);
        using var stop = new CancellationTokenSource();
        try
        {
            var attempt = Reconcile(loop, 3000, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(1, driver.Creates);
            Assert.Empty(driver.ReadTags);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Repeated_interruption_backoff_starts_at_completion_and_demand_touches_do_not_bypass_it()
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        hub.Touch(tags[0].TagId);
        var clock = new MutableClock { NowMilliseconds = 10_000 };
        var driver = new RecoveryDriver { Interruptions = 2, OnSubscribe = () => clock.NowMilliseconds += 3000 };
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P", scanMode: "active", clock: clock);
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, clock.NowMilliseconds);
            Assert.Equal(2, driver.Creates);
            hub.Touch(tags[1].TagId);
            clock.NowMilliseconds += 4999;
            await Reconcile(loop, clock.NowMilliseconds);
            Assert.Equal(2, driver.Creates);
            clock.NowMilliseconds++;
            await Reconcile(loop, clock.NowMilliseconds);
            Assert.Equal(3, driver.Creates);
            Assert.Contains(tags[1].TagId, driver.Accepted);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Rejected_items_keep_polling_while_accepted_items_keep_pushing()
    {
        await using var hub = new DataHub();
        var tags = new[] { Tag("A"), Tag("B") };
        foreach (var tag in tags) { hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId); }
        var driver = new RecoveryDriver { Rejected = [tags[1].TagId] };
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P", scanMode: "active");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(tags[1].TagId, driver.ReadTags);
            Assert.DoesNotContain(tags[0].TagId, driver.ReadTags);
            Assert.True(hub.TryGet(tags[0].TagId, out var accepted));
            Assert.True(accepted.IsGood);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    [Fact]
    public async Task Interrupted_subscription_retries_before_starting_slow_fallback_read()
    {
        await using var hub = new DataHub();
        var tag = Tag("A"); hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId);
        var driver = new RecoveryDriver { Interruptions = 1, ReadDelayMs = 2500 };
        var loop = new DeviceLoop(driver, [tag], hub, NullLogger.Instance, "L.P", scanMode: "active", idlePollMs: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await Task.WhenAny(driver.SecondSubscribe.Task, driver.FirstRead.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "subscribe", "subscribe" }, driver.Events.Take(2));
            Assert.False(driver.FirstRead.Task.IsCompleted);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    [Fact]
    public async Task Repeated_interruptions_fall_back_after_one_priority_retry_without_starving_reads()
    {
        await using var hub = new DataHub();
        var tag = Tag("A"); hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId);
        var driver = new RecoveryDriver { Interruptions = int.MaxValue };
        var loop = new DeviceLoop(driver, [tag], hub, NullLogger.Instance, "L.P", scanMode: "active", idlePollMs: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "subscribe", "subscribe", "read" }, driver.Events.Take(3));
            await Task.Delay(250);
            Assert.Equal(2, driver.Creates);
        }
        finally { stop.Cancel(); await loop.Completion; }
    }

    private static Task Reconcile(DeviceLoop loop, long now, CancellationToken ct = default) => (Task)typeof(DeviceLoop)
        .GetMethod("ReconcileRealtimeSubscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(loop, [now, ct])!;
    private static Task Close(DeviceLoop loop) => (Task)typeof(DeviceLoop)
        .GetMethod("CloseRealtimeSubscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, [])!;
    private static TagBinding Tag(string id) => new()
    {
        TagId = "L.P." + id,
        Def = new() { Address = "ns=2;s=" + id, DataType = IgwDataType.Int16, ScanRateMs = 20 },
        Address = new(1, 0, 2),
    };

    private sealed class RecoveryDriver : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
    {
        public HashSet<string> Rejected = [];
        public IReadOnlyCollection<string> Accepted = [];
        public int Interruptions, ReadDelayMs, Creates, Disposes;
        public readonly List<string> Removed = new();
        public Action? OnSubscribe;
        public Dictionary<string, int> RequestedPeriods = [];
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<string> ReadTags { get; } = new();
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSubscribe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => "recovery-test";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with { SupportsSubscriptions = true };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Events.Enqueue("read");
            foreach (var tag in tags) ReadTags.Enqueue(tag.TagId);
            FirstRead.TrySetResult();
            await Task.Delay(ReadDelayMs, ct);
            return tags.Select(t => new TagSample(t.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray();
        }
        public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags, Action<IReadOnlyList<TagSample>> publish, CancellationToken ct)
        {
            Creates++; Events.Enqueue("subscribe");
            OnSubscribe?.Invoke();
            RequestedPeriods = tags.ToDictionary(t => t.TagId, t => t.RequestedSamplingIntervalMs ?? t.Def.ScanRateMs);
            if (Creates == 2) SecondSubscribe.TrySetResult();
            if (Creates <= Interruptions) throw new DriverOperationException("interrupted", DriverFailureScope.Block,
                DriverRetryDisposition.RetrySameConnection, StatusCode.BadRequestInterrupted);
            Accepted = tags.Where(t => !Rejected.Contains(t.TagId)).Select(t => t.TagId).ToArray();
            publish(Accepted.Select(id => new TagSample(id, DataValue.GoodAt((short)2, DateTime.UtcNow))).ToArray());
            return Task.FromResult<IDriverSubscription>(new Lease(Accepted, () => Disposes++, gone => Removed.AddRange(gone)));
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Lease(IReadOnlyCollection<string> ids, Action dispose, Action<IReadOnlyCollection<string>> removed)
            : IIncrementalDriverSubscription
        {
            private HashSet<string> _ids = ids.ToHashSet(StringComparer.Ordinal);
            public IReadOnlyCollection<string> TagIds => _ids.ToArray();
            public Task<int> RemoveAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct)
            {
                var gone = tagIds.Where(_ids.Contains).ToArray();
                if (gone.Length == 0) return Task.FromResult(0);
                _ids = _ids.Where(id => !gone.Contains(id, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
                removed(gone);
                return Task.FromResult(gone.Length);
            }
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class Parser : IAddressParser
        { public bool TryParse(string a, IgwDataType t, out TagAddress result, out string? error) { result = new(1, 0, 2); error = null; return true; } }
    }

    private sealed class MutableClock : IMonotonicClock
    { public long NowMilliseconds { get; set; } }
}
