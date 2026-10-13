# IGW OpenObserve Unified Log Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the text-file-backed IGW log center with an independently designed, bundled OpenObserve log center that never blocks PLC acquisition.

**Architecture:** A new `IGW.LogService` supervises loopback-only OpenObserve. `IGW.Runtime` exports structured logs, metrics, and traces through OpenTelemetry with a bounded disk spool, while protected Runtime APIs query OpenObserve and power an original IGW Vue interface.

**Tech Stack:** .NET 10, OpenTelemetry .NET 1.18.0/1.15.1 instrumentation, OTLP/HTTP, OpenObserve 1.0.0 OSS, Vue 3, Element Plus, Vitest, Inno Setup, systemd.

## Global Constraints

- New business logs use OpenObserve; do not add SQLite or another searchable local structured log database.
- OpenObserve listens only on `127.0.0.1`; credentials never reach the browser.
- Export is asynchronous and bounded to 128 MiB or 512 batches; collection never waits for logging.
- Old `igw-*.log` files remain untouched and are not imported.
- Preserve `/api/v1/runtime-logs` compatibility for one version, backed by OpenObserve.
- Preserve QC, `/monitor`, MES flows, existing authentication, bilingual UI, and project data.
- Frontend templates, CSS, component structure, copy, charts, and assets must be designed and implemented independently for IGW.
- Validate only with Simulator and loopback services; do not connect to a real PLC.

---

### Task 1: Observability configuration and bounded transport spool

**Files:**
- Create: `src/IGW.Observability/IGW.Observability.csproj`
- Create: `src/IGW.Observability/ObservabilitySettings.cs`
- Create: `src/IGW.Observability/PrivateFiles.cs`
- Create: `src/IGW.Observability/TelemetrySpool.cs`
- Modify: `IGW.slnx`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`
- Test: `tests/IGW.Tests/ObservabilitySettingsTests.cs`
- Test: `tests/IGW.Tests/TelemetrySpoolTests.cs`

**Interfaces:**
- Produces: `ObservabilitySettings.Initialize(string dataDirectory, string? storageDirectory, int? port)` and `Load(string dataDirectory)`.
- Produces: `TelemetrySpool.EnqueueAsync(HttpRequestMessage, byte[], CancellationToken)`, `ReplayAsync(HttpClient, CancellationToken)`, and counters `Pending`, `Failed`, `Dropped`.

- [x] **Step 1: Write failing configuration tests**

```csharp
[Fact]
public void Initialize_persists_unicode_storage_loopback_and_credentials()
{
    string root = Temp("IGW 日志 空格");
    var created = ObservabilitySettings.Initialize(root, Path.Combine(root, "观测 数据"), 28020);
    var loaded = ObservabilitySettings.Load(root)!;
    Assert.Equal(created.InstanceId, loaded.InstanceId);
    Assert.Equal("127.0.0.1", loaded.OpenObserveEnvironment()["ZO_HTTP_ADDR"]);
    Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "观测 数据")), loaded.OpenObserveEnvironment()["ZO_DATA_DIR"]);
    Assert.DoesNotContain(loaded.Password, File.ReadAllText(Path.Combine(root, "observability.json")));
}
```

- [x] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ObservabilitySettingsTests|FullyQualifiedName~TelemetrySpoolTests"`

Expected: compilation fails because `IGW.Observability`, `ObservabilitySettings`, and `TelemetrySpool` do not exist.

- [x] **Step 3: Implement settings and spool**

Use an `ObservabilitySettings` JSON document with `StorageDirectory`, `Port`, `Username`, `ProtectedPassword`, `RetentionDays`, `PreviousStorageDirectories`, and `InstanceId`. Generate a 32-byte random secret, protect it using a machine-local key under `%ProgramData%\IGW`, write config and key through create-and-replace temporary files, and set OpenObserve environment variables for data, database, WAL, stream, and index directories.

Store failed OTLP requests as atomic `*.batch` files containing method, relative endpoint, content type, authorization-independent headers, and body. Enforce both limits before rename. Replay oldest first, delete only after a 2xx response, and expose counts without reading payload bodies.

- [x] **Step 4: Run focused tests and commit**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~ObservabilitySettingsTests|FullyQualifiedName~TelemetrySpoolTests"`

Expected: all focused tests pass.

Commit: `feat: add OpenObserve settings and bounded telemetry spool`

### Task 2: Independent OpenObserve supervisor service

**Files:**
- Create: `src/IGW.LogService/IGW.LogService.csproj`
- Create: `src/IGW.LogService/Program.cs`
- Create: `src/IGW.Observability/LogStorageChange.cs`
- Create: `tests/IGW.Tests/LogStorageChangeTests.cs`
- Modify: `IGW.slnx`
- Modify: `tests/IGW.Tests/IGW.Tests.csproj`

**Interfaces:**
- Consumes: `ObservabilitySettings` from Task 1.
- Produces: `LogStorageChange.Begin`, `Load`, `CopyHistoryAsync`, `Complete`, and `Fail`.
- Produces: `IGW.LogService --initialize --log-dir <path> [--port <port>]` and normal Windows service/systemd mode.

- [x] **Step 1: Write failing migration state tests**

```csharp
[Fact]
public async Task Migration_copies_history_without_deleting_source_and_records_progress()
{
    string source = Temp("旧 日志"), target = Temp("新 日志");
    File.WriteAllText(Path.Combine(source, "record.bin"), new string('x', 2048));
    var change = LogStorageChange.Begin(Temp("state"), source, target);
    await change.CopyHistoryAsync(CancellationToken.None);
    Assert.True(File.Exists(Path.Combine(source, "record.bin")));
    Assert.True(File.Exists(Path.Combine(target, "record.bin")));
    Assert.Equal(change.TotalBytes, change.CopiedBytes);
}
```

- [x] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~LogStorageChangeTests`

Expected: compilation fails because `LogStorageChange` does not exist.

- [x] **Step 3: Implement service supervision and rollback**

The service starts `openobserve/openobserve.exe` or `openobserve/openobserve`, injects only settings-owned environment variables, redirects output to 1 MiB rotating bootstrap diagnostic files, kills the child process tree on service stop, and retries crashes after five seconds. A pending storage change stops the child, copies all observability content except its state document, starts against the destination, waits up to 30 seconds for `/healthz`, then commits the new settings. A failed health check restores the source settings and persists a sanitized failure reason.

- [x] **Step 4: Run tests and service initialize smoke**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~LogStorageChangeTests`

Run: `dotnet run --project src/IGW.LogService/IGW.LogService.csproj -- --initialize --log-dir "$env:TEMP\IGW 日志 smoke"`

Expected: tests pass and initialization prints the normalized storage directory and selected loopback HTTP port without exposing credentials. It prefers `28020/28021` and skips to the next free pair when either port is occupied.

Commit: `feat: add independent OpenObserve supervisor service`

### Task 3: OpenTelemetry runtime and protected log APIs

**Files:**
- Create: `src/IGW.Observability/RuntimeTelemetry.cs`
- Create: `src/IGW.Observability/LogCenterContracts.cs`
- Create: `src/IGW.Observability/LogCenterQuery.cs`
- Create: `src/IGW.Observability/LogCenterApi.cs`
- Create: `src/IGW.Observability/LogRetention.cs`
- Create: `src/IGW.Observability/LogPayloadStore.cs`
- Create: `src/IGW.Observability/LogPayloadApi.cs`
- Modify: `src/IGW.Runtime/IGW.Runtime.csproj`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.Runtime/RuntimeLogging.cs`
- Modify: `src/IGW.Runtime/RuntimeLogApi.cs`
- Delete after compatibility tests move to the new API: `src/IGW.Runtime/FileLogger.cs`
- Modify: `tests/IGW.Tests/RuntimeLogCenterTests.cs`
- Test: `tests/IGW.Tests/LogCenterQueryTests.cs`
- Test: `tests/IGW.Tests/LogCenterApiTests.cs`
- Test: `tests/IGW.Tests/LogRetentionTests.cs`
- Test: `tests/IGW.Tests/LogPayloadStoreTests.cs`

**Interfaces:**
- Produces: `RuntimeTelemetry.Create(ObservabilitySettings)` returning `ILoggerFactory`, `ActivitySource`, `Meter`, `TelemetrySpool`, and payload store.
- Produces: `LogCenterQuery.Build(LogCenterRequest)` with fixed SQL, start/end microseconds, offset, and size.
- Produces HTTP endpoints specified in the design document.

- [x] **Step 1: Write failing query and API tests**

```csharp
[Fact]
public void Query_rejects_unbounded_ranges_and_escapes_exact_filters()
{
    var request = new LogCenterRequest { Category = "state", From = DateTimeOffset.UtcNow.AddHours(-1), To = DateTimeOffset.UtcNow,
        Device = "PLC' OR true --", Page = 2, PageSize = 50 };
    var query = LogCenterQuery.Build(request);
    Assert.Contains("igw_device = 'PLC'' OR true --'", query.Filter);
    Assert.Equal(50, query.From);
    Assert.Throws<ArgumentException>(() => LogCenterQuery.Build(new() { From = request.From.AddDays(-32), To = request.To }));
}
```

Add TestServer tests for 503 when OpenObserve is unavailable, empty results for a missing stream, status counters, retention validation, payload paging, and compatibility response shape.

- [x] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~LogCenter|FullyQualifiedName~LogRetention|FullyQualifiedName~LogPayload"`

Expected: compilation fails because the new contracts and endpoints do not exist.

- [x] **Step 3: Implement providers and APIs**

Configure OpenTelemetry logging with formatted messages, scopes, parsed state values, OTLP/HTTP endpoint `/api/default/v1/logs`, Basic authorization, 2-second export timeout, and a custom HTTP handler that spools failed bodies. The handler must override both synchronous `Send` and asynchronous `SendAsync`, because the exporter can use either path. Configure traces and metrics with the same resource instance ID. Exclude loopback OpenObserve paths from ASP.NET Core and HttpClient instrumentation.

Build query SQL exclusively from allow-listed categories and fields. Limit time range to 31 days, page size to 200, page to 10,000, search/filter values to 500 characters, OpenObserve timeout to 12 seconds, and response size to 8 MiB. Return `items`, `total`, `tookMs`, `histogram`, `devices`, and `outages`.

Implement payload files with random IDs, atomic metadata, redaction before persistence, 32,768-character pages, streamed download, retention cleanup, and failure counter. Compatibility endpoints translate old date/level/search/limit inputs to the new query and setting operations.

- [x] **Step 4: Run focused tests and commit**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~LogCenter|FullyQualifiedName~LogRetention|FullyQualifiedName~LogPayload|FullyQualifiedName~TelemetrySpool"`

Expected: all focused tests pass.

Commit: `feat: add OpenObserve telemetry and log center APIs`

### Task 4: Structured acquisition, state, performance, and interface telemetry

**Files:**
- Create: `src/IGW.Core/GatewayTelemetry.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRealtimeSignalR.cs`
- Test: `tests/IGW.Tests/GatewayTelemetryTests.cs`
- Test: `tests/IGW.Tests/CollectionTelemetryTests.cs`
- Test: `tests/IGW.Tests/MesTelemetryTests.cs`
- Test: `tests/IGW.Tests/InterfaceTelemetryTests.cs`

**Interfaces:**
- Produces: `GatewayTelemetry.Log(logger, category, level, message, IReadOnlyDictionary<string, object?> fields)`.
- Produces stable fields `igw_category`, `igw_channel`, `igw_device`, `igw_db`, `igw_task_id`, `igw_node_id`, `igw_duration_ms`, `igw_status`, and scan timing fields.

- [x] **Step 1: Write failing telemetry contract tests**

```csharp
[Fact]
public void Structured_event_redacts_credentials_and_keeps_stable_dimensions()
{
    var record = GatewayTelemetry.Build("interface", "POST completed", new Dictionary<string, object?> {
        ["device"] = "PLC8.OP140", ["target"] = "http://host/api?token=secret", ["authorization"] = "Bearer secret" });
    Assert.Equal("PLC8.OP140", record["igw_device"]);
    Assert.Equal("http://host/api", record["igw_target"]);
    Assert.DoesNotContain("secret", JsonSerializer.Serialize(record));
}
```

Add real Simulator loop tests that assert one summary event per scan batch, explicit disconnect/recovery events, complete timing units in milliseconds, and no per-tag log explosion.

- [x] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~TelemetryTests"`

Expected: compilation fails because `GatewayTelemetry` and event emission do not exist.

- [x] **Step 3: Instrument operation boundaries**

Create one `Activity` at the acquisition batch boundary and propagate it through MES upload and response write. Emit collection and performance summaries after the existing scan timing record is complete. Emit state records only on initial observation, disconnect/fault, reconnect, recovery, and quality transition. Record inbound/outbound interface summaries after status and duration are known; record HTTP transport success separately from MES business success.

Do not change scheduling, PLC read batches, retry semantics, MES success rules, or write queues. Do not include tag values, credentials, full point lists, or high-cardinality IDs in metric labels.

- [x] **Step 4: Run focused and full backend tests, then commit**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~DeviceLoop|FullyQualifiedName~Mes"`

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`

Expected: all tests pass with no new warnings.

Commit: `feat: emit structured gateway telemetry`

### Task 5: Original IGW log center frontend

**Files:**
- Replace: `src/IGW.WebUi/src/views/Logs.vue`
- Create: `src/IGW.WebUi/src/components/logs/LogScopeRail.vue`
- Create: `src/IGW.WebUi/src/components/logs/LogFilterBar.vue`
- Create: `src/IGW.WebUi/src/components/logs/LogTimeCorridor.vue`
- Create: `src/IGW.WebUi/src/components/logs/LogDetailDrawer.vue`
- Create: `src/IGW.WebUi/src/components/logs/LogTrendPanel.vue`
- Create: `src/IGW.WebUi/src/utils/logCenter.ts`
- Create: `src/IGW.WebUi/src/utils/logAttributes.ts`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/router.ts`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `src/IGW.WebUi/src/utils/logCenter.spec.ts`
- Test: `src/IGW.WebUi/src/views/Logs.workbench.spec.ts`
- Test: `src/IGW.WebUi/src/components/logs/LogTrendPanel.spec.ts`

**Interfaces:**
- Consumes: Task 3 status, query, retention, storage, and payload APIs.
- Produces: original IGW `/logs` workbench and `/performance` redirect.

- [x] **Step 1: Write failing query, stale-state, and UI tests**

```ts
it('builds a bounded query and omits empty filters', () => {
  const query = buildLogQuery(createLogFilters({ category: 'performance' }), 2, 50, Date.parse('2026-09-13T08:00:00Z'))
  expect(query).toMatchObject({ category: 'performance', page: 2, pageSize: 50 })
  expect(query).not.toHaveProperty('device')
})

it('marks device state unknown after sixty seconds', () => {
  expect(observedDeviceState({ status: 'online', timestamp: '2026-09-13T07:58:00Z' }, Date.parse('2026-09-13T08:00:00Z'))).toBe('unknown')
})
```

Mount `Logs.vue` with API mocks and assert IGW-specific scope rail, active-filter chips, time corridor, retained last results on query failure, detail drawer, pagination, status strip, and bilingual keys.

- [x] **Step 2: Run frontend tests and verify RED**

Run: `pnpm test -- src/utils/logCenter.spec.ts src/views/Logs.workbench.spec.ts src/components/logs/LogTrendPanel.spec.ts`

Expected: tests fail because the new utility and components do not exist.

- [x] **Step 3: Implement the original IGW interface**

Use Element Plus controls and existing IGW CSS variables. Keep filter drafts separate from the applied query. Cancel superseded requests with `AbortController`. Render categories in a left scope rail, records in a central time corridor with a severity edge, and structured details in a right drawer. Implement the SVG trend chart independently from requirements, with keyboard-accessible bucket selection and toggled series. Use translation catalog keys for all user-facing text.

Do not import, copy, rename, or mechanically translate source, styles, text, icons, or layout from another product. Verify responsive behavior at 1920, 1366, and 1024 CSS pixels.

- [x] **Step 4: Run frontend verification and commit**

Run: `pnpm test`

Run: `pnpm typecheck`

Run: `pnpm build`

Expected: all tests, typecheck, and production build pass.

Commit: `feat: add original IGW unified log workbench`

### Task 6: Bundle OpenObserve and install both services

**Files:**
- Create: `installer/openobserve-dependency.json`
- Create: `installer/get-openobserve.ps1`
- Create: `installer/test-openobserve-package.ps1`
- Create: `tools/linux/igw-log.service`
- Modify: `installer/IGW.iss`
- Modify: `installer/build.bat`
- Modify: `installer/build-linux.sh`
- Modify: `tools/linux/install.sh`
- Test: `installer/IGW.log-service.test.cjs`
- Test: `installer/test-log-service.ps1`

**Interfaces:**
- Consumes: `IGW.LogService` and OpenObserve 1.0.0.
- Produces: offline Windows installer and Linux archive with both services and preserved data directories.

- [x] **Step 1: Write failing installer contract tests**

Assert that the Inno script contains a log directory wizard page, registers `IGW Log Service`, sets its recovery actions, initializes settings before service start, binds no public OpenObserve port, and preserves observability data on uninstall. Assert Linux stages `openobserve`, `IGW.LogService`, and `igw-log.service`.

- [x] **Step 2: Run tests and verify RED**

Run: `node installer/IGW.log-service.test.cjs`

Expected: assertions fail because the log service and OpenObserve packaging are absent.

- [x] **Step 3: Implement pinned dependency and packaging**

Pin OpenObserve 1.0.0 OSS with Windows SHA256 `c9b4710873bedd297475657074fd75da9cfeaf6372ea962fcb6157d9102fdf5c`, Linux SHA256 `debba8c9267498bff9ca0994b1b96f9a27bab9e9b1cba1941b3ad89c3db4a1e8`, and license SHA256 `0d96a4ff68ad6d4b6f1f30f713b18d5184912ba8dd389f86aa7710db079abcb0`. Refuse packaging when any checksum differs.

Publish `IGW.Runtime` and `IGW.LogService` self-contained, stage OpenObserve and its license, add a selectable log root with write/free-space validation, register the log service independently, preserve existing observability settings on upgrade, and keep data on uninstall. Start the log service before Runtime but do not fail Runtime installation when OpenObserve health is delayed.

- [x] **Step 4: Run packaging tests and commit**

Run: `node installer/IGW.log-service.test.cjs`

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer/test-openobserve-package.ps1`

Expected: dependency and installer contract tests pass.

Commit: `feat: package local OpenObserve log service`

### Task 7: Real loopback integration, performance, and delivery verification

**Files:**
- Create: `docs/log-center-acceptance-2026-09-13.md`
- Modify: `installer/version.txt`
- Create: `docs/release-notes-1.6.47.md`

**Interfaces:**
- Consumes all prior tasks.
- Produces a verified release installer, SHA256, and explicit acceptance boundaries.

- [x] **Step 1: Run real OpenObserve integration with isolated directories**

Initialize and start bundled OpenObserve under a temporary Chinese/space directory. Start IGW Runtime with Simulator on alternate ports. Generate collection, state, performance, MES failure, and HTTP interface events. Query through `/api/v1/log-center/query` and assert all categories, structured fields, histogram, pagination, and correlation IDs.

- [ ] **Step 2: Verify failure isolation and migration**

Stop OpenObserve, keep the Simulator collection running, assert `pending > 0` and unchanged scan progress, restart the log service, assert pending batches replay, then migrate to another isolated directory. Force one unhealthy destination and verify rollback preserves the source.

- [ ] **Step 3: Verify performance and complete test suites**

Run the existing scan performance harness before and after telemetry with identical Simulator configuration. Record throughput, CPU, memory, export queue peak, and dropped count. Fail acceptance if logging adds PLC reads or if normal-level logging causes sustained scan throughput loss above 5%.

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`

Run: `pnpm test && pnpm typecheck && pnpm build` from `src/IGW.WebUi`.

Run all repository CJS contract tests under `installer`, `src/IGW.WebUi`, and `tests`.

- [x] **Step 4: Build installer and document evidence**

Set `installer/version.txt` to `1.6.47`, then run: `installer\build.bat --no-bump`

Record the exact installer path, version, byte size, SHA256, test totals, loopback acceptance, performance comparison, and unperformed clean-machine/real-PLC/long-duration tests in `docs/log-center-acceptance-2026-09-13.md`.

- [x] **Step 5: Perform originality review and commit**

Review all new frontend files for independent template hierarchy, CSS selectors, wording, component names, SVG/chart implementation, and static assets. Confirm no other product source path appears in tracked source or generated license notices except the OpenObserve dependency attribution.

Commit: `docs: record OpenObserve log center acceptance`
