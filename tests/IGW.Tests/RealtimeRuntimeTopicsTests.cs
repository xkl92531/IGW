using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeRuntimeTopicsTests
{
    [Fact]
    public async Task Runtime_topic_only_session_is_available_when_legacy_tag_stream_switch_is_disabled()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings { Enabled = false }, NullLogger<RealtimeStreamCoordinator>.Instance);

        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "runtime-page", "operator", "rev", [], ["devices"], false, 10), default);

        Assert.Equal(1, coordinator.SnapshotMetrics().ActiveStreams);
    }

    [Fact]
    public async Task Legacy_disabled_tag_stream_does_not_block_point_value_sessions()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        hub.Register("Line.Plc.Value", new TagDef { Id = "Line.Plc.Value", Name = "Value" });
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings { Enabled = false }, NullLogger<RealtimeStreamCoordinator>.Instance);

        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "points-page", "operator", "rev", ["Line.Plc.Value"], [], false, 10), default);

        Assert.Single(lease.AcceptedTagIds);
        Assert.Equal(1, coordinator.SnapshotMetrics().ActiveStreams);
    }

    [Fact]
    public async Task Retained_device_state_is_delivered_to_a_late_topic_only_session()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings(), NullLogger<RealtimeStreamCoordinator>.Instance);
        coordinator.PublishDeviceState(new DeviceStatus(
            "Line.Plc", ConnectionState.Connected, 10, 10, true,
            new DeviceStats(1, 0, 0, 0, 1, "", 100)));

        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "runtime-page", "operator", "rev", [], ["devices"], false, 10), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = lease.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.True(await events.MoveNextAsync());
        var message = Assert.IsType<RealtimeOrderedEvent>(events.Current);
        Assert.Equal("devices", message.Event.Topic);
        Assert.Equal("device-state", message.Event.Kind);
        var state = Assert.IsType<RealtimeDeviceStatus>(message.Event.Payload);
        Assert.Equal("Line.Plc", state.Name);
        Assert.Equal("Connected", state.State);
    }

    [Fact]
    public async Task Mes_state_topic_does_not_receive_high_frequency_task_transitions()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings(), NullLogger<RealtimeStreamCoordinator>.Instance);

        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "runtime-page", "operator", "rev", [], ["mes-state"], false, 10), default);
        coordinator.PublishMesRuntimeEvent("task-1", "Task 1", new MesFlowEvent("trigger", "ok", "", DateTime.UtcNow, 1));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await using var events = lease.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await events.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task Mes_availability_topic_does_not_receive_task_states()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings(), NullLogger<RealtimeStreamCoordinator>.Instance);
        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "app", "operator", "rev", [], ["mes-availability"], false, 10), default);

        coordinator.PublishMesState(new MesTaskStatus(
            "task-1", "Task", "Group", true, 0, 0, "", "", [], []));
        coordinator.PublishMesAvailability(true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = lease.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        var message = Assert.IsType<RealtimeOrderedEvent>(events.Current);
        Assert.Equal("mes-availability", message.Event.Topic);
        Assert.Equal("mes-availability", message.Event.Kind);
    }

    [Fact]
    public async Task Clearing_MES_runtime_state_emits_task_tombstones_to_connected_status_clients()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings(), NullLogger<RealtimeStreamCoordinator>.Instance);
        coordinator.PublishMesState(new MesTaskStatus(
            "task-1", "Task", "Group", true, 0, 0, "", "", [], []));
        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "mes-page", "operator", "rev", [], ["mes-state"], false, 10), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = lease.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());

        coordinator.ClearMesRuntimeStates();

        Assert.True(await events.MoveNextAsync());
        var removed = Assert.IsType<RealtimeOrderedEvent>(events.Current);
        Assert.Equal("mes-task-removed", removed.Event.Kind);
        Assert.NotNull(removed.Event.Payload);
        Assert.Equal("task-1", removed.Event.Payload!.GetType().GetProperty("id")?.GetValue(removed.Event.Payload));
    }

    [Fact]
    public async Task MES_reload_coalesces_tombstone_with_the_new_state_for_a_surviving_task()
    {
        await using var hub = new DataHub(shards: 1, busCapacityPerShard: 128);
        await using var coordinator = new RealtimeStreamCoordinator(
            hub, "rev", new TagStreamSettings(), NullLogger<RealtimeStreamCoordinator>.Instance);
        coordinator.PublishMesState(new MesTaskStatus(
            "task-1", "Old", "Group", true, 0, 0, "", "", [], []));
        await using var lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "mes-page", "operator", "rev", [], ["mes-state"], false, 10), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = lease.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());

        coordinator.ClearMesRuntimeStates();
        coordinator.PublishMesState(new MesTaskStatus(
            "task-1", "New", "Group", true, 0, 0, "", "", [], []));

        Assert.True(await events.MoveNextAsync());
        var state = Assert.IsType<RealtimeOrderedEvent>(events.Current);
        Assert.Equal("mes-task-state", state.Event.Kind);
        Assert.Equal("New", Assert.IsType<MesTaskStatus>(state.Event.Payload).Name);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await events.MoveNextAsync().AsTask());
    }

    [Fact]
    public void Mes_engine_exposes_authoritative_state_and_ordered_runtime_events()
    {
        Assert.NotNull(typeof(MesUploadEngine).GetEvent("StateChanged"));
        Assert.NotNull(typeof(MesUploadEngine).GetEvent("RuntimeEventRaised"));
    }
}
