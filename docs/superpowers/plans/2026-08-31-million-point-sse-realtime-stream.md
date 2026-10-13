# IGW Million-Point SSE Realtime Stream Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace IGW Web UI's high-frequency full HTTP polling with one bounded, generation-aware SSE realtime path that supports a 1,000,000-point project, 500,000 active points, and 100 default realtime clients without affecting PLC reads, writes, or MES execution.

**Architecture:** `RuntimeEngine` and protocol drivers continue publishing authoritative state into `DataHub`; a single `RealtimeStreamCoordinator` owns one configurable-capacity point subscription, reference-counts the union of all client tag sets, coalesces state changes, preserves ordered runtime events, and isolates each SSE session behind bounded buffers. The REST layer validates and authorizes a POST stream before sending headers, while a standalone Vue stream client applies chunked snapshots atomically, handles deltas and reconnects without polling, and gives Quick Client, Monitor, Overview, and MES views shared realtime state.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, `System.Threading.Channels`, IGW `DataHub`, Vue 3, TypeScript, Pinia, Element Plus `el-table-v2`, Vitest, xUnit (local private test tree), PowerShell, Inno Setup.

**Spec:** `docs/superpowers/specs/2026-08-31-million-point-sse-realtime-stream-design.md`

## Global Constraints

- Low-frequency engineering configuration, log queries, history, exports, array slices, users, permissions, NIC settings, and gateway settings remain HTTP.
- SSE reads only `DataHub` and runtime snapshots; it must never call a PLC driver or change a device/session state.
- Existing PLC scanning, priority writes, write verification, retry, MES writeback, and protocol limits remain the only PLC I/O path.
- A project supports at most 1,000,000 configured points; the simultaneous active-point target is 500,000.
- `MaxTagsPerStream` defaults to 10,000 and is hard-clamped to 10,000.
- `MaxStreams` defaults to 100, is configurable from 1 to 200, and has a hard maximum of 200.
- Heartbeat defaults to 10 seconds and is configurable from 5 to 30 seconds; the browser declares the stream stale after 20 seconds without a frame.
- Periodic snapshots default to 60 seconds and are configurable from 15 to 300 seconds; no more than 4 snapshots run concurrently by default.
- Snapshot and delta frames contain at most 256 values and every SSE frame is at most 1 MiB.
- Exact `TagId` and `Handle` identity uses `StringComparer.Ordinal`; display names and case-insensitive equality are forbidden for routing or deduplication.
- State updates may coalesce to the latest value during overload; ordered MES/alarm/connection events may not coalesce.
- Normal-load targets are 50,000 DataHub changes/second sustained, 200,000 changes/second for a one-second burst, DataHub-to-SSE P95 at most 20 ms, internal P99 at most 50 ms, and browser apply P95 at most 20 ms.
- A slow or failed SSE client must not block DataHub, PLC scanning, writes, MES, another SSE client, or trigger a PLC reconnect.
- Engineering changes fully destroy the old generation, sessions, routing relationships, handles, demand counts, and browser caches before the new generation starts.
- UI writes carry the current project revision; stale UI writes return HTTP 409 `project_changed`. External clients without the optional revision header retain current behavior.
- Arrays remain one logical stream value and are not expanded into per-element stream subscriptions.
- No frontend realtime page silently falls back to `/api/v1/values`, `/api/v1/devices`, `/api/v1/mes`, or timeline polling.
- The repository intentionally ignores `tests/`; C# xUnit sources there are local validation evidence and must not be staged. Tracked Vitest/static tests and production changes are committed normally.
- Baseline before implementation is Git commit `516c922` (`chore: checkpoint pre-SSE implementation baseline`).

---

## File Structure

### Runtime contracts and configuration

- Modify `src/IGW.Abstractions/ProjectModel.cs`: production defaults and bounded realtime settings.
- Modify `src/IGW.Abstractions/DataHub.cs`: configurable internal point-subscription capacity contract.
- Modify `src/IGW.Core/DataHub.cs`: implement configurable subscription capacity and expose deterministic dropped-count snapshots.
- Modify `src/IGW.Core/IGatewayHost.cs`: expose the coordinator and immutable current project revision.

### Realtime coordinator

- Create `src/IGW.Core/RealtimeStreamModels.cs`: protocol-neutral session requests, state records, event records, batches, reset reasons, and metric snapshots.
- Create `src/IGW.Core/RealtimeStreamSession.cs`: one client's bounded state/event buffers, sequence, snapshot gate, cancellation, and cleanup.
- Create `src/IGW.Core/RealtimeStreamCoordinator.cs`: union subscription, exact routing, admission, coalescing, snapshots, generation replacement, metrics, and disposal.
- Create `src/IGW.Core/RealtimeLatencyHistogram.cs`: allocation-bounded P50/P95/P99 latency buckets.

### HTTP/SSE boundary

- Modify `src/IGW.Northbound.RestApi/TagValueStreamContracts.cs`: versioned realtime request and all wire event payloads.
- Modify `src/IGW.Northbound.RestApi/TagValueSseWriter.cs`: explicit snapshot begin/chunk/complete and all event types.
- Create `src/IGW.Northbound.RestApi/RealtimeStreamApi.cs`: admission, authorization, endpoint streaming loop, request limits, cancellation, and metrics endpoint.
- Create `src/IGW.Northbound.RestApi/ProjectTagScopeApi.cs`: paged tag definitions for the selected device/group without downloading the whole project.
- Modify `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`: recognize the read-only POST stream path.
- Modify `src/IGW.Northbound.RestApi/IgwRestApi.cs`: map the new APIs and enforce optional UI project revisions on writes.

### Runtime lifecycle and event sources

- Modify `src/IGW.Runtime/GatewayHost.cs`: create/rebind the coordinator, replace generations during reload, and publish runtime status/event changes.
- Modify `src/IGW.Northbound.Mes/MesUploadEngine.cs`: publish typed MES state and ordered transition events after the authoritative in-memory timeline is updated.
- Modify `src/IGW.Drivers.Mqtt/MqttDriver.cs`: implement `IDriverSubscriptionProvider` so broker messages reach the engine/DataHub immediately.

### Frontend stream and pages

- Create `src/IGW.WebUi/src/utils/sseParser.ts`: incremental UTF-8/SSE parser that survives arbitrary network chunk boundaries.
- Create `src/IGW.WebUi/src/utils/realtimeStream.ts`: fetch POST connection, snapshot state machine, sequence/revision validation, heartbeat timeout, backoff, and deterministic abort.
- Create `src/IGW.WebUi/src/stores/realtime.ts`: shared per-window store for values, device state, MES state/events, connection status, and revision.
- Modify `src/IGW.WebUi/src/api.ts`: scope API, revision-aware writes, auth header helper, and no Axios response buffering for SSE.
- Modify `src/IGW.WebUi/src/views/Points.vue`: range load, stream subscription, virtual rows, write/readback status, and removal of value polling.
- Modify `src/IGW.WebUi/src/views/Monitor.vue`: realtime device/diagnostic presentation while retaining user-triggered detail HTTP calls.
- Modify `src/IGW.WebUi/src/views/Overview.vue`: realtime device summaries while keeping low-frequency static health details HTTP-based.
- Modify `src/IGW.WebUi/src/views/Mes.vue`: realtime MES task status and HTTP-only history/log actions.
- Modify `src/IGW.WebUi/src/views/MesFlow.vue`: realtime node state and ordered transitions with no one-second polling.
- Modify `src/IGW.WebUi/src/components/MesTimeline.vue`: live events from the stream and explicit HTTP history refresh only.

### Verification and release

- Create `benchmarks/IGW.Realtime.Performance/IGW.Realtime.Performance.csproj`: tracked deterministic Simulator/DataHub load harness.
- Create `benchmarks/IGW.Realtime.Performance/Program.cs`: million-point registration, union-client load, sustained/burst changes, slow clients, reloads, and write-latency comparison.
- Modify `IGW.slnx`: include the performance harness.
- Create `docs/validation/2026-08-31-sse-realtime-validation.md`: commands, machine profile, metrics, pass/fail boundaries, and现场 PLC exclusions.
- Modify `docs/RELEASE_NOTES.md`: behavior, migration defaults, HTTP compatibility, and现场 verification boundary.

---

### Task 1: Lock production settings, exact identity, and configurable DataHub subscription capacity

**Files:**
- Modify: `src/IGW.Abstractions/ProjectModel.cs`
- Modify: `src/IGW.Abstractions/DataHub.cs`
- Modify: `src/IGW.Core/DataHub.cs`
- Modify: `src/IGW.Northbound.RestApi/TagValueStreamContracts.cs`
- Test locally: `tests/IGW.Tests/TagValueStreamContractsTests.cs`
- Create locally: `tests/IGW.Tests/DataHubRealtimeSubscriptionTests.cs`

**Interfaces:**
- Produces: `PointSubscriptionOptions(int QueueCapacity)` and `IDataHub.SubscribePoints(Func<TagChange, ValueTask>, string, PointSubscriptionOptions?)`.
- Produces: `TagStreamSettings` defaults and validation bounds used by every later task.
- Produces: `ValidatedRealtimeStreamRequest(ClientId, ProjectRevision, TagIds, Topics, IncludeSnapshot, HeartbeatSeconds)`.

- [ ] **Step 1: Write failing configuration and exact-identity tests**

```csharp
[Fact]
public void Defaults_match_production_capacity()
{
    var s = new TagStreamSettings();
    Assert.True(s.Enabled);
    Assert.Equal(10_000, s.MaxTagsPerStream);
    Assert.Equal(100, s.MaxStreams);
    Assert.Equal(60, s.SnapshotIntervalSeconds);
    Assert.Equal(262_144, s.CoordinatorQueueCapacity);
}

[Fact]
public void Validator_preserves_case_distinct_tag_ids()
{
    var result = RealtimeStreamRequestValidator.Validate(new()
    {
        ProtocolVersion = 1,
        ClientId = "qc-1",
        ProjectRevision = "rev-a",
        TagIds = ["Line.PLC.Tag", "Line.PLC.tag"]
    }, new TagStreamSettings());
    Assert.Equal(2, result.Request!.TagIds.Count);
}
```

- [ ] **Step 2: Run the targeted tests and verify the baseline fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~TagValueStreamContractsTests|FullyQualifiedName~DataHubRealtimeSubscriptionTests"`

Expected: FAIL because defaults are 4,096/32/disabled, case-insensitive deduplication collapses the IDs, and the configurable overload does not exist.

- [ ] **Step 3: Implement bounded defaults and the configurable overload**

```csharp
public sealed class PointSubscriptionOptions
{
    public int QueueCapacity { get; init; } = 10_000;
}

public sealed class TagStreamSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxTagsPerStream { get; set; } = 10_000;
    public int MaxStreams { get; set; } = 100;
    public int HeartbeatSeconds { get; set; } = 10;
    public int SnapshotIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentSnapshots { get; set; } = 4;
    public int DeltaBatchWindowMs { get; set; } = 20;
    public int CoordinatorQueueCapacity { get; set; } = 262_144;
}
```

Use `StringComparer.Ordinal`, clamp `MaxTagsPerStream` to 10,000, clamp streams to 200, and create the subscription channel with `Math.Clamp(options.QueueCapacity, 1_024, 1_048_576)` and `DropOldest` plus dropped-count accounting.

- [ ] **Step 4: Run focused and full backend tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~TagValueStream|FullyQualifiedName~DataHub"`

Expected: PASS, including two case-distinct IDs and a deterministic drop count when a frozen subscriber queue exceeds its configured capacity.

- [ ] **Step 5: Commit production changes**

```powershell
git add src/IGW.Abstractions/ProjectModel.cs src/IGW.Abstractions/DataHub.cs src/IGW.Core/DataHub.cs src/IGW.Northbound.RestApi/TagValueStreamContracts.cs
git commit -m "feat: define production realtime stream limits"
```

### Task 2: Build union subscriptions, admission, coalescing, and per-session isolation

**Files:**
- Create: `src/IGW.Core/RealtimeStreamModels.cs`
- Create: `src/IGW.Core/RealtimeStreamSession.cs`
- Create: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Create: `src/IGW.Core/RealtimeLatencyHistogram.cs`
- Create locally: `tests/IGW.Tests/RealtimeStreamCoordinatorTests.cs`

**Interfaces:**
- Consumes: configurable `IDataHub.SubscribePoints` from Task 1.
- Produces: `RealtimeStreamCoordinator.OpenAsync(RealtimeStreamOpenRequest, CancellationToken)` returning `RealtimeStreamLease`.
- Produces: `RealtimeStreamLease.ReadAllAsync(CancellationToken)` yielding `RealtimeOutboundMessage` and `DisposeAsync()` releasing all exact routes.
- Produces: `ReplaceGenerationAsync(string revision, IDataHub hub, IgwProject project, CancellationToken)`.

- [ ] **Step 1: Write failing union, final-value, ordered-event, and cleanup tests**

```csharp
[Fact]
public async Task Two_sessions_share_one_DataHub_registration_and_last_close_removes_it()
{
    await using var coordinator = Fixture.Coordinator();
    await using var a = await coordinator.OpenAsync(Fixture.Open("a", "Tag.A"), default);
    await using var b = await coordinator.OpenAsync(Fixture.Open("b", "Tag.A"), default);
    Assert.Equal(1, coordinator.SnapshotMetrics().UniqueTags);
    Assert.Equal(2, coordinator.SnapshotMetrics().SubscriptionRelationships);
    await a.DisposeAsync();
    Assert.Equal(1, coordinator.SnapshotMetrics().UniqueTags);
    await b.DisposeAsync();
    Assert.Equal(0, coordinator.SnapshotMetrics().UniqueTags);
}

[Fact]
public async Task State_coalesces_to_final_value_but_runtime_events_keep_order()
{
    await using var fixture = await Fixture.StartAsync(deltaBatchWindowMs: 20);
    fixture.Update("Tag.A", 1);
    fixture.Update("Tag.A", 2);
    fixture.PublishEvent("mes", 10);
    fixture.PublishEvent("mes", 11);
    var batch = await fixture.ReadBatchAsync();
    Assert.Equal(2, batch.Values.Single().Value);
    Assert.Equal([10L, 11L], batch.Events.Select(e => e.SourceSequence));
}
```

- [ ] **Step 2: Run tests and verify missing coordinator types fail compilation**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeStreamCoordinatorTests`

Expected: FAIL because coordinator/session models do not exist.

- [ ] **Step 3: Implement the coordinator and bounded session**

```csharp
public sealed record RealtimeStreamOpenRequest(
    string ClientId, string UserName, string ProjectRevision,
    IReadOnlyList<string> TagIds, IReadOnlyList<string> Topics,
    bool IncludeSnapshot, int HeartbeatSeconds);

public sealed class RealtimeStreamCoordinator : IAsyncDisposable
{
    public ValueTask<RealtimeStreamLease> OpenAsync(
        RealtimeStreamOpenRequest request, CancellationToken cancellationToken);
    public ValueTask ReplaceGenerationAsync(
        string revision, IDataHub hub, IgwProject project, CancellationToken cancellationToken);
    public void PublishDeviceState(RealtimeDeviceState state);
    public void PublishMesState(RealtimeMesState state);
    public void PublishRuntimeEvent(RealtimeRuntimeEvent runtimeEvent);
    public RealtimeStreamMetrics SnapshotMetrics();
}
```

Use one coordinator-owned point subscription, an ordinal `Dictionary<string, RouteEntry>`, exact handle registration, one non-blocking ingress channel, up to eight workers, 10–20 ms dirty-handle coalescing, session-local dirty sets, a bounded ordered-event channel, and idempotent cleanup. A session queue overflow marks only that session `ResetRequired`; no coordinator callback awaits network I/O.

- [ ] **Step 4: Run coordinator tests plus DataHub demand tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeStreamCoordinatorTests|FullyQualifiedName~DeviceLoopScanDemandTests"`

Expected: PASS with one unique registration for duplicated client interest, ordinal routing, zero relationships after cancellation, final value retained under coalescing, and ordered events unchanged.

- [ ] **Step 5: Commit the coordinator**

```powershell
git add src/IGW.Core/RealtimeStreamModels.cs src/IGW.Core/RealtimeStreamSession.cs src/IGW.Core/RealtimeStreamCoordinator.cs src/IGW.Core/RealtimeLatencyHistogram.cs
git commit -m "feat: add shared realtime stream coordinator"
```

### Task 3: Complete the wire protocol and expose the POST SSE API

**Files:**
- Modify: `src/IGW.Northbound.RestApi/TagValueStreamContracts.cs`
- Modify: `src/IGW.Northbound.RestApi/TagValueSseWriter.cs`
- Create: `src/IGW.Northbound.RestApi/RealtimeStreamApi.cs`
- Modify: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify locally: `tests/IGW.Tests/TagValueSseWriterTests.cs`
- Create locally: `tests/IGW.Tests/RealtimeStreamApiTests.cs`

**Interfaces:**
- Consumes: `RealtimeStreamCoordinator.OpenAsync` and `RealtimeStreamLease.ReadAllAsync` from Task 2.
- Produces: `POST /api/v1/streams/realtime` and `GET /api/v1/streams/realtime/metrics`.
- Produces wire events: `hello`, `snapshot-begin`, `snapshot-chunk`, `snapshot-complete`, `tag-delta`, `device-state`, `mes-state`, `runtime-event`, `heartbeat`, `project-changed`, and `reset-required`.

- [ ] **Step 1: Write failing API tests for admission-before-headers and frame order**

```csharp
[Fact]
public async Task Stream_writes_hello_complete_snapshot_then_delta_with_monotonic_ids()
{
    using var client = await Fixture.StartAuthorizedAsync();
    using var response = await client.PostAsJsonAsync("/api/v1/streams/realtime", Fixture.Request("rev-1", "Tag.A"));
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
    var frames = await Fixture.ReadFramesAsync(response, 5);
    Assert.Equal(["hello", "snapshot-begin", "snapshot-chunk", "snapshot-complete", "tag-delta"], frames.Select(f => f.Event));
    Assert.True(frames.Select(f => f.Sequence).SequenceEqual(frames.Select(f => f.Sequence).Order()));
}

[Fact]
public async Task Unauthorized_branch_is_rejected_before_SSE_headers_without_echoing_tag_names()
{
    using var response = await Fixture.PostAsync(tags: ["Secret.PLC.Password"]);
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.DoesNotContain("Secret.PLC.Password", await response.Content.ReadAsStringAsync());
}
```

- [ ] **Step 2: Run API tests and verify endpoint-not-found/order failures**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeStreamApiTests|FullyQualifiedName~TagValueSseWriterTests"`

Expected: FAIL because the realtime endpoint and complete event set are absent.

- [ ] **Step 3: Implement validation, authorization, response configuration, and the streaming loop**

```csharp
public static class RealtimeStreamApi
{
    public static void MapRealtimeStreamApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        app.MapPost(PublicPlcApiPolicy.RealtimeStreamPath, StreamAsync)
            .DisableAntiforgery();
        app.MapGet(PublicPlcApiPolicy.RealtimeStreamMetricsPath,
            () => Results.Ok(host.RealtimeStreams.SnapshotMetrics()));
    }
}
```

Read and validate at most 8 MiB before `TagValueSseWriter.ConfigureResponse`, authorize every requested tag/topic, reject stale revision with 409, reject capacity with 429, report missing authorized tags only in `hello`, disable buffering/compression/cache, write at most 256 values per frame, flush once per frame, and dispose only the current lease on cancellation/network failure.

- [ ] **Step 4: Run API, policy, and writer tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeStream|FullyQualifiedName~TagValueSse|FullyQualifiedName~PublicPlcApiPolicy"`

Expected: PASS for auth, request limits, frame size, sequence, heartbeat, cancellation, capacity, missing tags, and no response compression.

- [ ] **Step 5: Commit the endpoint**

```powershell
git add src/IGW.Northbound.RestApi/TagValueStreamContracts.cs src/IGW.Northbound.RestApi/TagValueSseWriter.cs src/IGW.Northbound.RestApi/RealtimeStreamApi.cs src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Runtime/Program.cs
git commit -m "feat: expose authenticated realtime SSE endpoint"
```

### Task 4: Make snapshots atomic, periodic, staggered, and recoverable

**Files:**
- Modify: `src/IGW.Core/RealtimeStreamSession.cs`
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Modify: `src/IGW.Core/RealtimeStreamModels.cs`
- Modify: `src/IGW.Northbound.RestApi/TagValueSseWriter.cs`
- Create locally: `tests/IGW.Tests/RealtimeSnapshotTests.cs`

**Interfaces:**
- Consumes: session outbound messages from Task 2.
- Produces: `RealtimeSnapshotBegin`, `RealtimeSnapshotChunk`, `RealtimeSnapshotComplete` and buffered post-snapshot deltas/events.
- Produces: `RealtimeStreamLease.RequestReset(string reason)` for manual refresh and recoverable overflow.

- [ ] **Step 1: Write failing tests for subscribe-before-snapshot, periodic staggering, and overflow reset**

```csharp
[Fact]
public async Task Change_during_snapshot_is_sent_after_complete()
{
    await using var f = await Fixture.StartSnapshotAsync(chunkSize: 1);
    await f.ReadEventAsync("snapshot-begin");
    f.Update("Tag.A", 7);
    var names = await f.ReadEventNamesThroughAsync("tag-delta");
    Assert.True(names.IndexOf("snapshot-complete") < names.IndexOf("tag-delta"));
    Assert.Equal(7, f.LastDelta("Tag.A").Value);
}
```

- [ ] **Step 2: Run snapshot tests and verify missing state-machine behavior fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeSnapshotTests`

Expected: FAIL because snapshots are not yet separate begin/chunk/complete phases and periodic reset scheduling is absent.

- [ ] **Step 3: Implement snapshot gates and scheduling**

```csharp
private async Task SendSnapshotAsync(RealtimeStreamSession session, CancellationToken ct)
{
    await _snapshotSlots.WaitAsync(ct);
    try
    {
        var capture = session.BeginSnapshot();
        await session.EnqueueSnapshotAsync(ReadChunks(session.Handles, 256), capture, ct);
        session.CompleteSnapshot(capture);
    }
    finally { _snapshotSlots.Release(); }
}
```

Register routes before reading any value, buffer dirty handles and ordered events while chunks are emitted, use a deterministic `StreamId` phase offset, allow at most `MaxConcurrentSnapshots`, continue normal deltas while another session waits, and emit `reset-required` rather than `snapshot-complete` if the session buffer overflows.

- [ ] **Step 4: Run snapshot, coordinator, and writer tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeSnapshotTests|FullyQualifiedName~RealtimeStreamCoordinatorTests|FullyQualifiedName~TagValueSseWriterTests"`

Expected: PASS with complete snapshots, post-snapshot correction, four-slot concurrency, staggered periodic starts, and reset on incomplete state.

- [ ] **Step 5: Commit snapshot consistency**

```powershell
git add src/IGW.Core/RealtimeStreamSession.cs src/IGW.Core/RealtimeStreamCoordinator.cs src/IGW.Core/RealtimeStreamModels.cs src/IGW.Northbound.RestApi/TagValueSseWriter.cs
git commit -m "feat: add atomic scheduled realtime snapshots"
```

### Task 5: Integrate engineering generations and stale-write protection

**Files:**
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Northbound.RestApi/ArrayValueApi.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Create locally: `tests/IGW.Tests/RealtimeProjectGenerationTests.cs`
- Create: `src/IGW.WebUi/src/api.project-revision.spec.ts`

**Interfaces:**
- Produces: `IGatewayHost.ProjectRevision` and `IGatewayHost.RealtimeStreams`.
- Consumes: `ReplaceGenerationAsync` from Task 2.
- Produces: optional request header `X-Igw-Project-Revision` on UI scalar and array writes; mismatch returns `{ error: "project_changed", projectRevision }` with status 409.

- [ ] **Step 1: Write failing generation teardown and stale-write tests**

```csharp
[Fact]
public async Task Reload_clears_old_sessions_handles_and_demand_before_new_generation()
{
    await using var host = await Fixture.HostAsync(Fixture.Project("Tag.Old"));
    await using var old = await host.RealtimeStreams.OpenAsync(Fixture.Open(host.ProjectRevision, "Tag.Old"), default);
    await host.ReloadAsync(Fixture.Project("Tag.New"));
    Assert.Equal(0, host.RealtimeStreams.SnapshotMetrics().OldGenerationRelationships);
    Assert.Equal("project-changed", (await old.ReadAllAsync(default).FirstAsync()).Kind);
}
```

- [ ] **Step 2: Run backend and frontend revision tests and verify failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeProjectGenerationTests`

Run: `pnpm --dir src/IGW.WebUi test -- api.project-revision.spec.ts`

Expected: FAIL because host lifecycle and UI write preconditions are not wired.

- [ ] **Step 3: Implement generation replacement and optional write preconditions**

```csharp
public string ProjectRevision => JsonHash.Of(Project);
public RealtimeStreamCoordinator RealtimeStreams { get; }

private IResult? RejectStaleUiWrite(HttpRequest request)
{
    var expected = request.Headers["X-Igw-Project-Revision"].FirstOrDefault();
    return string.IsNullOrEmpty(expected) || expected == host.ProjectRevision
        ? null
        : Results.Json(new { error = "project_changed", projectRevision = host.ProjectRevision }, statusCode: 409);
}
```

At reload start, freeze/notify/close the old generation and await route cleanup before stopping old engine/Hub; after the new Hub and project are ready, bind the new generation. Do not reuse handles. Keep legacy external write behavior unchanged when the header is absent.

- [ ] **Step 4: Run lifecycle, reload, write, and frontend revision tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeProjectGenerationTests|FullyQualifiedName~RuntimeEngineReconnectTests|FullyQualifiedName~IgwRestWriteStatusTests"`

Run: `pnpm --dir src/IGW.WebUi test -- api.project-revision.spec.ts`

Expected: PASS; old relationships/demand are zero, delayed old frames are rejected, stale UI writes are 409, and headerless external writes preserve existing semantics.

- [ ] **Step 5: Commit lifecycle safety**

```powershell
git add src/IGW.Core/IGatewayHost.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Northbound.RestApi/ArrayValueApi.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/api.project-revision.spec.ts
git commit -m "feat: rebuild realtime streams on project changes"
```

### Task 6: Implement the frontend parser, atomic store, timeout, and reconnect policy

**Files:**
- Create: `src/IGW.WebUi/src/utils/sseParser.ts`
- Create: `src/IGW.WebUi/src/utils/sseParser.spec.ts`
- Create: `src/IGW.WebUi/src/utils/realtimeStream.ts`
- Create: `src/IGW.WebUi/src/utils/realtimeStream.spec.ts`
- Create: `src/IGW.WebUi/src/stores/realtime.ts`
- Create: `src/IGW.WebUi/src/stores/realtime.spec.ts`
- Modify: `src/IGW.WebUi/src/api.ts`

**Interfaces:**
- Produces: `parseSseChunks(chunks: AsyncIterable<Uint8Array>): AsyncIterable<SseFrame>`.
- Produces: `RealtimeStreamClient.start(request)`, `requestSnapshot()`, `stop()`, and state `connecting | live | reconnecting | stale | resyncing | project-changed | capacity-limited`.
- Produces: a Pinia store keyed by exact `TagId`, `deviceName`, `taskId`, and monotonically ordered event sequence.

- [ ] **Step 1: Write failing parser and atomic-snapshot tests**

```ts
it('parses utf8 and SSE fields split across arbitrary chunks', async () => {
  const frames = await collect(parseSseChunks(chunks('event: tag-d', 'elta\nid: s:2\ndata: {"值":"中', '文"}\n\n')))
  expect(frames).toEqual([{ event: 'tag-delta', id: 's:2', data: '{"值":"中文"}' }])
})

it('publishes no partial snapshot and replays buffered deltas after complete', () => {
  const store = createRealtimeStore('rev-1')
  store.apply(begin('snap-1', 2))
  store.apply(chunk('snap-1', 0, [{ id: 'A', value: 1 }]))
  store.apply(delta(4, [{ id: 'A', value: 2 }]))
  expect(store.values.size).toBe(0)
  store.apply(chunk('snap-1', 1, [{ id: 'B', value: 3 }]))
  store.apply(complete('snap-1'))
  expect(store.values.get('A')?.value).toBe(2)
})
```

- [ ] **Step 2: Run tests and verify modules are missing**

Run: `pnpm --dir src/IGW.WebUi test -- sseParser.spec.ts realtimeStream.spec.ts stores/realtime.spec.ts`

Expected: FAIL because parser, client, and store do not exist.

- [ ] **Step 3: Implement fetch POST streaming and deterministic state transitions**

```ts
export class RealtimeStreamClient {
  start(request: RealtimeStreamRequest): void
  requestSnapshot(): void
  stop(): void
}

const RETRY_MS = [1_000, 2_000, 5_000, 10_000, 30_000]
const STALE_AFTER_MS = 20_000
```

Use `TextDecoder(..., { stream: true })`, preserve incomplete lines, validate `StreamId:Sequence`, discard old stream/revision frames, stage snapshots in a temporary `Map`, buffer deltas during snapshots, abort after 20 seconds without any frame, add bounded jitter to retries, and never call an HTTP polling fallback.

- [ ] **Step 4: Run all new frontend tests and typecheck**

Run: `pnpm --dir src/IGW.WebUi test -- sseParser.spec.ts realtimeStream.spec.ts stores/realtime.spec.ts`

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: PASS, including split UTF-8, sequence gaps, timeout, backoff, cancellation, old-revision discard, snapshot rollback, and atomic swap.

- [ ] **Step 5: Commit the shared frontend client**

```powershell
git add src/IGW.WebUi/src/utils/sseParser.ts src/IGW.WebUi/src/utils/sseParser.spec.ts src/IGW.WebUi/src/utils/realtimeStream.ts src/IGW.WebUi/src/utils/realtimeStream.spec.ts src/IGW.WebUi/src/stores/realtime.ts src/IGW.WebUi/src/stores/realtime.spec.ts src/IGW.WebUi/src/api.ts
git commit -m "feat: add atomic frontend realtime stream client"
```

### Task 7: Migrate Quick Client to scoped definitions, virtual rows, and SSE readback

**Files:**
- Create: `src/IGW.Northbound.RestApi/ProjectTagScopeApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/views/Points.vue`
- Create: `src/IGW.WebUi/src/views/Points.realtime.spec.ts`
- Modify: `src/IGW.WebUi/src/views/Points.suspend.test.cjs`
- Create locally: `tests/IGW.Tests/ProjectTagScopeApiTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/project/tag-scope?root=<exact path>&cursor=<token>&limit=1000` returning revision, groups, definitions, next cursor, and total count.
- Consumes: shared realtime store/client from Task 6.
- Produces: manual refresh via stream resync and revision-aware HTTP write followed by SSE actual-value confirmation.

- [ ] **Step 1: Write failing scope and page tests**

```ts
it('subscribes only current group, renders virtual rows, and never polls values', async () => {
  mountPoints({ q: 'PLC8.PLC8.OP200Data' })
  await flushPromises()
  expect(api.projectTagScope).toHaveBeenCalledWith(expect.objectContaining({ root: 'PLC8.PLC8.OP200Data' }))
  expect(realtime.start).toHaveBeenCalledWith(expect.objectContaining({ tagIds: expect.any(Array) }))
  expect(api.values).not.toHaveBeenCalled()
  expect(wrapper.findComponent({ name: 'ElTableV2' }).exists()).toBe(true)
})
```

- [ ] **Step 2: Run scope/page tests and verify polling remains**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~ProjectTagScopeApiTests`

Run: `pnpm --dir src/IGW.WebUi test -- Points.realtime.spec.ts`

Expected: FAIL because Points loads the whole project, calls `/api/v1/values`, and uses a normal table.

- [ ] **Step 3: Implement paged definitions and virtual incremental rows**

```ts
const columns = [
  { key: 'itemId', dataKey: 'itemId', title: '项 ID', width: 360 },
  { key: 'dataType', dataKey: 'dataType', title: '数据类型', width: 120 },
  { key: 'value', dataKey: 'value', title: '值', width: 220 },
  { key: 'quality', dataKey: 'quality', title: 'Quality', width: 120 },
]

function pickGroup(group: GroupDefinition) {
  realtime.stop()
  realtime.start({ projectRevision: revision.value, tagIds: group.scalarTagIds, topics: [] })
}
```

Load only the requested tree range, cap the active group at 10,000 scalar logical points, keep arrays on slice HTTP, bind `el-table-v2` rows by stable exact ID, update only touched row objects, expose stream state/staleness, make refresh request a new snapshot, and after writes wait for the SSE value/quality rather than applying an optimistic Good value.

- [ ] **Step 4: Run page tests, frontend suite, and typecheck**

Run: `pnpm --dir src/IGW.WebUi test -- Points.realtime.spec.ts Points.suspend.test.cjs`

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: PASS with 10,000 virtual rows, no `/values` timer, deterministic abort on group/window change, and actual SSE write readback.

- [ ] **Step 5: Commit Quick Client migration**

```powershell
git add src/IGW.Northbound.RestApi/ProjectTagScopeApi.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/views/Points.vue src/IGW.WebUi/src/views/Points.realtime.spec.ts src/IGW.WebUi/src/views/Points.suspend.test.cjs
git commit -m "feat: stream Quick Client values with virtual rows"
```

### Task 8: Stream device status, monitor summaries, and MES state/events

**Files:**
- Modify: `src/IGW.Core/RealtimeStreamModels.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.WebUi/src/views/Monitor.vue`
- Modify: `src/IGW.WebUi/src/views/Overview.vue`
- Modify: `src/IGW.WebUi/src/views/Mes.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/components/MesTimeline.vue`
- Create: `src/IGW.WebUi/src/views/realtime-pages.spec.ts`
- Create locally: `tests/IGW.Tests/RealtimeRuntimeTopicsTests.cs`

**Interfaces:**
- Produces: `RuntimeEngine.DeviceStatusChanged` state callback and `MesUploadEngine.StateChanged` / `RuntimeEventRaised` callbacks.
- Consumes: `PublishDeviceState`, `PublishMesState`, and `PublishRuntimeEvent` from Task 2.
- Consumes frontend topics `devices`, `mes`, and `mes-task:<task-id>`.

- [ ] **Step 1: Write failing tests for authoritative state and ordered MES transitions**

```csharp
[Fact]
public async Task Mes_timeline_is_persisted_before_event_is_published()
{
    await using var f = await Fixture.MesAsync();
    await f.AdvanceNodeAsync("task-1", "upload", "ok");
    var evt = await f.ReadRuntimeEventAsync();
    Assert.Contains(f.Engine.Timeline("task-1"), x => x.NodeId == evt.NodeId && x.Timestamp == evt.Timestamp);
}
```

```ts
it('shows stream state and creates no devices/mes/timeline interval', async () => {
  mountRealtimePages()
  await flushPromises()
  expect(api.devices).not.toHaveBeenCalled()
  expect(api.mes).not.toHaveBeenCalled()
  expect(api.mesTimeline).not.toHaveBeenCalled()
})
```

- [ ] **Step 2: Run runtime-topic and page tests and verify polling calls fail assertions**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeRuntimeTopicsTests`

Run: `pnpm --dir src/IGW.WebUi test -- realtime-pages.spec.ts`

Expected: FAIL because authoritative callbacks/topics are absent and pages still use intervals.

- [ ] **Step 3: Publish state changes and migrate the pages**

```csharp
public event Action<DeviceStatus>? DeviceStatusChanged;
public event Action<MesTaskStatus>? StateChanged;
public event Action<RealtimeRuntimeEvent>? RuntimeEventRaised;
```

Raise device state only from engine connection/diagnostic transitions. In MES `Mark`, append the authoritative `MesFlowEvent` first, then publish the same typed event. Page disconnection marks cached data stale and never changes a real device to offline. Keep explicit history/detail/log HTTP actions, but remove the recurring `api.devices()`, `api.mes()`, and `api.mesTimeline()` calls.

- [ ] **Step 4: Run page/runtime tests and typecheck**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RealtimeRuntimeTopicsTests|FullyQualifiedName~MesResponseWritebackTests|FullyQualifiedName~ConnectionDiagnosticsTests"`

Run: `pnpm --dir src/IGW.WebUi test -- realtime-pages.spec.ts MesFlow.runtime-state.test.cjs Monitor.ui.test.cjs`

Run: `pnpm --dir src/IGW.WebUi typecheck`

Expected: PASS; MES event order is preserved, device state stays authoritative, and no realtime page polling interval remains.

- [ ] **Step 5: Commit page/topic migration**

```powershell
git add src/IGW.Core/RealtimeStreamModels.cs src/IGW.Core/Engine.cs src/IGW.Runtime/GatewayHost.cs src/IGW.Northbound.Mes/MesUploadEngine.cs src/IGW.WebUi/src/views/Monitor.vue src/IGW.WebUi/src/views/Overview.vue src/IGW.WebUi/src/views/Mes.vue src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/components/MesTimeline.vue src/IGW.WebUi/src/views/realtime-pages.spec.ts
git commit -m "feat: stream device and MES runtime state"
```

### Task 9: Push MQTT broker changes directly into the existing engine subscription path

**Files:**
- Modify: `src/IGW.Drivers.Mqtt/MqttDriver.cs`
- Create locally: `tests/IGW.Tests/MqttDriverSubscriptionTests.cs`
- Modify locally: `tests/IGW.Tests/DeviceLoopSubscriptionTests.cs`

**Interfaces:**
- Produces: `MqttDeviceDriver : IDriverSubscriptionProvider` with `SubscribeAsync(IReadOnlyList<TagBinding>, Action<IReadOnlyList<TagSample>>, CancellationToken)`.
- Consumes: the existing engine `IDriverSubscriptionProvider` path; MQTT does not know about SSE or DataHub.

- [ ] **Step 1: Write failing subscription tests**

```csharp
[Fact]
public async Task Broker_message_immediately_invokes_native_subscription_callback()
{
    await using var f = await MqttFixture.ConnectAsync();
    var samples = new TaskCompletionSource<IReadOnlyList<TagSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var lease = await f.Driver.SubscribeAsync([f.Binding("plant/temp")], samples.SetResult, default);
    await f.PublishAsync("plant/temp", "42");
    Assert.Equal(42, Convert.ToInt32((await samples.Task.WaitAsync(TimeSpan.FromSeconds(2))).Single().Value.Value));
}
```

- [ ] **Step 2: Run MQTT subscription tests and verify interface failure**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~MqttDriverSubscriptionTests|FullyQualifiedName~DeviceLoopSubscriptionTests"`

Expected: FAIL because `MqttDeviceDriver` advertises subscription support but does not implement the provider.

- [ ] **Step 3: Implement reference-counted topic leases and callback decoupling**

```csharp
public Task<IDriverSubscription> SubscribeAsync(
    IReadOnlyList<TagBinding> tags,
    Action<IReadOnlyList<TagSample>> onSamples,
    CancellationToken ct)
```

Maintain exact topic-to-binding registrations and lease reference counts, decode once per incoming message, enqueue callbacks on a bounded driver-local channel, and remove only the disposed lease's registrations. Do not await engine/DataHub/SSE work in `ApplicationMessageReceivedAsync`, do not close the shared MQTT client on lease disposal, and retain periodic `ReadAsync` for normal business/scan semantics.

- [ ] **Step 4: Run MQTT, engine subscription, and reconnect tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~MqttDriverSubscriptionTests|FullyQualifiedName~DeviceLoopSubscriptionTests|FullyQualifiedName~RuntimeEngineReconnectTests"`

Expected: PASS with immediate messages, final values, independent lease cleanup, and no shared connection closure.

- [ ] **Step 5: Commit MQTT native change delivery**

```powershell
git add src/IGW.Drivers.Mqtt/MqttDriver.cs
git commit -m "feat: deliver MQTT changes through native subscriptions"
```

### Task 10: Add observability and a reproducible million-point performance/fault harness

**Files:**
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Modify: `src/IGW.Core/RealtimeLatencyHistogram.cs`
- Modify: `src/IGW.Northbound.RestApi/RealtimeStreamApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Create: `benchmarks/IGW.Realtime.Performance/IGW.Realtime.Performance.csproj`
- Create: `benchmarks/IGW.Realtime.Performance/Program.cs`
- Modify: `IGW.slnx`
- Create locally: `tests/IGW.Tests/RealtimeMetricsTests.cs`

**Interfaces:**
- Produces: allocation-bounded counters/histograms for streams, unique tags, relationships, ingress, coalesced changes, deliveries, queue depths/drops, snapshots, resets, bytes, and P50/P95/P99 latency.
- Produces performance command switches `--tags`, `--active`, `--clients`, `--tags-per-client`, `--rate`, `--burst-rate`, `--duration`, `--slow-client`, and `--reload-every`.

- [ ] **Step 1: Write failing metric reset/isolation tests**

```csharp
[Fact]
public async Task Slow_session_increments_only_its_reset_and_does_not_raise_other_latency_or_device_faults()
{
    await using var f = await Fixture.StartAsync(twoSessions: true, slowFirst: true);
    await f.PublishBurstAsync(20_000);
    var m = f.Coordinator.SnapshotMetrics();
    Assert.True(m.Sessions.Single(x => x.ClientId == "slow").ResetCount > 0);
    Assert.Equal(0, m.Sessions.Single(x => x.ClientId == "fast").ResetCount);
    Assert.Equal(0, f.DeviceReconnectCount);
}
```

- [ ] **Step 2: Run metrics tests and verify fields/harness are absent**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeMetricsTests`

Expected: FAIL because required counters and per-session snapshots do not exist.

- [ ] **Step 3: Implement bounded metrics and the harness**

```csharp
var options = PerformanceOptions.Parse(args);
await Scenario.RegisterPointsAsync(options.Tags);
await Scenario.OpenClientsAsync(options.Clients, options.TagsPerClient);
await Scenario.RunSteadyAsync(options.Rate, options.Duration);
await Scenario.RunBurstAsync(options.BurstRate, TimeSpan.FromSeconds(1));
await Scenario.AssertFinalSnapshotsAsync();
```

Use fixed histograms rather than per-event samples. The harness prints JSON containing machine/runtime metadata, allocated bytes, working set, GC counts, P50/P95/P99, drops, resets, session cleanup, engine scan jitter, write P50/P95/P99 with and without SSE, and final-value mismatches. It exits nonzero on threshold failure.

- [ ] **Step 4: Run focused metrics tests and short smoke load**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~RealtimeMetricsTests`

Run: `dotnet run --project benchmarks/IGW.Realtime.Performance/IGW.Realtime.Performance.csproj -c Release -- --tags 100000 --active 50000 --clients 10 --tags-per-client 1000 --rate 50000 --burst-rate 200000 --duration 30`

Expected: PASS, zero normal-load drops, all sessions cleaned, no device reconnect, and zero final-value mismatches.

- [ ] **Step 5: Commit observability and harness**

```powershell
git add src/IGW.Core/RealtimeStreamCoordinator.cs src/IGW.Core/RealtimeLatencyHistogram.cs src/IGW.Northbound.RestApi/RealtimeStreamApi.cs src/IGW.Northbound.RestApi/IgwRestApi.cs benchmarks/IGW.Realtime.Performance/IGW.Realtime.Performance.csproj benchmarks/IGW.Realtime.Performance/Program.cs IGW.slnx
git commit -m "test: add realtime stream load and fault harness"
```

### Task 11: Run complete regression, long-load acceptance, runtime browser proof, and release packaging

**Files:**
- Create: `docs/validation/2026-08-31-sse-realtime-validation.md`
- Modify: `docs/RELEASE_NOTES.md`
- Modify: `installer/version.txt` only through `installer/bump_version.ps1`
- Output (ignored): `installer/out/IGW-Setup-<version>.exe`

**Interfaces:**
- Consumes all earlier tasks.
- Produces evidence-separated results for source review, unit/integration tests, frontend build/typecheck, 1M-point load, browser behavior, local Simulator runtime, packaging, and现场 PLC validation not performed locally.

- [ ] **Step 1: Run backend, frontend, and static regression suites**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore --verbosity minimal`

Run: `pnpm --dir src/IGW.WebUi test`

Run: `pnpm --dir src/IGW.WebUi typecheck`

Run: `pnpm --dir src/IGW.WebUi build`

Expected: all commands exit 0; test totals and durations are copied into the validation document.

- [ ] **Step 2: Run full capacity, sustained, burst, overlap, reload, and slow-client scenarios**

Run: `dotnet run --project benchmarks/IGW.Realtime.Performance/IGW.Realtime.Performance.csproj -c Release -- --tags 1000000 --active 500000 --clients 100 --tags-per-client 10000 --rate 50000 --burst-rate 200000 --duration 1800 --slow-client 1 --reload-every 300`

Expected: DataHub-to-send P95 <= 20 ms and P99 <= 50 ms during normal load; zero normal-load drops; final mismatches zero after overload reset; old sessions/routes/demand zero after each reload; no PLC/device reconnect; stable memory after warmup; write latency regression <= 5%.

- [ ] **Step 3: Start the Simulator runtime and verify the real browser path**

Run: `dotnet run --project src/IGW.Runtime/IGW.Runtime.csproj -c Release -- --project examples/demo.igw.json`

Verify in the app browser:

```text
Quick Client Network: one POST /api/v1/streams/realtime, no repeating /api/v1/values
Monitor/Overview Network: no repeating /api/v1/devices or /api/v1/mes
Disconnect: status becomes 正在重连 then 数据已过期 without displaying device offline
Reconnect: a complete snapshot arrives before status returns to 实时
Project edit: old rows disappear, write is disabled during rebuild, new revision opens a new stream
Write: HTTP write returns, then the row changes only when SSE carries the actual DataHub readback
```

Expected: every visible assertion is captured with timestamped screenshots/network evidence in the validation document. This proves local Simulator/browser behavior, not real PLC timing.

- [ ] **Step 4: Document evidence and explicit现场 boundary**

Record exact commands, commits, machine CPU/RAM, .NET/Node versions, result JSON, screenshots, installer hash, and any skipped checks. State explicitly that OPC UA, S7/S7 Plus, Modbus, MC, FINS, and EtherNet/IP scan/write latency on production PLC hardware remains a separate现场 acceptance item even when internal tests pass.

- [ ] **Step 5: Bump version, build installer, and verify artifact**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer/bump_version.ps1`

Run: `cmd /c installer\build.bat`

Run:

```powershell
$version = (Get-Content installer/version.txt -Raw).Trim()
$installer = Resolve-Path "installer/out/IGW-Setup-$version.exe"
Get-Item $installer | Select-Object FullName,Length,LastWriteTime
Get-FileHash $installer -Algorithm SHA256
```

Expected: version rollover never creates a `.100` patch; build exits 0; one matching installer exists and its version, byte size, timestamp, and SHA-256 are recorded.

- [ ] **Step 6: Commit documentation and version metadata**

```powershell
git add docs/validation/2026-08-31-sse-realtime-validation.md docs/RELEASE_NOTES.md installer/version.txt
git commit -m "release: validate million-point SSE realtime pipeline"
```

---

## Self-Review Result

- Spec coverage: all confirmed limits, union subscription, coalescing, ordered events, initial/periodic snapshot, reconnect, stale UI, generation teardown, stale-write protection, MQTT direct delivery, observability, capacity, fault injection, and packaging requirements map to Tasks 1–11.
- Physical boundary: the plan does not claim SSE changes PLC physics and keeps现场 protocol acceptance separate from Simulator/DataHub evidence.
- Placeholder scan: the plan contains no deferred implementation markers; every task has concrete files, signatures, tests, commands, expected results, and a commit boundary.
- Type consistency: Tasks 2–10 consistently use `RealtimeStreamCoordinator`, `RealtimeStreamOpenRequest`, `RealtimeStreamLease`, `RealtimeOutboundMessage`, `ProjectRevision`, and exact ordinal `TagId` identity.
- Safety: baseline commit `516c922` provides the requested rollback point, and every implementation task ends in an independently reviewable commit.
