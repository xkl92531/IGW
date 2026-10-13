# PLC Connection Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every offline or partially usable PLC a stable diagnostic category, actionable explanation, occurrence history for the current Runtime process, and consistent presentation in the project tree and monitoring center.

**Architecture:** Add a protocol-neutral classifier and thread-safe incident registry in `IGW.Core`. Device loops report connection/read/write failures and recoveries to the shared registry, while driver-provided detail codes override generic exception classification. REST APIs expose the same snapshot to the project tree and a new monitoring table.

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core minimal APIs, Vue 3, TypeScript, Element Plus, Node test runner.

## Global Constraints

- Cover every registered runtime driver through a generic fallback; preserve vendor-specific raw errors.
- Current incidents and recovered incidents live only for the Runtime process lifetime; do not write them to project JSON or SQLite.
- Standby devices without data demand are not connection incidents.
- A live connection with invalid DB/tag addresses is a data-access incident, not an offline device.
- Redact credentials and cap raw error text at 2,000 characters.
- Do not remove or rename existing `/api/v1/devices` and `/api/v1/anomalies` fields.

---

### Task 1: Protocol-neutral classification and incident lifecycle

**Files:**
- Create: `src/IGW.Core/ConnectionDiagnostics.cs`
- Test: `tests/IGW.Tests/ConnectionDiagnosticsTests.cs`

**Interfaces:**
- Produces: `ConnectionDiagnosticClassifier.Classify(Exception?, DeviceConnectionDetail?)`
- Produces: `ConnectionIncidentRegistry.Report(...)`, `Recover(...)`, `Current(...)`, and `Snapshot()`
- Produces: stable category codes, scope, cause, suggestion, raw error, timestamps, count, retry fields.

- [ ] **Step 1: Write failing classifier tests**

Cover socket refusal, timeout, unreachable network, DNS failure, authentication, OPC UA security checks, permission, protocol mismatch, data-address mismatch, remote close, unknown fallback, redaction, and truncation.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~ConnectionDiagnosticsTests"`

Expected: compilation failure because classifier and incident types do not exist.

- [ ] **Step 3: Implement the minimal classifier**

Use exception/socket types first, explicit driver codes second with highest priority, and conservative message matching last. Every result must include a stable suggestion and raw error fallback.

- [ ] **Step 4: Add failing lifecycle tests**

Verify repeated category aggregation, category transition, recovery, a 200-entry recovered bound, per-device lookup, and immutable snapshot behavior.

- [ ] **Step 5: Implement the thread-safe registry and verify GREEN**

Run the focused filter again and require all diagnostics tests to pass.

### Task 2: Capture real runtime failures and recoveries

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `tests/IGW.Tests/RuntimeEngineReconnectTests.cs`
- Modify: `tests/IGW.Tests/DeviceLoopTransientReadTests.cs`

**Interfaces:**
- Consumes: classifier and registry from Task 1.
- Produces: `DeviceStatus.Diagnostic`, `RuntimeEngine.ConnectionDiagnostics()`.

- [ ] **Step 1: Write failing DeviceLoop tests**

Assert that a connection exception populates `LastError` and a classified active incident, repeated retries increment its count, a successful connection/read resolves it, standby does not create one, and a driver `DbAddressMismatch` detail remains connected with data scope.

- [ ] **Step 2: Run focused engine tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeEngineReconnectTests|FullyQualifiedName~DeviceLoopTransientReadTests"`.

- [ ] **Step 3: Wire the registry into every device loop**

Report connect exceptions with actual retry delay; inspect `IDeviceConnectionDiagnostics` after connect/read/write; recover only after confirmed success or standby; copy the current incident into `DeviceStatus`.

- [ ] **Step 4: Run focused engine tests and verify GREEN**

Require both test classes to pass without changing existing reconnect timing behavior.

### Task 3: Expose structured diagnostics through REST

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Northbound.RestApi/AnomalyService.cs`
- Test: `tests/IGW.Tests/ConnectionDiagnosticsApiTests.cs`

**Interfaces:**
- Consumes: `RuntimeEngine.ConnectionDiagnostics()` and `DeviceStatus.Diagnostic`.
- Produces: `GET /api/v1/connection-diagnostics` and additive device/anomaly fields.

- [ ] **Step 1: Write failing API/DTO tests**

Verify active/recovered arrays, device diagnostic fields, data-scope incidents appearing as `fail`, transport incidents appearing as `offline`, and existing response fields remaining present.

- [ ] **Step 2: Run focused API tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~ConnectionDiagnosticsApiTests"`.

- [ ] **Step 3: Implement additive endpoints and mappings**

Return stable camel-case JSON fields; derive `ageMs` from `FirstOccurredAt`; make anomaly search include category, suggestion, protocol, and raw error.

- [ ] **Step 4: Run focused API tests and verify GREEN**

Require all new API tests to pass.

### Task 4: Project-tree category labels and tooltips

**Files:**
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Modify: `src/IGW.WebUi/src/i18n/catalog.test.cjs`

**Interfaces:**
- Consumes: `/api/v1/devices[].diagnostic`.
- Produces: `diagnosticRuntimeMark(...)` behavior through `deviceRuntimeMark(...)`.

- [ ] **Step 1: Write failing Web tests**

Assert short labels for network unreachable, port refusal, timeout, protocol mismatch, authentication, security, permissions, address mismatch, partial failure, remote close, and unknown; assert tooltips contain cause and suggestion.

- [ ] **Step 2: Run focused Node tests and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/deviceRuntime.test.cjs src/IGW.WebUi/src/i18n/catalog.test.cjs`.

- [ ] **Step 3: Implement structured tree presentation**

Prefer `diagnostic` over legacy `detailState`; retain legacy fallback for older Runtime versions; add all system-owned strings to both locale catalogs.

- [ ] **Step 4: Run focused Web tests and verify GREEN**

Require both focused files to pass.

### Task 5: Monitoring-center connection exception table

**Files:**
- Create: `src/IGW.WebUi/src/utils/connectionDiagnostics.ts`
- Create: `src/IGW.WebUi/src/utils/connectionDiagnostics.test.cjs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/views/Monitor.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`

**Interfaces:**
- Consumes: `api.connectionDiagnostics()` response `{ active, recovered }`.
- Produces: category/protocol/state/query/station filtering and stable abnormal-first sorting.

- [ ] **Step 1: Write failing filtering/sorting tests**

Verify active-first ordering, channel multi-select, query matching across device/cause/raw text, protocol/category filters, and recovered-state selection.

- [ ] **Step 2: Run focused Node test and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/connectionDiagnostics.test.cjs`.

- [ ] **Step 3: Implement utility and table UI**

Add a compact card with active/recovered tabs, query/category/protocol filters, count summary, category badge, cause, suggestion, first/latest time, count, retry state, and expandable raw error. Reuse selected station cards as an additional filter.

- [ ] **Step 4: Run focused Web tests and verify GREEN**

Require the utility and source-level UI tests to pass.

### Task 6: Full verification

**Files:**
- Verify all files above.

- [ ] Run every `*.test.cjs` under `src/IGW.WebUi/src`.
- [ ] Run `vue-tsc --noEmit`.
- [ ] Run the Web production build.
- [ ] Run `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`.
- [ ] Run `dotnet build IGW.slnx --no-restore`.
- [ ] Run `git diff --check` and inspect the final diff for unrelated changes and secrets.
