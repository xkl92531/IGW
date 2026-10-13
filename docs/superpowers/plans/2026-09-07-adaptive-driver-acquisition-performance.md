# Adaptive Driver Acquisition Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make each device/period/priority produce one logical polling batch while every built-in driver automatically chooses the latency-optimal protocol-safe request set, exposes its real timing, and requires no user-supplied `maxGap`.

**Architecture:** Keep `DeviceLoop` as the only scheduler. Extend driver capabilities so drivers that own protocol partitioning receive the complete due tag set once per cycle; give Siemens a definition-aware storage span and contiguous DB reader; maintain one primary protocol session per IGW device identity; share a bounded adaptive request window between OPC UA and Siemens; instrument every real protocol call through `ProtocolOperationTiming`. Preserve management HTTP and the existing SignalR point/status path.

**Tech Stack:** .NET 10, C#, xUnit, Vue 3, TypeScript, Vitest, Element Plus, SQLite project settings, IoTClient, OPC UA .NET SDK, Inno Setup.

**Spec:** `docs/superpowers/specs/2026-09-07-adaptive-driver-acquisition-performance-design.md`

**Final correction amendment (2026-09-07):** Tasks 1–7 below describe the original implementation and 1.6.42 baseline. The final whole-branch correction wave must deliver a separately built **1.6.43** installer and retain 1.6.42. Unknown S7 Plus protocol return codes and local ABI buffer growth are not capacity evidence; the current pinned native library does not emit positively identified CapacityFailure, so real automatic Plus downshift remains inactive for unknown errors until a verified native classification is available. Explicit-capacity contract tests and recovery logic remain supported. See `docs/performance/2026-09-07-adaptive-acquisition-verification.md` for final results and artifacts.

## Global Constraints

- Per the user's latest workflow instruction, implement each remaining change first and then run its focused/regression tests. Checklist items labeled RED describe required coverage only; do not pause for or record an intentional failing run.
- Read only configured tags with active runtime demand; never browse or read an entire DB/address space as part of polling.
- One device/period/priority creates one logical polling batch; PDU, frame and server limits may produce multiple physical requests inside it.
- Optimize logical-batch completion P95 and priority-write wait, not physical request count alone. A proven smaller window may deliberately use more requests.
- Decode all configured members covered by one response from its bounded in-memory byte image, publish one logical batch to DataHub, then release the response reference; do not keep whole-DB copies.
- Default `batchingMode` is `auto`; users do not enter `maxGap` in the normal flow.
- Capacity failures split only the affected request and do not rebuild a healthy connection.
- Network/session failures use the existing reconnect path and do not train the capacity window.
- MES/QC writes keep their priority, stage order and write-back verification.
- Opening QC, picture view or acquisition diagnostics must not add PLC reads.
- Point read/write and realtime state use SignalR; configuration, logs and ZIP download remain HTTP.
- One IGW device identity owns one active primary protocol session. Different device IDs remain isolated even when their IP and connection settings match; an explicit standby is active only during failover.
- Unknown diagnostics remain null/`—`; never present missing values as zero.
- Final corrected release artifact is `IGW-Setup-1.6.43.exe`; preserve the original Task 7 1.6.42 artifact. Real PLC acceptance remains separate from automated verification.

---

### Task 1: Compile one logical polling batch and pass full tag definitions to protocol-aware parsers

**Files:**
- Modify: `src/IGW.Abstractions/Addressing.cs`
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/DeviceScanPlan.cs`
- Test: `tests/IGW.Tests/DeviceScanCapabilityTests.cs`
- Test: `tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs`

**Interfaces:**
- Produces: `ITagDefinitionAddressParser.TryParse(TagDef definition, out TagAddress result, out string? error)`.
- Produces: `DriverCapabilitySnapshot.PartitionsReadRequests` with default `false` for compatibility.
- Produces: one `CompiledScanBlock` for every `(PeriodMs, Priority)` when `PartitionsReadRequests` is true.

- [ ] **Step 1: Write failing tests for definition-aware parsing and one logical batch**

Add a fake driver capability with `PartitionsReadRequests: true` and 72 same-period bindings split across two address areas. Assert that `DeviceScanPlanCompiler.Compile(...)` returns one block, contains all 72 distinct tags, and does not duplicate a tag. Add a compatibility case with `PartitionsReadRequests: false` and assert the existing address/byte partitioning remains active.

```csharp
var capabilities = DriverCapabilitySnapshot.FromLimits(limits) with
{
    PartitionsReadRequests = true,
    MaxTagsPerRead = 19,
    MaxReadBytes = 880,
};
var blocks = DeviceScanPlanCompiler.Compile(bindings, hub,
    DeviceScanDemandSnapshot.Empty, limits, capabilities, includeAllTags: true);
var logical = Assert.Single(blocks);
Assert.Equal(72, logical.Tags.Count);
Assert.Equal(72, logical.Tags.Select(x => x.TagId).Distinct(StringComparer.Ordinal).Count());
```

- [ ] **Step 2: Run the focused tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~DeviceScanCapabilityTests|FullyQualifiedName~SiemensHybridDiagnosticsTests"
```

Expected: failure because `PartitionsReadRequests` and `ITagDefinitionAddressParser` do not exist and the compiler currently emits physical blocks.

- [ ] **Step 3: Add the backward-compatible contracts**

Add to `Addressing.cs`:

```csharp
public interface ITagDefinitionAddressParser : IAddressParser
{
    bool TryParse(TagDef definition, out TagAddress result, out string? error);
}
```

Append an optional positional member to `DriverCapabilitySnapshot` so existing constructors compile:

```csharp
bool PartitionsReadRequests = false
```

In `Engine.BindDevice`, select the richer parser when available:

```csharp
TagAddress addr;
string? err;
bool parsed = parser is ITagDefinitionAddressParser definitionParser
    ? definitionParser.TryParse(tag, out addr, out err)
    : parser.TryParse(tag.Address, tag.DataType, out addr, out err);
```

- [ ] **Step 4: Add logical-batch compilation without removing the compatibility planner**

In `DeviceScanPlanCompiler`, after collecting the tags for a period/priority group, create one `CompiledScanBlock` when `capabilities.PartitionsReadRequests` is true. Use `Area = -1`, `Start = 0`, `Count = 0`, preserve stable input order, and reuse a focused helper to populate handles. Keep the existing `AddressBlockOptimizer` and `TransferBatchPlanner` branch unchanged for drivers that return false.

```csharp
if (capabilities.PartitionsReadRequests)
{
    result.Add(CreateBlock(id++, periodGroup.Key.PeriodMs,
        periodGroup.Key.Priority, area: -1, start: 0, count: 0, tags, hub));
    continue;
}
```

- [ ] **Step 5: Run focused tests and commit**

Run the command from Step 2. Expected: PASS.

```powershell
git add src/IGW.Abstractions/Addressing.cs src/IGW.Abstractions/Drivers.cs src/IGW.Core/Engine.cs src/IGW.Core/DeviceScanPlan.cs tests/IGW.Tests/DeviceScanCapabilityTests.cs tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs
git commit -m "feat: schedule one logical driver polling batch"
```

---

### Task 2: Read S7 Classic contiguous DB ranges with correct string and array spans

**Files:**
- Create: `src/IGW.Drivers.IoTClient/SiemensClassicReadPlanner.cs`
- Create: `src/IGW.Drivers.IoTClient/SiemensClassicBlockCodec.cs`
- Create: `src/IGW.Abstractions/ProtocolStorageSize.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensAddressDescriptor.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensArrayCodec.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcFactories.cs`
- Create: `tests/IGW.Tests/SiemensClassicReadPlannerTests.cs`
- Modify: `tests/IGW.Tests/SiemensClassicArrayTests.cs`
- Modify: `tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `ITagDefinitionAddressParser` and `PartitionsReadRequests` from Task 1.
- Produces: `ProtocolStorageSize.Estimate(TagDef definition, TagAddress address)`.
- Produces: `SiemensClassicReadRange(int DbNumber, int Start, int Count, int GapBytes, IReadOnlyList<TagBinding> Tags)`.
- Produces: `SiemensClassicReadPlanner.Plan(IReadOnlyList<TagBinding> tags, int maxPayloadBytes)`.
- Produces: `SiemensClassicBlockCodec.Decode(...)` for scalar/string/WString values from one raw DB image.

- [ ] **Step 1: Add the real PLC3 layout regression fixture**

Generate 72 tags in `SiemensClassicReadPlannerTests`: DB2100 header fields at `DBX0.0`, `DBW2`, `DBW4`; 11 cells at `base = 6 + index * 266` with `STRING[50]`, two REALs, `STRING[100]`, WORD and `STRING[100]`; DB2101 WORD, WORD and `STRING[100]`. Parse every tag through `SiemensScanAddressParser.TryParse(TagDef, ...)`.

```csharp
Assert.Equal(52, AddressOf("DB2100.DBB6", IgwDataType.String, 50).Length);
Assert.Equal(102, AddressOf("DB2100.DBB66", IgwDataType.String, 100).Length);
var ranges = SiemensClassicReadPlanner.Plan(bindings, maxPayloadBytes: 880);
Assert.Equal(5, ranges.Count);
Assert.All(ranges, range => Assert.InRange(range.Count, 1, 880));
Assert.Equal(72, ranges.SelectMany(x => x.Tags).DistinctBy(x => x.TagId).Count());
```

Add decoding assertions for Bool, signed/unsigned integers, Float, Double, STRING and WSTRING at non-zero offsets. A short response or invalid S7 string length must return per-tag Bad quality.

- [ ] **Step 2: Run the Siemens tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~SiemensClassicReadPlannerTests|FullyQualifiedName~SiemensClassicArrayTests|FullyQualifiedName~SiemensHybridDiagnosticsTests"
```

Expected: failure because strings still parse as one byte and the contiguous planner/codec do not exist.

- [ ] **Step 3: Make Siemens address parsing definition-aware**

Implement `ITagDefinitionAddressParser` on `SiemensScanAddressParser`. Delegate legacy calls through a temporary `TagDef`, and compute storage span through `ProtocolStorageSize.Estimate`. The helper must encode scalar widths, `STRING[n] = n + 2`, `WSTRING[n] = 2n + 4`, logical arrays and confirmed struct sizes.

```csharp
public bool TryParse(TagDef definition, out TagAddress result, out string? error)
{
    if (!TryParseCore(definition.Address, definition.DataType, out result, out error)) return false;
    result = new TagAddress(result.Area, result.Offset,
        ProtocolStorageSize.Estimate(definition, result), result.BitOffset);
    return true;
}
```

- [ ] **Step 4: Implement deterministic S7 range planning**

Group only classic DB addresses by DB number and ascending offset. Merge overlapping/adjacent fields. Allow a positive gap only when the candidate remains within `maxPayloadBytes` and `gap <= Math.Min(16, maxPayloadBytes / 16)`. End the range before crossing the payload or DB boundary. Symbolic tags and arrays without a proven classic byte span remain separate.

```csharp
int allowedGap = Math.Min(16, Math.Max(0, maxPayloadBytes / 16));
bool merge = next.Start - end <= allowedGap && next.End - start <= maxPayloadBytes;
```

- [ ] **Step 5: Implement raw-block decoding and per-range fallback**

Use `SiemensClient.Read($"DB{db}.DBB{start}", (ushort)count, false)` once per planned range. Decode every configured member covered by the returned byte image in memory, accumulate the logical batch samples, and release the image reference after the driver call completes. On a connected data/range failure, split the member list in half and retry each half; on a disconnected client throw `IOException`. Never retain or publish bytes between configured fields, and never cache an entire DB. Task 4 will attach request descriptors at this exact call boundary.

```csharp
var result = client.Read($"DB{range.DbNumber}.DBB{range.Start}",
    checked((ushort)range.Count), false);
```

Keep `SiemensArrayCodec` for logical arrays and make it share the same configured `MaxPayloadBytes`.

- [ ] **Step 6: Derive a conservative payload and advertise internal partitioning**

In `IoTPlcFactories`, read device/channel `pdu`; choose 200 bytes for PDU below 480, 400 for PDU below 960, and 880 for PDU 960 or above. Pass it into the Classic path. Set Siemens `MaxReadBytes` to that payload and `PartitionsReadRequests = true`; remove the hardcoded planning dependency on `MaxGap = 0`.

- [ ] **Step 7: Run focused tests and commit**

Run the command from Step 2. Expected: PASS with exactly five PLC3 ranges and all decode assertions green.

```powershell
git add src/IGW.Abstractions/ProtocolStorageSize.cs src/IGW.Drivers.IoTClient tests/IGW.Tests/SiemensClassicReadPlannerTests.cs tests/IGW.Tests/SiemensClassicArrayTests.cs tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs
git commit -m "feat: batch S7 classic reads by safe DB ranges"
```

---

### Task 3: Share bounded adaptive windows and enforce device-scoped sessions

**Files:**
- Create: `src/IGW.Abstractions/AdaptiveRequestWindow.cs`
- Delete: `src/IGW.Drivers.OpcUaClient/AdaptiveBatchWindow.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Modify: `src/IGW.Core/OpcUaConnectionPool.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensClassicReadPlanner.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Modify: `tests/IGW.Tests/OpcUaAdaptiveBatchTests.cs`
- Modify: `tests/IGW.Tests/SharedOpcUaConnectionsTests.cs`
- Modify: `tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs`
- Create: `tests/IGW.Tests/SiemensPlusAdaptiveBatchTests.cs`

**Interfaces:**
- Produces: `AdaptiveRequestWindow` with `Current`, `Ceiling`, `Version`, `OnCapacityFailure(int attempted)`, `OnSuccess(int workUnits, double requestMs)` and an injectable monotonic millisecond clock.
- Consumes: real service capacity classification from OPC UA and `SiemensPlusResultCode`.
- Produces: a device-scoped physical session key that includes stable device identity without exposing credentials.

- [ ] **Step 1: Write RED tests for fixed learning rules**

Assert immediate halving on capacity failure, no growth before 20 stable successes, maximum 25% growth, no growth while the 60-second cooldown is active, and declared ceilings never exceeded. After growth, compare five normal-request samples with the preceding stable normalized cost (`requestMs / workUnits`); if it worsens by more than 20%, roll back to the prior window and hold growth for 60 seconds. Ignore cancelled, disconnected and retried samples. For S7 Plus, use a fake native adapter that rejects chunks above 32 and assert a 100-tag logical read completes as `32, 32, 32, 4` without disconnecting.

- [ ] **Step 2: Run adaptive-window tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~OpcUaAdaptiveBatchTests|FullyQualifiedName~SiemensPlusAdaptiveBatchTests"
```

- [ ] **Step 3: Move and harden the adaptive window**

Implement a thread-safe abstraction. `OnCapacityFailure` records `now + 60_000` as the next growth time. `OnSuccess` tracks bounded normalized-cost aggregates, grows after 20 qualifying successes by `Math.Min(Ceiling, Math.Max(Current + 1, Current + Current / 4))`, then evaluates five samples at the larger window. A greater-than-20% regression restores the prior window and starts the same cooldown. Keep only aggregates and the prior-window baseline, not an unbounded sample list.

```csharp
public sealed class AdaptiveRequestWindow
{
    public int Current { get; }
    public int Ceiling { get; }
    public long Version { get; }
    public int OnCapacityFailure(int attempted);
    public bool OnSuccess(int workUnits, double requestMs);
}
```

- [ ] **Step 4: Update OPC UA without changing subscription fallback semantics**

Replace `AdaptiveBatchWindow` with `AdaptiveRequestWindow`. Keep `MaxNodesPerRead` as a hard ceiling, undeclared initial 512, and only call `OnCapacityFailure` for capacity-scoped failures. Keep native subscriptions alive and poll only `_subscriptionFallbackTagIds`; do not add a full-tag periodic read.

Change `OpcUaConnectionPool.ConnectionKey` to include the stable `DeviceDef.Id` (fall back to the channel/device full name only for legacy empty IDs supplied by the runtime). Two devices with the same URL and security settings must create two pool entries; read, write, subscription and reconnect leases for the same device keep one entry and one generation. Update the existing shared-connection tests to assert device isolation while retaining same-device lifetime and reconnect deduplication.

- [ ] **Step 5: Partition S7 Plus scalar reads inside one logical driver call**

Chunk compatible scalar symbols by the current window, measure every `_native.Read` request, and combine results in original tag order. Arrays retain their existing safe slice path. Map Plus capacity/protocol-size results to same-session downshift; transport failure remains reconnectable.

Use the same window for S7 Classic payload bytes. Start from the model/PDU-safe payload selected in Task 2. Feed each successful raw range's byte count and duration into the window; after a proven normalized-cost regression, re-plan later logical batches with the restored smaller payload. Do not split or repeat a successful in-flight batch solely to train the window, and do not issue probe reads.

For Siemens, keep one active transport per device. A Plus-to-Classic fallback must disconnect Plus before Classic connects. Add validation/test coverage so one effective device cannot silently mix Classic DB addresses with Plus symbolic addresses and open both transports; return an exact configuration error identifying the conflicting point paths. A separately configured device on the same IP remains a separate session.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~OpcUaAdaptiveBatchTests|FullyQualifiedName~SiemensPlusAdaptiveBatchTests|FullyQualifiedName~DeviceLoopSubscriptionTests"
git add src/IGW.Abstractions/AdaptiveRequestWindow.cs src/IGW.Core/OpcUaConnectionPool.cs src/IGW.Core/Engine.cs src/IGW.Drivers.OpcUaClient src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs src/IGW.Drivers.IoTClient/SiemensClassicReadPlanner.cs src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs src/IGW.Drivers.IoTClient/IoTPlcDriver.cs src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs tests/IGW.Tests/OpcUaAdaptiveBatchTests.cs tests/IGW.Tests/SharedOpcUaConnectionsTests.cs tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs tests/IGW.Tests/SiemensPlusAdaptiveBatchTests.cs
git commit -m "feat: adapt OPC UA and S7 Plus request windows"
```

---

### Task 4: Instrument and audit every built-in driver-owned request plan

**Files:**
- Modify: `src/IGW.Abstractions/ProtocolOperationTiming.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTModbusDriver.cs`
- Modify: `src/IGW.Drivers.Mitsubishi/McTcpDeviceDriver.cs`
- Modify: `src/IGW.Drivers.EtherNetIP/EtherNetIpDriver.cs`
- Modify: `src/IGW.Drivers.Mqtt/MqttDriver.cs`
- Modify: `src/IGW.Drivers.Simulator/SimulatorDeviceDriver.cs`
- Create: `tests/IGW.Tests/DriverProtocolTimingTests.cs`
- Modify: `tests/IGW.Tests/ProtocolBatchIntegrationTests.cs`
- Modify: `tests/IGW.Tests/EtherNetIpFailureIsolationTests.cs`
- Modify: `tests/IGW.Tests/ProtocolWriteLatencySchedulingTests.cs`

**Interfaces:**
- Produces: `ProtocolRequestDescriptor(Operation, Scope, TagCount, PayloadBytes, GapBytes)`.
- Produces: bounded `ProtocolRequestSample` entries on `ProtocolTimingSnapshot`.
- Produces: synchronous `ProtocolOperationTiming.Measure<T>(Func<T>, ProtocolRequestDescriptor?)` and descriptor overload for `MeasureAsync`.

- [ ] **Step 1: Write RED timing and batching matrix tests**

For each built-in driver, execute a logical read under `ProtocolOperationTiming.Begin()` and assert the snapshot reports the real physical request count. Assert Modbus and MC contiguous tags use one request, generic FINS/MC IoTClient uses native `BatchRead`, EIP reports one request per libplctag operation, MQTT reports no polling request, and Simulator reports no network request. Assert the descriptor sample list is capped at 64 while aggregate counts continue.

Extend `ProtocolWriteLatencySchedulingTests` so a long logical read yields at a safe protocol boundary when a MES/QC priority write is queued; the write completes before the remaining read ranges resume.

- [ ] **Step 2: Run focused matrix tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~DriverProtocolTimingTests|FullyQualifiedName~ProtocolBatchIntegrationTests|FullyQualifiedName~EtherNetIpFailureIsolationTests"
```

- [ ] **Step 3: Extend timing without breaking existing call sites**

Add optional descriptor/sample members at the end of existing records. Implement synchronous measurement with the same cancellation/failure accounting as `MeasureAsync`. Retain at most 64 immutable samples and track `OmittedSamples` separately.

```csharp
public sealed record ProtocolRequestDescriptor(
    string Operation, string Scope, int TagCount, int PayloadBytes, int GapBytes);

public static T Measure<T>(Func<T> action, ProtocolRequestDescriptor? descriptor = null)
{
    long started = Stopwatch.GetTimestamp();
    try { return action(); }
    finally { Current.Value?.Record(Stopwatch.GetElapsedTime(started), descriptor); }
}
```

- [ ] **Step 4: Mark driver-owned partitioning and instrument real I/O boundaries**

Set `PartitionsReadRequests = true` for OPC UA, Siemens Hybrid, IoT Modbus, native MC, EIP, MQTT and Simulator. Keep it false for an unknown third-party driver. Wrap only actual client/socket/native calls, not local decode loops. Add queue timing around S7 Plus/EIP operation lanes. Do not wrap a driver `ReadAsync` around already measured child requests because that would double count.

- [ ] **Step 5: Keep protocol-specific automatic plans safe**

Use Modbus function code/area and 120-register or 512-bit limits. Allow MC same-area gaps up to 8 units only when total span stays within 480 words/960 bits. Keep EIP `SupportsBatchRead = false` until the selected library exposes a verified Multiple Service Packet API; limit workers to advertised `MaxInFlight` rather than 64. Preserve native `BatchRead` for FINS/IoT MC; if the library throws its unimplemented sentinel, cache that fact and report per-point physical requests.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~DriverProtocolTimingTests|FullyQualifiedName~ProtocolBatchIntegrationTests|FullyQualifiedName~EtherNetIpFailureIsolationTests|FullyQualifiedName~IoTPlcRetryTests"
git add src/IGW.Abstractions/ProtocolOperationTiming.cs src/IGW.Drivers.IoTClient src/IGW.Drivers.Mitsubishi src/IGW.Drivers.EtherNetIP src/IGW.Drivers.Mqtt src/IGW.Drivers.Simulator tests/IGW.Tests
git commit -m "feat: measure physical requests across built-in drivers"
```

---

### Task 5: Make automatic batching the default configuration and hide ordinary `maxGap`

**Files:**
- Create: `src/IGW.Abstractions/DriverBatchingSettings.cs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Modify: `src/IGW.Configurator/MainViewModel.cs`
- Modify: `src/IGW.Configurator/ChannelWizard.xaml.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcFactories.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTModbusDriver.cs`
- Modify: `src/IGW.Drivers.Mitsubishi/MitsubishiMcDriverFactory.cs`
- Modify: `src/IGW.Drivers.Mitsubishi/McTcpDeviceDriver.cs`
- Create: `tests/IGW.Tests/DriverBatchingSettingsTests.cs`
- Modify: `src/IGW.WebUi/src/views/Project.channel-maintenance.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.group-properties.test.cjs`

**Interfaces:**
- Produces: `DriverBatchingMode { Auto, Manual }` and `DriverBatchingSettings.Parse(IReadOnlyDictionary<string,string>)`.
- Produces: settings keys `batchingMode=auto|manual`; legacy `maxGap` becomes active only when manual mode is explicit.

- [ ] **Step 1: Add RED migration and UI source tests**

Assert missing mode parses as Auto even when legacy `maxGap=8` exists; explicit `manual` parses/clamps `maxGap` to 0–4096; new Web and desktop channel creation writes `batchingMode=auto`; ordinary creation does not write `maxGap`; the advanced panel shows `maxGap` only when manual and only for address-based drivers.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~DriverBatchingSettingsTests
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm test -- --run src/views/Project.channel-maintenance.test.cjs src/views/Project.group-properties.test.cjs"
```

- [ ] **Step 3: Implement parsing and both configuration surfaces**

Use Auto for absent/unknown modes. Preserve the old key in loaded dictionaries and exports. Pass parsed settings into the Siemens Classic, Modbus and MC planners: Auto uses their protocol cost rule; explicit Manual uses the clamped gap as an additional upper bound. In Web and desktop advanced properties, show a mode selector; show a numeric gap only after explicit Manual selection. For S7 Plus and OPC UA display “由协议容量自动决定” and no gap field. Add Chinese/English translations for `自动批处理`, `手动兼容`, `手动最大间隙` and the explanation.

- [ ] **Step 4: Run tests and commit**

Run both commands from Step 2. Expected: PASS.

```powershell
git add src/IGW.Abstractions/DriverBatchingSettings.cs src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/i18n/catalog.ts src/IGW.Configurator/MainViewModel.cs src/IGW.Configurator/ChannelWizard.xaml.cs src/IGW.Drivers.IoTClient src/IGW.Drivers.Mitsubishi tests/IGW.Tests/DriverBatchingSettingsTests.cs src/IGW.WebUi/src/views/Project.channel-maintenance.test.cjs src/IGW.WebUi/src/views/Project.group-properties.test.cjs
git commit -m "feat: default driver batching configuration to auto"
```

---

### Task 6: Show logical batches, polling points and physical request evidence clearly

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/ScanTimingHistory.cs`
- Modify: `src/IGW.Northbound.RestApi/AcquisitionDiagnosticsExport.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/components/ScanTimingPanel.vue`
- Modify: `src/IGW.WebUi/src/components/ScanTimingPanel.spec.ts`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Modify: `tests/IGW.Tests/ScanTimingHistoryTests.cs`
- Modify: `tests/IGW.Tests/ScanTimingApiTests.cs`
- Modify: `tests/IGW.Tests/AcquisitionDiagnosticsExportTests.cs`
- Modify: `tests/IGW.Tests/AcquisitionDiagnosticsNoReadTests.cs`

**Interfaces:**
- Produces: compatible DTO additions `LogicalBatchCount`, `LatestProtocolRequestCount`, `ProtocolRequestSamples`, `OmittedProtocolRequestSamples`.
- Keeps: existing `PollingTags`, `SubscribedTags`, `PlannedBlocks` JSON members for old clients.

- [ ] **Step 1: Add RED API/export/UI tests using the 71-point example**

Create a snapshot with 71 polling points, one logical batch and five physical requests. Assert UI text includes `轮询点位 71`, `逻辑批次 1`, `实际协议请求 5`; assert it does not render `轮询 / 订阅 71 / 0`. Expand the row and assert request scopes show DB/range, point count, payload, gap and duration. Assert CSV/JSON export contains the same counts and no secrets.

Add two same-endpoint device snapshots and assert the UI/export shows two device sessions, each with its own anonymous device fingerprint and generation. Add a near-limit fixture and assert a warning is shown without logging the raw session key or credentials.

- [ ] **Step 2: Run focused backend and frontend tests and confirm RED**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~ScanTiming|FullyQualifiedName~AcquisitionDiagnostics"
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm test -- --run src/components/ScanTimingPanel.spec.ts"
```

- [ ] **Step 3: Extend immutable records and summaries**

Append new optional members to `ScanTimingRecord` and `ScanTimingDeviceSummary`. Begin `ProtocolOperationTiming` for every driver, not only OPC UA. Populate logical batch count from the compiled plan and physical request count from the newest timing snapshot. Preserve null when no protocol instrumentation exists.

```csharp
using var protocolTiming = ProtocolOperationTiming.Begin();
var samples = await scanDriver.ReadAsync(readTags, ct);
```

- [ ] **Step 4: Update logs and diagnostic ZIP**

Add aggregate fields to the existing rate-limited successful batch log and immediate error/downshift logs. Add physical request rows to `requests.csv`; cap exported request samples through the existing retained 200-batch window. Keep the existing sensitive-value filters and no-extra-I/O test.

- [ ] **Step 5: Update frontend labels and expansion**

Replace the ambiguous overview header with separate `轮询点位` and `订阅点位` columns. Rename `读取块` to `逻辑批次`. Add `最近实际请求` and display `—` when absent. Expanded details render the bounded request sample table. Keep the existing pause-on-expand behavior so automatic refresh cannot close the row.

```vue
<el-table-column prop="pollingTags" :label="$t('轮询点位')" />
<el-table-column prop="subscribedTags" :label="$t('订阅点位')" />
<el-table-column prop="logicalBatchCount" :label="$t('逻辑批次')" />
<el-table-column :label="$t('最近实际请求')">
  <template #default="{ row }">{{ row.latestProtocolRequestCount ?? '—' }}</template>
</el-table-column>
```

- [ ] **Step 6: Run focused tests and commit**

Run both commands from Step 2. Expected: PASS.

```powershell
git add src/IGW.Core src/IGW.Northbound.RestApi/AcquisitionDiagnosticsExport.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/components/ScanTimingPanel.vue src/IGW.WebUi/src/components/ScanTimingPanel.spec.ts src/IGW.WebUi/src/i18n/catalog.ts tests/IGW.Tests/ScanTimingHistoryTests.cs tests/IGW.Tests/ScanTimingApiTests.cs tests/IGW.Tests/AcquisitionDiagnosticsExportTests.cs tests/IGW.Tests/AcquisitionDiagnosticsNoReadTests.cs
git commit -m "feat: distinguish polling points from protocol requests"
```

---

### Task 7: Regression, performance evidence and 1.6.42 installer

**Files:**
- Create: `docs/performance/2026-09-07-adaptive-acquisition-verification.md`
- Modify: `installer/version.txt` through the existing build script only
- Generated: `installer/out/IGW-Setup-1.6.42.exe`

**Interfaces:**
- Consumes: all tasks above.
- Produces: reproducible automated evidence and the Windows installer with SHA-256.

- [ ] **Step 1: Run the PLC3 fixture and record request reduction**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~SiemensClassicReadPlannerTests|FullyQualifiedName~DriverProtocolTimingTests" --logger "console;verbosity=normal"
```

Record in the verification document: 72 configured points, one logical batch, five initial planned Classic ranges at payload 880, no duplicate/missing points, exact value/quality parity, and one response image decoded into multiple points. Add a latency-shaped protocol-stub case where the larger window is intentionally slower; record that the adaptive rollback may use more requests but lowers logical-batch completion P95.

Record session-lifecycle evidence: one device's polling and priority writes use one primary session; two different device IDs at the same IP use two independent sessions; reconnect increments only the affected device generation and does not create overlapping live sessions.

- [ ] **Step 2: Run complete backend verification**

```powershell
dotnet test IGW.slnx --no-restore --nologo
```

Expected: all tests pass; capture total passed/failed/skipped counts.

- [ ] **Step 3: Run complete frontend verification**

```powershell
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm test -- --run"
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm exec vue-tsc --noEmit"
cmd /c "cd src\IGW.WebUi && set COREPACK_ENABLE_PROJECT_SPEC=0&& pnpm build"
```

Expected: all Vitest tests, typecheck and production build pass.

- [ ] **Step 4: Verify diagnostics add no device I/O**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AcquisitionDiagnosticsNoReadTests
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~IgwSignalRTests
```

Expected: opening/querying/exporting acquisition diagnostics adds zero driver reads, and point read/write plus realtime status still use SignalR.

- [ ] **Step 4a: Prepare a reproducible KEPServer parity worksheet**

Add the exact field A/B procedure to the verification document: same computer, NIC, PLC, point table, scan periods, ten-minute window and priority-write script; run KEP and IGW separately so they do not compete for PLC sessions. Capture completion P50/P95, protocol-request P95, requests/second, tag throughput, missed periods, timeout/reconnect counts, priority-write P95, CPU and working-set memory. Define parity as IGW completion P95 <= 105% of KEP, IGW write P95 <= KEP, and no extra missed periods. Do not claim parity or superiority until the field worksheet is populated.

- [ ] **Step 5: Build and test the installer**

Verify `D:\sdk` first if the build reports a missing reusable tool. Then run:

```powershell
cmd /c installer\build.bat
node --test installer\bump_version.test.cjs installer\build.runtime.test.cjs installer\IGW.firewall.test.cjs
```

Expected: `installer/out/IGW-Setup-1.6.42.exe` exists and installer tests report 4/4 passing.

- [ ] **Step 6: Record artifact identity and commit release evidence**

```powershell
$artifact = Resolve-Path 'installer\out\IGW-Setup-1.6.42.exe'
Get-Item $artifact | Select-Object FullName,Length
Get-FileHash $artifact -Algorithm SHA256
Get-AuthenticodeSignature $artifact | Select-Object Status,StatusMessage
git status --short
```

Copy the installer to `E:\项目\IGW\installer\out\IGW-Setup-1.6.42.exe` without touching the original dirty source tree. Update the verification document with test counts, size, SHA-256 and signature state.

```powershell
git add docs/performance/2026-09-07-adaptive-acquisition-verification.md installer/version.txt
git commit -m "chore: release installer 1.6.42"
```

- [ ] **Step 7: Final branch verification**

```powershell
git status --short --branch
git log --oneline -10
```

Expected: clean `codex/layered-acquisition-diagnostics` worktree. Report automated evidence separately from the pending 10-minute real PLC3 field comparison.
