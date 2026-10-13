# SSE Production Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden IGW realtime delivery so one-million-point projects and one hundred concurrent SSE streams remain bounded, reconnect cleanly, and do not disturb PLC scan/write or unrelated runtime components.

**Architecture:** Keep DataHub as the sole boundary between PLC execution and browser delivery. Bound and time-limit the HTTP writer, separate the full project revision from the realtime topology revision, batch coordinator fan-out per session, and make browser application/rendering coalesced and task-scoped.

**Tech Stack:** .NET 10, ASP.NET Core/Kestrel, xUnit, Vue 3, Pinia, TypeScript, Vitest.

**Spec:** `docs/superpowers/specs/2026-09-01-sse-production-hardening.md`

## Global Constraints

- Preserve all existing uncommitted SSE and MES UI work in the linked worktree.
- Do not add a second PLC read loop or change the existing write queue/read-back confirmation path.
- Keep the per-stream tag limit at 10,000, default stream limit at 100, snapshot chunk size at 256, and maximum SSE event size at 1 MiB.
- Low-frequency pages continue using HTTP.

---

### Task 1: Bounded SSE frames and slow-client timeout

**Files:**
- Modify: `tests/IGW.Tests/TagValueSseWriterTests.cs`
- Modify: `tests/IGW.Tests/RealtimeStreamApiTests.cs`
- Modify: `src/IGW.Northbound.RestApi/TagValueSseWriter.cs`
- Modify: `src/IGW.Northbound.RestApi/RealtimeStreamApi.cs`

**Interfaces:**
- Consumes: `RealtimeStateBatch`, `TagStreamDelta`, `TagValueSseWriter.MaxEventBytes`.
- Produces: bounded multi-frame delta writing and configurable per-frame write timeout.

- [ ] Write a failing writer test proving 600 values become three ordered `tag-delta` events of at most 256 values each.
- [ ] Run the focused writer test and confirm it fails because only one delta frame is currently written.
- [ ] Add a failing stream test whose output blocks and assert the writer throws `TimeoutException` and observes cancellation.
- [ ] Run the timeout test and confirm it fails because no writer deadline exists.
- [ ] Implement count/byte-aware recursive delta splitting and one linked cancellation budget per frame.
- [ ] Run writer and realtime API tests and confirm they pass.

### Task 2: Browser connection lifecycle and retry policy

**Files:**
- Modify: `src/IGW.WebUi/src/utils/sseParser.ts`
- Modify: `src/IGW.WebUi/src/utils/realtimeStream.ts`
- Modify: `src/IGW.WebUi/src/utils/realtimeStream.spec.ts`
- Modify: `src/IGW.WebUi/src/utils/sseParser.spec.ts`

**Interfaces:**
- Consumes: Fetch `Response.body`, SSE event IDs, HTTP status codes.
- Produces: guaranteed response-body cancellation, terminal authentication/configuration states, and slow capacity retry.

- [ ] Add a failing test proving a sequence gap cancels the original response body before the next fetch.
- [ ] Add failing status tests for terminal 400/401/403 and retryable 429.
- [ ] Implement reader cancellation and abort in every non-natural stream exit.
- [ ] Implement classified retry behavior and reset backoff after a successful hello.
- [ ] Run the focused Vitest files and confirm they pass.

### Task 3: Realtime topology revision and targeted configuration reload

**Files:**
- Create: `src/IGW.Core/RealtimeProjectRevision.cs`
- Create: `tests/IGW.Tests/RealtimeProjectRevisionTests.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/ProjectTagScopeApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/App.vue`
- Modify: `src/IGW.WebUi/src/views/Points.vue`

**Interfaces:**
- Consumes: full `ProjectRevision`, channel/tag topology, stream policy, security read branches, MES upload settings.
- Produces: `RealtimeRevision`, `UpdateMesUploadAsync`, and a tag-scope response containing both full and realtime revisions.

- [ ] Add failing fingerprint tests proving MES/log changes keep the realtime revision while tag/security/stream changes alter it.
- [ ] Add a failing tag-scope API test proving both revisions are returned and paging cursors use the realtime revision.
- [ ] Implement the fingerprint and expose it through `IGatewayHost`/`GatewayHost`.
- [ ] Add targeted MES engine replacement and remove realtime retirement from log-retention updates.
- [ ] Retire realtime streams only when the realtime revision changes; continue using the full revision for optimistic writes.
- [ ] Update frontend callers to use `realtimeRevision` for SSE and `projectRevision` for writes.
- [ ] Run revision, API, runtime, and frontend revision tests.

### Task 4: Batched coordinator fan-out and cached metrics

**Files:**
- Modify: `tests/IGW.Tests/RealtimeStreamCoordinatorTests.cs`
- Modify: `tests/IGW.Tests/RealtimeMetricsTests.cs`
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Modify: `src/IGW.Core/RealtimeStreamSession.cs`

**Interfaces:**
- Consumes: ingress shard coalescing window and immutable per-route subscriber snapshots.
- Produces: `MarkDirtyBatch` and a one-second aggregate metrics cache.

- [ ] Add a failing test proving a batch with repeated handles produces one final value per handle and one session wakeup batch.
- [ ] Add a failing metrics test proving repeated snapshots inside one second reuse the aggregate snapshot.
- [ ] Replace per-change target-array allocation with route subscriber snapshots updated only on open/close.
- [ ] Group shard changes by target session and call `MarkDirtyBatch` once per batch.
- [ ] Cache aggregate metrics for one second while keeping per-session values current enough for health reporting.
- [ ] Run coordinator, snapshot, metrics, and performance quick tests.

### Task 5: Coalesced frontend application and task-scoped MES status

**Files:**
- Modify: `src/IGW.WebUi/src/stores/realtime.ts`
- Modify: `src/IGW.WebUi/src/stores/realtime.spec.ts`
- Modify: `src/IGW.WebUi/src/views/Points.vue`
- Modify: `src/IGW.WebUi/src/App.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/views/realtime-pages.spec.ts`

**Interfaces:**
- Consumes: parsed SSE payloads and topics `devices`, `mes-state`, `mes-state:{taskId}`, `mes-task:{taskId}`.
- Produces: one parse per frame, snapshot-complete notifications, animation-frame delta application, and selected-task MES state.

- [ ] Add failing store tests proving snapshot chunks do not notify consumers and parsed payloads are reused.
- [ ] Add failing page tests proving global status does not subscribe to wildcard `mes-state` and flow pages subscribe to both selected task topics.
- [ ] Parse payloads once in the client/store boundary and return applied values/kind to consumers.
- [ ] Coalesce Quick Client item mutations to one animation-frame batch and update only loaded items.
- [ ] Replace the one-second full MES fallback poll with an initial/reconnect one-shot and selected-task subscription.
- [ ] Run focused and full frontend tests plus typecheck.

### Task 6: End-to-end verification and documentation

**Files:**
- Modify: `docs/validation/2026-08-31-sse-realtime-validation.md`
- Modify: `benchmarks/IGW.Realtime.Performance/Program.cs` only if a discovered correctness gap requires coverage.

**Interfaces:**
- Consumes: completed backend and frontend behavior.
- Produces: reproducible verification evidence without overstating real-PLC coverage.

- [ ] Run all backend tests in Release configuration.
- [ ] Run all frontend tests, TypeScript checking, and the production build.
- [ ] Run the Release Runtime build and quick realtime performance profile.
- [ ] Run a TestServer/Kestrel-level SSE scenario covering bounded frames, reconnect cleanup, and slow-client isolation.
- [ ] Update validation notes with exact commands, results, and the remaining real-PLC/network acceptance boundary.
