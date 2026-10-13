# MES Response Inherit Upload Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让响应直写节点直接继承上游上报节点的 JSON 与 PLC 映射，不需用户重复配置。

**Architecture:** `MesUploadEngine` 在上报成功后保留当次 `MesPayload`，`MesResponseWritePlanner` 使用其 `FieldName/AliasName` 生成 PLC 写入计划。网页端保留自动识别前的接口别名，并将响应节点中的手工映射明确标注为可选覆盖。

**Tech Stack:** .NET 10, C#, xUnit, Vue 3, TypeScript, Node test runner, Vite.

## Global Constraints

- 只在上报节点配置一次 JSON 名称和 PLC 点位映射。
- 单点类型或写入失败不得中断同批其他点位。
- 手工响应映射仅作为可选覆盖。
- 不创建额外分支，保留工作区中的无关修改。

---

### Task 1: 锁定后端自动反向映射行为

**Files:**
- Modify: `tests/IGW.Tests/MesResponseWritebackTests.cs`
- Verify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Verify: `src/IGW.Northbound.Mes/MesResponseWritePlanner.cs`

**Interfaces:**
- Consumes: `MesResponseWritePlanner.Plan(MesPayload, JsonElement, string?, IEnumerable<string>, CompiledMesPayload?)`
- Produces: 真实包装 JSON、结构体别名反向映射和单点失败隔离的回归保护。

- [ ] **Step 1: 用真实 `value.backResult` JSON 编写失败隔离测试**

```csharp
new MesField { TagId = "ErrorMessage", FieldName = "NgReason" }
new MesField { TagId = "Errorinfo", FieldName = "ErrorInfo" }
```

- [ ] **Step 2: 运行定向测试，确认旧行为缺口**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ResponseWriteback_WritesActualUnderscoreStructTagsAndConfiguredMesFlag" --nologo`

- [ ] **Step 3: 确保响应映射使用当次上报 `MesPayload` 且逐点写入**

```csharp
lastUploadPayload = node.Payload;
var automatic = MesResponseWritePlanner.Plan(lastUploadPayload, lastResponse.Value, ...);
foreach (var item in pending) await _writeBack(item.TagId, item.Value, ...);
```

- [ ] **Step 4: 重跑定向测试并确认通过**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MesResponseWritebackTests" --nologo`

### Task 2: 保护上报映射并明确界面语义

**Files:**
- Modify: `src/IGW.WebUi/src/utils/mesStructMapping.ts`
- Modify: `src/IGW.WebUi/src/utils/mesStructMapping.test.cjs`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.response-writeback.test.cjs`

**Interfaces:**
- Consumes: `preserveAutoDetectedMappings(previous, detected): void`
- Produces: 自动识别后保留 JSON 别名，响应节点明确展示“自动继承，无需重复配置”。

- [ ] **Step 1: 新增界面语义失败测试**

```javascript
assert.match(flowSource, /自动使用上游上报节点的参数映射/)
assert.match(flowSource, /自定义覆盖（可选）/)
```

- [ ] **Step 2: 运行 Node 测试并确认因界面未说明自动继承而失败**

Run: `node --test src/IGW.WebUi/src/views/MesFlow.response-writeback.test.cjs`

- [ ] **Step 3: 更新响应直写说明和可选覆盖标题**

```vue
<span>自动使用上游上报节点的参数映射回写原 PLC 点位，无需在本节点重复配置。</span>
<div class="hint">自定义覆盖（可选）：响应字段 → PLC 点位</div>
```

- [ ] **Step 4: 运行前端测试、类型检查和生产构建**

Run: `node --test src/IGW.WebUi/src/utils/mesStructMapping.test.cjs src/IGW.WebUi/src/views/MesFlow.response-writeback.test.cjs`

Run: `node src/IGW.WebUi/node_modules/vue-tsc/bin/vue-tsc.js --noEmit -p src/IGW.WebUi/tsconfig.json`

Run from `src/IGW.WebUi`: `node node_modules/vite/bin/vite.js build`

### Task 3: 提交相关文件

**Files:**
- Add: `docs/superpowers/specs/2026-08-11-mes-response-inherit-upload-mapping-design.md`
- Add: `docs/superpowers/plans/2026-08-11-mes-response-inherit-upload-mapping.md`
- Stage only: Task 1 and Task 2 files plus these documents.

**Interfaces:**
- Consumes: Task 1 and Task 2 verified changes.
- Produces: one focused commit on `master` without unrelated workspace files.

- [ ] **Step 1: 检查差异和暂存文件列表**

Run: `git diff --check`

- [ ] **Step 2: 仅暂存本次相关文件**

```powershell
git add docs/superpowers/specs/2026-08-11-mes-response-inherit-upload-mapping-design.md docs/superpowers/plans/2026-08-11-mes-response-inherit-upload-mapping.md src/IGW.WebUi/src/utils/mesStructMapping.ts src/IGW.WebUi/src/utils/mesStructMapping.test.cjs src/IGW.WebUi/src/views/MesFlow.vue src/IGW.WebUi/src/views/MesFlow.response-writeback.test.cjs tests/IGW.Tests/MesResponseWritebackTests.cs
```

- [ ] **Step 3: 提交**

```powershell
git commit -m "fix: inherit MES upload mapping for response writeback"
```
