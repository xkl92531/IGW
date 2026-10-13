# OPC UA Latency Fixes Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development for independent driver/scheduler tasks; integrate DeviceLoop changes in the parent task. Steps use checkbox syntax.

**Goal:** Fix the source-confirmed latency mechanisms while retaining authoritative write verification, independent concurrent scanning and completion-based MES collection cycles.

**Architecture:** Distinguish request timeouts from explicit capacity errors; track requested subscriptions separately from accepted leases; expose an optional priority verification-read interface; complete resolved writes promptly and make retries yield to queued work without replaying successful tags.

**Tech Stack:** .NET 10, OPC UA SDK, xUnit, Vue/Vitest.

**Spec:** `docs/opcua-qc-latency-review-2026-09-17.md`.

## Global Constraints

- Preserve current QC fixes and all unrelated changes.
- Do not write to live PLCs, restart services, publish or commit automatically.
- Do not interpret protocol acceptance or missing/Bad readback as verified success.
- Preserve cancellation before dispatch, same-tag ordering/supersession, batch limits and partial-read evidence.
- Ruling: continue in the existing user-visible checkout on `codex/opcua-latency-fixes`, preserving the already authorized working changes instead of relocating them.

## Task 1: Bound timeout recovery

Files: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`, `tests/IGW.Tests/OpcUaReadRecoveryTests.cs`.

- [x] Add failing tests: 512 continuously timing-out nodes must not cause a per-node retry storm; an already read prefix must retain Good values; explicit `BadTooManyOperations` recovery must still work.
- [x] Separate service timeouts from capacity subdivision: preserve successful samples, mark the unread remainder with actual failure quality, return control after one timeout. A reduced next-read window may remain; no reconnect for a service timeout alone.
- [x] Run `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter FullyQualifiedName~OpcUaReadRecoveryTests` and related capability/recovery tests.

## Task 2: Retain accepted subscriptions

Files: `src/IGW.Core/Engine.cs`, `tests/IGW.Tests/PartialSubscriptionIsolationTests.cs`; retain `DeviceLoopSubscriptionTests` regressions.

- [x] Add failing test with unchanged two-tag demand, one rejected monitored item and fake-clock reconciliation.
- [x] Track the complete requested periods independently of accepted IDs. Keep accepted leases when demand is unchanged; rejected points remain in polling fallback. Retry rejected points separately after bounded backoff and merge accepted leases without disposing healthy subscriptions; dispose all leases on actual demand/connection changes.
- [x] Cover rejected-point recovery, unchanged-demand stability, demand removal and existing transient-retention behavior.

## Task 3: Prioritize verification reads

Files: `src/IGW.Abstractions/Drivers.cs`, `src/IGW.Core/SharedOpcUaDriverLease.cs`, `src/IGW.Core/Engine.cs`, `tests/IGW.Tests/SharedOpcUaConnectionsTests.cs`.

- [x] Add optional `IWriteVerificationReader.ReadForWriteVerificationAsync(IReadOnlyList<TagBinding>, CancellationToken)` and a failing queued-read ordering test for shared OPC UA.
- [x] Implement the shared lease method through priority admission. DeviceLoop invokes it for scalar and array confirmation only; other drivers retain normal `ReadAsync` behavior. Retain two slots; later field feedback reduces priority burst from eight to one when collection is queued.
- [x] Run shared connection/lifetime and write verification tests.

## Task 4: Release completed writes and yield retries

Files: `src/IGW.Core/Engine.cs`, `tests/IGW.Tests/WriteVerificationEngineTests.cs`, `tests/IGW.Tests/DeviceLoopWriteQueueSafetyTests.cs`.

- [x] Add failing gated-driver tests: a verified point completes while a peer is still retrying; an unrelated queued write runs before a bad point exhausts retries.
- [x] Complete terminal points using their authoritative result immediately; publish evidence before completion. Carry retry attempt/normalized target state for unresolved writes when yielding. Maintain the existing maximum of initial write plus ten retries and preserve already-dispatched cancellation semantics.
- [x] Verify late failures do not change successful completions, no successful tag is rewritten, and superseding writes remain correct.
- [x] Apply the same retry-yield rule to array writes so a failed array cannot monopolize the write pump ahead of healthy scalar requests. Preserve array slice retry state and generation/cancellation semantics; cover priority verification routing in the engine.

## Task 5: Keep concurrent scanning independent of queued writes

User clarification: writes are fast, PLC values appear slowly in QC; read and write processing may be separated.

- [x] Reproduce polling starvation with a concurrency-capable driver, an in-flight write and a second queued write; an unrelated demanded read must continue publishing actual samples.
- [x] Let concurrency-capable drivers scan while the independent writer has queued work. Schedule both loops independently; preserve exclusive serial behavior for single-flight drivers.
- [x] Reject stale in-flight scan responses after write verification; serialize only cache publication with the epoch check.
- [x] Check subscription-to-DataHub-to-QC routing: prioritize live deltas during progressive snapshots, prevent old slices restoring stale values, and update unchanged samples' timestamps. Retain field sampling/transport limits.

## Task 6: Wait for terminal writeback, then honor the configured collection interval

Latest user clarification supersedes indefinite success waiting: after ten failed rewrites, record final failure and stop retrying that request. Wait until all current nodes finish, then wait the task's configured interval. 200ms is an example, never a fixed production constant.

- [x] Remove the previously introduced MES-level unbounded retry loop; retain engine's initial write plus at most ten rewrites and immediate per-point terminal completion.
- [x] Cover automatic response mapping, terminal failure/exception, stop cancellation, no overlapping cycles and multiple configured intervals. Preserve event/command replay and legacy linear-step behavior.

## Task 7: Publish completed OPC UA batches promptly

- [x] Reproduce first-batch samples withheld behind a blocked later physical read using production OPC UA ReadCore and a gated transport.
- [x] Expose the physical window to the collection planner so each completed batch publishes separately; keep internal capacity recovery and direct-call partitioning.
- [x] Reproduce eight priority operations ahead of collection; limit shared OPC UA priority bursts to one without increasing the two-operation limit.

## Final verification

- [x] Review all changes for concurrency, subscriptions, request ownership, counters and diagnostic consistency.
- [x] Run targeted tests together and broader backend regression: final 533 backend tests pass. Earlier unchanged QC verification: 33 tests and vue-tsc pass. Document the isolated-output startup test limitation and the EIP counter test's transient failure, followed by passing isolated and full reruns.
- [x] Update the offline investigation harness and report with post-fix behavior; old measurements remain explicitly historical.
- [x] Deliver source changes and verified limits; no field performance claim without field measurements.
