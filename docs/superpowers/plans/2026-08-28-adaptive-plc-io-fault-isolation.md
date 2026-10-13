# IGW Adaptive PLC I/O Fault Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make high-density PLC collection behave like a mature gateway: learn safe request sizes, prioritize writes, preserve healthy sessions on point/block/capacity failures, and reconnect only for confirmed session or transport loss.

**Architecture:** Add a protocol-neutral operation-failure contract in `IGW.Abstractions`, let drivers report failure scope and retry disposition, and make `DeviceLoop` act on that contract rather than disconnecting for every read exception. OPC UA starts with a conservative adaptive window when the server does not declare limits, feeds the learned capability back into scan-plan compilation, and converts large-request timeouts into same-session downshift/replan events so queued writes can run first.

**Tech Stack:** .NET 10, C#, xUnit, OPC Foundation UA .NET Standard client, existing IGW `DeviceLoop`, `DeviceScanPlanCompiler`, and driver capability interfaces.

**Spec:** `docs/superpowers/specs/2026-08-28-capability-driven-plc-io-engine-design.md`

## Global Constraints

- Users configure business scan periods and write relationships only; no batch-size, thread-count, or fast-lane controls are exposed.
- Point, block, capacity, permission, type, and verification errors never disconnect the device.
- Only confirmed session or transport loss triggers connection reset or channel hard reconnect.
- Every accepted write is actively read back and mismatches are selectively retried up to 10 times.
- The initial OPC UA probe window is not a total project limit; all configured points remain schedulable.
- Preserve all unrelated dirty-worktree changes and stage only files belonging to each task.

**Scope split:** This plan implements the I/O scheduling, failure isolation, and write-latency portion of the spec. Unified array recognition and virtualized array selection are already tracked in `docs/superpowers/plans/2026-08-28-unified-array-recognition-implementation.md` and are not duplicated here.

---

### Task 1: Protocol-neutral operation failure contract

**Files:**
- Create: `src/IGW.Abstractions/DriverOperationFailure.cs`
- Test: `tests/IGW.Tests/DriverOperationFailureTests.cs`

**Interfaces:**
- Produces: `DriverFailureScope`, `DriverRetryDisposition`, `DriverOperationException`, and `DriverOperationFailure.FromException(Exception, ConnectionState)`.
- Consumes: existing `IGW.Abstractions.StatusCode` and `ConnectionState`.

- [ ] **Step 1: Write failing classification tests**

```csharp
[Theory]
[InlineData(DriverFailureScope.Capacity, DriverRetryDisposition.ReduceBatchAndRetry, false)]
[InlineData(DriverFailureScope.Point, DriverRetryDisposition.DoNotRetry, false)]
[InlineData(DriverFailureScope.Transport, DriverRetryDisposition.RebuildConnection, true)]
public void Explicit_driver_failure_preserves_scope_and_retry(
    DriverFailureScope scope,
    DriverRetryDisposition retry,
    bool connectionLost)
{
    var error = new DriverOperationException(
        "test", scope, retry, StatusCode.BadTimeout);

    DriverOperationFailure actual = DriverOperationFailure.FromException(
        error, connectionLost ? ConnectionState.Faulted : ConnectionState.Connected);

    Assert.Equal(scope, actual.Scope);
    Assert.Equal(retry, actual.Retry);
    Assert.Equal(connectionLost, actual.ConnectionLost);
}

[Fact]
public void Plain_timeout_on_a_connected_driver_is_an_operation_timeout()
{
    DriverOperationFailure actual = DriverOperationFailure.FromException(
        new TimeoutException("request timed out"), ConnectionState.Connected);

    Assert.Equal(DriverFailureScope.Block, actual.Scope);
    Assert.Equal(DriverRetryDisposition.RetrySameConnection, actual.Retry);
    Assert.False(actual.ConnectionLost);
    Assert.Equal(StatusCode.BadTimeout, actual.Status);
}
```

- [ ] **Step 2: Run the tests and verify the contract is missing**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~DriverOperationFailureTests`

Expected: FAIL because `DriverFailureScope` and related types do not exist.

- [ ] **Step 3: Implement the failure contract**

```csharp
public enum DriverFailureScope { Point, Block, Capacity, Session, Transport, Overload }
public enum DriverRetryDisposition { DoNotRetry, RetrySameConnection, ReduceBatchAndRetry, RebuildSession, RebuildConnection }

public sealed class DriverOperationException : Exception
{
    public DriverFailureScope Scope { get; }
    public DriverRetryDisposition Retry { get; }
    public StatusCode Status { get; }

    public DriverOperationException(string message, DriverFailureScope scope,
        DriverRetryDisposition retry, StatusCode status, Exception? inner = null)
        : base(message, inner)
    {
        Scope = scope;
        Retry = retry;
        Status = status;
    }
}

public readonly record struct DriverOperationFailure(
    DriverFailureScope Scope,
    DriverRetryDisposition Retry,
    StatusCode Status,
    bool ConnectionLost,
    string Message)
{
    public static DriverOperationFailure FromException(Exception error, ConnectionState state)
    {
        if (error is DriverOperationException explicitFailure)
            return new(explicitFailure.Scope, explicitFailure.Retry, explicitFailure.Status,
                explicitFailure.Retry is DriverRetryDisposition.RebuildSession or DriverRetryDisposition.RebuildConnection,
                explicitFailure.Message);
        if (error is TimeoutException && state == ConnectionState.Connected)
            return new(DriverFailureScope.Block, DriverRetryDisposition.RetrySameConnection,
                StatusCode.BadTimeout, false, error.Message);
        if (error is IOException || state is ConnectionState.Disconnected or ConnectionState.Faulted)
            return new(DriverFailureScope.Transport, DriverRetryDisposition.RebuildConnection,
                StatusCode.BadCommFailure, true, error.Message);
        return new(DriverFailureScope.Block, DriverRetryDisposition.DoNotRetry,
            StatusCode.BadDeviceFailure, false, error.Message);
    }
}
```

Include inner-exception traversal for `SocketException`, `IOException`, timeout, value/type, and authorization exceptions. Preserve the explicit driver status code unchanged.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~DriverOperationFailureTests`

Expected: PASS.

- [ ] **Step 5: Commit the contract**

```powershell
git add -- src/IGW.Abstractions/DriverOperationFailure.cs tests/IGW.Tests/DriverOperationFailureTests.cs
git commit -m "feat: classify driver operation failures"
```

---

### Task 2: Preserve sessions and pending writes on transient read failures

**Files:**
- Modify: `src/IGW.Core/Engine.cs:414-548`
- Test: `tests/IGW.Tests/DeviceLoopTransientReadTests.cs`
- Test: `tests/IGW.Tests/DeviceLoopScanDemandTests.cs`

**Interfaces:**
- Consumes: `DriverOperationFailure.FromException(Exception, ConnectionState)` from Task 1.
- Produces: `DeviceLoop` behavior that distinguishes same-session retry from true connection loss.

- [ ] **Step 1: Add a failing regression test for the field failure chain**

Create a fake driver whose first large read throws a `DriverOperationException` with `Capacity/ReduceBatchAndRetry/BadTimeout`, whose state remains connected, and whose write/readback succeeds. Assert:

```csharp
Assert.True((await write).IsGood);
Assert.Equal(0, driver.DisconnectCalls);
Assert.Equal(ConnectionState.Connected, driver.State);
Assert.True(driver.WriteCalls > 0);
```

Also add a test where a manual reconnect cancellation throws `DriverOperationException` with `Session/RetrySameConnection/BadTimeout` and assert that no false transport incident is recorded.

- [ ] **Step 2: Run the two regression tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~DeviceLoopTransientReadTests|FullyQualifiedName~DeviceLoopScanDemandTests"`

Expected: FAIL because the read catch resets transport and calls `FailPendingWrites` unconditionally.

- [ ] **Step 3: Replace the unconditional read reset with scoped handling**

In both half-open and normal scan read catches, classify the exception and branch explicitly:

```csharp
var failure = DriverOperationFailure.FromException(ex, _active.State);
MarkBlockBad(block, failure.Status);
if (!failure.ConnectionLost)
{
    int delay = failure.Retry == DriverRetryDisposition.ReduceBatchAndRetry ? 1 : block.PeriodMs;
    _deadlines.CompleteAfter(block, _clock.NowMilliseconds, Math.Clamp(delay, 1, 5_000));
    _planInitialized = failure.Retry == DriverRetryDisposition.ReduceBatchAndRetry ? false : _planInitialized;
    hadReadFailure = true;
    break; // leave the read loop so queued writes drain immediately
}

await ResetTransportAsync(_active, ct);
connectionLost = true;
_planInitialized = false;
break;
```

Only the `connectionLost` branch may call `FailPendingWrites(StatusCode.BadCommFailure)`. Expected cancellation caused by the loop token exits quietly.

- [ ] **Step 4: Run the DeviceLoop tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~DeviceLoopTransientReadTests|FullyQualifiedName~DeviceLoopScanDemandTests|FullyQualifiedName~WriteVerificationEngineTests"`

Expected: PASS, including existing write-timeout and bad-DB isolation tests.

- [ ] **Step 5: Commit the engine isolation**

```powershell
git add -- src/IGW.Core/Engine.cs tests/IGW.Tests/DeviceLoopTransientReadTests.cs tests/IGW.Tests/DeviceLoopScanDemandTests.cs
git commit -m "fix: preserve PLC sessions on transient reads"
```

---

### Task 3: OPC UA adaptive request window and timeout downshift

**Files:**
- Create: `src/IGW.Drivers.OpcUaClient/AdaptiveBatchWindow.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs:17-72,243-364,453-497`
- Test: `tests/IGW.Tests/OpcUaAdaptiveBatchTests.cs`
- Test: `tests/IGW.Tests/OpcUaClientEndpointTests.cs`

**Interfaces:**
- Produces: `AdaptiveBatchWindow.Current`, `Ceiling`, `Version`, `OnSuccess()`, and `OnCapacityFailure(int attempted)`.
- Produces: OPC UA capacity timeouts as `DriverOperationException(Capacity, ReduceBatchAndRetry, BadTimeout)`.

- [ ] **Step 1: Write failing adaptive-window tests**

```csharp
[Fact]
public void Undeclared_limit_starts_conservatively_without_limiting_total_points()
{
    var window = AdaptiveBatchWindow.Undeclared(initial: 512, ceiling: 100_000);
    Assert.Equal(512, window.Current);
    Assert.Equal(100_000, window.Ceiling);
}

[Fact]
public void Timeout_halves_only_the_current_request_window()
{
    var window = AdaptiveBatchWindow.Undeclared(512, 100_000);
    Assert.Equal(256, window.OnCapacityFailure(512));
    Assert.Equal(100_000, window.Ceiling);
}

[Fact]
public void Thirty_two_successes_grow_slowly_without_exceeding_the_ceiling()
{
    var window = AdaptiveBatchWindow.Declared(256);
    for (int i = 0; i < 32; i++) window.OnSuccess();
    Assert.Equal(256, window.Current); // declared server ceiling is authoritative
}
```

Add theory cases proving `BadTooManyOperations`, request/response-too-large, `BadRequestTimeout`, and `TimeoutException` are capacity pressure, while `BadConnectionClosed` is transport loss and `BadRequestInterrupted` is expected cancellation/reconnect interruption.

- [ ] **Step 2: Run the OPC UA adaptive tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaAdaptiveBatchTests|FullyQualifiedName~OpcUaClientEndpointTests"`

Expected: FAIL because the window type and timeout classification do not exist.

- [ ] **Step 3: Implement the adaptive window and safe initial limit**

Replace the fallback `10_000` with `512` for undeclared OPC UA limits. Keep `100_000` only as the exploration ceiling. Do not change a positive declared limit.

```csharp
private const int UndeclaredInitialBatch = 512;

private static bool IsCapacityPressure(Exception error) =>
    ErrorChain(error).Any(item => item is TimeoutException ||
        item is ServiceResultException service && service.StatusCode is
            StatusCodes.BadTooManyOperations or
            StatusCodes.BadRequestTooLarge or
            StatusCodes.BadResponseTooLarge or
            StatusCodes.BadEncodingLimitsExceeded or
            StatusCodes.BadOutOfMemory or
            StatusCodes.BadRequestTimeout);
```

On a multi-node capacity failure, reduce the window and throw an explicit capacity operation exception instead of continuing all remaining chunks inside one `ReadAsync` call. This returns control to `DeviceLoop`, allowing pending writes to execute before the recompiled smaller read blocks.

- [ ] **Step 4: Keep genuine transport errors explicit**

Map SecureChannel/session/connection-closed statuses to `RebuildSession` or `RebuildConnection`. Map `BadRequestInterrupted` caused by cancellation/reconnect to `RetrySameConnection` without a false transport status. Preserve original OPC UA status codes in diagnostics.

- [ ] **Step 5: Run OPC UA and endpoint tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~OpcUaAdaptiveBatchTests|FullyQualifiedName~OpcUaClientEndpointTests|FullyQualifiedName~OpcUaArrayCodecTests"`

Expected: PASS.

- [ ] **Step 6: Commit the OPC UA adaptation**

```powershell
git add -- src/IGW.Drivers.OpcUaClient/AdaptiveBatchWindow.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs tests/IGW.Tests/OpcUaAdaptiveBatchTests.cs tests/IGW.Tests/OpcUaClientEndpointTests.cs
git commit -m "fix: adapt OPC UA batches without reconnecting"
```

---

### Task 4: Compile scan blocks from learned driver capabilities

**Files:**
- Modify: `src/IGW.Core/Engine.cs:564-626`
- Modify: `src/IGW.Core/DeviceScanPlan.cs:50-112`
- Test: `tests/IGW.Tests/DeviceLoopScanDemandTests.cs`
- Test: `tests/IGW.Tests/HighDensityWritePipelineTests.cs`

**Interfaces:**
- Consumes: `IDriverCapabilityProvider.Capabilities` and its monotonic `Version`.
- Produces: `DeviceScanPlanCompiler.Compile(..., DriverLimits limits, DriverCapabilitySnapshot capabilities, ...)`.

- [ ] **Step 1: Write a failing capability-replan test**

Use 2,477 symbolic tags with a fake capability provider. Start at `MaxTagsPerRead=512`, change it to `256`, increment `Version`, and assert that subsequent read blocks contain at most 256 tags while all 2,477 tags remain scheduled.

```csharp
Assert.Equal(2477, driver.DistinctReadTags.Count);
Assert.All(driver.ReadBatchSizesAfterCapabilityChange, size => Assert.InRange(size, 1, 256));
```

- [ ] **Step 2: Run the capability tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~DeviceLoopScanDemandTests|FullyQualifiedName~HighDensityWritePipelineTests"`

Expected: FAIL because `RebuildPlanIfNeeded` passes static `_active.Limits` instead of the learned capability snapshot.

- [ ] **Step 3: Make the compiler honor both physical and learned limits**

Create an effective limit where physical frame/area constraints remain from `DriverLimits`, but symbolic request counts use the learned snapshot:

```csharp
var limits = new DriverLimits
{
    MaxReadUnits = _active.Limits.MaxReadUnits,
    MaxGap = _active.Limits.MaxGap,
    MaxTagsPerRead = Math.Max(1, ActiveCapabilities.MaxTagsPerRead),
    MaxTagsPerWrite = Math.Max(1, ActiveCapabilities.MaxTagsPerWrite),
};
```

Recompile when `Capabilities.Version` changes. Preserve previous deadlines by `ScanBlockKey` where possible; newly split blocks become due immediately without replaying missed historical periods.

- [ ] **Step 4: Verify write preemption between learned read blocks**

Extend `Pending_write_preempts_remaining_due_read_blocks` so a write queued during the first 512-point read completes before the next large read block. Assert no transport reset and a bounded number of reads before the write.

- [ ] **Step 5: Run focused scheduling tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~DeviceLoopScanDemandTests|FullyQualifiedName~HighDensityWritePipelineTests|FullyQualifiedName~DeviceTimingMetricsTests"`

Expected: PASS.

- [ ] **Step 6: Commit learned-plan compilation**

```powershell
git add -- src/IGW.Core/Engine.cs src/IGW.Core/DeviceScanPlan.cs tests/IGW.Tests/DeviceLoopScanDemandTests.cs tests/IGW.Tests/HighDensityWritePipelineTests.cs
git commit -m "feat: compile scans from learned driver capacity"
```

---

### Task 5: Apply point/block/session isolation across protocol drivers

**Files:**
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTModbusDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Modify: `src/IGW.Drivers.EtherNetIP/EtherNetIpDriver.cs`
- Modify: `src/IGW.Drivers.Mitsubishi/McTcpDeviceDriver.cs`
- Test: `tests/IGW.Tests/IoTPlcRetryTests.cs`
- Test: `tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs`
- Test: `tests/IGW.Tests/EtherNetIpArrayTests.cs`
- Test: `tests/IGW.Tests/DeviceLoopTransientReadTests.cs`

**Interfaces:**
- Consumes: Task 1 failure contract.
- Produces: each protocol returns per-point `WriteResult` for data/permission/type failures and throws explicit session/transport failures only when the connection is actually unusable.

- [ ] **Step 1: Add protocol-matrix failure tests**

For each driver family, inject or simulate:

- one invalid address among valid addresses;
- one read-only point among writable points;
- one operation timeout while the driver remains connected;
- one socket/session loss.

Assert that the first three preserve the connection and other Good points, while socket/session loss requests a reset. Reuse the existing fake transports in each test file; do not require field hardware.

- [ ] **Step 2: Run the protocol-matrix tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~IoTPlcRetryTests|FullyQualifiedName~SiemensHybridDiagnosticsTests|FullyQualifiedName~EtherNetIpArrayTests|FullyQualifiedName~DeviceLoopTransientReadTests"`

Expected: FAIL for drivers that still throw undifferentiated exceptions.

- [ ] **Step 3: Normalize protocol failures at each driver boundary**

At each driver's read/write service boundary:

```csharp
catch (Exception ex) when (ProtocolErrorClassifier.IsPointOrBlockFailure(ex, out var failure))
{
    throw new DriverOperationException(failure.Message, failure.Scope,
        failure.Retry, failure.Status, ex);
}
```

Keep existing protocol-specific diagnostics, but set scope/retry explicitly. Do not convert genuine socket, closed-session, or disposed-native-handle errors into point failures. Return `WriteResult` per tag when the underlying API supplies per-item statuses.

- [ ] **Step 4: Run all driver-focused tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~IoTPlcRetryTests|FullyQualifiedName~SiemensHybridDiagnosticsTests|FullyQualifiedName~EtherNetIpArrayTests|FullyQualifiedName~SiemensClassicArrayTests|FullyQualifiedName~SiemensPlusArrayTests|FullyQualifiedName~RegisterAndMqttArrayTests"`

Expected: PASS.

- [ ] **Step 5: Commit protocol isolation**

```powershell
git add -- src/IGW.Drivers.IoTClient src/IGW.Drivers.EtherNetIP src/IGW.Drivers.Mitsubishi tests/IGW.Tests/IoTPlcRetryTests.cs tests/IGW.Tests/SiemensHybridDiagnosticsTests.cs tests/IGW.Tests/EtherNetIpArrayTests.cs tests/IGW.Tests/DeviceLoopTransientReadTests.cs
git commit -m "fix: isolate PLC protocol operation failures"
```

---

### Task 6: Exact write diagnostics and 2,477-point stress regression

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs:73-84`
- Modify: `src/IGW.Core/DeviceTimingMetrics.cs`
- Modify: `src/IGW.Core/Engine.cs`
- Test: `tests/IGW.Tests/DcsTagOperationsTests.cs`
- Test: `tests/IGW.Tests/HighDensityWritePipelineTests.cs`
- Test: `tests/IGW.Tests/DeviceTimingMetricsTests.cs`

**Interfaces:**
- Produces: REST write failure payload `{ error, quality, statusCode, statusHex }`.
- Produces: queue-to-send, response, and verification timings without per-point success logging.

- [ ] **Step 1: Add failing REST and stress assertions**

```csharp
Assert.Equal("BadNotWritable", payload.quality);
Assert.Equal("0x803B0000", payload.statusHex);
```

Create a 2,477-tag fake OPC UA-style device that times out above 512 points, succeeds at 256, accepts a concurrent write, and verifies it. Assert all tags eventually become Good, the write completes, and `DisconnectCalls == 0`.

- [ ] **Step 2: Run the focused diagnostics/stress tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~DcsTagOperationsTests|FullyQualifiedName~HighDensityWritePipelineTests|FullyQualifiedName~DeviceTimingMetricsTests"`

Expected: FAIL because REST currently emits only the quality class and there is no field regression covering the 2,477-point timeout chain.

- [ ] **Step 3: Return exact statuses and add aggregate timings**

Map known IGW/OPC UA status values to symbolic names while always returning the hex code. Record aggregate histograms/counters for queue delay, protocol response, verification duration, batch downshift, and same-session retries. Avoid logging every successful point in high-density paths.

- [ ] **Step 4: Run the complete backend suite**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`

Expected: PASS with no failed tests.

- [ ] **Step 5: Build the full solution**

Run: `dotnet build IGW.slnx --no-restore -c Release`

Expected: build succeeds with zero errors. Existing unrelated warnings must be reported separately rather than hidden.

- [ ] **Step 6: Commit diagnostics and stress coverage**

```powershell
git add -- src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.Core/DeviceTimingMetrics.cs src/IGW.Core/Engine.cs tests/IGW.Tests/DcsTagOperationsTests.cs tests/IGW.Tests/HighDensityWritePipelineTests.cs tests/IGW.Tests/DeviceTimingMetricsTests.cs
git commit -m "test: cover high-density PLC write recovery"
```

---

### Task 7: Final verification and field evidence checklist

**Files:**
- Modify: `docs/RELEASE_NOTES.md`
- Create: `docs/validation/2026-08-28-adaptive-plc-io-validation.md`

**Interfaces:**
- Consumes: test and build output from Tasks 1–6.
- Produces: explicit separation of synthetic verification from real PLC field validation.

- [ ] **Step 1: Run the deterministic regression set three times**

Run:

```powershell
1..3 | ForEach-Object {
  dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DeviceLoop|FullyQualifiedName~OpcUa|FullyQualifiedName~HighDensityWritePipeline"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

Expected: all three runs pass without timing flakiness.

- [ ] **Step 2: Record synthetic evidence**

Document test counts, Release build result, 2,477-point batch sequence, disconnect count, write latency, retry count, and peak queue depth. Label these results `合成验证`, not `现场 PLC 认证`.

- [ ] **Step 3: Define the field replay**

For the Siemens S7-1500 endpoint used in the supplied log, record:

- endpoint and PLC model without credentials;
- server-declared OperationLimits;
- learned batch progression;
- 2,477-point scan average/P95/P99;
- a QC write's queue, response, and verification latency;
- session ID continuity before and after an injected large-read timeout;
- exact per-point write status.

Do not claim field success until this evidence is captured from the real PLC.

- [ ] **Step 4: Commit validation documentation**

```powershell
git add -- docs/RELEASE_NOTES.md docs/validation/2026-08-28-adaptive-plc-io-validation.md
git commit -m "docs: record adaptive PLC I/O validation"
```
