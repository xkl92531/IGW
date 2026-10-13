# IGW Structured Log Context and Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development and implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every new IGW log identify its module and feature, make acquisition and interface records identify their task and operation, and replace the compressed one-line filter bar with a readable responsive layout.

**Architecture:** The observability logger provider adds a stable context envelope to every `ILogger` record before OTLP export. Business boundaries add the more precise acquisition owner, MES task/node, REST route, direction, protocol, operation, result and timing fields. The log query API accepts the new dimensions, while the Vue page derives a consistent human-readable summary from those fields.

**Tech Stack:** .NET 10, Microsoft `ILogger`, OpenTelemetry OTLP, OpenObserve 1.0.0, Vue 3, Element Plus, TypeScript and Vitest.

## Global Constraints

- Logging must remain asynchronous and must never add PLC reads or block acquisition.
- One physical PLC read batch produces one record; shared task owners are represented as a bounded list instead of duplicate records.
- Every new record has `igw_module`, `igw_feature`, `igw_event_kind` and `igw_logger`.
- Task-aware records use `igw_task_id`, `igw_task_name`, `igw_task_ids`, `igw_task_names`, `igw_node_id` and `igw_node_name` as applicable.
- Interface records use `igw_direction`, `igw_protocol`, `igw_method`, `igw_route`, `igw_target`, `igw_operation`, transport status and duration.
- Existing authorization, password, token, cookie, request-body and response-body redaction remains mandatory.
- Old OpenObserve records remain readable; missing new fields display as unknown instead of breaking the page.
- The frontend remains an original IGW design and does not copy another project's layout or CSS.

---

### Task 1: Universal log envelope

**Files:**
- Create: `src/IGW.Observability/LogSourceContext.cs`
- Modify: `src/IGW.Observability/RuntimeTelemetry.cs`
- Modify: `src/IGW.Core/GatewayTelemetry.cs`
- Test: `tests/IGW.Tests/LogSourceContextTests.cs`
- Test: `tests/IGW.Tests/GatewayTelemetryTests.cs`

**Interfaces:**
- Produces: `LogSourceContext.FieldsFor(string categoryName)` for stable module/feature defaults.
- Produces: an `ILoggerProvider` wrapper that merges defaults with structured state while preserving formatter output and scopes.

- [x] Write tests proving ordinary logs and already structured logs both receive a complete envelope without overwriting explicit fields.
- [x] Run focused tests and confirm they fail before the provider exists.
- [x] Implement source-category mapping and provider enrichment.
- [x] Run focused tests and confirm they pass.

### Task 2: Acquisition task ownership and interface context

**Files:**
- Modify: `src/IGW.Core/ScanDemandRegistry.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwInterfaceTelemetry.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRealtimeSignalR.cs`
- Test: `tests/IGW.Tests/CollectionTelemetryTests.cs`
- Test: `tests/IGW.Tests/MesTelemetryTests.cs`
- Test: `tests/IGW.Tests/InterfaceTelemetryTests.cs`
- Test: `tests/IGW.Tests/IgwSignalRTests.cs`

**Interfaces:**
- `ScanDemandRegistry.Acquire` accepts optional owner name and scope and publishes immutable owner metadata in its snapshot.
- Collection records contain a single task identity when unambiguous and bounded comma-separated owner lists when a batch is shared.
- HTTP telemetry reports the stable route pattern separately from the bounded concrete target path.

- [x] Extend focused tests with module, feature, task, node, route, direction and operation assertions.
- [x] Run focused tests and confirm the new assertions fail.
- [x] Propagate cached scan-demand owner metadata without new PLC access or per-cycle configuration traversal.
- [x] Enrich MES, REST and SignalR records at their existing transport boundaries.
- [x] Run focused tests and confirm they pass.

### Task 3: Query model and readable workbench

**Files:**
- Modify: `src/IGW.Observability/LogCenterContracts.cs`
- Modify: `src/IGW.Observability/LogCenterQuery.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/utils/logCenter.ts`
- Create: `src/IGW.WebUi/src/utils/logPresentation.ts`
- Modify: `src/IGW.WebUi/src/components/logs/LogFilterBar.vue`
- Modify: `src/IGW.WebUi/src/components/logs/LogTimeCorridor.vue`
- Modify: `src/IGW.WebUi/src/components/logs/LogDetailDrawer.vue`
- Modify: `src/IGW.WebUi/src/views/Logs.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `tests/IGW.Tests/LogCenterQueryTests.cs`
- Test: `src/IGW.WebUi/src/utils/logCenter.spec.ts`
- Test: `src/IGW.WebUi/src/utils/logPresentation.spec.ts`
- Test: `src/IGW.WebUi/src/components/logs/LogFilterBar.spec.ts`
- Test: `src/IGW.WebUi/src/views/Logs.workbench.spec.ts`

**Interfaces:**
- Query filters add module, feature, task and interface dimensions while retaining `taskId` compatibility.
- The primary filter row contains keyword, time, level and actions; the expandable row contains module, feature, PLC scope, task, interface and Trace ID.
- Corridor rows visibly render module/feature, task/source, interface operation and acquisition object before the detail drawer is opened.

- [x] Add failing backend and frontend contract tests.
- [x] Implement bounded OpenObserve predicates for the new fields.
- [x] Implement the two-level responsive filter and shared presentation helper.
- [x] Add bilingual labels and detail fields.
- [x] Run focused frontend and backend tests.

### Task 4: Regression and release package

**Files:**
- Modify: `installer/version.txt`
- Create: `docs/release-notes-1.6.50.md`
- Modify: `docs/log-center-acceptance-2026-09-13.md`

**Interfaces:**
- Produces: `installer/out/IGW-Setup-1.6.50.exe` with recorded size and SHA-256.

- [x] Run the complete .NET and frontend test suites.
- [x] Build the locked frontend, publish Runtime and LogService, and compile the Inno installer.
- [x] Verify package layout, ProductVersion, size and SHA-256.
- [x] Record verification evidence, commit all code and Markdown, push, and verify remote HEAD equals local HEAD.
