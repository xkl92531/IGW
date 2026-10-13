using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>按设备应用(ApplyDevicesAsync):只重启点名设备,其余设备运行实例不动,既有点位句柄保留。</summary>
public sealed class EngineScopedApplyTests
{
    [Fact]
    public async Task Only_target_device_is_restarted()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        var runtimeA = engine.RunningDeviceIdentity("Ch.A");
        var runtimeB = engine.RunningDeviceIdentity("Ch.B");
        Assert.NotNull(runtimeA);
        Assert.NotNull(runtimeB);

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags.Add(Tag("New"));
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A"]);

        Assert.Equal(["Ch.A"], result.Restarted);
        Assert.Equal(["Ch.A.New"], result.AddedTagIds);
        Assert.Empty(result.RemovedTagIds);
        Assert.Same(runtimeB, engine.RunningDeviceIdentity("Ch.B"));
        Assert.NotSame(runtimeA, engine.RunningDeviceIdentity("Ch.A"));
        Assert.True(engine.Hub.TryGetDef("Ch.A.New", out _));
        Assert.Same(next, engine.Project);
    }

    [Fact]
    public async Task Existing_tag_handles_survive_restart()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        var before = new Dictionary<string, int>();
        foreach (var id in new[] { "Ch.A.T0", "Ch.A.T1", "Ch.A.T2", "Ch.B.T0" })
        {
            Assert.True(engine.Hub.TryGetHandleId(id, out int handle));
            before[id] = handle;
        }
        var handleObject = engine.Hub.GetHandle("Ch.A.T0");

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags.Add(Tag("New"));
        await engine.ApplyDevicesAsync(next, ["Ch.A"]);

        foreach (var (id, handle) in before)
        {
            Assert.True(engine.Hub.TryGetHandleId(id, out int after));
            Assert.Equal(handle, after);
        }
        Assert.Same(handleObject, engine.Hub.GetHandle("Ch.A.T0"));
    }

    [Fact]
    public async Task Changed_tag_definition_is_refreshed_in_place_keeping_handle()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T1", out int handle));

        var next = ProjectStore.Clone(project);
        var changed = next.Channels[0].Devices[0].Tags[1];
        changed.DataType = IgwDataType.Float;
        changed.Address = "SINE";
        changed.Description = "updated note";
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A"]);

        Assert.Equal(["Ch.A"], result.Restarted);
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T1", out int after));
        Assert.Equal(handle, after);
        Assert.True(engine.Hub.TryGetDef("Ch.A.T1", out var def));
        Assert.Equal(IgwDataType.Float, def.DataType);
        Assert.Equal("SINE", def.Address);
        Assert.Equal("updated note", def.Description);
    }

    [Fact]
    public async Task Removed_tags_are_unregistered()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        Assert.True(engine.Hub.TryGetDef("Ch.A.T2", out _));

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags.RemoveAll(t => t.Name == "T2");
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A"]);

        Assert.False(engine.Hub.TryGetDef("Ch.A.T2", out _));
        Assert.Equal(["Ch.A.T2"], result.RemovedTagIds);
        Assert.Empty(result.AddedTagIds);
        Assert.True(engine.Hub.TryGetDef("Ch.A.T0", out _));
        Assert.True(engine.Hub.TryGetDef("Ch.B.T2", out _));
    }

    [Fact]
    public async Task Unchanged_device_is_skipped()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        var runtimeA = engine.RunningDeviceIdentity("Ch.A");

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags[0].Description = "note only";   // 纯备注不改变运行签名
        bool began = false;
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A"], () => { began = true; return Task.CompletedTask; });

        Assert.True(began);
        Assert.Empty(result.Restarted);
        Assert.Empty(result.AddedTagIds);
        Assert.Empty(result.RemovedTagIds);
        Assert.Same(runtimeA, engine.RunningDeviceIdentity("Ch.A"));
        Assert.True(engine.Hub.TryGetDef("Ch.A.T0", out var def));
        Assert.Equal("note only", def.Description);
        Assert.Same(next, engine.Project);
    }

    [Fact]
    public async Task New_device_is_started_and_unknown_name_is_ignored()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        var runtimeA = engine.RunningDeviceIdentity("Ch.A");

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices.Add(Device("C", 2));
        var result = await engine.ApplyDevicesAsync(next, ["Ch.C", "Ch.Missing"]);

        Assert.Equal(["Ch.C"], result.Restarted);
        Assert.Equal(["Ch.C.T0", "Ch.C.T1"], result.AddedTagIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.NotNull(engine.RunningDeviceIdentity("Ch.C"));
        Assert.Same(runtimeA, engine.RunningDeviceIdentity("Ch.A"));
        Assert.True(engine.Hub.TryGetDef("Ch.C.T1", out _));
    }

    [Fact]
    public async Task Apply_before_start_fails_fast_without_touching_state()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags.Add(Tag("New"));
        bool began = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ApplyDevicesAsync(next, ["Ch.A"], () => { began = true; return Task.CompletedTask; }));

        Assert.False(began);
        Assert.Same(project, engine.Project);
        Assert.False(engine.Hub.TryGetDef("Ch.A.New", out _));
    }

    [Fact]
    public async Task Remove_and_add_in_same_apply_keeps_survivor_handles()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T0", out int h0));
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T2", out int h2));
        var entry0 = engine.Hub.GetHandle("Ch.A.T0");

        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Tags.RemoveAll(t => t.Name == "T1");
        next.Channels[0].Devices[0].Tags.Add(Tag("New"));
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A"]);

        Assert.Equal(["Ch.A.T1"], result.RemovedTagIds);
        Assert.Equal(["Ch.A.New"], result.AddedTagIds);
        Assert.False(engine.Hub.TryGetDef("Ch.A.T1", out _));
        Assert.True(engine.Hub.TryGetDef("Ch.A.New", out _));
        engine.Hub.Update("Ch.A.New", DataValue.GoodAt(5, DateTime.UtcNow));
        Assert.True(engine.Hub.TryGet("Ch.A.New", out var fresh));
        Assert.Equal(5, fresh.Value);
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T0", out int a0));
        Assert.True(engine.Hub.TryGetHandleId("Ch.A.T2", out int a2));
        Assert.Equal(h0, a0);
        Assert.Equal(h2, a2);
        Assert.Same(entry0, engine.Hub.GetHandle("Ch.A.T0"));
        Assert.NotSame(entry0, engine.Hub.GetHandle("Ch.A.New"));
    }

    [Fact]
    public async Task Drain_timeout_falls_back_to_fresh_hub_entries()
    {
        // 首个驱动实例卡在连接里且无视取消 → 旧循环在 ApplyStopGrace(默认 1.5s)内退不出。
        // 不改静态宽限:它是全局静态,改小会影响并行运行的其它测试类。
        var factory = new HangingFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        var device = new DeviceDef { Name = "A" };
        device.Tags.Add(new TagDef { Name = "T0", Address = "X", DataType = IgwDataType.Int32 });
        device.Tags.Add(new TagDef { Name = "T1", Address = "X", DataType = IgwDataType.Int32 });
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "Ch", DriverId = HangingFactory.Id, Devices = [device] }],
        };
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
        try
        {
            await engine.StartAsync();
            await factory.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old0 = engine.Hub.GetHandle("Ch.A.T0");
            var old1 = engine.Hub.GetHandle("Ch.A.T1");

            var next = ProjectStore.Clone(project);
            next.Channels[0].Devices[0].Tags.Add(new TagDef { Name = "New", Address = "X", DataType = IgwDataType.Int32 });
            var result = await engine.ApplyDevicesAsync(next, ["Ch.A"]);

            Assert.Equal(["Ch.A"], result.Restarted);
            Assert.Equal(["Ch.A.T0", "Ch.A.T1"], result.RemovedTagIds.OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(["Ch.A.New", "Ch.A.T0", "Ch.A.T1"], result.AddedTagIds.OrderBy(x => x, StringComparer.Ordinal));
            Assert.NotSame(old0, engine.Hub.GetHandle("Ch.A.T0"));
            Assert.NotSame(old1, engine.Hub.GetHandle("Ch.A.T1"));

            // 新循环采到好值(之后读挂起,值保持不变)
            using var demand = engine.ScanDemands.Acquire("drain-test", ["Ch.A.T0", "Ch.A.T1", "Ch.A.New"], 50);
            await WaitUntilAsync(() => engine.Hub.TryGet("Ch.A.T0", out var v) && v.Quality == StatusCode.Good,
                TimeSpan.FromSeconds(5));

            // 放行旧循环:它退出时 MarkAllBad(BadNotConnected) 只能落到孤儿条目上
            factory.ReleaseFirstConnect.TrySetResult();
            await factory.FirstDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);

            Assert.True(engine.Hub.TryGet("Ch.A.T0", out var after));
            Assert.Equal(StatusCode.Good, after.Quality);
            Assert.Equal(7, after.Value);
        }
        finally { factory.ReleaseFirstConnect.TrySetResult(); }
    }

    [Fact]
    public async Task Drain_timeout_during_a_batch_mutation_patch_uses_fresh_hub_entries()
    {
        // 界面局部保存(Batch → ProjectMutationPatcher)与批量注册走同一个按设备应用：
        // 旧循环排空超时时不得复用它仍持有的 Hub 条目(否则两个循环同时写同一条目)。
        var factory = new HangingFactory();
        var registry = new DriverRegistry();
        registry.Register(factory);
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Id = "c1", Name = "Ch", DriverId = HangingFactory.Id,
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "d1", Name = "A",
                            Tags =
                            [
                                new TagDef { Id = "t0", Name = "T0", Address = "X", DataType = IgwDataType.Int32 },
                                new TagDef { Id = "t1", Name = "T1", Address = "X", DataType = IgwDataType.Int32 },
                            ],
                        },
                    ],
                },
            ],
        };
        await using var engine = new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
        try
        {
            await engine.StartAsync();
            await factory.FirstConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old0 = engine.Hub.GetHandle("Ch.A.T0");

            ProjectMutation batch = new(ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null,
                System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new ProjectMutation(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Tag, [], "d1",
                        System.Text.Json.JsonSerializer.Serialize(
                            new TagDef { Id = "t0", Name = "T0", Address = "X", DataType = IgwDataType.Int32, Description = "改" },
                            ProjectStore.Options)),
                    new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, ["t1"], null, null),
                }, ProjectStore.Options));
            ProjectPatch patch = Assert.IsType<ProjectPatch>(ProjectMutationPatcher.TryApply(project, batch));
            var result = await engine.ApplyDevicesAsync(patch.Project, patch.Summary.ChangedDevices, patch.Summary.RemovedDevices);

            Assert.Equal(["Ch.A"], result.Restarted);
            Assert.Equal(["Ch.A.T0", "Ch.A.T1"], result.RemovedTagIds.OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(["Ch.A.T0"], result.AddedTagIds);
            Assert.NotSame(old0, engine.Hub.GetHandle("Ch.A.T0"));   // 新条目，不与旧循环共享
            Assert.False(engine.Hub.TryGetDef("Ch.A.T1", out _));
            Assert.True(engine.Hub.TryGetDef("Ch.A.T0", out var def));
            Assert.Equal("改", def.Description);

            using var demand = engine.ScanDemands.Acquire("drain-test", ["Ch.A.T0"], 50);
            await WaitUntilAsync(() => engine.Hub.TryGet("Ch.A.T0", out var v) && v.Quality == StatusCode.Good,
                TimeSpan.FromSeconds(5));
            factory.ReleaseFirstConnect.TrySetResult();
            await factory.FirstDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.True(engine.Hub.TryGet("Ch.A.T0", out var after));
            Assert.Equal(StatusCode.Good, after.Quality);   // 旧循环退出时的 MarkAllBad 只落到孤儿条目
        }
        finally { factory.ReleaseFirstConnect.TrySetResult(); }
    }

    [Fact]
    public async Task Removed_and_renamed_devices_are_stopped_and_their_tags_unregistered()
    {
        var project = TwoDeviceProject();
        await using var engine = CreateEngine(project);
        await engine.StartAsync();
        var runtimeB = engine.RunningDeviceIdentity("Ch.B");

        // A 改名为 A2，B 不动
        var next = ProjectStore.Clone(project);
        next.Channels[0].Devices[0].Name = "A2";
        var result = await engine.ApplyDevicesAsync(next, ["Ch.A2"], ["Ch.A"]);

        Assert.Equal(["Ch.A2"], result.Restarted);
        Assert.Null(engine.RunningDeviceIdentity("Ch.A"));
        Assert.NotNull(engine.RunningDeviceIdentity("Ch.A2"));
        Assert.Same(runtimeB, engine.RunningDeviceIdentity("Ch.B"));
        Assert.Equal(["Ch.A.T0", "Ch.A.T1", "Ch.A.T2"], result.RemovedTagIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(engine.Hub.TryGetDef("Ch.A.T0", out _));
        Assert.True(engine.Hub.TryGetDef("Ch.A2.T0", out _));

        // 删除 B：停掉并注销；仍在新工程里的名字不按删除处理
        var removed = ProjectStore.Clone(next);
        removed.Channels[0].Devices.RemoveAt(1);
        var second = await engine.ApplyDevicesAsync(removed, [], ["Ch.B", "Ch.A2"]);
        Assert.Empty(second.Restarted);
        Assert.Null(engine.RunningDeviceIdentity("Ch.B"));
        Assert.NotNull(engine.RunningDeviceIdentity("Ch.A2"));
        Assert.False(engine.Hub.TryGetDef("Ch.B.T0", out _));
        Assert.True(engine.Hub.TryGetDef("Ch.A2.T0", out _));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    // 首个实例:连接无视取消一直卡住,直到测试放行;后续实例:正常连接,首次读返回 7,之后读挂起(值保持)
    private sealed class HangingFactory : IDriverFactory
    {
        public const string Id = "Hanging";
        private int _created;
        public TaskCompletionSource FirstConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => Id;
        public DriverDescriptor Describe() => new() { DriverId = Id, DisplayName = Id };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) =>
            new HangingDriver(this, Interlocked.Increment(ref _created) == 1);
    }

    private sealed class HangingDriver(HangingFactory owner, bool first) : IDeviceDriver
    {
        private int _reads;
        public string DriverId => HangingFactory.Id;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new SimulatorAddressParser();
        public DriverLimits Limits { get; } = new();

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (first)
            {
                owner.FirstConnectStarted.TrySetResult();
                await owner.ReleaseFirstConnect.Task;   // 故意无视取消:模拟阻塞式连接
            }
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
        }

        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            if (first) owner.FirstDisconnected.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) > 1) await Task.Delay(Timeout.Infinite, ct);
            return tags.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt(7, DateTime.UtcNow))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }

    private static RuntimeEngine CreateEngine(IgwProject project)
    {
        var registry = new DriverRegistry();
        registry.Register(new SimulatorDriverFactory());
        return new RuntimeEngine(project, registry, new DataHub(), NullLoggerFactory.Instance);
    }

    private static IgwProject TwoDeviceProject() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Ch",
                DriverId = SimulatorDriverFactory.Id,
                Devices = [Device("A", 3), Device("B", 3)],
            },
        ],
    };

    private static DeviceDef Device(string name, int tags)
    {
        var device = new DeviceDef { Name = name };
        for (int i = 0; i < tags; i++) device.Tags.Add(Tag("T" + i));
        return device;
    }

    private static TagDef Tag(string name) => new() { Name = name, Address = "RAMP", DataType = IgwDataType.Int32 };
}
