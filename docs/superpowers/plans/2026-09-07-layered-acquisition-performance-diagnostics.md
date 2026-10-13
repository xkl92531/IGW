# Layered Acquisition Performance Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task by task. Apply `superpowers:test-driven-development` to every behavior change and `superpowers:verification-before-completion` before reporting completion.

**Goal:** Add evidence-based acquisition diagnostics that separates IGW queueing and local processing from suspected network and PLC/OPC UA server delay, exposes the evidence in the performance page and logs, and creates a safe field-export ZIP without performing extra PLC reads.

**Architecture:** Keep `DeviceLoop` as the owner of batch timing, add a process-wide network probe coordinator keyed by remote PLC IP and configured local IPv4, and combine both streams with a pure attribution classifier. Store the immutable network window and attribution with each `ScanTimingRecord`, so UI queries, logs, and exports all describe the same batch. The probe runs independently from OPC UA sessions and the shared driver scheduler.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, OPC UA .NET Standard, xUnit, Vue 3, TypeScript, Vitest, Axios, ICMP on Windows.

**Spec:** `docs/superpowers/specs/2026-09-07-layered-acquisition-performance-diagnostics-design.md`

## Global Constraints

- Preserve all current working-tree changes, including the SignalR migration and UI responsiveness work. Do not reset, clean, or overwrite `tests/IGW.ArrayStress/`.
- Point reads, point writes, and live point state remain on SignalR. The diagnostics ZIP is a read-only HTTP file download.
- Network probes must never use an OPC UA session, call `Read`, enter the shared OPC UA scheduler, or block a `DeviceLoop`.
- A single background probe is shared for every unique `(remote IP, configured local IPv4)` pair. Logical devices only hold reference-counted leases.
- All durations use monotonic timing for elapsed time and `DateTimeOffset` only for correlation timestamps.
- Missing measurements serialize and log as unavailable. Never convert missing network, protocol, or local data to zero.
- UI and logs must say `疑似` for network and PLC/OPC UA server attribution and show the evidence used.
- All bounded collections, timers, tasks, exports, and network calls need cancellation and capacity limits.

---

## Task 1: Add immutable diagnostic contracts and the pure attribution classifier

**Files:**

- Create: `src/IGW.Core/AcquisitionPerformanceDiagnostics.cs`
- Modify: `src/IGW.Core/ScanTimingHistory.cs`
- Test: `tests/IGW.Tests/AcquisitionPerformanceAttributionTests.cs`

### Step 1: Write failing table-driven attribution tests

Cover these cases with explicit numbers and evidence assertions:

- `LocalMs >= 50` and at least 20% of total selects `igw-local-processing`.
- Queue wait at least 100 ms and at least 40% of driver time selects `igw-shared-queue`.
- At least six usable network samples plus loss or a 3x/+20 ms RTT increase and a current `RequestMaxMs` above the prior retained per-batch `RequestMaxMs` P95 baseline selects `network-suspected`.
- Stable network, low queue/local time, a current `RequestMaxMs` above the prior retained per-batch `RequestMaxMs` P95 baseline, and request max at least 70% of total selects `plc-opc-server-suspected`.
- Multiple protocol requests, failures, cancellations, retries, or downshift add `request-split-or-retry` as a factor.
- Missing protocol timing or an insufficient network window selects `insufficient-evidence`.
- Direct IGW measurements take priority over suspected causes while preserving the other matching factors.

Use the public result codes in assertions so API and UI labels can change without weakening behavior checks.

### Step 2: Run the new tests and observe the expected compile failure

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter AcquisitionPerformanceAttributionTests --nologo
```

Expected: FAIL because the contracts and classifier do not exist.

### Step 3: Add the contracts

Implement records equivalent to:

```csharp
public sealed record NetworkProbeSample(
    DateTimeOffset CapturedAt,
    string Status,
    double? RttMs,
    string? Error,
    string SourceAddress,
    string RemoteAddress,
    bool RouteVerified);

public sealed record NetworkProbeWindow(
    DateTimeOffset From,
    DateTimeOffset To,
    int Count,
    int SuccessCount,
    int FailedCount,
    double? LatestRttMs,
    double? P95RttMs,
    double? BaselineP95RttMs,
    double? LossPercent,
    string Status,
    string? Error,
    bool RouteVerified);

public sealed record AcquisitionAttribution(
    string Code,
    string Label,
    string Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Factors);

public sealed record AcquisitionAttributionContext(
    ScanTimingRecord Batch,
    NetworkProbeWindow? Network,
    double? ProtocolBaselineP95Ms);
```

Add nullable `Network` and `Attribution` properties at the end of `ScanTimingRecord` so current JSON clients remain compatible.

### Step 4: Implement a deterministic classifier

Create `AcquisitionAttributionClassifier.Classify(AcquisitionAttributionContext context)`. Treat the protocol request as degraded only when its maximum exceeds the prior-history protocol P95 by at least 50% and 20 ms. When no protocol baseline exists, retain the measurements but do not select a suspected network or PLC/server cause. Keep thresholds as named constants and return evidence strings containing the measured values. Do not read wall-clock time or mutable global state in this method.

### Step 5: Run focused tests

Run the Task 1 test command again. Expected: PASS.

### Step 6: Commit

```powershell
git add src/IGW.Core/AcquisitionPerformanceDiagnostics.cs src/IGW.Core/ScanTimingHistory.cs tests/IGW.Tests/AcquisitionPerformanceAttributionTests.cs
git commit -m "feat: classify layered acquisition delays"
```

---

## Task 2: Implement shared, source-bound network probing

**Files:**

- Create: `src/IGW.Core/NetworkPerformanceProbe.cs`
- Create: `src/IGW.Core/SourceBoundIcmpEchoClient.cs`
- Test: `tests/IGW.Tests/NetworkPerformanceProbeTests.cs`

### Step 1: Write failing coordinator tests with a fake echo client and fake clock

Verify:

- Two leases for the same key start one worker and share samples.
- Different remote or source IP values start different workers.
- The client receives the configured source IPv4 and the remote IPv4.
- Default cadence is five seconds and timeout is one second.
- Samples stop after the last lease is disposed.
- A timeout, unavailable ICMP, recovery, and cancellation are recorded without throwing into the owner.
- History retains only the latest 120 samples.
- `Window(at, radius)` uses only samples inside the requested batch correlation range.

Inject a small `TimeProvider`-based interval in tests; do not wait real five-second periods.

### Step 2: Run the focused tests and confirm failure

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter NetworkPerformanceProbeTests --nologo
```

Expected: FAIL because the coordinator does not exist.

### Step 3: Implement the probe interface and reference-counted coordinator

Use contracts equivalent to:

```csharp
public sealed record NetworkProbeKey(
    string RemoteAddress,
    string SourceAddress,
    bool RouteVerified);

public sealed record NetworkEchoResult(
    bool Success,
    double? RttMs,
    string Status,
    string? Error);

public interface INetworkEchoClient
{
    Task<NetworkEchoResult> SendAsync(
        IPAddress remote,
        IPAddress? source,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

`NetworkPerformanceProbeCoordinator.Acquire(key)` returns an `IDisposable` lease. Internally store one worker and reference count per key under a short lock. Run echo calls outside locks. Stop and remove the worker when the count reaches zero.

### Step 4: Implement source-bound Windows ICMP and safe fallback

On Windows, use `IcmpCreateFile`, `IcmpSendEcho2Ex`, and `IcmpCloseHandle` so the configured source address is honored. Validate native return sizes and map errors to status/error fields; never log or throw repeatedly from the worker loop. On non-Windows, use `Ping` and set `RouteVerified=false` because source routing cannot be guaranteed.

### Step 5: Add state-transition logging

Log only these transitions per probe key: healthy to unavailable, continuing failures after the existing merge threshold, and unavailable to recovered. Include source IP, remote IP, status, RTT when present, and the error text.

### Step 6: Run focused tests and commit

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter NetworkPerformanceProbeTests --nologo
git add src/IGW.Core/NetworkPerformanceProbe.cs src/IGW.Core/SourceBoundIcmpEchoClient.cs tests/IGW.Tests/NetworkPerformanceProbeTests.cs
git commit -m "feat: probe acquisition network paths"
```

---

## Task 3: Correlate probes with scan batches and extend summaries and logs

**Files:**

- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/ScanTimingHistory.cs`
- Modify: `src/IGW.Core/NetworkBindingPlan.cs`
- Modify: `tests/IGW.Tests/DeviceLoopTransientReadTests.cs`
- Test: `tests/IGW.Tests/RuntimeNetworkDiagnosticsTests.cs`

### Step 1: Extend the existing failing log tests before production edits

The current working tree already has TDD coverage for protocol fields:

`实际协议请求数、请求累计、请求最大、共享队列等待、失败请求、取消请求`.

Extend the same success and failure tests so logs also require:

`网络探测状态、网络RTT、窗口丢包率、归因、置信度、归因证据`.

Add a no-network case asserting `未提供` and `证据不足`, never `0 ms` or `0%`.

### Step 2: Write failing runtime lifecycle tests

Using fake driver and probe coordinator, verify:

- Logical devices on one physical endpoint share a probe worker.
- The key contains the source IP selected by `NetworkBindingPlan` and the remote host resolved from the OPC UA endpoint.
- A scan record receives only a network window near its start time.
- Engineering reload and device stop dispose leases and cancel orphan workers.
- Probe failure never fails or delays a device scan.

### Step 3: Add probe ownership to `RuntimeEngine`

Allow `RuntimeEngine` to receive an optional coordinator for tests and create the production coordinator when absent. In `StartDevice`, resolve the physical remote IPv4 and the configured outbound IPv4 after binding selection, acquire the endpoint lease, and store it on `DeviceRuntime`. Dispose the lease in every stop/reload/shutdown path.

If the endpoint is a hostname, cache DNS resolution per runtime start. If it cannot be resolved, leave network diagnostics unavailable and continue collection.

### Step 4: Attach snapshots and attribution inside `DeviceLoop`

Inject a read-only callback such as:

```csharp
Func<DateTimeOffset, NetworkProbeWindow?>? networkWindow = null
```

After protocol timing and local timing are finalized, create the base record, fetch the trailing 30-second network window ending at batch completion, calculate the protocol baseline from prior records only as the P95 of each retained batch's `RequestMaxMs`, classify it, and store one immutable final record. Logging and history must consume this exact record so their evidence cannot drift. The current batch must not contribute to its own baseline.

### Step 5: Extend device summaries from cached history

Add cached-history aggregate fields to `ScanTimingDeviceSummary`:

- `QueueP95Ms`
- `ProtocolRequestP95Ms` — P95 of retained per-batch `RequestMaxMs`, not a P95 over individual protocol requests
- `ProtocolRequestMaxMs`
- `LocalP95Ms`
- `NetworkP95RttMs`
- `NetworkLossPercent`
- `LatestAttribution`
- `LatestAttributionConfidence`

Calculate them from at most the existing 200 records. Do not read the PLC and do not retain a second unbounded history.

### Step 6: Run focused and adjacent tests

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "DeviceLoopTransientReadTests|RuntimeNetworkDiagnosticsTests|AcquisitionPerformanceAttributionTests" --nologo
```

Expected: PASS, including the already completed protocol log tests.

### Step 7: Commit

```powershell
git add src/IGW.Core/Engine.cs src/IGW.Core/ScanTimingHistory.cs src/IGW.Core/NetworkBindingPlan.cs tests/IGW.Tests/DeviceLoopTransientReadTests.cs tests/IGW.Tests/RuntimeNetworkDiagnosticsTests.cs
git commit -m "feat: correlate scan and network diagnostics"
```

---

## Task 4: Add a bounded, sanitized diagnostic ZIP export

**Files:**

- Create: `src/IGW.Northbound.RestApi/AcquisitionDiagnosticsExport.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Test: `tests/IGW.Tests/AcquisitionDiagnosticsExportTests.cs`

### Step 1: Write failing ZIP contract tests

Request `GET /api/v1/diagnostics/acquisition-export` through the test host and assert a ZIP response with:

- `summary.json`
- `batches.csv`
- `network.csv`
- `connection-diagnostics.json`
- `README.txt`

Assert the files include IGW path, NodeId, local/remote IP, layer timings, attribution, confidence, and evidence. Seed password-like values, authorization headers, MES bodies, and project secrets into test fixtures and assert their literal values do not occur anywhere in the archive.

Also assert export does not increment fake driver reads or create a new OPC UA connection/session.

### Step 2: Run the export tests and observe failure

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter AcquisitionDiagnosticsExportTests --nologo
```

Expected: FAIL because the route/exporter does not exist.

### Step 3: Expose one immutable runtime snapshot

Add a `RuntimeEngine.AcquisitionDiagnostics()` snapshot containing:

- current build/project identity already safe for diagnostics;
- the bounded scan histories;
- unique network samples for active probe keys;
- existing connection diagnostics already exposed to the UI.

Copy collections before returning. Do not expose live dictionaries, driver objects, options, credentials, or request payloads.

### Step 4: Generate the archive as a streaming HTTP result

Map the read-only HTTP route. Use invariant CSV formatting and RFC 4180 escaping. Add UTF-8 BOM only if the existing project download convention requires Excel compatibility. Put field definitions and the network/PLC attribution limitation in `README.txt`.

Enforce bounded data from the current in-memory capacity. Add cancellation to archive creation and use a temporary stream strategy with an explicit upper limit rather than buffering arbitrary project data.

### Step 5: Run tests and commit

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "AcquisitionDiagnosticsExportTests|IgwRestApi" --nologo
git add src/IGW.Northbound.RestApi/AcquisitionDiagnosticsExport.cs src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Core/Engine.cs tests/IGW.Tests/AcquisitionDiagnosticsExportTests.cs
git commit -m "feat: export acquisition diagnostics"
```

---

## Task 5: Present layered evidence and export controls in the performance page

**Files:**

- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/components/ScanTimingPanel.vue`
- Modify: `src/IGW.WebUi/src/components/ScanTimingPanel.spec.ts`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `src/IGW.WebUi/src/i18n/catalog.spec.ts`

### Step 1: Write failing UI tests

Cover:

- Device overview renders queue P95, protocol request P95/max, network RTT P95/loss, local P95, and latest attribution/confidence.
- Expanded batch renders evidence strings, probe status, source/remote address, and full protocol counters.
- Unavailable network data says it cannot distinguish network from PLC/OPC UA server and shows the concrete reason.
- The export button downloads the HTTP ZIP and names it with a local timestamp.
- Visibility/collapse behavior retains the current two-second cached polling rule and does not start an extra polling loop.
- API mock read counters remain unchanged when opening, expanding, and exporting the panel.

### Step 2: Run focused tests and observe failure

```powershell
pnpm --dir src/IGW.WebUi exec vitest run src/components/ScanTimingPanel.spec.ts
```

Expected: FAIL because the new DTO fields and controls are absent.

### Step 3: Extend TypeScript DTOs and the download client

Mirror nullable backend fields. Preserve optional properties so an older backend still renders existing timing data. Add a management-HTTP method that requests the diagnostics endpoint as a blob and validates the content type before download.

### Step 4: Implement the layered UI

Add the five overview values and recent attribution as text-first fields. In expanded rows render measured evidence and factors. Keep `疑似` in labels for network and PLC/OPC UA server. Use warning/error color as secondary information only.

Show snapshot age whenever SignalR/API refresh is disconnected so cached attribution is not presented as current.

### Step 5: Add Chinese and English catalog entries and run tests

```powershell
pnpm --dir src/IGW.WebUi exec vitest run src/components/ScanTimingPanel.spec.ts src/i18n/catalog.spec.ts
pnpm --dir src/IGW.WebUi exec vue-tsc --noEmit
```

Expected: PASS.

### Step 6: Commit

```powershell
git add src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/components/ScanTimingPanel.vue src/IGW.WebUi/src/components/ScanTimingPanel.spec.ts src/IGW.WebUi/src/i18n/catalog.ts src/IGW.WebUi/src/i18n/catalog.spec.ts
git commit -m "feat: show layered acquisition diagnostics"
```

---

## Task 6: Document metric meaning and prove diagnostics add no PLC load

**Files:**

- Modify: `docs/scan-performance.md`
- Test: `tests/IGW.Tests/AcquisitionDiagnosticsNoReadTests.cs`

### Step 1: Add the no-extra-read integration test

Start the runtime with a counting fake driver, allow normal collection to establish a baseline, then repeatedly:

- query scan timing summaries;
- query expanded batch history;
- collect network windows;
- create the diagnostics ZIP.

Assert the read count follows only the configured collection schedule, and diagnostics do not create sessions, reads, writes, retries, or scheduler entries.

### Step 2: Document precise timing boundaries

Explain:

- `总耗时 = 驱动耗时 + IGW本地处理` within normal rounding;
- driver time can include shared queue wait, protocol calls, split/retry, and driver overhead;
- OPC UA request time includes network and server processing and is not PLC execution time;
- ICMP measures only auxiliary reachability/RTT;
- how each attribution rule works and when `证据不足` appears;
- how to collect the ZIP and compare its time range with KEP logs.

### Step 3: Run the no-load test

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter AcquisitionDiagnosticsNoReadTests --nologo
```

Expected: PASS.

### Step 4: Commit

```powershell
git add docs/scan-performance.md tests/IGW.Tests/AcquisitionDiagnosticsNoReadTests.cs
git commit -m "docs: explain acquisition performance evidence"
```

---

## Task 7: Full verification and review

### Step 1: Run backend validation from a clean process

```powershell
dotnet test IGW.slnx --no-restore --nologo --verbosity quiet
```

Expected: all tests pass. Record the exact passed/failed/skipped counts.

### Step 2: Run frontend validation

```powershell
pnpm --dir src/IGW.WebUi test -- --run
pnpm --dir src/IGW.WebUi exec vue-tsc --noEmit
pnpm --dir src/IGW.WebUi build
```

Expected: all tests, type checking, and production build pass.

### Step 3: Inspect the final diff and boundary conditions

```powershell
git diff --check
git status --short
git diff --stat
```

Manually confirm:

- no secret-bearing project configuration is serialized;
- no probe path references `DeviceLoop.Read`, OPC UA sessions, or the shared driver scheduler;
- every background task is cancelled during reload and shutdown;
- existing SignalR point I/O remains intact;
- performance page queries only cached snapshots;
- all user/pre-existing dirty files remain preserved.

### Step 4: Request code review

Apply `superpowers:requesting-code-review` to the implementation diff. Resolve high-confidence correctness and lifecycle findings, then rerun only the checks affected by fixes followed by the full verification commands once.

### Step 5: Report evidence accurately

Report separately:

- source changes;
- automated test/build results with exact counts;
- runtime checks performed locally;
- remaining现场 PLC/KEP acceptance steps.

Do not claim that local tests identify the production network or PLC as the cause. The feature produces evidence;现场 attribution requires a diagnostic ZIP and matching KEP time window.
