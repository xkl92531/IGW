# Channel Reconnect and Live Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add fast channel-scoped reconnect, make channel diagnostics share live runtime state, and remove IGW Runtime's internal simulation mode without affecting the standalone PLC simulator.

**Architecture:** `RuntimeEngine` will rebuild only the selected channel's device runtimes, while `GatewayHost` serializes reconnect against project reloads with its existing lifecycle gate. The Web UI will consume one shared `/api/v1/devices` state index for the tree and diagnostic dialog. Legacy simulation settings will be ignored by Runtime and removed from both Web and WPF configuration surfaces.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit, Vue 3, TypeScript, Element Plus, Node test runner.

## Global Constraints

- Reconnect affects only the selected channel.
- Devices without collection or consumer demand remain `Standby` after reconnect.
- Do not reload the full project or disturb other channels and MES tasks.
- Remove only IGW's internal simulation mode; do not modify the standalone `YanJing.NK.PlcSim` tool.
- Preserve unrelated dirty files in `MesFlow.vue` and `.pnpm-store`.

---

### Task 1: Channel-scoped Runtime reconnect

**Files:**
- Create: `tests/IGW.Tests/RuntimeEngineReconnectTests.cs`
- Modify: `src/IGW.Core/Engine.cs`

**Interfaces:**
- Produces: `Task<int> RuntimeEngine.ReconnectChannelAsync(string channelName, CancellationToken ct)`.
- Returns the configured device count restarted for the selected channel.
- Throws `KeyNotFoundException` for an unknown channel.

- [ ] **Step 1: Write failing Runtime tests**

Create a recording `IDriverFactory` and `IDeviceDriver`. Start an engine with channels `LineA` and `LineB`, then assert:

```csharp
int restarted = await engine.ReconnectChannelAsync("LineA", CancellationToken.None);
Assert.Equal(1, restarted);
Assert.Equal(2, factory.CreatedFor("LineA.DeviceA"));
Assert.Equal(1, factory.CreatedFor("LineB.DeviceB"));
Assert.True(factory.First("LineA.DeviceA").Disposed);
Assert.False(factory.First("LineB.DeviceB").Disposed);
```

Add tests that `simulate=true` still creates the channel's real factory and that a no-demand replacement driver has zero `ConnectAsync` calls.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --filter RuntimeEngineReconnectTests
```

Expected: compile failure because `ReconnectChannelAsync` does not exist.

- [ ] **Step 3: Implement the minimal reconnect method**

In `RuntimeEngine`, locate the channel case-insensitively, resolve its real driver factory, stop only runtimes whose exact full names belong to that channel, recreate those configured devices with existing template maps, and return the count. Remove the `settings.simulate` override from `ResolveFactory`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 1 command and require all reconnect tests to pass.

---

### Task 2: REST management endpoint and lifecycle serialization

**Files:**
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Create: `tests/IGW.Tests/ChannelReconnectApiContractTests.cs`

**Interfaces:**
- Produces: `Task<object> IGatewayHost.ReconnectChannelAsync(string channelName, CancellationToken ct)`.
- Produces: `POST /api/v1/channels/{name}/reconnect` returning `{ channel, restarted }`.

- [ ] **Step 1: Write failing API contract tests**

Test that the REST source maps the exact POST route and invokes `host.ReconnectChannelAsync(name, ct)`. Test the host interface exposes the method and that route error mappings include 404 for `KeyNotFoundException` and 409 for an unavailable Runtime.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --filter ChannelReconnectApiContractTests
```

Expected: assertions fail because the interface and route are absent.

- [ ] **Step 3: Implement host and route**

Add the host method, acquire `_gate`, reject an unavailable engine, call `Engine.ReconnectChannelAsync`, and return an anonymous response with channel and restarted count. Add the POST route with 404/409/503/500 responses and no full project reload.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 command and require all tests to pass.

---

### Task 3: Live diagnostic state and Web reconnect action

**Files:**
- Modify: `src/IGW.WebUi/src/api.ts`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`
- Create: `src/IGW.WebUi/src/views/Project.channel-maintenance.test.cjs`

**Interfaces:**
- Consumes: `POST /api/v1/channels/{name}/reconnect`.
- Produces: `api.reconnectChannel(channel: string)`.
- Reuses: `runtimeDevices`, `findRuntimeDevice`, and `effectiveRuntimeState` for both tree and diagnostics.

- [ ] **Step 1: Write failing UI tests**

Assert the project source contains “立即重连” and `api.reconnectChannel`, does not declare `diagStatus`, and derives diagnostic rows from `runtimeDevices`. Assert failed polling preserves the existing index and exposes an error message. Add a `Standby` diagnostic mapping test.

- [ ] **Step 2: Run UI tests and verify RED**

Run:

```powershell
& 'C:\Users\Administrator\.nvmd\versions\22.23.1\node.exe' --test src\IGW.WebUi\src\views\Project.channel-maintenance.test.cjs src\IGW.WebUi\src\utils\deviceRuntime.test.cjs
```

Expected: failure because reconnect and shared diagnostic state are absent.

- [ ] **Step 3: Implement shared live state**

Add `runtimeLastUpdated` and `runtimeStatusError`. Make `pollRuntimeStatus` keep the last known device index on failure. Remove `diagStatus`; make `diagRows` use `runtimeDevices` and `effectiveRuntimeState`, with `状态未知` when the runtime endpoint is unavailable. Display an error alert, last update time, detail message, and last communication error.

- [ ] **Step 4: Implement reconnect UI**

Add the API method and channel context-menu action. Prevent duplicate requests per channel, show the returned restarted count, call `pollRuntimeStatus()` immediately, and schedule follow-up polls after 800 ms and 2 seconds.

- [ ] **Step 5: Run UI tests and verify GREEN**

Run the Task 3 test command and require all tests to pass.

---

### Task 4: Remove IGW internal simulation mode

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`
- Modify: `src/IGW.Configurator/MainWindow.xaml.cs`
- Modify: `src/IGW.Configurator/MainViewModel.cs`
- Modify: `src/IGW.Runtime/Program.cs`
- Modify: `src/IGW.Runtime/IGW.Runtime.csproj`
- Modify: `src/IGW.WebUi/src/views/Project.channel-maintenance.test.cjs`

**Interfaces:**
- Removes: `simulationRuntimeMark`, Web `toggleSim/confirmSim`, WPF `OnToggleSimulate`, and Runtime Simulator registration.
- Legacy `settings.simulate` remains deserializable but has no effect.

- [ ] **Step 1: Extend source-contract tests and verify RED**

Assert canonical Web, Configurator, Runtime program, and Runtime project files contain no simulation-mode menu, dialog, factory registration, or project reference. The standalone simulator project is not part of these assertions.

- [ ] **Step 2: Remove Web simulation UI**

Remove context-menu and summary buttons, simulation dialog/state/functions, status overrides, simulation form fields, and generated `simulate/simSpeed/simDelayMs` defaults.

- [ ] **Step 3: Remove WPF and Runtime activation**

Remove the WPF menu/dialog and view-model setter. Remove `SimulatorDriverFactory` registration and the Runtime project reference. Keep `src/IGW.Drivers.Simulator` source intact.

- [ ] **Step 4: Run source-contract tests and verify GREEN**

Run the Task 3 UI command and focused Runtime tests; require both to pass.

---

### Task 5: Full verification and focused commit

**Files:**
- Verify all files listed above.

- [ ] **Step 1: Run backend regression**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore --nologo
```

Expected: all tests pass.

- [ ] **Step 2: Run frontend tests, typecheck, and production build**

```powershell
& 'C:\Users\Administrator\.nvmd\versions\22.23.1\node.exe' --test src\IGW.WebUi\src\views\Project.channel-maintenance.test.cjs src\IGW.WebUi\src\utils\deviceRuntime.test.cjs
& 'C:\Users\Administrator\.nvmd\versions\22.23.1\node.exe' src\IGW.WebUi\node_modules\vue-tsc\index.js --noEmit -p src\IGW.WebUi\tsconfig.json
& 'C:\Users\Administrator\.nvmd\versions\22.23.1\node.exe' src\IGW.WebUi\node_modules\vite\bin\vite.js build
```

Expected: tests and typecheck pass; build exits 0.

- [ ] **Step 3: Build Runtime and Configurator**

```powershell
dotnet build src\IGW.Runtime\IGW.Runtime.csproj -c Release --no-restore --nologo
dotnet build src\IGW.Configurator\IGW.Configurator.csproj -c Release --no-restore --nologo
```

Expected: both builds exit 0.

- [ ] **Step 4: Review and commit only feature files**

Stage only the files in this plan. Do not stage `.pnpm-store` or existing `MesFlow` changes. Commit with:

```powershell
git commit -m "feat: add channel reconnect and live diagnostics"
```
