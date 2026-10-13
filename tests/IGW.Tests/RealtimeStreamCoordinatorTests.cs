using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeStreamCoordinatorTests
{
    [Fact]
    public async Task Session_batch_keeps_the_latest_value_per_handle_and_emits_one_state_batch()
    {
        var request = new RealtimeStreamOpenRequest(
            "client", "operator", "rev-1", ["A", "B"], [], false, 10);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var session = new RealtimeStreamSession(Guid.NewGuid(), request, [1, 2], ["A", "B"], []);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        long coalesced = session.MarkDirtyBatch(
        [
            new RealtimeValueState(1, "A", DataValue.GoodAt(1, DateTime.UtcNow), now),
            new RealtimeValueState(2, "B", DataValue.GoodAt(2, DateTime.UtcNow), now),
            new RealtimeValueState(1, "A", DataValue.GoodAt(3, DateTime.UtcNow), now),
        ]);

        RealtimeOutboundMessage? message = null;
        await foreach (var item in session.ReadAllAsync(lifetime.Token))
        {
            message = item;
            break;
        }
        var batch = Assert.IsType<RealtimeStateBatch>(message);
        Assert.Equal(1, coalesced);
        Assert.Equal(2, batch.Values.Count);
        Assert.Equal(3, Assert.IsType<int>(Assert.Single(batch.Values, value => value.Handle == 1).Value.Value));
        session.Complete();
    }

    [Fact]
    public async Task Same_exact_tag_across_sessions_uses_one_DataHub_registration_until_last_close()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var first = await fixture.OpenAsync("first", "Line.Plc.Group.Value");
        await using var second = await fixture.OpenAsync("second", "Line.Plc.Group.Value");

        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().UniqueTags);
        Assert.Equal(2, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);
        Assert.Equal(1, fixture.Hub.Stats().Subs.Single(s => s.Name == "realtime-streams").Points);

        await first.DisposeAsync();
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().UniqueTags);
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);

        await second.DisposeAsync();
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().UniqueTags);
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);
        Assert.Equal(0, fixture.Hub.Stats().Subs.Single(s => s.Name == "realtime-streams").Points);
    }

    [Fact]
    public async Task State_changes_coalesce_to_the_final_value_for_each_exact_handle()
    {
        await using var fixture = await Fixture.StartAsync(deltaBatchWindowMs: 20);
        await using var lease = await fixture.OpenAsync("client", "Line.Plc.Group.Value");

        fixture.Hub.Update("Line.Plc.Group.Value", DataValue.GoodAt(1, DateTime.UtcNow));
        fixture.Hub.Update("Line.Plc.Group.Value", DataValue.GoodAt(2, DateTime.UtcNow));
        fixture.Hub.Update("Line.Plc.Group.Value", DataValue.GoodAt(3, DateTime.UtcNow));

        var message = await ReadNextAsync(lease);
        var batch = Assert.IsType<RealtimeStateBatch>(message);
        Assert.Single(batch.Values);
        Assert.Equal("Line.Plc.Group.Value", batch.Values[0].TagId);
        Assert.Equal(3, Assert.IsType<int>(batch.Values[0].Value.Value));
    }

    [Fact]
    public async Task Runtime_events_keep_source_order_and_are_not_coalesced()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var lease = await fixture.OpenAsync("client", "Line.Plc.Group.Value", ["mes-task:task-1"]);

        fixture.Coordinator.PublishRuntimeEvent(new RealtimeRuntimeEvent(
            "mes-task:task-1", 10, "node-state", new { node = "upload", state = "run" }, DateTime.UtcNow));
        fixture.Coordinator.PublishRuntimeEvent(new RealtimeRuntimeEvent(
            "mes-task:task-1", 11, "node-state", new { node = "upload", state = "ok" }, DateTime.UtcNow));

        var first = Assert.IsType<RealtimeOrderedEvent>(await ReadNextAsync(lease));
        var second = Assert.IsType<RealtimeOrderedEvent>(await ReadNextAsync(lease));
        Assert.Equal(10L, first.Event.SourceSequence);
        Assert.Equal(11L, second.Event.SourceSequence);
    }

    [Fact]
    public async Task Late_topic_only_session_receives_retained_device_state_with_string_state()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Coordinator.PublishDeviceState(new DeviceStatus(
            "Line.Plc", ConnectionState.Connected, 10, 10, true,
            new DeviceStats(1, 0, 0, 0, 1, "", 100)));

        await using var lease = await fixture.Coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "runtime-page", "operator", "rev-1", [], ["devices"], false, 10), default);

        var message = Assert.IsType<RealtimeOrderedEvent>(await ReadNextAsync(lease));
        var status = Assert.IsType<RealtimeDeviceStatus>(message.Event.Payload);
        Assert.Equal("Line.Plc", status.Name);
        Assert.Equal("Connected", status.State);
    }

    [Fact]
    public async Task Case_distinct_tag_ids_route_independently()
    {
        await using var fixture = await Fixture.StartAsync(includeCaseVariant: true);
        await using var lease = await fixture.Coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "client", "operator", "rev-1",
            ["Line.Plc.Group.Value", "Line.Plc.Group.value"], [], false, 10), default);

        Assert.Equal(2, fixture.Coordinator.SnapshotMetrics().UniqueTags);
        fixture.Hub.Update("Line.Plc.Group.value", DataValue.GoodAt(7, DateTime.UtcNow));

        var batch = Assert.IsType<RealtimeStateBatch>(await ReadNextAsync(lease));
        Assert.Single(batch.Values);
        Assert.Equal("Line.Plc.Group.value", batch.Values[0].TagId);
    }

    [Fact]
    public async Task HasRoutesForTags_reflects_open_streams_only()
    {
        await using var fixture = await Fixture.StartAsync();

        Assert.False(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));

        await using var lease = await fixture.OpenAsync("client", "Line.Plc.Group.Value");
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));
        Assert.False(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Other"]));

        await lease.DisposeAsync();
        Assert.False(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));
    }

    [Fact]
    public async Task Admitting_a_route_for_a_reused_handle_replaces_a_stale_route_for_a_different_tag()
    {
        // 模拟范围化应用:旧点从 Hub 注销、新点复用同一句柄(LIFO 槽位回收)，
        // 但旧点上仍挂着一个没关闭的实时流会话(还没轮到北向回退/RetireGeneration)。
        await using var fixture = await Fixture.StartAsync();
        await using var staleLease = await fixture.OpenAsync("first", "Line.Plc.Group.Value");
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));

        fixture.Hub.Unregister("Line.Plc.Group.Value");
        fixture.Hub.Register("Line.Plc.Group.NewValue", new TagDef
        {
            Name = "NewValue",
            Address = "Line.Plc.Group.NewValue",
            DataType = IgwDataType.Int32,
        });

        await using var freshLease = await fixture.OpenAsync("second", "Line.Plc.Group.NewValue");

        // 旧点的路由必须被摘掉，不能再被新会话误挂上去；但在真正的一次性代际回退
        // (RetireGenerationAsync)发生之前，HasRoutesForTags 仍要如实报告"曾经有人订阅过"，
        // 否则 GatewayHost 会误判为"没人订阅这个被删的点，不用回退"而漏掉唯一一次通知客户端重连的机会。
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.NewValue"]));

        fixture.Hub.Update("Line.Plc.Group.NewValue", DataValue.GoodAt(42, DateTime.UtcNow));
        var message = await ReadNextAsync(freshLease);
        var batch = Assert.IsType<RealtimeStateBatch>(message);
        Assert.Single(batch.Values);
        Assert.Equal("Line.Plc.Group.NewValue", batch.Values[0].TagId);
        Assert.Equal(42, Assert.IsType<int>(batch.Values[0].Value.Value));
    }

    [Fact]
    public async Task Evicting_a_stale_route_notifies_the_stranded_session_and_keeps_relationship_accounting_correct()
    {
        await using var fixture = await Fixture.StartAsync();
        var staleLease = await fixture.OpenAsync("first", "Line.Plc.Group.Value");
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);

        fixture.Hub.Unregister("Line.Plc.Group.Value");
        fixture.Hub.Register("Line.Plc.Group.NewValue", new TagDef
        {
            Name = "NewValue",
            Address = "Line.Plc.Group.NewValue",
            DataType = IgwDataType.Int32,
        });

        var freshLease = await fixture.OpenAsync("second", "Line.Plc.Group.NewValue");

        // 被摘路由的旧会话("A")应该收到重置通知(要求它重新取快照/重新订阅)，
        // 而不是悄悄挂在一个已经归属别的点位的路由上收着无关数据、或者干脆被无声丢弃。
        var resetMessage = await ReadNextAsync(staleLease);
        var reset = Assert.IsType<RealtimeResetRequired>(resetMessage);
        Assert.Equal("tag-removed", reset.Reason);

        // 已注销点在真正的一次性代际回退之前，仍然应该被认为"有实时流关注过"。
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.Value"]));
        Assert.True(fixture.Coordinator.HasRoutesForTags(["Line.Plc.Group.NewValue"]));

        // 摘旧、挂新各一次，净关系数不变；不能因为摘除动作本身多算或漏算。
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().UniqueTags);

        // 旧会话("A")事后关闭：它的句柄如今指向一个跟它毫不相干的新路由(New Value/B)，
        // 关闭逻辑必须能识别出"我不在这个路由的会话集合里"而安全跳过，不能误扣 B 的计数。
        await staleLease.DisposeAsync();
        Assert.Equal(1, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);

        await freshLease.DisposeAsync();
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().SubscriptionRelationships);
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().UniqueTags);
    }

    private static async Task<RealtimeOutboundMessage> ReadNextAsync(RealtimeStreamLease lease)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var message in lease.ReadAllAsync(default).WithCancellation(
                           timeout.Token))
            return message;
        throw new InvalidOperationException("Realtime lease completed without a message.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DataHub hub, RealtimeStreamCoordinator coordinator)
        {
            Hub = hub;
            Coordinator = coordinator;
        }

        public DataHub Hub { get; }
        public RealtimeStreamCoordinator Coordinator { get; }

        public static Task<Fixture> StartAsync(int deltaBatchWindowMs = 10, bool includeCaseVariant = false)
        {
            var hub = new DataHub(shards: 1, busCapacityPerShard: 16_384);
            Register(hub, "Line.Plc.Group.Value");
            if (includeCaseVariant) Register(hub, "Line.Plc.Group.value");
            var settings = new TagStreamSettings
            {
                Enabled = true,
                MaxStreams = 100,
                MaxTagsPerStream = 10_000,
                DeltaBatchWindowMs = deltaBatchWindowMs,
                CoordinatorQueueCapacity = 16_384,
            };
            var coordinator = new RealtimeStreamCoordinator(
                hub, "rev-1", settings, NullLogger<RealtimeStreamCoordinator>.Instance);
            return Task.FromResult(new Fixture(hub, coordinator));
        }

        public ValueTask<RealtimeStreamLease> OpenAsync(
            string clientId,
            string tagId,
            IReadOnlyList<string>? topics = null) =>
            Coordinator.OpenAsync(new RealtimeStreamOpenRequest(
                clientId, "operator", "rev-1", [tagId], topics ?? [], false, 10), default);

        private static void Register(DataHub hub, string tagId) =>
            hub.Register(tagId, new TagDef
            {
                Name = tagId[(tagId.LastIndexOf('.') + 1)..],
                Address = tagId,
                DataType = IgwDataType.Int32,
            });

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }
}
