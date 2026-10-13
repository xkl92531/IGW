# OPC UA Device Root and Empty Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import the OPC UA browse root as the IGW device identity, expose only its child business groups as MES boundaries, and stop treating empty MES tasks as configured flows.

**Architecture:** Extend the pure OPC UA import mapper with single-device-root detection, root stripping, and safe legacy-wrapper flattening. The browser dialog emits device identity together with imported tags, `Project.vue` applies device/path migration atomically, and all MES template targets come from the shared boundary catalog. Frontend and backend separately track task-record existence and actual flow content so empty records can be replaced without a false warning.

**Tech Stack:** Vue 3, TypeScript, Element Plus, Node test runner, .NET 10, xUnit.

## Global Constraints

- OPC UA NodeId remains unchanged when device, structure, or point names are edited.
- One import applies to exactly one OPC UA device root.
- Existing customized IGW device names are not silently overwritten.
- Existing non-empty MES flows are never discarded during legacy-wrapper migration.
- Empty task replacement keeps the single-task-per-group invariant.
- Existing dirty workspace files outside this feature are not staged, reverted, or reformatted.

---

### Task 1: Model the OPC UA browse root as a device

**Files:**
- Modify: `src/IGW.WebUi/src/utils/opcUaStructuredImport.ts`
- Modify: `src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`
- Modify: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.vue`
- Modify: `src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs`

**Interfaces:**
- Produces: `opcUaDeviceRoots(tags: OpcUaBrowsedTag[]): string[]`.
- Produces: `toImportedOpcUaTag(tag, aliases, sourceDeviceRoot?: string): ImportedTag`.
- Produces: `flattenLegacyOpcUaDeviceRoot(target: any, sourceDeviceRoot: string): { target: any; changed: boolean; error?: string }`.
- Emits: `{ tags, overwriteConflicts, sourceDeviceName, targetDeviceName }` from `OpcUaTagBrowserDialog`.

- [ ] **Step 1: Write failing mapper tests**

Add tests with two tags under `S7Sim.DB2100` and assert:

```js
assert.deepEqual(imp.opcUaDeviceRoots(tags), ['S7Sim'])
assert.deepEqual(imp.toImportedOpcUaTag(tags[0], aliases, 'S7Sim').groupPath, ['DB2100'])
assert.equal(imp.toImportedOpcUaTag(tags[0], aliases, 'S7Sim').address, tags[0].address)
```

Also assert two selected first-level roots return two names, and that flattening `{ groups: [{ name: 'S7Sim', subGroups: [{ name: 'DB2100' }] }] }` produces a direct `DB2100` group without mutating the input. Add a collision case that returns `error` rather than dropping either group.

- [ ] **Step 2: Run the mapper test and verify RED**

Run: `node --test src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs`

Expected: FAIL because root detection, stripping, and flattening do not exist.

- [ ] **Step 3: Implement the pure root helpers**

Derive roots from `structuredOpcUaTagPath(tag).folderSegments[0]`, return case-insensitive unique names in source order, and only strip an exact case-insensitive first segment. Clone the legacy target before removing the wrapper. Reject tag/group name collisions with an explicit Chinese error string; never overwrite.

- [ ] **Step 4: Add dialog device semantics**

For OPC UA preview mode, compute selected roots from raw selected tags. Disable Apply when the count is not exactly one and show `一次只能导入一个设备，请只勾选一个顶层设备`. Render a selected top-level folder as `设备`, edit `targetDeviceName` instead of a folder alias, and pass the root into `toImportedOpcUaTag`. Keep Siemens device browsing and its optional root-group behavior unchanged.

- [ ] **Step 5: Run mapper and dialog tests and verify GREEN**

Run:

```powershell
node --test src/IGW.WebUi/src/utils/opcUaStructuredImport.test.cjs
node --test src/IGW.WebUi/src/components/OpcUaTagBrowserDialog.ui.test.cjs
```

Expected: all focused tests pass.

### Task 2: Apply device naming, legacy flattening, and shared MES boundaries

**Files:**
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/views/Project.opcua-import.test.cjs`
- Modify: `src/IGW.WebUi/src/utils/mesDataRanges.test.cjs`
- Modify: `src/IGW.WebUi/src/components/FlowTemplatePanel.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs`

**Interfaces:**
- Consumes: Task 1 `flattenLegacyOpcUaDeviceRoot` and browser payload.
- Consumes: `buildMesTaskBoundaries(project): MesTaskBoundary[]`.
- Produces: template candidates `{ path, hasTask, hasFlow, tpl? }`.

- [ ] **Step 1: Write failing project and boundary tests**

Assert `Project.vue` accepts `sourceDeviceName`/`targetDeviceName`, checks sibling device-name collisions, calls `migrateMesRefs` for a device rename and legacy wrapper removal, and refuses to flatten a wrapper with an exact non-empty task. Add a boundary fixture shaped `channel -> device S7Sim -> DB2100 -> CellData_0` and assert only `channel.S7Sim.DB2100` is returned.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
node --test src/IGW.WebUi/src/views/Project.opcua-import.test.cjs
node --test src/IGW.WebUi/src/utils/mesDataRanges.test.cjs
```

Expected: new source-contract and boundary assertions fail.

- [ ] **Step 3: Apply the import atomically in `Project.vue`**

Before applying tags, build a flattened clone. If a task is bound exactly to the legacy wrapper, allow removal only when both `flow.nodes` and `flow.edges` are empty; otherwise show an error and leave the project unchanged. Rename a generic device (`设备`, `Device`, or blank) to the source device by default, preserve an existing custom name unless edited, reject sibling collisions, migrate MES path prefixes, then replace the target and mark the project dirty once.

- [ ] **Step 4: Use the shared boundary catalog in the template panel**

Replace the direct `device.groups` loop with `buildMesTaskBoundaries(props.proj)`. For each boundary, find its task and compute:

```ts
const hasTask = !!task
const hasFlow = !!task && ((task.flow?.nodes?.length || 0) > 0 || (task.flow?.edges?.length || 0) > 0)
```

Use `hasFlow` for labels, warnings, bulk disabling, and “全选无流程组”; retain `hasTask` for the single-apply replacement flag.

- [ ] **Step 5: Run project, boundary, and template UI tests and verify GREEN**

Run:

```powershell
node --test src/IGW.WebUi/src/views/Project.opcua-import.test.cjs
node --test src/IGW.WebUi/src/utils/mesDataRanges.test.cjs
node --test src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs
```

Expected: the device root is absent from MES targets and blank tasks are shown as available.

### Task 3: Allow atomic bulk template replacement of empty tasks

**Files:**
- Modify: `src/IGW.Core/MesFlowTemplateOps.cs`
- Modify: `tests/IGW.Tests/MesFlowTemplateOpsTests.cs`

**Interfaces:**
- Produces: private `HasConfiguredFlow(MesTask task): bool` using nodes or edges.
- Changes: `ApplyBulk` rejects only existing non-empty flows and calls `Apply(..., replaceExisting: existing is not null, ...)` for empty records.

- [ ] **Step 1: Write failing xUnit tests**

Create one existing task with `Flow = new MesFlow { Nodes = [], Edges = [] }` and one with an Upload node. Assert bulk apply replaces the empty task while preserving its Id, but returns an error and leaves the project unchanged for the non-empty task.

- [ ] **Step 2: Run the focused .NET test and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter MesFlowTemplateOpsTests`

Expected: the empty-task bulk case fails with `目标组已有采集任务`.

- [ ] **Step 3: Implement empty-flow-aware bulk apply**

Add:

```csharp
private static bool HasConfiguredFlow(MesTask task) =>
    (task.Flow?.Nodes.Count ?? 0) > 0 || (task.Flow?.Edges.Count ?? 0) > 0;
```

During preflight, reject an existing task only when this helper returns true. During mutation, call the existing single-group `Apply` overload with `replaceExisting: existing is not null` so no duplicate task can be created.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter MesFlowTemplateOpsTests`

Expected: all template-operation tests pass.

### Task 4: Full verification

**Files:**
- Verify only; no planned production file changes.

**Interfaces:**
- Consumes all earlier tasks.
- Produces verification evidence for handoff.

- [ ] **Step 1: Run all Web UI Node tests**

Run: `Get-ChildItem src/IGW.WebUi/src -Recurse -Filter *.test.cjs | ForEach-Object { node --test $_.FullName }`

Expected: every test process exits 0.

- [ ] **Step 2: Run Web UI typecheck and production build**

Run:

```powershell
pnpm --dir src/IGW.WebUi typecheck
pnpm --dir src/IGW.WebUi build
```

Expected: both commands exit 0.

- [ ] **Step 3: Run the full .NET suite**

Run: `dotnet test IGW.slnx --no-restore --verbosity minimal`

Expected: all tests pass with zero failures.

- [ ] **Step 4: Review the final diff**

Run: `git diff --check` and `git diff --stat`.

Expected: no whitespace errors, no `.pnpm-store` artifacts intentionally added, and no unrelated user changes reverted.
