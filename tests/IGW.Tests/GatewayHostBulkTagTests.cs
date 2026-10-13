using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using IGW.Drivers.Simulator;
using IGW.Persistence.Sqlite;
using IGW.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

/// <summary>GatewayHost.ApplyBulkTagsAsync：写库后只按设备应用运行态，其它设备/实时流/OPC UA 订阅不受影响。</summary>
public sealed class GatewayHostBulkTagTests
{
    [Fact]
    public async Task Bulk_tags_create_channel_device_and_are_readable()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        using var stop = new CancellationTokenSource();
        bool sawReloading = false;
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (host.Reloading) sawReloading = true;
                await Task.Delay(5);
            }
        });

        var request = Request("PLC1", "Sim",
            Enumerable.Range(0, 10).Select(i => Item(i < 5 ? "G1" : "G2", "T" + i, "SINE")).ToArray());
        ProjectBulkTagResponse response = await host.ApplyBulkTagsAsync(request, host.ProjectRevision, default);
        stop.Cancel();
        await sampler;

        Assert.True(response.Applied, response.Error);
        Assert.False(response.Conflict);
        Assert.Equal(10, response.Created);
        Assert.Equal(0, response.Updated);
        Assert.True(response.DeviceRestarted);
        Assert.NotNull(response.ChannelId);
        Assert.NotNull(response.DeviceId);
        Assert.Equal(host.ProjectRevision, response.Revision);
        Assert.False(string.IsNullOrEmpty(response.Timing));
        Assert.True(host.Hub.TryGetDef("PLC1.Sim.G1.T0", out _));
        Assert.True(host.Hub.TryGetDef("PLC1.Sim.G2.T9", out _));
        Assert.False(sawReloading);
        Assert.False(host.ConfigurationDegraded);
        // 分层采集：无订阅需求时不轮询，按需读取验证新点已接入新设备循环
        await EventuallyAsync(async () =>
            (await host.Engine.ReadNowAsync(["PLC1.Sim.G1.T0"], default)).GoodCount == 1, default);
        Assert.True(host.Hub.TryGet("PLC1.Sim.G1.T0", out var value) && value.Quality.IsGood);

        // 持久化的工程与内存一致
        var persisted = await host.ProjectRepository.LoadRuntimeProjectAsync(default);
        Assert.Equal(10, persisted.Channels.Single().Devices.Single().Groups.Sum(g => g.Tags.Count));
    }

    [Fact]
    public async Task Other_devices_keep_running()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        object? identity = host.Engine.RunningDeviceIdentity("Line.X");
        Assert.NotNull(identity);
        Assert.True(host.Hub.TryGetHandleId("Line.X.Points.Value", out int handle));
        var engine = host.Engine;
        var streams = host.RealtimeStreams;
        string realtimeRevision = host.RealtimeRevision;

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "Y", Item("G", "N0", "SINE"), Item("G", "N1", "RAMP")), null, default);

        Assert.True(response.Applied, response.Error);
        Assert.True(response.DeviceRestarted);
        Assert.Same(engine, host.Engine);
        Assert.Same(identity, host.Engine.RunningDeviceIdentity("Line.X"));
        Assert.True(host.Hub.TryGetHandleId("Line.X.Points.Value", out int after));
        Assert.Equal(handle, after);
        Assert.Same(streams, host.RealtimeStreams);
        Assert.Equal(realtimeRevision, host.RealtimeRevision);
        Assert.True(host.Hub.TryGetDef("Line.Y.G.N1", out _));
        Assert.Equal(2, host.Project.Channels.Single().Devices.Count);
    }

    [Fact]
    public async Task Mixed_siemens_addresses_are_rejected_before_persisting()
    {
        var project = new IgwProject
        {
            Name = "s7",
            Channels =
            [
                new ChannelDef
                {
                    Name = "S7",
                    DriverId = "Siemens.S7",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "P",
                            Endpoint = "127.0.0.1:102",
                            Tags = [new TagDef { Name = "Classic", Address = "DB1.DBX0.0", DataType = IgwDataType.Bool }],
                        },
                    ],
                },
            ],
        };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        string revision = host.ProjectRevision;
        ProjectRevision stored = await host.ProjectRepository.GetRevisionAsync(default);

        var request = new ProjectBulkTagRequest
        {
            Channel = new ProjectBulkChannelSpec("S7", "Siemens.S7", null),
            Device = new ProjectBulkDeviceSpec("P", "", null),
            Items = [new ProjectBulkTagItem { GroupPath = ["G"], Name = "Sym", Address = "DB490.Type.Error1", DataType = IgwDataType.Bool }],
        };
        var response = await host.ApplyBulkTagsAsync(request, revision, default);

        Assert.False(response.Applied);
        Assert.False(response.Conflict);
        Assert.Equal("mixed_siemens_address_mode", response.ErrorCode);
        Assert.False(string.IsNullOrEmpty(response.Error));
        Assert.Equal(revision, host.ProjectRevision);
        Assert.Equal(stored, await host.ProjectRepository.GetRevisionAsync(default));
        var persisted = await host.ProjectRepository.LoadRuntimeProjectAsync(default);
        Assert.Empty(persisted.Channels.Single().Devices.Single().Groups);
    }

    [Fact]
    public async Task Mes_referenced_device_is_applied_per_device_and_restarts_mes_only()
    {
        // 设备被运行中的 MES 任务按组引用：不再退回整体应用，只重启该设备并重建 MES(组下新增了点，需要重新绑定)
        var project = SimProject("Line", "PLC", "Points", "Value");
        project.MesUpload.LogDbPath = "";
        project.MesUpload.Tasks.Add(new MesTask
        {
            Id = "task", Name = "Task", Enabled = true, GroupRef = "Line.PLC.Points", CollectIntervalMs = 60000,
        });
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "PLC", Item("Points", "Extra", "SINE")), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
        Assert.True(response.DeviceRestarted);
        Assert.Equal(1, response.Created);
        Assert.DoesNotContain("重建 PLC 采集", response.Timing);
        Assert.Contains("重启受影响设备", response.Timing);
        Assert.Contains("启动 MES 引擎", response.Timing);
        Assert.True(host.Hub.TryGetDef("Line.PLC.Points.Extra", out _));
        Assert.False(host.Reloading);
    }

    [Fact]
    public async Task Disabled_mes_reference_does_not_restart_mes()
    {
        var project = SimProject("Line", "PLC", "Points", "Value");
        project.MesUpload.LogDbPath = "";
        project.MesUpload.Tasks.Add(new MesTask
        {
            Id = "task", Name = "Task", Enabled = false, GroupRef = "Line.PLC.Points", CollectIntervalMs = 60000,
        });
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "PLC", Item("Points", "Extra", "SINE")), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
        Assert.True(response.DeviceRestarted);
        Assert.DoesNotContain("MES", response.Timing);
        Assert.DoesNotContain("重建 PLC 采集", response.Timing);
    }

    [Fact]
    public async Task Scoped_apply_does_not_use_the_full_path()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "PLC", "Points", "Value"));
        var host = fx.Host;

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "PLC", Item("Points", "Extra", "SINE")), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
        Assert.Equal(1, response.Created);
        Assert.DoesNotContain("重建 PLC 采集", response.Timing);
        Assert.Contains("重启受影响设备", response.Timing);
        Assert.Contains("候选设备预检", response.Timing);
        Assert.Equal(response.Timing, host.LastApplyTiming);
        Assert.True(host.Hub.TryGetDef("Line.PLC.Points.Extra", out _));
    }

    [Fact]
    public async Task External_database_offline_blocks_bulk_writes_like_other_configuration_saves()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        var sync = new FakeDatabaseSync { BlockedReason = "主数据库暂时无法连接" };
        host.DatabaseSync = sync;
        string before = host.ProjectRevision;

        var response = await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "A", "SINE")), null, default);

        Assert.False(response.Applied);
        Assert.False(response.Conflict);
        Assert.True(response.Retryable);
        Assert.Equal("database_unavailable", response.ErrorCode);
        Assert.Equal("主数据库暂时无法连接", response.Error);
        Assert.Equal(before, host.ProjectRevision);
        Assert.Empty((await host.ProjectRepository.LoadRuntimeProjectAsync(default)).Channels);
        Assert.Equal(0, sync.Nudges);
    }

    [Fact]
    public async Task Successful_bulk_write_nudges_database_replication()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        var sync = new FakeDatabaseSync();
        host.DatabaseSync = sync;

        var response = await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "A", "SINE")), null, default);

        Assert.True(response.Applied, response.Error);
        Assert.Equal(1, sync.Nudges);
        var conflict = await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "B", "SINE")), "stale-revision", default);
        Assert.True(conflict.Conflict);
        Assert.Equal(1, sync.Nudges);
    }

    private sealed class FakeDatabaseSync : IDatabaseSync
    {
        private int _nudges;
        public string? BlockedReason { get; set; }
        public int Nudges => Volatile.Read(ref _nudges);
        public string State => BlockedReason is null ? "online" : "offline";
        public string? ConfigurationWriteBlockedReason => BlockedReason;
        public object Snapshot() => new { State };
        public void Nudge() => Interlocked.Increment(ref _nudges);
    }

    [Fact]
    public async Task Stale_if_match_is_a_conflict()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        string stale = host.ProjectRevision;
        Assert.True((await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "A", "SINE")), stale, default)).Applied);
        string current = host.ProjectRevision;
        Assert.NotEqual(stale, current);

        var response = await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "B", "SINE")), stale, default);

        Assert.True(response.Conflict);
        Assert.False(response.Applied);
        Assert.Equal(current, response.Revision);
        Assert.Equal(current, host.ProjectRevision);
        Assert.False(host.Hub.TryGetDef("PLC1.Sim.G.B", out _));

        var garbage = await host.ApplyBulkTagsAsync(Request("PLC1", "Sim", Item("G", "B", "SINE")), "not-a-revision", default);
        Assert.True(garbage.Conflict);
    }

    [Fact]
    public async Task Replacing_a_group_with_a_subscribed_tag_resyncs_realtime_streams()
    {
        var project = SimProject("Line", "PLC", "Old", "A");
        project.Channels[0].Devices[0].Groups.Add(new TagGroupDef { Name = "Unwatched", Tags = [SimTag("U")] });
        project.Channels[0].Devices[0].Groups.Add(new TagGroupDef { Name = "Keep", Tags = [SimTag("B")] });
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        var streams = Assert.IsType<RealtimeStreamCoordinator>(host.RealtimeStreams);
        string revision = host.RealtimeRevision;
        await using var lease = await streams.OpenAsync(new RealtimeStreamOpenRequest(
            "client", "operator", revision, ["Line.PLC.Old.A"], [], false, 10), default);

        // 删除一个没人订阅的组：实时流不动
        var quiet = new ProjectBulkTagRequest
        {
            Channel = new ProjectBulkChannelSpec("Line", SimulatorDriverFactory.Id, null),
            Device = new ProjectBulkDeviceSpec("PLC", "", null),
            ReplaceGroups = [["Unwatched"]],
            Items = [Item("Keep", "B2", "SINE")],
        };
        var first = await host.ApplyBulkTagsAsync(quiet, host.ProjectRevision, default);
        Assert.True(first.Applied, first.Error);
        Assert.Equal(1, first.Deleted);
        Assert.False(host.Hub.TryGetDef("Line.PLC.Unwatched.U", out _));
        Assert.Same(streams, host.RealtimeStreams);
        Assert.Equal(revision, host.RealtimeRevision);

        // 删除被订阅的点：整体退役，客户端收到 project-changed
        var loud = new ProjectBulkTagRequest
        {
            Channel = new ProjectBulkChannelSpec("Line", SimulatorDriverFactory.Id, null),
            Device = new ProjectBulkDeviceSpec("PLC", "", null),
            ReplaceGroups = [["Old"]],
            Items = [Item("Keep", "B3", "SINE")],
        };
        var second = await host.ApplyBulkTagsAsync(loud, host.ProjectRevision, default);
        Assert.True(second.Applied, second.Error);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RealtimeProjectChanged? changed = null;
        await foreach (var message in lease.ReadAllAsync(timeout.Token))
            if (message is RealtimeProjectChanged pc) { changed = pc; break; }
        Assert.NotNull(changed);
        Assert.Equal(host.RealtimeRevision, changed.ProjectRevision);
        Assert.NotEqual(revision, host.RealtimeRevision);
        Assert.NotNull(host.RealtimeStreams);
        Assert.NotSame(streams, host.RealtimeStreams);
        Assert.False(host.Hub.TryGetDef("Line.PLC.Old.A", out _));
        Assert.True(host.Hub.TryGetDef("Line.PLC.Keep.B3", out _));
    }

    [Fact]
    public async Task Opc_ua_exposes_new_tags_and_keeps_peer_subscription()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int port = FreePort();
        var project = SimProject("Line", "X", "Points", "Value");
        project.OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;

        await using var client = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await client.ConnectAsync(timeout.Token);
        var peerValues = new ConcurrentQueue<DateTime>();
        await using var sub = await client.SubscribeAsync([Binding("Line.X.Points.Value")], samples =>
        {
            foreach (var sample in samples)
                if (sample.Value.Quality.IsGood) peerValues.Enqueue(DateTime.UtcNow);
        }, timeout.Token);
        await EventuallyAsync(() => !peerValues.IsEmpty, timeout.Token);

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "Y", Item("G1", "N", "SINE")), host.ProjectRevision, timeout.Token);
        Assert.True(response.Applied, response.Error);
        DateTime applied = DateTime.UtcNow;

        var session = (Opc.Ua.Client.Session)typeof(OpcUaClientDeviceDriver)
            .GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        Assert.Equal(["X", "Y"], await BrowseNamesAsync(session, "Line", timeout.Token));
        Assert.Equal(["G1"], await BrowseNamesAsync(session, "Line.Y", timeout.Token));
        Assert.Equal(["N"], await BrowseNamesAsync(session, "Line.Y.G1", timeout.Token));
        await EventuallyAsync(async () =>
        {
            var read = Assert.Single(await client.ReadAsync([Binding("Line.Y.G1.N")], timeout.Token));
            return read.Value.Quality.IsGood;
        }, timeout.Token);

        // 另一台设备的订阅未中断：应用之后仍持续收到推送
        await EventuallyAsync(() => peerValues.Any(t => t > applied.AddMilliseconds(200)), timeout.Token);
        Assert.Same(host.RealtimeStreams, host.RealtimeStreams);
    }

    [Fact]
    public async Task Later_saves_after_a_scoped_apply_do_not_needlessly_retire_realtime_streams()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        project.MesUpload.LogDbPath = "";
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        var streams = Assert.IsType<RealtimeStreamCoordinator>(host.RealtimeStreams);
        string token = host.RealtimeRevision;
        await using var lease = await streams.OpenAsync(new RealtimeStreamOpenRequest(
            "client", "operator", token, ["Line.X.Points.Value"], [], false, 10), default);

        // 按设备应用（无被订阅点删除）：不退役
        Assert.True((await host.ApplyBulkTagsAsync(Request("Line", "Y", Item("G", "N", "SINE")), null, default)).Applied);
        Assert.Same(streams, host.RealtimeStreams);

        // MES 定向保存
        var mes = new MesUploadSettings { DefaultMesUrl = "http://127.0.0.1:1/mes", LogDbPath = "" };
        Assert.True((await host.ApplyMesUploadAsync(mes, null, default)).Applied);
        Assert.Same(streams, host.RealtimeStreams);

        // 仅日志设置
        await host.UpdateLogRetentionDaysAsync(12, default);
        Assert.Same(streams, host.RealtimeStreams);

        // 全量保存但实时指纹不变（只改 MES 地址）：比较基准须按当前工程重算，不能用应用前的旧版本号
        var full = ProjectStore.Clone(host.Project);
        full.MesUpload.DefaultMesUrl = "http://127.0.0.1:2/mes";
        Assert.True((await host.ApplyProjectAsync(full, host.ProjectRevision, default)).Applied);
        Assert.Same(streams, host.RealtimeStreams);
        Assert.Equal(token, host.RealtimeRevision);   // 在用协调器的准入版本保持不变
        await using (var second = await streams.OpenAsync(new RealtimeStreamOpenRequest(
                         "client2", "operator", host.RealtimeRevision, ["Line.Y.G.N"], [], false, 10), default))
            Assert.Equal(["Line.Y.G.N"], second.AcceptedTagIds);

        // 真正改变实时点表的保存仍然退役
        var changed = ProjectStore.Clone(host.Project);
        changed.Channels[0].Devices[0].Groups[0].Tags.Add(SimTag("Extra"));
        Assert.True((await host.ApplyProjectAsync(changed, host.ProjectRevision, default)).Applied);
        Assert.NotSame(streams, host.RealtimeStreams);
        Assert.NotEqual(token, host.RealtimeRevision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RealtimeProjectChanged? notice = null;
        await foreach (var message in lease.ReadAllAsync(timeout.Token))
            if (message is RealtimeProjectChanged pc) { notice = pc; break; }
        Assert.NotNull(notice);
        Assert.Equal(host.RealtimeRevision, notice.ProjectRevision);
    }

    [Fact]
    public async Task Opc_ua_sync_failure_falls_back_to_full_apply_and_the_device_is_served()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int port = FreePort();
        var project = SimProject("Line", "X", "Points", "Value");
        project.OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        int failures = 0;
        host.BeforeOpcUaReplaceDevice = (_, _) =>
        {
            if (Interlocked.Increment(ref failures) == 1) throw new InvalidOperationException("injected opc ua failure");
        };

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "Y", Item("G1", "N", "SINE")), host.ProjectRevision, timeout.Token);
        // 局部应用失败后在同一次调用内回退为整体重读 + 整体应用：调用方看到成功，不进入降级
        Assert.True(response.Applied, response.Error);
        Assert.Equal(1, response.Created);
        Assert.True(response.DeviceRestarted);
        Assert.Contains("局部应用失败，改为整体应用", response.Timing);
        Assert.False(host.ConfigurationDegraded);
        Assert.Same(host.ConfigurationHealthyProject, host.Project);
        Assert.Equal(1, failures);
        Assert.True(host.Hub.TryGetDef("Line.Y.G1.N", out _));

        await using var client = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await client.ConnectAsync(timeout.Token);
        var values = new ConcurrentQueue<DateTime>();
        await using var sub = await client.SubscribeAsync([Binding("Line.Y.G1.N")], samples =>
        {
            foreach (var sample in samples)
                if (sample.Value.Quality.IsGood) values.Enqueue(DateTime.UtcNow);
        }, timeout.Token);
        await EventuallyAsync(() => values.Count >= 2, timeout.Token);
    }

    [Fact]
    public async Task Invalid_items_are_rejected_by_the_host_before_persisting()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        string revision = host.ProjectRevision;

        var response = await host.ApplyBulkTagsAsync(
            Request("PLC1", "Sim", Item("G", "bad.name", "SINE"), Item("G", "ok", "SINE")), revision, default);

        Assert.False(response.Applied);
        Assert.False(response.Conflict);
        Assert.Equal("invalid_items", response.ErrorCode);
        Assert.Contains("第 0 项", response.Error);
        Assert.Equal(revision, host.ProjectRevision);
        Assert.Empty((await host.ProjectRepository.LoadRuntimeProjectAsync(default)).Channels);
    }

    [Fact]
    public async Task Group_nesting_is_capped_at_sixteen_levels_and_the_cap_applies_end_to_end()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        string revision = host.ProjectRevision;
        string[] deep = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxGroupDepth + 1).Select(i => "L" + i)];

        var rejected = await host.ApplyBulkTagsAsync(
            Request("PLC1", "Sim", new ProjectBulkTagItem { GroupPath = [.. deep], Name = "T", Address = "SINE" }),
            revision, default);
        Assert.False(rejected.Applied);
        Assert.Equal("invalid_items", rejected.ErrorCode);
        Assert.Equal(revision, host.ProjectRevision);

        string[] max = deep[..ProjectBulkTagValidator.MaxGroupDepth];
        var accepted = await host.ApplyBulkTagsAsync(
            Request("PLC1", "Sim", new ProjectBulkTagItem { GroupPath = [.. max], Name = "T", Address = "SINE" }),
            revision, default);
        Assert.True(accepted.Applied, accepted.Error);
        Assert.False(host.ConfigurationDegraded);
        string id = "PLC1.Sim." + string.Join('.', max) + ".T";
        Assert.True(host.Hub.TryGetDef(id, out _));
        await EventuallyAsync(async () => (await host.Engine.ReadNowAsync([id], default)).GoodCount == 1, default);
        // 重启时按持久化工程计算设备签名也不会因嵌套过深失败
        var persisted = await host.ProjectRepository.LoadRuntimeProjectAsync(default);
        ChannelDef channel = persisted.Channels.Single();
        Assert.False(string.IsNullOrEmpty(RuntimeEngine.DeviceSig(channel, channel.Devices.Single(),
            new Dictionary<string, string>())));
    }

    [Fact]
    public async Task Too_many_items_are_rejected_by_the_host()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        var request = Request("PLC1", "Sim");
        request.Items = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxItems + 1).Select(i => Item("G", "T" + i, "SINE"))];

        var response = await host.ApplyBulkTagsAsync(request, null, default);

        Assert.False(response.Applied);
        Assert.Equal("too_many_items", response.ErrorCode);
        Assert.Empty((await host.ProjectRepository.LoadRuntimeProjectAsync(default)).Channels);
    }

    [Fact]
    public async Task Write_only_item_without_confirmation_is_rejected_and_full_project_validation_still_passes()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        string revision = host.ProjectRevision;
        var item = Item("G", "Cmd", "SINE");
        item.Access = TagAccess.Write;

        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", item), revision, default);

        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");
        Assert.Contains("Line.X.G.Cmd", response.Error);
        // 被拒后整个工程仍能通过全量校验（/Tag/AddTags、界面保存、导入都按全量校验）
        ProjectStore.Validate(await host.ProjectRepository.LoadRuntimeProjectAsync(default));
    }

    [Fact]
    public async Task Write_only_update_keeps_an_existing_confirmation_and_is_accepted()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        var cmd = SimTag("Cmd");
        cmd.Access = TagAccess.Write;
        cmd.WriteConfirmation = new WriteConfirmationDef { Mode = WriteConfirmationMode.ProtocolAcknowledged };
        project.Channels[0].Devices[0].Groups[0].Tags.Add(cmd);
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        var item = Item("Points", "Cmd", "RAMP");
        item.Access = TagAccess.Write;

        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", item), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
        var persisted = await host.ProjectRepository.LoadRuntimeProjectAsync(default);
        ProjectStore.Validate(persisted);
        var stored = persisted.Channels[0].Devices[0].Groups[0].Tags.Single(t => t.Name == "Cmd");
        Assert.Equal("RAMP", stored.Address);
        Assert.Equal(WriteConfirmationMode.ProtocolAcknowledged, stored.WriteConfirmation?.Mode);
    }

    [Fact]
    public async Task Replace_groups_cannot_delete_a_referenced_confirmation_tag()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        var cmd = SimTag("Cmd");
        cmd.Access = TagAccess.ReadWrite;
        cmd.WriteConfirmation = new WriteConfirmationDef
        {
            Mode = WriteConfirmationMode.ConfirmationTag, ConfirmationTagId = "Line.X.Points.Value", ExpectedValue = "1",
        };
        project.Channels[0].Devices[0].Tags.Add(cmd);
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        string revision = host.ProjectRevision;
        var request = Request("Line", "X", Item("Other", "N", "SINE"));
        request.ReplaceGroups = [["Points"]];

        var response = await host.ApplyBulkTagsAsync(request, revision, default);

        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");
        Assert.Contains("Line.X.Points.Value", response.Error);
        ProjectStore.Validate(await host.ProjectRepository.LoadRuntimeProjectAsync(default));
    }

    [Fact]
    public async Task Invalid_array_definitions_are_rejected_before_persisting()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        string revision = host.ProjectRevision;

        var mismatched = Item("G", "A1", "SINE");
        mismatched.DataType = IgwDataType.Int16;
        mismatched.Array = new ArrayDef { ElementType = IgwDataType.Float, Dimensions = [4] };
        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", mismatched), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");

        var zeroLength = Item("G", "A2", "SINE");
        zeroLength.Array = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [0] };
        response = await host.ApplyBulkTagsAsync(Request("Line", "X", zeroLength), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");

        var badBounds = Item("G", "A3", "SINE");
        badBounds.Array = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [2, 2], LowerBounds = [0] };
        response = await host.ApplyBulkTagsAsync(Request("Line", "X", badBounds), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");
        ProjectStore.Validate(await host.ProjectRepository.LoadRuntimeProjectAsync(default));
    }

    [Fact]
    public async Task Tag_name_equal_to_a_sibling_group_name_is_rejected()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        string revision = host.ProjectRevision;

        // 设备根层新点与已有组 Points 同名（OPC UA NodeId 冲突，NodeId 区分大小写）
        var rootTag = new ProjectBulkTagItem { Name = "Points", Address = "SINE" };
        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", rootTag), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");

        // 新组与已有点 Value 同名
        response = await host.ApplyBulkTagsAsync(Request("Line", "X",
            new ProjectBulkTagItem { GroupPath = ["Points", "Value"], Name = "N", Address = "SINE" }), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");

        // 同一请求内：一项的点名与另一项的组段同名
        response = await host.ApplyBulkTagsAsync(Request("Line", "X",
            new ProjectBulkTagItem { GroupPath = ["New"], Name = "Sub", Address = "SINE" },
            new ProjectBulkTagItem { GroupPath = ["New", "Sub"], Name = "T", Address = "SINE" }), revision, default);
        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");
    }

    [Fact]
    public async Task Names_differing_only_in_case_are_not_a_clash()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "X", new ProjectBulkTagItem { Name = "points", Address = "SINE" }), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
    }

    [Fact]
    public async Task Pre_existing_sibling_clash_does_not_block_unrelated_items()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        // 历史数据：设备根层已有与组 Points 同名的点（经其它入口写入）
        host.Project.Channels[0].Devices[0].Tags.Add(SimTag("Points"));

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "X", Item("Other", "N", "SINE")), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
    }

    [Fact]
    public async Task Legacy_violations_on_the_target_device_do_not_block_a_valid_request()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        InjectLegacyViolations(host);
        await fx.ExecSqlAsync($"UPDATE IGW_tags SET access={(int)TagAccess.Write} WHERE name='Value'");

        var response = await host.ApplyBulkTagsAsync(
            Request("Line", "X", Item("Other", "N", "SINE")), host.ProjectRevision, default);

        Assert.True(response.Applied, response.Error);
        Assert.True(host.Hub.TryGetDef("Line.X.Other.N", out _));
    }

    [Fact]
    public async Task New_violation_is_still_rejected_on_a_device_with_legacy_violations()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        InjectLegacyViolations(host);
        string revision = host.ProjectRevision;
        var cmd = Item("Other", "Cmd", "SINE");
        cmd.Access = TagAccess.Write;

        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", cmd), revision, default);

        AssertRejectedBeforePersisting(host, response, revision, "invalid_items");
        Assert.Contains("Line.X.Other.Cmd", response.Error);
        Assert.DoesNotContain("Line.X.Points.Value", response.Error);
        Assert.DoesNotContain("Legacy", response.Error);
    }

    /// <summary>在内存工程里造出经其它入口遗留的违规：纯写点无确认、确认点指向不存在的点、数组类型与元素类型不一致。</summary>
    private static void InjectLegacyViolations(GatewayHost host)
    {
        DeviceDef device = host.Project.Channels[0].Devices[0];
        device.Groups[0].Tags[0].Access = TagAccess.Write;
        var dangling = SimTag("LegacyConfirm");
        dangling.Access = TagAccess.ReadWrite;
        dangling.WriteConfirmation = new WriteConfirmationDef
        {
            Mode = WriteConfirmationMode.ConfirmationTag, ConfirmationTagId = "Line.X.Missing", ExpectedValue = "1",
        };
        device.Tags.Add(dangling);
        var array = SimTag("LegacyArray");
        array.Array = new ArrayDef { ElementType = IgwDataType.Float, Dimensions = [2] };
        array.IsArray = true;
        device.Tags.Add(array);
    }

    [Fact]
    public async Task Template_instance_target_device_is_rejected()
    {
        var project = new IgwProject
        {
            Name = "tpl",
            Templates = [new DeviceTemplate { Name = "Tpl", DriverId = SimulatorDriverFactory.Id, Tags = [SimTag("A")] }],
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = SimulatorDriverFactory.Id,
                    Devices = [new DeviceDef { Name = "X", TemplateRef = "Tpl" }],
                },
            ],
        };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        string revision = host.ProjectRevision;

        var response = await host.ApplyBulkTagsAsync(Request("Line", "X", Item("G", "N", "SINE")), revision, default);

        AssertRejectedBeforePersisting(host, response, revision, "template_instance_not_supported");
    }

    [Fact]
    public async Task Retry_on_a_healthy_host_is_a_no_op_success()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        IgwProject project = host.Project;
        IgwProject healthy = host.ConfigurationHealthyProject;
        object? identity = host.Engine.RunningDeviceIdentity("Line.X");
        string revision = host.ProjectRevision;

        ProjectApplyResult result = await host.RetryRuntimeApplyAsync(default);

        Assert.True(result.RuntimeApplied);
        Assert.Null(result.ErrorCode);
        Assert.False(host.ConfigurationDegraded);
        Assert.Same(project, host.Project);                      // 没有全量重载
        Assert.Same(healthy, host.ConfigurationHealthyProject);
        Assert.Same(identity, host.Engine.RunningDeviceIdentity("Line.X"));
        Assert.Equal(revision, host.ProjectRevision);
    }

    [Fact]
    public async Task Cancelling_the_request_after_commit_leaves_the_host_healthy_and_applied()
    {
        await using var fx = await HostFixture.StartAsync(SimProject("Line", "X", "Points", "Value"));
        var host = fx.Host;
        using var cts = new CancellationTokenSource();
        // 在按设备应用拿宿主闸门之前取消请求（提交已完成）
        host.BeforeScopedApplyGate = cts.Cancel;

        var response = await host.ApplyBulkTagsAsync(Request("Line", "Y", Item("G", "N", "SINE")), null, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(response.Applied, response.Error);
        Assert.False(host.ConfigurationDegraded);
        Assert.True(host.Hub.TryGetDef("Line.Y.G.N", out _));
    }

    // ---------------- helpers ----------------

    private static void AssertRejectedBeforePersisting(
        GatewayHost host, ProjectBulkTagResponse response, string revision, string code)
    {
        Assert.False(response.Applied);
        Assert.False(response.Conflict);
        Assert.Equal(code, response.ErrorCode);
        Assert.False(string.IsNullOrEmpty(response.Error));
        Assert.Equal(revision, host.ProjectRevision);
        Assert.Equal(revision, host.ProjectRepository.GetRevisionAsync(default).GetAwaiter().GetResult().ETag);
    }

    // ---- 界面局部保存(Batch 修改 → ProjectMutationPatcher → 同一个局部应用入口) ----

    private static ProjectMutation UiBatch(params ProjectMutation[] items) => new(
        ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null,
        System.Text.Json.JsonSerializer.Serialize(items, ProjectStore.Options));

    private static ProjectMutation UiTagUpsert(string parentId, TagDef tag) => new(
        ProjectMutationKind.UpsertEntity, ProjectNodeKind.Tag, [], parentId,
        System.Text.Json.JsonSerializer.Serialize(tag, ProjectStore.Options));

    [Fact]
    public async Task Ui_batch_delete_of_a_monitored_tag_releases_opc_ua_pins_and_device_demand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int port = FreePort();
        var project = SimProject("Line", "X", "Points", "Value");
        project.Channels[0].Devices[0].Groups[0].Tags.Add(SimTag("Gone"));
        project.OpcUa = new OpcUaSettings { Enabled = true, Port = port, AllowAnonymous = true, AllowEncryption = false };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;

        await using var client = new OpcUaClientDeviceDriver($"opc.tcp://127.0.0.1:{port}", 5_000, true, "", "", "None");
        await client.ConnectAsync(timeout.Token);
        var goneQuality = new ConcurrentQueue<bool>();
        var goneSub = await client.SubscribeAsync([Binding("Line.X.Points.Gone")], samples =>
        {
            foreach (var sample in samples) goneQuality.Enqueue(sample.Value.Quality.IsGood);
        }, timeout.Token);
        await EventuallyAsync(() => goneQuality.Contains(true), timeout.Token);
        Assert.True(host.Hub.IsPinned("Line.X.Points.Gone"));
        Assert.True(host.Hub.HasDeviceDemand("Line.X", 1));

        TagGroupDef group = host.Project.Channels[0].Devices[0].Groups[0];
        string goneId = group.Tags.Single(t => t.Name == "Gone").Id;
        var result = await host.ApplyProjectMutationAsync(
            UiBatch(new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Tag, [goneId], null, null)),
            host.ProjectRevision, timeout.Token);

        Assert.True(result.Applied, result.Error);
        Assert.False(host.ConfigurationDegraded);
        Assert.False(host.Hub.TryGetDef("Line.X.Points.Gone", out _));
        await EventuallyAsync(() => goneQuality.Contains(false), timeout.Token);   // 旧监控项收到 Bad 通知
        Assert.False(host.Hub.IsPinned("Line.X.Points.Gone"));
        await EventuallyAsync(() => !host.Hub.HasDeviceDemand("Line.X", 1), timeout.Token);   // 设备不再被一直轮询
        Assert.DoesNotContain("重建 PLC 采集", host.LastApplyTiming);

        // 客户端之后再删掉这个死监控项：不得重复释放(同名新点的钉点不能被误摘)
        await goneSub.DisposeAsync();
        var readded = await host.ApplyProjectMutationAsync(
            UiBatch(UiTagUpsert(group.Id, new TagDef { Id = goneId, Name = "Gone", Address = "SINE", DataType = IgwDataType.Int16 })),
            host.ProjectRevision, timeout.Token);
        Assert.True(readded.Applied, readded.Error);
        Assert.False(host.Hub.IsPinned("Line.X.Points.Gone"));
    }

    /// <summary>局部保存删掉 MES 任务绑定的组后，与整包保存一样清理失效任务，并告诉前端重新取 MES 配置。</summary>
    [Fact]
    public async Task Ui_partial_save_that_deletes_a_mes_bound_group_removes_the_orphaned_task()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        project.Channels[0].Devices[0].Groups.Add(new TagGroupDef { Name = "Keep", Tags = [SimTag("K")] });
        project.MesUpload.LogDbPath = "";
        project.MesUpload.Tasks =
        [
            new MesTask { Id = "gone", Name = "Gone", GroupRef = "Line.X.Points", Enabled = false },
            new MesTask { Id = "keep", Name = "Keep", GroupRef = "Line.X.Keep", Enabled = false },
        ];
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        string pointsId = host.Project.Channels[0].Devices[0].Groups.Single(g => g.Name == "Points").Id;

        var unrelated = await host.ApplyProjectMutationAsync(
            UiBatch(UiTagUpsert(pointsId, new TagDef { Id = Guid.NewGuid().ToString("N"), Name = "N", Address = "SINE", DataType = IgwDataType.Int16 })),
            host.ProjectRevision, default);
        Assert.True(unrelated.Applied, unrelated.Error);
        Assert.False(unrelated.MesTasksReconciled);   // 没有失效任务时不额外写入

        var deleted = await host.ApplyProjectMutationAsync(
            UiBatch(new ProjectMutation(ProjectMutationKind.DeleteEntities, ProjectNodeKind.Group, [pointsId], null, null)),
            host.ProjectRevision, default);

        Assert.True(deleted.Applied, deleted.Error);
        Assert.True(deleted.MesTasksReconciled);
        Assert.Equal(["keep"], host.Project.MesUpload.Tasks.Select(task => task.Id));
        IgwProject stored = await host.ProjectRepository.LoadRuntimeProjectAsync(default);
        Assert.Equal(["keep"], stored.MesUpload.Tasks.Select(task => task.Id));
        Assert.Equal(host.ProjectRevision, (await host.ProjectRepository.GetRevisionAsync(default)).ETag);
    }

    [Fact]
    public async Task Ui_batch_patch_then_full_save_does_not_needlessly_retire_realtime_streams()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        project.MesUpload.LogDbPath = "";
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        var streams = Assert.IsType<RealtimeStreamCoordinator>(host.RealtimeStreams);
        string token = host.RealtimeRevision;
        await using var lease = await streams.OpenAsync(new RealtimeStreamOpenRequest(
            "client", "operator", token, ["Line.X.Points.Value"], [], false, 10), default);

        // 界面局部保存新增一个点(没有被订阅的点被删除)：不退役
        TagGroupDef group = host.Project.Channels[0].Devices[0].Groups[0];
        var added = await host.ApplyProjectMutationAsync(
            UiBatch(UiTagUpsert(group.Id, new TagDef { Id = Guid.NewGuid().ToString("N"), Name = "N", Address = "SINE", DataType = IgwDataType.Int16 })),
            host.ProjectRevision, default);
        Assert.True(added.Applied, added.Error);
        Assert.DoesNotContain("重建 PLC 采集", host.LastApplyTiming);   // 走的是局部应用
        Assert.Same(streams, host.RealtimeStreams);
        Assert.Equal(token, host.RealtimeRevision);

        // 全量保存但实时指纹不变（只改 MES 地址）：比较基准按当前工程重算，不退役
        var full = ProjectStore.Clone(host.Project);
        full.MesUpload.DefaultMesUrl = "http://127.0.0.1:2/mes";
        Assert.True((await host.ApplyProjectAsync(full, host.ProjectRevision, default)).Applied);
        Assert.Same(streams, host.RealtimeStreams);
        Assert.Equal(token, host.RealtimeRevision);
        await using (var second = await streams.OpenAsync(new RealtimeStreamOpenRequest(
                         "client2", "operator", host.RealtimeRevision, ["Line.X.Points.N"], [], false, 10), default))
            Assert.Equal(["Line.X.Points.N"], second.AcceptedTagIds);

        // 真正改变实时点表的保存仍然退役
        var changed = ProjectStore.Clone(host.Project);
        changed.Channels[0].Devices[0].Groups[0].Tags.Add(SimTag("Extra"));
        Assert.True((await host.ApplyProjectAsync(changed, host.ProjectRevision, default)).Applied);
        Assert.NotSame(streams, host.RealtimeStreams);
        Assert.NotEqual(token, host.RealtimeRevision);
    }

    [Fact]
    public async Task Ui_batch_patch_on_a_device_with_alarms_still_restarts_alarms()
    {
        var project = SimProject("Line", "X", "Points", "Value");
        project.Channels[0].Devices[0].Groups[0].Tags[0].Alarm = new AlarmDef { Enabled = true, Hi = 1000 };
        await using var fx = await HostFixture.StartAsync(project);
        var host = fx.Host;
        var alarms = host.Alarms;
        var engineDevice = host.Engine.RunningDeviceIdentity("Line.X");

        TagGroupDef group = host.Project.Channels[0].Devices[0].Groups[0];
        TagDef edited = System.Text.Json.JsonSerializer.Deserialize<TagDef>(
            System.Text.Json.JsonSerializer.Serialize(group.Tags[0], ProjectStore.Options), ProjectStore.Options)!;
        edited.Description = "改说明";
        var result = await host.ApplyProjectMutationAsync(UiBatch(UiTagUpsert(group.Id, edited)), host.ProjectRevision, default);

        Assert.True(result.Applied, result.Error);
        Assert.NotSame(alarms, host.Alarms);                                // 报警引擎重建(master 行为保留)
        Assert.Contains("启动报警/计算/计划/历史/MQTT", host.LastApplyTiming);
        Assert.DoesNotContain("重建 PLC 采集", host.LastApplyTiming);       // 仍是局部应用
        // 只改说明：设备运行签名不含说明，采集循环不重启，说明就地刷新到缓存定义
        Assert.Same(engineDevice, host.Engine.RunningDeviceIdentity("Line.X"));
        Assert.True(host.Hub.TryGetDef("Line.X.Points.Value", out var def));
        Assert.Equal("改说明", def.Description);
        Assert.Same(host.ConfigurationHealthyProject, host.Project);
    }

    private static IgwProject SimProject(string channel, string device, string group, string tag) => new()
    {
        Name = "sim",
        Channels =
        [
            new ChannelDef
            {
                Name = channel,
                DriverId = SimulatorDriverFactory.Id,
                Devices =
                [
                    new DeviceDef
                    {
                        Name = device,
                        Groups = [new TagGroupDef { Name = group, Tags = [SimTag(tag)] }],
                    },
                ],
            },
        ],
    };

    private static TagDef SimTag(string name) => new()
    {
        Name = name, Address = "SINE", DataType = IgwDataType.Int16, ScanRateMs = 100,
    };

    [Fact]
    public async Task Dcs_add_tags_after_bulk_registration_ignores_the_bulk_project_channel()
    {
        await using var fx = await HostFixture.StartAsync(new IgwProject { Name = "empty" });
        var host = fx.Host;
        var request = Request("泰兴线_PLC1", "PLC1", Item("DB1", "Speed", "SINE"));
        request.Device = request.Device with
        {
            Settings = new() { [IGW.Northbound.RestApi.DcsTagMetadata.LogicalDevice] = "MemoryCache_10.0.0.1" },
        };
        var response = await host.ApplyBulkTagsAsync(request, host.ProjectRevision, default);
        Assert.True(response.Applied, response.Error);
        DeviceDef bulkDevice = host.Project.Channels.Single().Devices.Single();
        Assert.True(ProjectBulkTagMetadata.IsBulkManaged(bulkDevice));

        static IGW.Northbound.RestApi.DcsTagUpdateModel Add(string ip) => new()
        {
            CommunicationType = IGW.Northbound.RestApi.DcsCommunicationType.MemoryCache,
            Ip = ip,
            DbAddress = "SINE",
            TagName = "Value",
            DataType = IGW.Northbound.RestApi.DcsTagDataType.Short,
        };

        Assert.True(IGW.Northbound.RestApi.DcsTagProjectUpdater.TryBuild(host.Project, [Add("10.0.0.2")],
            out var updated, out var error), error);
        Assert.Equal("MemoryCache_10.0.0.2",
            Assert.Single(updated.Channels.Single(channel => channel.Name == "通用").Devices).Name);
        Assert.Single(updated.Channels.Single(channel => channel.Name == "泰兴线_PLC1").Devices);

        Assert.False(IGW.Northbound.RestApi.DcsTagProjectUpdater.TryBuild(host.Project, [Add("10.0.0.1")],
            out _, out error));
        Assert.Contains("/api/v1/project/bulk/tags", error);
    }

    private static ProjectBulkTagRequest Request(string channel, string device, params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec(channel, SimulatorDriverFactory.Id, null),
        Device = new ProjectBulkDeviceSpec(device, "", null),
        Items = [.. items],
    };

    private static ProjectBulkTagItem Item(string group, string name, string address) => new()
    {
        GroupPath = [group],
        Name = name,
        Address = address,
        DataType = IgwDataType.Int16,
        Access = TagAccess.Read,
        ScanRateMs = 100,
    };

    private static TagBinding Binding(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = $"ns=2;s={id}", DataType = IgwDataType.Int16, Access = TagAccess.Read },
        Address = new TagAddress(0, 0, 2),
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string[]> BrowseNamesAsync(Opc.Ua.Client.Session session, string id, CancellationToken ct)
    {
        var response = await session.BrowseAsync(null, null, 0, new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = new NodeId(id, 2),
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            },
        }, ct);
        var result = Assert.Single(response.Results);
        Assert.True(Opc.Ua.StatusCode.IsGood(result.StatusCode), result.StatusCode.ToString());
        return result.References.Select(r => r.BrowseName.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> predicate, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(20, limit.Token);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await predicate()) await Task.Delay(50, limit.Token);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly string _dir;
        public GatewayHost Host { get; }

        private HostFixture(string dir, GatewayHost host)
        {
            _dir = dir;
            Host = host;
        }

        public static async Task<HostFixture> StartAsync(IgwProject initial)
        {
            string dir = Path.Combine(Path.GetTempPath(), "igw-bulk-host", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            if (initial.OpcUa is null || initial.OpcUa.Port == 4840) initial.OpcUa = new OpcUaSettings { Enabled = false };
            string legacy = Path.Combine(dir, "project.igwproj");
            ProjectStore.Save(initial, legacy);
            ProjectBootstrapResult storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
                new ProjectStoragePaths(Path.Combine(dir, "project.db"), legacy, Path.Combine(dir, "igw.masterkey")),
                default);
            IgwProject project = await storage.Repository.LoadRuntimeProjectAsync(default);
            ProjectRevision revision = await storage.Repository.GetRevisionAsync(default);
            var registry = new DriverRegistry();
            registry.Register(new SimulatorDriverFactory());
            var host = new GatewayHost(project, storage.Repository, revision, registry, NullLoggerFactory.Instance, dir);
            var fixture = new HostFixture(dir, host);
            try { await host.StartAsync(); }
            catch { await fixture.DisposeAsync(); throw; }
            Assert.NotNull(host.Engine);
            return fixture;
        }

        public async Task ExecSqlAsync(string sql)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=" + Path.Combine(_dir, "project.db") + ";Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try
            {
                foreach (string file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_dir, recursive: true);
            }
            catch { }
        }
    }
}
