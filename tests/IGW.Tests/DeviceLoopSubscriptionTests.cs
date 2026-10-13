using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopSubscriptionTests
{
    [Fact]
    public async Task Realtime_and_mes_tags_are_primed_once_then_use_subscription_snapshots()
    {
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        var mes = Binding("Line.Plc.DB.Mes", 2);
        hub.Register(realtime.TagId, realtime.Def);
        hub.Register(mes.TagId, mes.Def);
        var demands = new ScanDemandRegistry(hub);
        using var mesLease = demands.Acquire("mes", [mes.TagId], 20, ScanDemandPriority.Process);
        var driver = new SubscriptionDriver(emitInitialNotification: false);
        var loop = new DeviceLoop(driver, [realtime, mes], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.Subscribed.Contains(realtime.TagId) &&
            driver.ReadCounts.ContainsKey(realtime.TagId) && driver.ReadCounts.ContainsKey(mes.TagId));

        int realtimeReadsAfterPrime = driver.ReadCounts[realtime.TagId];
        await Task.Delay(120);
        Assert.Equal(realtimeReadsAfterPrime, driver.ReadCounts[realtime.TagId]);
        Assert.True(hub.TryGet(realtime.TagId, out var value));
        Assert.Equal((short)1, value.Value);
        var qcTiming = loop.TagScanTimings(new HashSet<string> { realtime.TagId, mes.TagId });
        var pushed = Assert.Single(qcTiming, item => item.TagId == realtime.TagId);
        Assert.Equal("subscription", pushed.Mode);
        Assert.Null(pushed.Batch);
        var mesSnapshot = Assert.Single(qcTiming, item => item.TagId == mes.TagId);
        Assert.Equal("subscription", mesSnapshot.Mode);
        Assert.Null(mesSnapshot.Batch);
        Assert.Equal(realtimeReadsAfterPrime, driver.ReadCounts[realtime.TagId]);
        Assert.Equal(1, driver.ReadCounts[mes.TagId]);

        stop.Cancel();
        await loop.Completion;
        Assert.True(driver.SubscriptionDisposed);
    }

    [Fact]
    public async Task Mes_subscription_uses_effective_demand_period_and_stops_fallback_after_good_notification()
    {
        await using var hub = new DataHub();
        var mes = Binding("Line.Plc.DB.Mes", 2);
        hub.Register(mes.TagId, mes.Def);
        var demands = new ScanDemandRegistry(hub);
        using var mesLease = demands.Acquire("mes", [mes.TagId], 75, ScanDemandPriority.Process);
        var driver = new SubscriptionDriver(
            emitInitialNotification: false,
            returnBadForRealtimeRead: true);
        var loop = new DeviceLoop(driver, [mes], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ReadCounts.TryGetValue(mes.TagId, out int reads) && reads >= 2);
        Assert.Equal(75, driver.SubscriptionPeriods[mes.TagId]);
        Assert.Equal("polling", Assert.Single(loop.TagScanTimings(new HashSet<string> { mes.TagId })).Mode);

        driver.PublishGood(mes.TagId, 9);
        await WaitUntilAsync(() => Assert.Single(loop.TagScanTimings(new HashSet<string> { mes.TagId })).Mode == "subscription");
        await Task.Delay(100);
        int readsAfterRecovery = driver.ReadCounts[mes.TagId];
        await Task.Delay(180);
        Assert.Equal(readsAfterRecovery, driver.ReadCounts[mes.TagId]);
        Assert.True(hub.TryGet(mes.TagId, out var value));
        Assert.Equal((short)9, value.Value);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Subscription_without_good_prime_stays_active_while_unprimed_tags_fall_back_to_periodic_read()
    {
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        hub.Register(realtime.TagId, realtime.Def);
        var driver = new SubscriptionDriver(
            emitInitialNotification: false,
            returnBadForRealtimeRead: true);
        var loop = new DeviceLoop(driver, [realtime], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ReadCounts.TryGetValue(realtime.TagId, out int reads) && reads >= 2);

        Assert.Equal(1, driver.SubscriptionCreateCount);
        Assert.False(driver.SubscriptionDisposed);
        Assert.Contains(realtime.TagId, driver.Subscribed);
        Assert.True(hub.TryGet(realtime.TagId, out var value));
        Assert.False(value.IsGood);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Subscription_is_released_after_the_last_active_demand_expires()
    {
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        hub.Register(realtime.TagId, realtime.Def);
        var driver = new SubscriptionDriver();
        var loop = new DeviceLoop(driver, [realtime], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", activeWindowMs: 1000, idlePollMs: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.SubscriptionCreateCount == 1);
        await WaitUntilAsync(() => driver.SubscriptionDisposed);
        Assert.Equal(0, loop.ActiveTagCount);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Expired_transient_active_window_does_not_rebuild_subscription_while_mes_demand_remains()
    {
        // 活跃窗口 1 秒：机器繁忙时采集循环晚启动也不会在首次订阅前就过期；它仍早于 2 秒一次的订阅核对过期。
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        var mes = Binding("Line.Plc.DB.Mes", 2);
        hub.Register(realtime.TagId, realtime.Def);
        hub.Register(mes.TagId, mes.Def);
        var demands = new ScanDemandRegistry(hub);
        using var mesLease = demands.Acquire("mes:stable", [mes.TagId], 20, ScanDemandPriority.Process);
        var driver = new SubscriptionDriver();
        var loop = new DeviceLoop(driver, [realtime, mes], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", activeWindowMs: 1000, idlePollMs: 5, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.SubscriptionCreateCount == 1 &&
            driver.Subscribed.Contains(realtime.TagId) && driver.Subscribed.Contains(mes.TagId));

        // Subscription reconciliation currently runs every two seconds. The realtime touch has
        // expired by then, but removing it must not tear down and recreate the MES subscription.
        await Task.Delay(2_300);

        Assert.Equal(1, driver.SubscriptionCreateCount);
        Assert.Equal(0, driver.SubscriptionDisposeCount);

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Delayed_initial_notification_avoids_redundant_subscription_prime_read()
    {
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        hub.Register(realtime.TagId, realtime.Def);
        var driver = new SubscriptionDriver(initialNotificationDelayMs: 40);
        var loop = new DeviceLoop(driver, [realtime], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", idlePollMs: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => hub.TryGet(realtime.TagId, out var value) && value.IsGood);
        await Task.Delay(100);

        Assert.False(driver.ReadCounts.ContainsKey(realtime.TagId));

        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Interrupted_subscription_creation_retries_without_five_second_fallback()
    {
        await using var hub = new DataHub();
        var realtime = Binding("Line.Plc.DB.Realtime", 0);
        hub.Register(realtime.TagId, realtime.Def);
        var driver = new SubscriptionDriver(interruptSubscriptionAttempts: 1);
        var loop = new DeviceLoop(driver, [realtime], hub, NullLogger.Instance,
            "Line.Plc", scanMode: "active", idlePollMs: 5);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        hub.Touch(realtime.TagId);
        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.SubscriptionCreateCount >= 2);

        Assert.Contains(realtime.TagId, driver.Subscribed);

        stop.Cancel();
        await loop.Completion;
    }

    private static TagBinding Binding(string id, int offset) => new()
    {
        TagId = id,
        Def = new TagDef
        {
            Name = id.Split('.').Last(), Address = $"D{offset}", DataType = IgwDataType.Int16,
            ScanRateMs = 20, Access = TagAccess.ReadWrite,
        },
        Address = new TagAddress(1, offset, 2),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // 条件满足即返回；上限放宽是为了全量并行测试时机器繁忙也不误报超时。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class SubscriptionDriver(
        bool emitInitialNotification = true,
        bool returnBadForRealtimeRead = false,
        int initialNotificationDelayMs = 0,
        int interruptSubscriptionAttempts = 0) : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
    {
        public ConcurrentDictionary<string, int> ReadCounts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Subscribed { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> SubscriptionPeriods { get; } = new(StringComparer.Ordinal);
        public bool SubscriptionDisposed { get; private set; }
        public int SubscriptionDisposeCount { get; private set; }
        public int SubscriptionCreateCount { get; private set; }
        private int _remainingSubscriptionInterruptions = interruptSubscriptionAttempts;
        private Action<IReadOnlyList<TagSample>>? _onSamples;
        public string DriverId => "subscription-test";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SupportsSubscriptions = true,
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            foreach (var tag in tags) ReadCounts.AddOrUpdate(tag.TagId, 1, static (_, count) => count + 1);
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId,
                    returnBadForRealtimeRead
                        ? DataValue.BadAt(StatusCode.BadNotConnected, DateTime.UtcNow)
                        : DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray());
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
            { TagId = write.TagId, Status = StatusCode.Good }).ToArray());

        public Task<IDriverSubscription> SubscribeAsync(
            IReadOnlyList<TagBinding> tags,
            Action<IReadOnlyList<TagSample>> onSamples,
            CancellationToken ct)
        {
            SubscriptionCreateCount++;
            if (Interlocked.Decrement(ref _remainingSubscriptionInterruptions) >= 0)
                throw new DriverOperationException(
                    "[80840000] (BadRequestInterrupted)",
                    DriverFailureScope.Block,
                    DriverRetryDisposition.RetrySameConnection,
                    new StatusCode(0x80840000));
            _onSamples = onSamples;
            Subscribed.Clear();
            foreach (var tag in tags)
            {
                Subscribed.Add(tag.TagId);
                SubscriptionPeriods[tag.TagId] = tag.RequestedSamplingIntervalMs ?? tag.Def.ScanRateMs;
            }
            if (emitInitialNotification)
            {
                var initial = tags.Select(tag => new TagSample(
                    tag.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray();
                if (initialNotificationDelayMs <= 0) onSamples(initial);
                else _ = Task.Run(async () =>
                {
                    await Task.Delay(initialNotificationDelayMs);
                    onSamples(initial);
                });
            }
            return Task.FromResult<IDriverSubscription>(new Lease(
                Subscribed.ToArray(), () =>
                {
                    SubscriptionDisposed = true;
                    SubscriptionDisposeCount++;
                }));
        }

        public void PublishGood(string tagId, short value) =>
            _onSamples?.Invoke([new TagSample(tagId, DataValue.GoodAt(value, DateTime.UtcNow))]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(IReadOnlyCollection<string> tagIds, Action dispose) : IDriverSubscription
    {
        public IReadOnlyCollection<string> TagIds { get; } = tagIds;
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new TagAddress(1, 0, 2); error = null; return true; }
    }
}
