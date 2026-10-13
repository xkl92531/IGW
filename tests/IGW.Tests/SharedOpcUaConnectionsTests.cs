using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class SharedOpcUaConnectionsTests
{
    [Fact]
    public async Task Shared_scheduler_gives_waiting_collection_a_turn_after_one_priority_operation()
    {
        // 与共享会话相同的突发限制，但不保留写入槽，单独验证突发规则。
        var scheduler = new PriorityOperationScheduler(2, maxPriorityBurst: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var occupiedA = await scheduler.EnterAsync(false, stop.Token);
        using var occupiedB = await scheduler.EnterAsync(false, stop.Token);
        var normal = scheduler.EnterAsync(false, stop.Token);
        var priority = Enumerable.Range(0, 8).Select(_ => scheduler.EnterAsync(true, stop.Token)).ToArray();
        try
        {
            occupiedA.Dispose();
            using var firstPriority = await priority[0].WaitAsync(stop.Token);
            Assert.False(normal.IsCompleted);
            firstPriority.Dispose();
            using var collected = await normal.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(priority[1].IsCompleted);
        }
        finally
        {
            stop.Cancel();
            occupiedB.Dispose();
            foreach (var pending in priority.Append(normal))
            {
                try { (await pending).Dispose(); } catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task Shared_session_keeps_one_slot_for_write_verification_while_a_read_is_in_flight()
    {
        string[] ids = ["in-flight", "normal", "verification"];
        var started = ids.ToDictionary(id => id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var release = ids.ToDictionary(id => id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var driver = new RecordingDriver
        {
            Reading = async (tags, ct) =>
            {
                string id = Assert.Single(tags).TagId;
                order.Enqueue(id);
                started[id].TrySetResult();
                await release[id].Task.WaitAsync(ct);
                return [new TagSample(id, DataValue.GoodAt((short)42, DateTime.UtcNow))];
            }
        };
        await using var pool = new OpcUaConnectionPool();
        await using var lease = pool.Acquire("same", () => new(driver, null));
        await lease.ConnectAsync(default);
        var verificationReader = Assert.IsAssignableFrom<IWriteVerificationReader>(lease);

        Task<IReadOnlyList<TagSample>> first = lease.ReadAsync([Binding("in-flight")], default);
        await started["in-flight"].Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<IReadOnlyList<TagSample>> normal = lease.ReadAsync([Binding("normal")], default);
        Task<IReadOnlyList<TagSample>> verification = verificationReader.ReadForWriteVerificationAsync(
            [Binding("verification")], default);
        try
        {
            // 读取只占一个执行槽；回读不必等在途读取完成。
            await started["verification"].Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(started["normal"].Task.IsCompleted);
            release["verification"].TrySetResult();
            await verification.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(started["normal"].Task.IsCompleted);

            release["in-flight"].TrySetResult();
            await started["normal"].Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(["in-flight", "verification", "normal"], order.ToArray());
        }
        finally
        {
            foreach (var gate in release.Values) gate.TrySetResult();
            await Task.WhenAll(first, normal, verification).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Small_reads_go_before_queued_bulk_reads_but_bulk_is_not_starved()
    {
        var scheduler = new PriorityOperationScheduler(1);
        var running = await scheduler.EnterAsync(false, default, cost: 500);
        var bulk = scheduler.EnterAsync(false, default, cost: 500);
        var smalls = Enumerable.Range(0, 9).Select(_ => scheduler.EnterAsync(false, default, cost: 10)).ToArray();
        var order = new List<string>();

        running.Dispose();
        for (int i = 0; i < 8; i++)
        {
            (await smalls[i].WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
            order.Add("small");
        }
        // 连续 8 个小请求后，大块读取必须轮到一次。
        (await bulk.WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
        order.Add("bulk");
        (await smalls[8].WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
        order.Add("small");
        Assert.Equal([.. Enumerable.Repeat("small", 8), "bulk", "small"], order);
    }

    [Fact]
    public async Task Reserved_priority_slot_is_not_used_by_reads()
    {
        var scheduler = new PriorityOperationScheduler(2, reservePriorityLane: true);
        using var read = await scheduler.EnterAsync(false, default);
        var second = scheduler.EnterAsync(false, default);
        using var write = await scheduler.EnterAsync(true, default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);
        read.Dispose();
        using var admitted = await second.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Hard_reconnect_rebuilds_every_logical_device_on_the_shared_endpoint()
    {
        var factory = new RecordingFactory();
        var registry = new DriverRegistry(); registry.Register(factory);
        await using var hub = new DataHub();
        var project = new IgwProject { Channels = new[] { "A", "B" }.Select(name => new ChannelDef {
            Name = name, DriverId = "OpcUa.Client", Settings = { ["url"] = "opc.tcp://127.0.0.1:4840" },
            Devices = [new DeviceDef { Name = "OP" }]
        }).ToList() };
        await using var engine = new RuntimeEngine(project, registry, hub, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await engine.StartAsync();
        for (int i = 0; i < 100 && engine.DeviceStatuses().Any(s => s.State != ConnectionState.Connected); i++) await Task.Delay(10);
        Assert.Single(factory.Created);
        var old = factory.Created[0];
        await engine.ReconnectChannelAsync("A", default);
        Assert.True(old.Disposed);
        Assert.Equal(2, factory.Created.Count);
        Assert.False(factory.Created[1].Disposed);
        Assert.All(engine.DeviceStatuses(), device => Assert.Equal(ConnectionState.Connected, device.State));
    }

    [Fact]
    public async Task Connected_state_during_handshake_must_not_bypass_the_shared_completion_generation()
    {
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new RecordingDriver { ConnectedHook = async () => { announced.SetResult(); await finish.Task; } };
        await using var pool = new OpcUaConnectionPool();
        await using var a = pool.Acquire("plc", () => new(driver, null));
        await using var b = pool.Acquire("plc", () => new(driver, null));
        var connectingA = a.ConnectAsync(default);
        await announced.Task;
        var connectingB = b.ConnectAsync(default);
        try { Assert.False(connectingB.IsCompleted); }
        finally { finish.TrySetResult(); await Task.WhenAll(connectingA, connectingB); }
        Assert.Equal(ConnectionState.Connected, b.State);
    }

    [Fact]
    public void Connection_key_uses_the_default_4840_port_before_source_binding_is_prepared()
    {
        var channel = new ChannelDef { DriverId = "OpcUa.Client", Settings = { ["url"] = "opc.tcp://plc", ["networkAdapter"] = "Adapter" } };
        string inferred = OpcUaConnectionPool.ConnectionKey(channel, new DeviceDef());
        channel.Settings["url"] = "opc.tcp://plc:4840";
        Assert.Equal(inferred, OpcUaConnectionPool.ConnectionKey(channel, new DeviceDef()));
    }

    [Fact]
    public async Task Runtime_shares_matching_endpoint_and_reloading_one_does_not_close_peer()
    {
        var factory = new RecordingFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        await using var hub = new DataHub();
        IgwProject Project(params string[] names) => new() { Channels = [new ChannelDef {
            Name = "PLC", DriverId = "OpcUa.Client", Settings = { ["url"] = "opc.tcp://127.0.0.1:4840" },
            Devices = names.Select(name => new DeviceDef { Name = name }).ToList()
        }] };
        await using var engine = new RuntimeEngine(Project("A", "B"), registry, hub, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await engine.StartAsync();
        for (int i = 0; i < 100 && engine.DeviceStatuses().Any(s => s.State != ConnectionState.Connected); i++) await Task.Delay(10);
        Assert.Equal(2, engine.DeviceStatuses().Count);
        Assert.Single(factory.Created);
        var driver = factory.Created[0];
        Assert.Equal(1, driver.Connects);
        await engine.ApplyAsync(Project("B"));
        Assert.False(driver.Disposed);
        Assert.Equal(ConnectionState.Connected, Assert.Single(engine.DeviceStatuses()).State);
        await engine.StopAsync();
        Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task Matching_devices_share_one_connection_and_disposing_a_peer_keeps_reads_and_subscription_alive()
    {
        var driver = new RecordingDriver();
        await using var pool = new OpcUaConnectionPool();
        int created = 0;
        OpcUaConnectionResource Create() { created++; return new(driver, null); }
        var first = pool.Acquire("same", Create);
        var second = pool.Acquire("same", Create);
        await Task.WhenAll(first.ConnectAsync(default), second.ConnectAsync(default));
        var sub = await second.SubscribeAsync([Binding("B")], _ => { }, default);
        Assert.Equal(1, created);
        Assert.Equal(1, driver.Connects);
        await first.DisposeAsync();
        Assert.Equal(ConnectionState.Connected, second.State);
        Assert.False(driver.Disposed);
        Assert.False(((RecordingSubscription)driver.Subscriptions.Single()).Disposed);
        Assert.True(Assert.Single(await second.ReadAsync([Binding("B")], default)).Value.Quality.IsGood);
        await second.DisposeAsync();
        Assert.True(driver.Disposed);
        Assert.True(driver.Subscriptions.Single().Disposed);
        await sub.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_connect_waiter_does_not_cancel_the_shared_connection()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new RecordingDriver { Connecting = ct => ready.Task.WaitAsync(ct) };
        await using var pool = new OpcUaConnectionPool();
        await using var a = pool.Acquire("same", () => new(driver, null));
        await using var b = pool.Acquire("same", () => throw new Exception("duplicate factory"));
        using var cancel = new CancellationTokenSource();
        var cancelled = a.ConnectAsync(cancel.Token);
        var connected = b.ConnectAsync(default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        ready.SetResult();
        await connected.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, driver.Connects);
        Assert.Equal(ConnectionState.Connected, b.State);
    }

    [Fact]
    public async Task Recovered_session_requires_each_lease_to_reconnect_before_reusing_subscriptions()
    {
        var driver = new RecordingDriver();
        await using var pool = new OpcUaConnectionPool(retryBaseMs: 1);
        await using var a = pool.Acquire("same", () => new(driver, null));
        await using var b = pool.Acquire("same", () => new(driver, null));
        await Task.WhenAll(a.ConnectAsync(default), b.ConnectAsync(default));
        var subscription = await b.SubscribeAsync([Binding("B")], _ => { }, default);
        driver.FailRead = true;
        await Assert.ThrowsAsync<IOException>(() => a.ReadAsync([Binding("A")], default));
        driver.FailRead = false;
        await a.ConnectAsync(default);
        Assert.NotEqual(ConnectionState.Connected, b.State);
        await b.ConnectAsync(default);
        Assert.True(driver.Subscriptions.Single().Disposed);
        Assert.Equal(2, driver.Connects);
        Assert.Equal(ConnectionState.Connected, b.State);
    }

    [Fact]
    public async Task Failed_connection_attempt_is_shared_and_recreation_preserves_recovery_delay()
    {
        var driver = new RecordingDriver { Connecting = _ => throw new IOException("closed") };
        await using var pool = new OpcUaConnectionPool(retryBaseMs: 30_000);
        var a = pool.Acquire("same", () => new(driver, null));
        var b = pool.Acquire("same", () => new(driver, null));
        await Assert.ThrowsAsync<IOException>(() => a.ConnectAsync(default));
        using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.ConnectAsync(cancel.Token));
        Assert.Equal(1, driver.Connects);
        await a.DisposeAsync();
        await b.DisposeAsync();
        var next = new RecordingDriver();
        await using var c = pool.Acquire("same", () => new(next, null));
        using var shortWait = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.ConnectAsync(shortWait.Token));
        Assert.Equal(0, next.Connects);
    }

    [Fact]
    public void Connection_key_ignores_logical_names_but_isolates_credentials_network_and_server_path()
    {
        var channel = new ChannelDef { Name = "PLC9", DriverId = "OpcUa.Client", Settings = { ["url"] = "opc.tcp://PLC:4840/path", ["password"] = "secret" } };
        var device = new DeviceDef { Id = "stable-device", Name = "OP010" };
        string key = OpcUaConnectionPool.ConnectionKey(channel, device);
        channel.Name = "Alias"; device.Name = "OP020";
        Assert.Equal(key, OpcUaConnectionPool.ConnectionKey(channel, device));
        Assert.DoesNotContain("secret", key);
        foreach (string setting in new[] { "password", "username", "anonymous", "securityPolicy", "certificateTrust", "networkAdapterId", "networkAdapterIpv4", "timeoutMs" })
        {
            string? prior = channel.Settings.GetValueOrDefault(setting);
            channel.Settings[setting] = "different";
            Assert.NotEqual(key, OpcUaConnectionPool.ConnectionKey(channel, device));
            if (prior is null) channel.Settings.Remove(setting); else channel.Settings[setting] = prior;
        }
        channel.Settings["url"] = "opc.tcp://PLC:4840/other";
        Assert.NotEqual(key, OpcUaConnectionPool.ConnectionKey(channel, device));
    }

    [Fact]
    public void Keys_share_device_ids_and_legacy_full_names_when_connection_settings_match()
    {
        var channel = new ChannelDef { Name = "Line", Settings = { ["url"] = "opc.tcp://plc" } };
        var a = new DeviceDef { Id = "a", Name = "A" };
        var b = new DeviceDef { Id = "b", Name = "A" };
        Assert.Equal(OpcUaConnectionPool.ConnectionKey(channel, a), OpcUaConnectionPool.ConnectionKey(channel, b));
        a.Id = b.Id = "";
        b.Name = "B";
        Assert.Equal(OpcUaConnectionPool.ConnectionKey(channel, a), OpcUaConnectionPool.ConnectionKey(channel, b));

        b.Settings["timeoutMs"] = "2500";
        Assert.NotEqual(OpcUaConnectionPool.ConnectionKey(channel, a), OpcUaConnectionPool.ConnectionKey(channel, b));
    }

    [Fact]
    public async Task Scheduler_prioritizes_writes_bounds_queue_and_removes_cancelled_waiters()
    {
        var scheduler = new PriorityOperationScheduler(1, 2);
        using var running = await scheduler.EnterAsync(false, default);
        using var cancel = new CancellationTokenSource();
        var read = scheduler.EnterAsync(false, cancel.Token);
        var write = scheduler.EnterAsync(true, default);
        await Assert.ThrowsAsync<DriverOperationException>(() => scheduler.EnterAsync(false, default));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        running.Dispose();
        using var admittedWrite = await write.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, scheduler.Queued);
    }

    [Fact]
    public async Task Scheduler_exclusive_lifecycle_waits_for_all_requests_without_deadlocking()
    {
        var scheduler = new PriorityOperationScheduler(2);
        var one = await scheduler.EnterAsync(false, default);
        var two = await scheduler.EnterAsync(false, default);
        var close = scheduler.EnterAsync(true, default, exclusive: true);
        var pending = scheduler.EnterAsync(false, default);
        one.Dispose();
        Assert.False(close.IsCompleted);
        Assert.False(pending.IsCompleted);
        two.Dispose();
        using var exclusive = await close.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);
        exclusive.Dispose();
        using var resumed = await pending.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Shared_session_concurrency_follows_the_number_of_logical_devices()
    {
        await using var pool = new OpcUaConnectionPool();
        var driver = new RecordingDriver();
        OpcUaConnectionPool.Entry EntryOf(SharedOpcUaDriverLease lease) =>
            (OpcUaConnectionPool.Entry)typeof(SharedOpcUaDriverLease)
                .GetField("entry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(lease)!;

        // 单设备保持原有的 2：一条采集在途，一条留给写入/回读。
        var first = pool.Acquire("plc", () => new(driver, null));
        var scheduler = EntryOf(first).Scheduler;
        Assert.Equal(2, scheduler.Concurrency);

        // 现场 PLC7 通道下三个工位共用一条会话：并发度随之放宽，不再让第二、三个设备干等。
        var second = pool.Acquire("plc", () => new(driver, null));
        var third = pool.Acquire("plc", () => new(driver, null));
        Assert.Equal(4, scheduler.Concurrency);

        // 设备退租后收紧，不继续按旧规模压 PLC。
        await third.DisposeAsync();
        Assert.Equal(3, scheduler.Concurrency);
        await second.DisposeAsync();
        Assert.Equal(2, scheduler.Concurrency);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task Configured_ceiling_bounds_shared_session_concurrency()
    {
        await using var pool = new OpcUaConnectionPool();
        var driver = new RecordingDriver();
        var leases = new List<SharedOpcUaDriverLease>();
        for (int i = 0; i < 8; i++) leases.Add(pool.Acquire("plc", () => new(driver, null), maxConcurrentRequests: 3));
        var scheduler = (OpcUaConnectionPool.Entry)typeof(SharedOpcUaDriverLease)
            .GetField("entry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(leases[0])!;
        // 8 个设备本可放到 9，但 PLC 的 OPC UA 服务能力由配置封顶。
        Assert.Equal(3, scheduler.Scheduler.Concurrency);
        foreach (var lease in leases) await lease.DisposeAsync();
    }

    [Fact]
    public async Task Raising_concurrency_admits_requests_that_were_already_waiting()
    {
        var scheduler = new PriorityOperationScheduler(1, maxPriorityBurst: 1);
        using var running = await scheduler.EnterAsync(false, default);
        var waiting = scheduler.EnterAsync(false, default);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        scheduler.Concurrency = 2;
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, scheduler.Concurrency);
    }

    [Fact]
    public async Task Subscription_release_does_not_stall_collection_and_writes_on_the_shared_session()
    {
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new RecordingDriver
        {
            SubscriptionDisposing = async () =>
            {
                disposeStarted.TrySetResult();
                await releaseDispose.Task;
            }
        };
        await using var pool = new OpcUaConnectionPool();
        await using var lease = pool.Acquire("shared", () => new(driver, null));
        await lease.ConnectAsync(default);
        var provider = Assert.IsAssignableFrom<IDriverSubscriptionProvider>(lease);
        var subscription = await provider.SubscribeAsync([Binding("a")], _ => { }, default);

        // 订阅释放进行中（模拟 DeleteSubscriptions 网络往返未返回）。
        var releasing = subscription.DisposeAsync();
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 同一条共享会话上的 QC 写入和采集读取不得被订阅释放挡住。
        var write = lease.WriteAsync([new WriteRequest
        {
            TagId = "a",
            Def = new TagDef { Name = "a", Address = "ns=2;s=a", DataType = IgwDataType.Int16 },
            Address = new(0, 0, 2),
            Value = (short)7,
        }], default);
        var read = lease.ReadAsync([Binding("a")], default);

        await Task.WhenAll(write, read).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StatusCode.Good, Assert.Single(await write).Status);
        Assert.Single(await read);

        releaseDispose.SetResult();
        await releasing.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TagBinding Binding(string name) => new() { TagId = name, Def = new TagDef { Name = name, Address = "ns=2;s=" + name, DataType = IgwDataType.Int16 }, Address = new(0, 0, 2) };
    private sealed class RecordingDriver : IDeviceDriver, IDriverSubscriptionProvider
    {
        public int Connects;
        public bool Disposed;
        public bool FailRead;
        public Func<CancellationToken, Task>? Connecting;
        public Func<Task>? ConnectedHook;
        public Func<IReadOnlyList<TagBinding>, CancellationToken, Task<IReadOnlyList<TagSample>>>? Reading;
        public List<RecordingSubscription> Subscriptions = [];
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public async Task ConnectAsync(CancellationToken ct) { Interlocked.Increment(ref Connects); if (Connecting is not null) await Connecting(ct); State = ConnectionState.Connected; StateChanged?.Invoke(State); if (ConnectedHook is not null) await ConnectedHook(); }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            FailRead ? throw new IOException("closed") : Reading is not null ? Reading(tags, ct) :
            Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(t => new TagSample(t.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray());
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(w => new WriteResult { TagId = w.TagId, Status = StatusCode.Good }).ToArray());
        public Func<Task>? SubscriptionDisposing;
        public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags, Action<IReadOnlyList<TagSample>> callback, CancellationToken ct) { var sub = new RecordingSubscription(tags.Select(t => t.TagId).ToArray()) { Disposing = SubscriptionDisposing }; Subscriptions.Add(sub); return Task.FromResult<IDriverSubscription>(sub); }
        public ValueTask DisposeAsync() { Disposed = true; State = ConnectionState.Disconnected; return ValueTask.CompletedTask; }
    }
    private sealed class RecordingSubscription(IReadOnlyCollection<string> ids) : IDriverSubscription { public bool Disposed; public Func<Task>? Disposing; public IReadOnlyCollection<string> TagIds => ids; public async ValueTask DisposeAsync() { if (Disposing is not null) await Disposing(); Disposed = true; } }
    private sealed class RecordingFactory : IDriverFactory
    {
        public List<RecordingDriver> Created = [];
        public string DriverId => "OpcUa.Client";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) { var driver = new RecordingDriver(); Created.Add(driver); return driver; }
    }
    private sealed class Parser : IAddressParser { public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error) { result = new(0, 0, 2); error = null; return true; } }
}
