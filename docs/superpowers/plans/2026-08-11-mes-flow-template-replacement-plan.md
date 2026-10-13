# MES Flow Template Replacement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop auto-populating new MES flows and allow a user-confirmed template application to atomically replace the current task.

**Architecture:** Keep the existing blank task as the DB-bound editing context. Built-in templates replace the local canvas only after confirmation; saved templates use an opt-in `replaceExisting` flag and replace an existing task inside the backend project clone while preserving its identity and binding.

**Tech Stack:** Vue 3 `<script setup>`, Element Plus, TypeScript, Node test runner, ASP.NET Core minimal APIs, .NET 10, xUnit.

## Global Constraints

- A new DB task remains bound to its concrete `Channel.Device.DB` group and defaults to disabled.
- No nodes or edges are created automatically for a new task.
- Every built-in template application requires explicit confirmation before canvas mutation.
- Saved-template single-group replacement preserves task ID, name, group binding, and device binding, and sets `Enabled=false`.
- Saved-template bulk application continues to reject groups that already have tasks.
- A cancelled or failed operation must leave the original flow unchanged.

---

### Task 1: Core atomic task replacement

**Files:**
- Create: `tests/IGW.Tests/MesFlowTemplateOpsTests.cs`
- Modify: `src/IGW.Core/MesFlowTemplateOps.cs`

**Interfaces:**
- Consumes: existing `MesFlowTemplateOps.Apply(IgwProject, string, string, string?, out FlowApplyReport?)` behavior.
- Produces: `MesFlowTemplateOps.Apply(IgwProject proj, string templateName, string targetGroup, string? taskName, bool replaceExisting, out FlowApplyReport? report)`; the old overload remains and delegates with `replaceExisting=false`.

- [ ] **Step 1: Write the failing core tests**

Create an xUnit fixture that builds `Line.Plc.DB1`, a saved template sourced from `Source.Plc.DB0`, and an existing disabled task. Add these tests:

```csharp
[Fact]
public void Apply_DefaultMode_RejectsExistingTaskWithoutMutation()
{
    var project = BuildProjectWithExistingTask();
    var before = project.MesUpload.Tasks[0].Flow!.Nodes[0].Title;

    var error = MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, out var report);

    Assert.Contains("已有采集任务", error);
    Assert.Null(report);
    Assert.Equal(before, project.MesUpload.Tasks[0].Flow!.Nodes[0].Title);
}

[Fact]
public void Apply_ExplicitReplacement_PreservesIdentityBindingAndDisablesTask()
{
    var project = BuildProjectWithExistingTask();

    var error = MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out var report);

    Assert.Null(error);
    var task = Assert.Single(project.MesUpload.Tasks);
    Assert.Equal("existing-id", task.Id);
    Assert.Equal("现有流程", task.Name);
    Assert.Equal("Line.Plc.DB1", task.GroupRef);
    Assert.Equal("Line.Plc", task.DeviceRef);
    Assert.False(task.Enabled);
    Assert.Equal("模板上报", Assert.Single(task.Flow!.Nodes).Title);
    Assert.Equal("Line.Plc.DB1.Ready", Assert.Single(task.Flow.Nodes[0].Payload.Fields).TagId);
    Assert.Equal("现有流程", report!.TaskName);
}

[Fact]
public void Apply_InvalidTemplateWithReplacement_DoesNotRemoveExistingTask()
{
    var project = BuildProjectWithExistingTask();

    var error = MesFlowTemplateOps.Apply(project, "不存在", "Line.Plc.DB1", null, true, out _);

    Assert.Contains("流程模板不存在", error);
    Assert.Equal("existing-id", Assert.Single(project.MesUpload.Tasks).Id);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MesFlowTemplateOpsTests
```

Expected: compile failure because the overload containing `replaceExisting` does not exist.

- [ ] **Step 3: Implement minimal atomic replacement**

Add the compatibility overload and the new overload. Resolve the existing task before generation; reject unless `replaceExisting` is true. Build and validate the replacement first, then either append it or replace the original list element:

```csharp
public static string? Apply(IgwProject proj, string templateName, string targetGroup,
    string? taskName, out FlowApplyReport? report) =>
    Apply(proj, templateName, targetGroup, taskName, false, out report);

public static string? Apply(IgwProject proj, string templateName, string targetGroup,
    string? taskName, bool replaceExisting, out FlowApplyReport? report)
{
    var existingIndex = mes.Tasks.FindIndex(t =>
        string.Equals(t.GroupRef, targetGroup, StringComparison.OrdinalIgnoreCase));
    var existing = existingIndex >= 0 ? mes.Tasks[existingIndex] : null;
    if (existing is not null && !replaceExisting)
        return $"目标组已有采集任务:{targetGroup}(一个组只允许一个任务)。";

    // after the new task and report are fully built
    if (existing is null)
        mes.Tasks.Add(task);
    else
    {
        task.Id = existing.Id;
        task.Name = existing.Name;
        task.GroupRef = existing.GroupRef;
        task.DeviceRef = existing.DeviceRef;
        task.Enabled = false;
        mes.Tasks[existingIndex] = task;
        report = report with { TaskName = task.Name };
    }
    return null;
}
```

In the existing method, insert the `existingIndex` block immediately after `ValidateTarget` succeeds, replacing the current `mes.Tasks.Any(...)` guard. Keep the current task cloning, reference rewriting, target-set validation, and issue-report generation in the same order. Replace only the final `mes.Tasks.Add(task)` statement with the shown append-or-replace block, so all validation completes before the old task is replaced.

- [ ] **Step 4: Run focused and full core tests**

Run the focused command from Step 2, then:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore
```

Expected: all tests pass.

- [ ] **Step 5: Commit core behavior**

```powershell
git add tests/IGW.Tests/MesFlowTemplateOpsTests.cs src/IGW.Core/MesFlowTemplateOps.cs
git commit -m "feat: replace existing MES task from template"
```

### Task 2: REST and browser API opt-in flag

**Files:**
- Modify: `src/IGW.Northbound.RestApi/TemplateApi.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Create: `src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs`

**Interfaces:**
- Consumes: the new core overload from Task 1.
- Produces: request JSON `{ templateName, targetGroup, taskName, replaceExisting }` and browser method `flowTemplateApply(templateName: string, targetGroup: string, replaceExisting?: boolean, taskName?: string)`.

- [ ] **Step 1: Write the failing API contract assertions**

In the Node test, load `api.ts` and `TemplateApi.cs` and assert:

```javascript
test('单组流程模板接口显式传递覆盖开关且默认关闭', () => {
  assert.match(apiSource, /flowTemplateApply:\s*\(templateName: string, targetGroup: string, replaceExisting = false, taskName\?: string\)/)
  assert.match(apiSource, /\{ templateName, targetGroup, taskName, replaceExisting \}/)
  assert.match(restSource, /record FlowApplyBody\([^)]*bool ReplaceExisting = false\)/)
  assert.match(restSource, /body\.ReplaceExisting,\s*out report/)
})
```

- [ ] **Step 2: Run the Node test and verify RED**

Run:

```powershell
& "$env:USERPROFILE\.nvmd\versions\22.23.1\node.exe" --test src/views/MesFlow.template-application.test.cjs
```

Expected: assertions fail because neither client nor REST request exposes `replaceExisting`.

- [ ] **Step 3: Add the API flag**

Change the browser API to:

```ts
flowTemplateApply: (templateName: string, targetGroup: string, replaceExisting = false, taskName?: string) =>
  http.post('/api/v1/mes/flow-templates/apply',
    { templateName, targetGroup, taskName, replaceExisting }, { timeout: 120000 }).then((r) => r.data),
```

Change the REST body and route call to:

```csharp
public sealed record FlowApplyBody(string? TemplateName, string? TargetGroup,
    string? TaskName, bool ReplaceExisting = false);

proj => MesFlowTemplateOps.Apply(proj, body.TemplateName ?? "", body.TargetGroup ?? "",
    body.TaskName, body.ReplaceExisting, out report)
```

- [ ] **Step 4: Run API contract and .NET tests**

Run the Node test from Step 2 and the full `IGW.Tests` command from Task 1.

Expected: both pass.

- [ ] **Step 5: Commit API behavior**

```powershell
git add src/IGW.Northbound.RestApi/TemplateApi.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs
git commit -m "feat: expose MES template replacement flag"
```

### Task 3: Blank new flows and confirmed built-in templates

**Files:**
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs`

**Interfaces:**
- Consumes: Element Plus `ElMessageBox.confirm` and existing `applyTemplate` commands.
- Produces: asynchronous `applyTemplate(kind: string): Promise<void>` that mutates only after confirmation.

- [ ] **Step 1: Add failing UI source-contract tests**

Add assertions that there are no automatic handshake calls and that confirmation occurs before canvas clearing:

```javascript
test('新建采集任务保持空画布且不自动铺标准握手流程', () => {
  assert.doesNotMatch(flowSource, /if \(task\.value && !\(task\.value\.flow\?\.nodes\?\.length\)\) applyTemplate\('handshake'\)/)
})

test('内置模板只有在用户确认后才替换当前画布', () => {
  const start = flowSource.indexOf('async function applyTemplate(')
  const end = flowSource.indexOf('// ---------- 运行态高亮', start)
  const block = flowSource.slice(start, end)
  assert.match(block, /await ElMessageBox\.confirm\(/)
  assert.ok(block.indexOf('await ElMessageBox.confirm(') < block.indexOf('flow.value.nodes = []'))
  assert.match(block, /catch \{ return \}/)
})
```

- [ ] **Step 2: Run the Node test and verify RED**

Expected: both new tests fail against the current automatic and synchronous implementation.

- [ ] **Step 3: Remove automatic templates and add confirmation**

Delete the two automatic `applyTemplate('handshake')` calls. Make `applyTemplate` async, retain immediate routing for `_save` and `_manage`, derive the selected template label, and confirm before clearing:

```ts
async function applyTemplate(kind: string) {
  if (kind === '_save') { saveAsFlowTemplate(); return }
  if (kind === '_manage') { openTplPanel(); return }
  const labels: Record<string, string> = {
    handshake: '标准握手上报', timer: '定时上报', writeback: '上报+回写',
  }
  const replacing = (flow.value?.nodes?.length || 0) > 0
  try {
    await ElMessageBox.confirm(
      replacing ? `套用「${labels[kind]}」将替换当前全部节点和连线，继续？` : `确认套用「${labels[kind]}」？`,
      '套用流程模板',
      { type: replacing ? 'warning' : 'info', confirmButtonText: '确认套用', cancelButtonText: '取消' },
    )
  } catch { return }
  // existing template construction follows unchanged
}
```

- [ ] **Step 4: Run the focused Node test**

Expected: all tests in `MesFlow.template-application.test.cjs` pass.

- [ ] **Step 5: Commit blank-flow behavior**

```powershell
git add src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs
git commit -m "fix: keep new MES flow canvases blank"
```

### Task 4: Confirmed saved-template replacement UI

**Files:**
- Modify: `src/IGW.WebUi/src/components/FlowTemplatePanel.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs`

**Interfaces:**
- Consumes: `api.flowTemplateApply(templateName, targetGroup, replaceExisting)` from Task 2.
- Produces: single-target replacement confirmation; bulk selection remains non-destructive.

- [ ] **Step 1: Add failing template-panel assertions**

```javascript
test('单组模板允许选择已有任务并在确认后请求原子覆盖', () => {
  assert.match(panelSource, /已有任务，将覆盖/)
  assert.match(panelSource, /await ElMessageBox\.confirm\(/)
  assert.match(panelSource, /api\.flowTemplateApply\(applyFor\.value, targetGroup\.value, hasTask\)/)
})

test('批量模板仍禁用已有任务并只全选无任务组', () => {
  assert.match(panelSource, /mode\.value === 'bulk'[\s\S]*?:disabled="g\.hasTask"/)
  assert.match(panelSource, /filter\(\(g\) => !g\.hasTask\)/)
})
```

- [ ] **Step 2: Run the Node test and verify RED**

Expected: single-target replacement assertions fail because existing targets are disabled and no confirmation is shown.

- [ ] **Step 3: Implement single-target confirmation**

Add a computed lookup for the selected target. In `doApply`, validate selection, confirm before setting `applying`, then pass the existing-task flag:

```ts
const selectedTarget = computed(() => mainGroups.value.find((g) => g.path === targetGroup.value))

const hasTask = selectedTarget.value?.hasTask === true
try {
  await ElMessageBox.confirm(
    hasTask
      ? `目标「${targetGroup.value}」已有采集流程，确认后将使用模板「${applyFor.value}」覆盖。继续？`
      : `确认将模板「${applyFor.value}」套用到「${targetGroup.value}」？`,
    hasTask ? '覆盖现有流程' : '套用流程模板',
    { type: hasTask ? 'warning' : 'info', confirmButtonText: hasTask ? '确认覆盖' : '确认套用', cancelButtonText: '取消' },
  )
} catch { return }

applying.value = true
const r = await api.flowTemplateApply(applyFor.value, targetGroup.value, hasTask)
```

Remove `:disabled="g.hasTask"` only from the single selector and label it with `（已有任务，将覆盖）`. Keep the bulk selector disabled expression unchanged.

- [ ] **Step 4: Run focused and all Web UI tests**

Run:

```powershell
& "$env:USERPROFILE\.nvmd\versions\22.23.1\node.exe" --test src/views/MesFlow.template-application.test.cjs
$files = @(rg --files src/IGW.WebUi/src -g '*.test.cjs')
& "$env:USERPROFILE\.nvmd\versions\22.23.1\node.exe" --test $files
```

Expected: all pass.

- [ ] **Step 5: Commit saved-template UI**

```powershell
git add src/IGW.WebUi/src/components/FlowTemplatePanel.vue src/IGW.WebUi/src/views/MesFlow.template-application.test.cjs
git commit -m "feat: confirm MES template flow replacement"
```

### Task 5: Final verification

**Files:**
- Verify only; no production file changes expected.

**Interfaces:**
- Consumes: all behavior from Tasks 1-4.
- Produces: verification evidence suitable for release packaging.

- [ ] **Step 1: Run Web UI type checking and production build**

```powershell
& "$env:USERPROFILE\.nvmd\versions\22.23.1\node.exe" node_modules/vue-tsc/bin/vue-tsc.js --noEmit
& "$env:USERPROFILE\.nvmd\versions\22.23.1\node.exe" node_modules/vite/bin/vite.js build
```

- [ ] **Step 2: Run .NET tests and Runtime build**

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore
dotnet build src/IGW.Runtime/IGW.Runtime.csproj -c Release --no-restore --nologo
```

- [ ] **Step 3: Inspect repository state and commits**

```powershell
git diff --check
git status --short
git log -6 --oneline
```

Expected: source changes are committed; only the pre-existing `.pnpm-store` cache mirror modifications remain unstaged.
