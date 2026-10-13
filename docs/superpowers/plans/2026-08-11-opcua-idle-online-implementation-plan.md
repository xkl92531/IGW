# OPC UA Idle Online Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep configured OPC UA client sessions connected while idle so connected devices display online without polling business tags.

**Architecture:** Add an explicit driver-owned idle connection policy to `IGW.Abstractions`. `DeviceLoop` will preserve the existing demand-driven disconnect behavior for every driver except drivers that opt into session keep-alive; an opted-in driver connects and maintains transport state but returns to the idle wait before building or executing a scan plan. REST status remains sourced from the real driver state, and the Vue presentation supplements a connected zero-demand device with “采集待机” detail.

**Tech Stack:** .NET 10, xUnit, ASP.NET Core minimal APIs, OPC Foundation .NET Standard client, Vue 3, TypeScript, Node test runner.

## Global Constraints

- Existing PLC drivers remain disconnected when idle.
- OPC UA idle online must not create business reads, subscriptions, or active tags.
- Runtime state is the single source of truth for the engineering tree, diagnostics, and monitoring center.
- Work stays on `master` because the user explicitly requires one branch; preserve unrelated dirty-worktree changes.
- Use TDD: each production change follows a failing focused test.

---

### Task 1: Driver-owned idle connection policy

**Files:**
- Modify: `src/IGW.Abstractions/Drivers.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`
- Create: `tests/IGW.Tests/RuntimeEngineIdleConnectionTests.cs`

**Interfaces:**
- Produces: `IdleConnectionPolicy` with `DisconnectWhenIdle` and `KeepConnectedWhenIdle`.
- Produces: `IIdleConnectionPolicyProvider.IdleConnectionPolicy`.
- Consumes: `OpcUaClientDeviceDriver` opts into `KeepConnectedWhenIdle`.

- [ ] **Step 1: Write the failing contract test**

```csharp
[Fact]
public void OpcUa_driver_requests_idle_session_keep_alive()
{
    var driver = new OpcUaClientDeviceDriver(
        "opc.tcp://127.0.0.1:49320", 1000, true, "", "", "None");
    var policy = Assert.IsAssignableFrom<IIdleConnectionPolicyProvider>(driver);
    Assert.Equal(IdleConnectionPolicy.KeepConnectedWhenIdle, policy.IdleConnectionPolicy);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter FullyQualifiedName~RuntimeEngineIdleConnectionTests.OpcUa_driver_requests_idle_session_keep_alive`

Expected: compile failure because the policy types do not exist.

- [ ] **Step 3: Add the minimal policy contract**

```csharp
public enum IdleConnectionPolicy
{
    DisconnectWhenIdle,
    KeepConnectedWhenIdle,
}

public interface IIdleConnectionPolicyProvider
{
    IdleConnectionPolicy IdleConnectionPolicy { get; }
}
```

Implement on `OpcUaClientDeviceDriver`:

```csharp
public sealed class OpcUaClientDeviceDriver :
    IDeviceDriver, IBrowsableDriver, IIdleConnectionPolicyProvider
{
    public IdleConnectionPolicy IdleConnectionPolicy =>
        IdleConnectionPolicy.KeepConnectedWhenIdle;
}
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit only Task 1 files**

```powershell
git add src/IGW.Abstractions/Drivers.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs tests/IGW.Tests/RuntimeEngineIdleConnectionTests.cs
git commit -m "feat: declare OPC UA idle session policy"
```

### Task 2: Connect idle keep-alive drivers without scanning

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `tests/IGW.Tests/RuntimeEngineIdleConnectionTests.cs`

**Interfaces:**
- Consumes: `IIdleConnectionPolicyProvider.IdleConnectionPolicy` from Task 1.
- Produces: `DeviceLoop` behavior where keep-alive drivers connect at zero demand and execute zero reads.

- [ ] **Step 1: Add a failing RuntimeEngine behavior test**

Add a `KeepAliveRecordingFactory` whose driver implements `IIdleConnectionPolicyProvider`, increments `ConnectCalls` and `ReadCalls`, and transitions to `Connected` in `ConnectAsync`.

```csharp
[Fact]
public async Task Keep_alive_driver_connects_without_demand_but_does_not_read()
{
    var factory = new KeepAliveRecordingFactory();
    await using var engine = CreateEngine(factory, Project(Channel("Ua", Device("Server"))));
    await engine.StartAsync();

    await WaitUntilAsync(() => factory.Driver.ConnectCalls == 1, TimeSpan.FromSeconds(2));
    await Task.Delay(150);

    Assert.Equal(0, factory.Driver.ReadCalls);
    var status = Assert.Single(engine.DeviceStatuses());
    Assert.Equal(ConnectionState.Connected, status.State);
    Assert.False(status.DemandActive);
    Assert.Equal(0, status.ActiveTags);
}
```

Keep the existing `Reconnect_keeps_no_demand_device_disconnected` test unchanged to prove other drivers retain old behavior.

- [ ] **Step 2: Run both idle behavior tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --filter "FullyQualifiedName~RuntimeEngineIdleConnectionTests|FullyQualifiedName~RuntimeEngineReconnectTests.Reconnect_keeps_no_demand_device_disconnected"`

Expected: keep-alive test times out or sees zero connects; legacy reconnect test passes.

- [ ] **Step 3: Implement the idle connection branch in DeviceLoop**

Add a constructor-owned field:

```csharp
private readonly bool _keepConnectedWhenIdle;
```

Initialize it from the driver capability:

```csharp
_keepConnectedWhenIdle = driver is IIdleConnectionPolicyProvider
{
    IdleConnectionPolicy: IdleConnectionPolicy.KeepConnectedWhenIdle,
};
```

At the start of each loop iteration, capture demand once:

```csharp
bool hasDemand = HasDemand();
if (!hasDemand && !_keepConnectedWhenIdle)
{
    if (_active.State == ConnectionState.Connected)
    {
        try { await _active.DisconnectAsync(); } catch { }
        MarkAllBad(StatusCode.BadNotConnected);
    }
    if (!await WaitWorkOrDelay(_idlePollMs, ct)) break;
    continue;
}
```

After the existing connection block and before `RebuildPlanIfNeeded`, stop idle keep-alive work:

```csharp
if (!hasDemand)
{
    _activeTags = 0;
    if (!await WaitWorkOrDelay(_idlePollMs, ct)) break;
    continue;
}
```

Do not call `ReadAsync`, `RebuildPlanIfNeeded`, `DataHub.Touch`, or `ScanDemandRegistry.Acquire` in this branch.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: both tests PASS; keep-alive driver has one or more connection attempts and exactly zero reads.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/IGW.Core/Engine.cs tests/IGW.Tests/RuntimeEngineIdleConnectionTests.cs
git commit -m "feat: keep idle OPC UA sessions online"
```

### Task 3: Present connected idle OPC UA as online with collection-idle detail

**Files:**
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`

**Interfaces:**
- Consumes: `/api/v1/devices` fields `state`, `activeTags`, `tagCount`, and `demandActive`.
- Produces: connected zero-demand device mark `{ text: '在线', tone: 'online' }` with title containing `采集待机`.

- [ ] **Step 1: Write a failing UI presentation test**

```javascript
test('已连接但无采集需求的 OPC UA 设备显示在线并注明采集待机', () => {
  const mark = runtime.deviceRuntimeMark({
    name: 'Ua.Server', state: 'Connected', activeTags: 0, tagCount: 81, demandActive: false,
  })
  assert.equal(mark.text, '在线')
  assert.equal(mark.tone, 'online')
  assert.match(mark.title, /采集待机/)
})
```

- [ ] **Step 2: Run the Node test and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`

Expected: title does not contain `采集待机`.

- [ ] **Step 3: Add demand state to the DTO and connected title**

```typescript
export interface RuntimeDeviceStatus {
  // existing fields
  demandActive?: boolean
}
```

In the `Connected` branch:

```typescript
const idle = status.demandActive === false && (status.activeTags || 0) === 0
  ? ' · 采集待机（会话在线，当前不读取业务点位）'
  : ''
return { text: '在线', tone: 'online', title: `运行状态：在线${idle}${counts}${sampling}${overdue}` }
```

- [ ] **Step 4: Run the UI test and typecheck**

Run:

```powershell
node --test src/IGW.WebUi/src/utils/deviceRuntime.test.cjs
& .\tools\pnpm.cmd --dir src\IGW.WebUi typecheck
```

Expected: tests PASS and Vue TypeScript typecheck exits 0.

- [ ] **Step 5: Commit Task 3**

```powershell
git add src/IGW.WebUi/src/utils/deviceRuntime.ts src/IGW.WebUi/src/utils/deviceRuntime.test.cjs
git commit -m "feat: show idle OPC UA sessions as online"
```

### Task 4: Regression and live OPC UA verification

**Files:**
- Test only; do not modify unrelated files.

**Interfaces:**
- Verifies: Runtime policy, REST status, UI mapping, and actual OPC UA session behavior.

- [ ] **Step 1: Run the complete .NET test suite**

Run: `dotnet test IGW.slnx -c Release --no-restore`

Expected: all tests PASS; skipped external-infrastructure tests remain reported as skipped, not passed.

- [ ] **Step 2: Run Web UI tests, typecheck, and build**

Run:

```powershell
Get-ChildItem src\IGW.WebUi\src -Recurse -Filter *.test.cjs | ForEach-Object { node --test $_.FullName }
& .\tools\pnpm.cmd --dir src\IGW.WebUi typecheck
& .\tools\pnpm.cmd --dir src\IGW.WebUi build
```

Expected: all commands exit 0.

- [ ] **Step 3: Start the standalone simulator OPC UA server and IGW Runtime**

Use simulator endpoint `opc.tcp://127.0.0.1:49320`. Apply an IGW project with one enabled `OpcUa.Client` device and no MES/monitor demand.

- [ ] **Step 4: Verify runtime evidence**

Query `GET /api/v1/devices` twice at least five seconds apart. Expected for the OPC UA device:

```json
{
  "state": "Connected",
  "activeTags": 0,
  "demandActive": false,
  "stats": { "readsOk": 0, "readsErr": 0 }
}
```

The project tree must show `在线`, with hover detail `采集待机`; channel summary must show `全部在线`.

- [ ] **Step 5: Verify failure and recovery**

Stop the OPC UA simulator, wait through one reconnect attempt, confirm Runtime shows `Faulted` or `Disconnected` with a connection reason, restart the simulator, and confirm automatic return to `Connected` without applying the project again.

- [ ] **Step 6: Record verification evidence**

Save commands, status JSON, and timestamps in the completion message. Do not claim third-party OPC UA interoperability beyond the tested endpoint.
