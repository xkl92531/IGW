using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeEngineReconnectTests
{
    [Fact]
    public async Task Note_save_preserves_live_driver_cache_and_handle_but_address_change_rebuilds()
    {
        var factory = new RecordingFactory();
        var device = Device("DeviceA");
        device.Tags.Add(new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 });
        var project = Project(Channel("LineA", device));
        await using var engine = CreateEngine(factory, project);
        await engine.StartAsync();
        await WaitUntilAsync(() => factory.First("LineA.DeviceA").State == ConnectionState.Connected, TimeSpan.FromSeconds(2));
        const string id = "LineA.DeviceA.Value";
        engine.Hub.Update(id, DataValue.GoodAt((short)42, DateTime.UtcNow));
        Assert.True(engine.Hub.TryGetHandleId(id, out int handle));
        var changed = ProjectStore.Clone(project);
        changed.Channels[0].Devices[0].Tags[0].Description = "updated note";

        await engine.ApplyAsync(changed);

        Assert.Equal(1, factory.CreatedFor("LineA.DeviceA"));
        Assert.False(factory.First("LineA.DeviceA").Disposed);
        Assert.True(engine.Hub.TryGetHandleId(id, out int nextHandle));
        Assert.Equal(handle, nextHandle);
        Assert.True(engine.Hub.TryGet(id, out var value));
        Assert.Equal((short)42, value.Value);
        Assert.True(engine.Hub.TryGetDef(id, out var definition));
        Assert.Equal("updated note", definition.Description);
        var addressChanged = ProjectStore.Clone(changed);
        addressChanged.Channels[0].Devices[0].Tags[0].Address = "DB1.DBW2";
        await engine.ApplyAsync(addressChanged);
        Assert.Equal(2, factory.CreatedFor("LineA.DeviceA"));
        Assert.True(factory.First("LineA.DeviceA").Disposed);
    }

    [Fact]
    public async Task One_offline_device_does_not_automatically_destroy_a_connected_peer()
    {
        var factory = new RecordingFactory(firstInstancePermanentlyFails: true);
        await using var engine = CreateEngine(factory, Project(Channel("LineA", Device("Offline"), Device("Online"))));
        await engine.StartAsync();
        var offline = factory.First("LineA.Offline");
        var online = factory.First("LineA.Online");
        await WaitUntilAsync(() => offline.ConnectCalls >= 3, TimeSpan.FromSeconds(8));
        await Task.Delay(300);
        Assert.False(online.Disposed);
        Assert.Equal(1, factory.CreatedFor("LineA.Online"));
        Assert.Equal(ConnectionState.Connected, online.State);
    }

    [Fact]
    public async Task Connection_failure_is_classified_exposed_and_moved_to_recovered_after_a_good_read()
    {
        var factory = new RecordingFactory(
            connectFailures: 1,
            connectError: new SocketException((int)SocketError.ConnectionRefused));
        var device = Device("DeviceA");
        device.Tags.Add(new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 });
        await using var engine = CreateEngine(factory, Project(Channel("LineA", device)));
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("diagnostic-test", ["LineA.DeviceA.Value"], 100);

        await WaitUntilAsync(
            () => engine.DeviceStatuses().Single().Diagnostic is not null,
            TimeSpan.FromSeconds(2));

        var failed = Assert.Single(engine.DeviceStatuses());
        Assert.Equal(ConnectionDiagnosticCategory.ConnectionRefused, failed.Diagnostic!.Category);
        Assert.False(string.IsNullOrWhiteSpace(failed.Stats.LastError));
        Assert.Equal(failed.Diagnostic.RawError, failed.Stats.LastError);
        Assert.True(failed.Diagnostic.Retrying);

        await WaitUntilAsync(
            () => engine.DeviceStatuses().Single().State == ConnectionState.Connected
                && engine.DeviceStatuses().Single().Diagnostic is null,
            TimeSpan.FromSeconds(4));

        var recovered = engine.ConnectionDiagnostics();
        Assert.Empty(recovered.Active);
        Assert.Equal(ConnectionDiagnosticCategory.ConnectionRefused, Assert.Single(recovered.Recovered).Category);
        Assert.Equal("", engine.DeviceStatuses().Single().Stats.LastError);
    }

    [Fact]
    public async Task Reconnect_rebuilds_network_stack_only_for_devices_in_selected_channel_and_connects_immediately()
    {
        var factory = new RecordingFactory();
        await using var engine = CreateEngine(factory, Project(
            Channel("LineA", Device("DeviceA")),
            Channel("LineB", Device("DeviceB"))));
        await engine.StartAsync();
        var firstA = factory.First("LineA.DeviceA");
        var firstB = factory.First("LineB.DeviceB");
        await WaitUntilAsync(() => firstA.ConnectCalls >= 1 && firstB.ConnectCalls >= 1, TimeSpan.FromSeconds(2));

        int restarted = await engine.ReconnectChannelAsync("linea", CancellationToken.None);

        Assert.Equal(1, restarted);
        Assert.Equal(2, factory.CreatedFor("LineA.DeviceA"));
        Assert.Equal(1, factory.CreatedFor("LineB.DeviceB"));
        Assert.True(firstA.Disposed);
        Assert.False(firstB.Disposed);
        Assert.True(factory.Last("LineA.DeviceA").ConnectCalls >= 1);
        Assert.True(firstB.ConnectCalls >= 1);
    }

    [Fact]
    public async Task Reconnect_keeps_no_demand_device_connected_without_scanning()
    {
        var factory = new RecordingFactory();
        await using var engine = CreateEngine(factory, Project(Channel("LineA", Device("DeviceA"))));
        await engine.StartAsync();

        await engine.ReconnectChannelAsync("LineA", CancellationToken.None);
        await Task.Delay(80);

        var original = factory.Last("LineA.DeviceA");
        Assert.True(original.ConnectCalls >= 1);
        Assert.Equal(2, factory.CreatedFor("LineA.DeviceA"));
        await WaitUntilAsync(
            () => engine.DeviceStatuses().Single().State == ConnectionState.Connected,
            TimeSpan.FromSeconds(2));
        var status = Assert.Single(engine.DeviceStatuses());
        Assert.False(status.DemandActive);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(0, status.ActiveTags);
    }

    [Fact]
    public async Task Hard_reconnect_preserves_hub_handle_and_point_subscription()
    {
        var factory = new RecordingFactory();
        var device = Device("DeviceA");
        device.Tags.Add(new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 });
        await using var engine = CreateEngine(factory, Project(Channel("LineA", device)));
        await engine.StartAsync();
        const string tagId = "LineA.DeviceA.Value";
        Assert.True(engine.Hub.TryGetHandleId(tagId, out int handleBefore));
        int phase = 0;
        var firstGood = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredGood = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = engine.Hub.SubscribePoints(change =>
        {
            if (change.Value.IsGood)
            {
                if (Volatile.Read(ref phase) == 0) firstGood.TrySetResult();
                else recoveredGood.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, "reconnect-preserve");
        Assert.True(subscription.Add(tagId));
        await firstGood.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref phase, 1);
        await engine.ReconnectChannelAsync("LineA", CancellationToken.None);
        await recoveredGood.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(engine.Hub.TryGetHandleId(tagId, out int handleAfter));
        Assert.Equal(handleBefore, handleAfter);
    }

    [Fact]
    public async Task Automatic_disconnect_recovery_hard_rebuilds_the_whole_channel_network_stack()
    {
        var factory = new RecordingFactory(firstInstancePermanentlyFails: true);
        var device = Device("DeviceA");
        device.Tags.Add(new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 });
        await using var engine = CreateEngine(factory, Project(Channel("LineA", device)));
        await engine.StartAsync();
        using var demand = engine.ScanDemands.Acquire("auto-hard-reconnect", ["LineA.DeviceA.Value"], 50);

        await WaitUntilAsync(
            () => factory.CreatedFor("LineA.DeviceA") >= 2
                && engine.DeviceStatuses().SingleOrDefault()?.State == ConnectionState.Connected,
            TimeSpan.FromSeconds(12));

        Assert.True(factory.First("LineA.DeviceA").Disposed);
        Assert.True(factory.Last("LineA.DeviceA").ConnectCalls >= 1);
    }

    [Fact]
    public async Task Legacy_simulate_setting_uses_real_channel_driver()
    {
        var factory = new RecordingFactory();
        var channel = Channel("LineA", Device("DeviceA"));
        channel.Settings["simulate"] = "true";
        await using var engine = CreateEngine(factory, Project(channel));

        await engine.StartAsync();

        Assert.Equal(1, factory.CreatedFor("LineA.DeviceA"));
        Assert.Equal(RecordingFactory.Id, factory.First("LineA.DeviceA").DriverId);
    }

    [Fact]
    public async Task Reconnect_unknown_channel_reports_not_found()
    {
        var factory = new RecordingFactory();
        await using var engine = CreateEngine(factory, Project(Channel("LineA", Device("DeviceA"))));
        await engine.StartAsync();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            engine.ReconnectChannelAsync("Missing", CancellationToken.None));

        Assert.Contains("Missing", error.Message);
    }

    [Fact]
    public async Task Automatic_reconnect_retries_after_the_default_five_second_circuit_interval()
    {
        await using var hub = new DataHub();
        var driver = new RecordingDriver(connectFailures: 1);
        var loop = new DeviceLoop(driver, [], hub, NullLogger.Instance, "LineA.DeviceA",
            circuitFailureThreshold: 1);
        using var demand = loop.AcquireDemand();
        using var stop = new CancellationTokenSource();
        var started = DateTime.UtcNow;

        loop.Start(stop.Token);
        await WaitUntilAsync(() => driver.ConnectCalls >= 2, TimeSpan.FromSeconds(8));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(7));
        stop.Cancel();
        await loop.Completion;
    }

    [Fact]
    public async Task Device_status_queries_are_safe_while_channel_network_stack_is_rebuilt()
    {
        var factory = new RecordingFactory();
        var devices = Enumerable.Range(0, 200).Select(i => Device($"Device{i}")).ToArray();
        await using var engine = CreateEngine(factory, Project(Channel("LineA", devices)));
        await engine.StartAsync();
        using var readersDone = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
        {
            while (!readersDone.IsCancellationRequested)
                _ = engine.DeviceStatuses();
        })).ToArray();

        await Task.Run(() => engine.ReconnectChannelAsync("LineA", CancellationToken.None));
        readersDone.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(200, engine.DeviceStatuses().Count);
    }

    [Fact]
    public async Task Apply_does_not_wait_for_the_first_connect_after_a_channel_reconnect()
    {
        // 通道重建(停旧设备、起新设备)仍在生命周期锁内完成；等首轮连接结果不占锁——
        // PLC 离线时连接要等到超时，保存工程不能跟着卡十几秒。
        var factory = new RecordingFactory(blockReplacementConnect: true);
        await using var engine = CreateEngine(factory, Project(Channel("LineA", Device("DeviceA"))));
        await engine.StartAsync();
        await WaitUntilAsync(() => factory.First("LineA.DeviceA").State == ConnectionState.Connected,
            TimeSpan.FromSeconds(2));

        Task reconnect = engine.ReconnectChannelAsync("LineA", CancellationToken.None);
        await factory.ReplacementConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var applyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task apply = engine.ApplyAsync(
            Project(Channel("LineA", Device("DeviceA"))),
            () =>
            {
                applyStarted.TrySetResult();
                return Task.CompletedTask;
            });

        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await apply.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(reconnect.IsCompleted);

        factory.ReleaseReplacementConnect.TrySetResult();
        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for reconnect state.");
            await Task.Delay(10);
        }
    }

    private static RuntimeEngine CreateEngine(RecordingFactory factory, IgwProject project)
    {
        var registry = new DriverRegistry();
        registry.Register(factory);
        return new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
    }

    private static IgwProject Project(params ChannelDef[] channels) => new() { Channels = [.. channels] };

    private static ChannelDef Channel(string name, params DeviceDef[] devices) => new()
    {
        Name = name,
        DriverId = RecordingFactory.Id,
        Devices = [.. devices],
    };

    private static DeviceDef Device(string name) => new() { Name = name };

    private sealed class RecordingFactory(int connectFailures = 0, Exception? connectError = null,
        bool firstInstancePermanentlyFails = false, bool blockReplacementConnect = false) : IDriverFactory
    {
        public const string Id = "Recording";
        private readonly Dictionary<string, List<RecordingDriver>> _created = new(StringComparer.OrdinalIgnoreCase);
        private int _createdTotal;
        public TaskCompletionSource ReplacementConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReplacementConnect { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DriverId => Id;
        public DriverDescriptor Describe() => new() { DriverId = Id, DisplayName = Id };

        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
        {
            string key = $"{channel.Name}.{device.Name}";
            int instance = Interlocked.Increment(ref _createdTotal);
            int failures = firstInstancePermanentlyFails && instance == 1 ? int.MaxValue : connectFailures;
            if (!_created.TryGetValue(key, out var list)) _created[key] = list = [];
            bool replacement = list.Count > 0;
            var driver = new RecordingDriver(failures, connectError,
                blockReplacementConnect && replacement
                    ? async ct =>
                    {
                        ReplacementConnectStarted.TrySetResult();
                        await ReleaseReplacementConnect.Task.WaitAsync(ct);
                    }
                    : null);
            list.Add(driver);
            return driver;
        }

        public int CreatedFor(string name) => _created.TryGetValue(name, out var list) ? list.Count : 0;
        public RecordingDriver First(string name) => _created[name][0];
        public RecordingDriver Last(string name) => _created[name][^1];
    }

    private sealed class RecordingDriver(
        int connectFailures = 0,
        Exception? connectError = null,
        Func<CancellationToken, Task>? beforeConnect = null) : IDeviceDriver
    {
        private int _connectFailures = connectFailures;
        public string DriverId => RecordingFactory.Id;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new EmptyAddressParser();
        public DriverLimits Limits { get; } = new();
        public int ConnectCalls { get; private set; }
        public bool Disposed { get; private set; }

        public async Task ConnectAsync(CancellationToken ct)
        {
            ConnectCalls++;
            if (beforeConnect is not null) await beforeConnect(ct);
            if (Interlocked.Decrement(ref _connectFailures) >= 0)
            {
                State = ConnectionState.Faulted;
                StateChanged?.Invoke(State);
                throw connectError ?? new IOException("simulated connection failure");
            }
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray());

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            Disposed = true;
        }
    }

    private sealed class EmptyAddressParser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 1);
            error = null;
            return true;
        }
    }
}
