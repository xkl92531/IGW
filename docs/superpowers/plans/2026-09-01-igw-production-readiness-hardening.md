# IGW Production Readiness Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the release-blocking authorization, secret exposure, concurrent-save, PLC truthfulness, and SSE consistency defects while preserving existing projects and cross-DB tag selection.

**Architecture:** Centralize path authorization and project mutation so every protocol observes one security and concurrency contract. Keep PLC transport acknowledgement separate from verification evidence, and keep browser delivery isolated from PLC scan/write execution. Add byte-bounded SSE framing and per-stream browser state, then make the complete test suite reproducible from the solution.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit, Vue 3, Pinia, TypeScript, Vitest, SSE, pnpm.

**Spec:** `docs/superpowers/specs/2026-09-01-igw-production-readiness-hardening-design.md`

## Global Constraints

- Existing project files, channels, devices, tag IDs, MES flows, databases, and keys remain loadable.
- Authorization uses full actual tag paths and supports multiple authorized branches across DBs.
- No PLC read failure may be represented as a Good value.
- A successful write result identifies its verification evidence; point failures do not close a healthy session.
- Every SSE event is at most 1 MiB and PLC I/O is independent from browser delivery.
- Existing uncommitted changes outside the task's files are preserved.
- Each behavior follows red-green-refactor and the failing test is observed before production code changes.

---

### Task 1: Make the backend test gate reproducible

**Files:**
- Modify: `.gitignore`
- Modify: `IGW.slnx`
- Add: currently ignored files under `tests/IGW.Tests/`
- Create: `scripts/verify-backend-tests.ps1`

**Interfaces:**
- Produces: `dotnet test IGW.slnx` discovers `IGW.Tests`; `scripts/verify-backend-tests.ps1` fails when discovered/passed test count is zero.

- [ ] Remove only `/tests/` from `.gitignore`; keep runtime databases, keys, tools, outputs, and user-local data ignored.
- [ ] Add `<Folder Name="/tests/"><Project Path="tests/IGW.Tests/IGW.Tests.csproj" /></Folder>` to `IGW.slnx`.
- [ ] Create a PowerShell gate that runs `dotnet test IGW.slnx -c Release --no-restore --logger "console;verbosity=normal"`, captures output, propagates a non-zero exit code, and throws when no `Passed!`/`已通过!` summary with a positive total is present.
- [ ] Run `dotnet test IGW.slnx -c Release --no-restore --nologo` before the solution edit and record that it discovers zero tests.
- [ ] Run the same command after the edit and require the current 552-test-or-greater suite to execute successfully.
- [ ] Commit only the test gate and previously ignored test sources with `git commit -m "test: make backend suite part of solution"`.

### Task 2: Centralize path-segment authorization without breaking cross-DB selection

**Files:**
- Create: `src/IGW.Northbound.RestApi/TagBranchMatcher.cs`
- Modify: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`
- Test: `tests/IGW.Tests/PublicPlcApiPolicyTests.cs`
- Test: `tests/IGW.Tests/DcsTagOperationsTests.cs`
- Test: `tests/IGW.Tests/RealtimeStreamApiTests.cs`

**Interfaces:**
- Produces: `TagBranchMatcher.IsWithin(string tagId, string branch)` and `TagBranchMatcher.AreAllWithin(IEnumerable<string> tagIds, IEnumerable<string> branches)`.

- [ ] Add failing tests proving `PLC1.DB10` permits itself and `PLC1.DB10.Group.Tag`, rejects `PLC1.DB100.Tag`, and permits a request containing tags from both `PLC1.DB10` and `PLC1.DB20` when both branches are configured.
- [ ] Run the targeted tests and confirm the sibling-prefix case fails against `StartsWith(branch)`.
- [ ] Implement normalization as `Trim().TrimEnd('.')`; match only exact equality or `branch + "."`, using `OrdinalIgnoreCase`.
- [ ] Replace independent REST and DCS prefix checks with the shared matcher; route SSE authorization through the same policy.
- [ ] Run targeted REST, DCS, and SSE authorization tests, then the full backend suite.
- [ ] Commit with `git commit -m "fix: enforce tag path authorization boundaries"`.

### Task 3: Redact configuration APIs and hash API-user passwords

**Files:**
- Create: `src/IGW.Core/ProjectSecrets.cs`
- Create: `src/IGW.Northbound.RestApi/ProjectConfigurationDto.cs`
- Modify: `src/IGW.Abstractions/ProjectModel.cs`
- Modify: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Test: `tests/IGW.Tests/ProjectSecurityTests.cs`
- Test: `tests/IGW.Tests/IgwRestApiTests.cs`

**Interfaces:**
- Produces: `ProjectSecrets.HashPassword(string)`, `ProjectSecrets.VerifyPassword(UserDef,string)`, `ProjectConfigurationDto.FromProject(IgwProject)`, and `ProjectConfigurationDto.MergeInto(IgwProject)`.
- DTO secret contract: `secretState: "configured"|"empty"`; omitted secret preserves the live value; `clearSecret: true` explicitly clears it.

- [ ] Add failing tests proving the project response contains no plaintext passwords, tokens, or password hashes; read-only users receive 403; writer saves preserve omitted secrets; and a new password replaces the old one.
- [ ] Add failing tests for PBKDF2 verification and legacy plaintext compatibility.
- [ ] Implement versioned PBKDF2-SHA256 with a random 16-byte salt, at least 210,000 iterations, fixed-time comparison, and zero logging of credentials.
- [ ] Keep legacy `Password` deserialization for compatibility, authenticate it only until the next successful save, then store `PasswordHash` and clear plaintext.
- [ ] Return only the redacted DTO from `GET /api/v1/project`; require writer permission for both project read/edit endpoints.
- [ ] Add a health warning when security is disabled or HTTP is exposed on a non-loopback address; do not force HTTPS in this compatibility release.
- [ ] Run targeted security/API tests and the full backend suite.
- [ ] Commit with `git commit -m "fix: redact project credentials and hash api passwords"`.

### Task 4: Encrypt reversible connection secrets with a portable installation key

**Files:**
- Create: `src/IGW.Core/InstallationSecretProtector.cs`
- Modify: `src/IGW.Core/ProjectStore.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `docs/RELEASE_NOTES.md`
- Test: `tests/IGW.Tests/InstallationSecretProtectorTests.cs`
- Test: `tests/IGW.Tests/ProjectStoreTests.cs`

**Interfaces:**
- Produces: `IInstallationSecretProtector.Protect(string)`, `Unprotect(string)`, and `IsProtected(string)` using the `enc:v1:` envelope.
- Key location: application data directory beside the runtime database, file `igw.masterkey`; callers may override the data directory in tests.

- [ ] Add failing round-trip, random-nonce, wrong-key, corrupted-ciphertext, empty-secret, and legacy-plaintext migration tests.
- [ ] Implement AES-256-GCM with a random 96-bit nonce and authenticated `enc:v1:` envelope; create the 32-byte master key atomically if absent.
- [ ] Protect MQTT passwords, MES bearer tokens, and OPC UA server user passwords during a successful save, while runtime receives decrypted cloned values.
- [ ] Ensure a failed migration leaves the original project and key intact and logs no secret material.
- [ ] Document that migration to another computer requires both the project/database directory and `igw.masterkey`.
- [ ] Run targeted storage tests, restart/load-save-load tests, and the full backend suite.
- [ ] Commit with `git commit -m "fix: protect reversible project secrets at rest"`.

### Task 5: Make all project mutations revision-atomic

**Files:**
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Create: `src/IGW.Runtime/ProjectUpdateResult.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`
- Test: `tests/IGW.Tests/GatewayHostRevisionTests.cs`
- Test: `tests/IGW.Tests/IgwRestApiTests.cs`

**Interfaces:**
- Produces: `Task<ProjectUpdateResult> UpdateProjectAsync(string? expectedRevision, Func<IgwProject,IgwProject> mutation, ProjectUpdateScope scope, CancellationToken ct)`.
- `ProjectUpdateResult` contains `Applied`, `Conflict`, `Revision`, and validation errors.

- [ ] Add a failing concurrency test that launches two mutations with the same revision and proves the current implementation allows both or loses one update.
- [ ] Move revision comparison, cloning, mutation, secret merge, topology reconciliation, validation, atomic save, live assignment, and revision update under `_gate`.
- [ ] Route full project saves, targeted MES saves, log-retention changes, and DCS tag updates through the coordinator.
- [ ] Map conflict to HTTP 409 and return the live revision without stopping any runtime component.
- [ ] Run targeted concurrency/reload tests and the full backend suite.
- [ ] Commit with `git commit -m "fix: serialize project updates by expected revision"`.

### Task 6: Report Siemens WSTRING reads truthfully

**Files:**
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Test: `tests/IGW.Tests/IoTPlcDriverWStringTests.cs`

**Interfaces:**
- Produces: WSTRING helper result `(bool Success, object? Value, string Error)` where every failed address/length/character operation has `Success=false`.

- [ ] Add failing tests for invalid address, length-read failure, declared/current length violation, partial character failure, valid empty value, and successful Unicode value.
- [ ] Confirm existing code incorrectly reports the failure cases as success.
- [ ] Read both declared maximum and current length; reject current length above declared maximum or the supported safety ceiling.
- [ ] Return the original transport error plus address/offset context without logging the business string.
- [ ] Run targeted driver tests and all IoTClient/engine tests.
- [ ] Commit with `git commit -m "fix: preserve Siemens WSTRING read failures"`.

### Task 7: Add capability-driven generic write confirmation

**Files:**
- Modify: `src/IGW.Abstractions/ProjectModel.cs`
- Modify: `src/IGW.Abstractions/DataModel.cs`
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/ProjectStore.cs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `tests/IGW.Tests/EngineWriteVerificationTests.cs`
- Test: `tests/IGW.Tests/ProjectStoreTests.cs`
- Test: `src/IGW.WebUi/src/views/project-write-confirmation.spec.ts`

**Interfaces:**
- Produces: `WriteConfirmationMode { ReadBack, ConfirmationTag, ProtocolAcknowledged }` and `WriteConfirmationDef { Mode, TagId, ExpectedValue }` on `TagDef`.
- Produces: distinct write evidence `ReadBackVerified`, `ConfirmationTagVerified`, or `ProtocolAcknowledgedOnly` in the result/log DTO.

- [ ] Add failing tests for readable tag read-back, pure write tag plus confirmation tag, unsupported/missing confirmation rejection, protocol-acknowledged result, mismatch retries, and point failure that leaves a healthy driver session online.
- [ ] Validate confirmation references and prevent cycles/self-reference.
- [ ] Replace the unconditional `TagAccess.Write => BadNotReadable` branch with mode/capability dispatch.
- [ ] Keep read-back as the default for existing read/write tags; require an explicit strategy for pure write tags.
- [ ] Expose confirmation configuration and its verification limitation in the tag properties UI using full actual paths.
- [ ] Run engine/driver/frontend targeted tests and full suites.
- [ ] Commit with `git commit -m "feat: add generic write confirmation modes"`.

### Task 8: Make every SSE event byte-bounded

**Files:**
- Modify: `src/IGW.Abstractions/TagValueStreamContracts.cs`
- Modify: `src/IGW.Northbound.RestApi/TagValueSseWriter.cs`
- Modify: `src/IGW.Northbound.RestApi/RealtimeStreamApi.cs`
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Test: `tests/IGW.Tests/TagValueSseWriterTests.cs`
- Test: `tests/IGW.Tests/RealtimeStreamApiTests.cs`

**Interfaces:**
- Produces: `SseFrameSizer.MeasureUtf8(...)`, byte-aware `WriteValuesAsync`, bounded hello missing sample/count, and paged `/api/v1/streams/realtime/missing` diagnostics.

- [ ] Add failing tests for 10,000 maximum-length missing IDs, 256 medium values above 1 MiB, one oversized value, and headers-already-started errors.
- [ ] Make hello include `missingCount` and a byte-bounded sample rather than the full list.
- [ ] Split value events by actual serialized UTF-8 size and count; emit `value-too-large` for a single oversized value.
- [ ] Add the authorized paged diagnostics endpoint for complete missing IDs.
- [ ] Emit a terminal SSE error when possible and always release the lease without affecting PLC workers.
- [ ] Run writer/API/network tests and the full backend suite.
- [ ] Commit with `git commit -m "fix: enforce byte-bounded realtime frames"`.

### Task 9: Isolate browser stream state and delete obsolete MES state

**Files:**
- Modify: `src/IGW.WebUi/src/utils/realtimeStream.ts`
- Modify: `src/IGW.WebUi/src/stores/realtime.ts`
- Modify: `src/IGW.WebUi/src/App.vue`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/views/Mes.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.Core/RealtimeStreamCoordinator.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Test: `src/IGW.WebUi/src/utils/realtimeStream.spec.ts`
- Test: `src/IGW.WebUi/src/stores/realtime.spec.ts`
- Test: `tests/IGW.Tests/RealtimeRuntimeTopicsTests.cs`

**Interfaces:**
- Produces: independent `StreamConnectionState` entries keyed by `device-runtime`, `point-values`, `mes-status`, and `mes-flow:<taskId>`.
- Produces: runtime events `mes-task-removed` and `mes-generation-reset`.

- [ ] Add a failing client test: first attempt becomes stale, second attempt hangs before headers, and a second stale abort must occur.
- [ ] Add failing store tests proving a MES hello cannot mark device runtime live and tombstones remove task state.
- [ ] Arm/clear a stale timer per connection attempt in `try/finally`.
- [ ] Store status, generation, and last-frame time per stream key; expire cached entity state when its owner is stale or replaced.
- [ ] Publish task tombstones/generation reset when MES topology stops or changes, and clear only the affected client map.
- [ ] Keep one global runtime-state stream per browser; open point-value streams only on value pages.
- [ ] Run targeted frontend/backend runtime-topic tests and full suites.
- [ ] Commit with `git commit -m "fix: isolate realtime stream lifecycle state"`.

### Task 10: Remove remaining UI/server hot paths and dependency advisories

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.WebUi/src/views/Points.vue`
- Modify: `src/IGW.WebUi/package.json`
- Modify: `src/IGW.WebUi/pnpm-lock.yaml`
- Test: `tests/IGW.Tests/RealtimeMetricsTests.cs`
- Test: `src/IGW.WebUi/src/views/realtime-pages.spec.ts`

**Interfaces:**
- `/health` consumes `SnapshotMetricsCached()`.
- Quick Client applies snapshot values in bounded batches and keeps the UI responsive.

- [ ] Add a failing test proving `/health` uses cached metrics rather than enumerating live sessions per call.
- [ ] Add a fake-animation-frame test proving a 10,000-value snapshot is applied across multiple frames.
- [ ] Switch health to cached metrics and batch Quick Client snapshot application with a fixed per-frame budget.
- [ ] Upgrade PostCSS to at least 8.5.23 and nanoid to at least 3.3.18 through compatible direct/toolchain dependency updates.
- [ ] Run `pnpm audit --prod`, Vitest, legacy Node tests, `vue-tsc --noEmit`, and the Vite production build.
- [ ] Commit with `git commit -m "perf: remove realtime ui and metrics hot paths"`.

### Task 11: Execute final production evidence gates

**Files:**
- Modify: `benchmarks/IGW.Realtime.Performance/Program.cs`
- Modify: `docs/validation/2026-09-01-sse-realtime-validation.md`
- Modify: `docs/RELEASE_NOTES.md`

**Interfaces:**
- Produces separate evidence sections for source review, unit/integration tests, real network SSE, in-process million-point coordinator, simulator PLC, and real PLC.

- [ ] Add a real ASP.NET/TCP scenario with 100 concurrent streams, simultaneous snapshots, one slow client, and reconnect churn; assert zero leaked leases and no event over 1 MiB.
- [ ] Run the explicit backend test gate and require a positive test count.
- [ ] Run frontend Vitest, legacy Node tests, typecheck, production build, and dependency audit.
- [ ] Run the full Release build with zero errors and inspect warnings.
- [ ] Run the in-process million-point benchmark and the network scenario; record latency, throughput, memory, GC, drops, reconnects, and limitations separately.
- [ ] Run simulator read/write/verification regression tests. Mark real PLC checks as not executed unless actual PLC telemetry is collected.
- [ ] Run `git diff --check` and review the complete diff for unrelated changes or secrets.
- [ ] Update validation and release notes using only observed results.
- [ ] Commit with `git commit -m "docs: record IGW hardening validation evidence"`.
