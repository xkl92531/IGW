# MES 上报双模式保留 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让数据范围配置与字段/结构映射配置同时保存在工程中，切换时只改变当前生效模式，不清空任何一套配置。

**Architecture:** 在 `MesPayload` 增加显式 `Mode`，值为 `DataRanges` 或 `FieldMapping`；旧工程缺少该字段时按“存在数据范围则 DataRanges，否则 FieldMapping”自动识别。界面、工程校验、设备关联和运行时报文生成全部通过同一模式判断，只使用当前模式的数据，同时保留另一套配置供下次切换。

**Tech Stack:** .NET 8/C#、Vue 3/TypeScript、Node test runner、xUnit

## Global Constraints

- 切换模式不得清空 `dataRanges`、`fields`、`structs` 或 `http.bodyTemplate`。
- `append` 为两种模式共用配置，始终生效。
- 旧工程不要求人工迁移，缺少 `mode` 时必须保持原运行行为。
- 当前模式为数据范围时，字段、结构体和自定义报文模板仅保存、不参与运行与校验。
- 当前模式为字段/结构映射时，数据范围仅保存、不参与运行与校验。

---

### Task 1: Payload 模式模型与兼容判断

**Files:**
- Modify: `src/IGW.Abstractions/MesModel.cs`
- Test: `tests/IGW.Tests/MesDataRangeModelTests.cs`

**Interfaces:**
- Produces: `MesPayload.Mode`、`MesPayloadModes.DataRanges`、`MesPayloadModes.FieldMapping`、`MesPayloadModes.IsDataRanges(MesPayload)`。

- [ ] **Step 1: Write the failing test**

验证显式模式可序列化，并验证缺少模式的旧工程按已有数据自动识别。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter MesDataRangeModelTests`

- [ ] **Step 3: Write minimal implementation**

在模型中添加模式字段和集中兼容判断，不改变旧工程 JSON 的读取能力。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter MesDataRangeModelTests`

### Task 2: 校验与运行时只使用当前模式

**Files:**
- Modify: `src/IGW.Core/MesDataRangeValidator.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Test: `tests/IGW.Tests/MesDataRangeValidationTests.cs`
- Test: `tests/IGW.Tests/MesRangePayloadTests.cs`

**Interfaces:**
- Consumes: `MesPayloadModes.IsDataRanges(MesPayload)`。
- Produces: 非当前模式配置不参与校验、设备关联、预编译和报文生成。

- [ ] **Step 1: Write failing validation and payload tests**

覆盖两套配置共存、范围模式忽略模板、映射模式忽略数据范围三个行为。

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "MesDataRangeValidationTests|MesRangePayloadTests"`

- [ ] **Step 3: Write minimal runtime implementation**

让校验器和上传引擎统一读取当前模式，保留但不执行另一套配置。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "MesDataRangeValidationTests|MesRangePayloadTests"`

### Task 3: 画布模式切换不清空配置

**Files:**
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.data-ranges.test.cjs`

**Interfaces:**
- Consumes: payload JSON 的 `mode` 字段。
- Produces: `uploadMode(node)` 与只写 `payload.mode` 的 `switchUploadMode(node, target)`。

- [ ] **Step 1: Write the failing UI source contract test**

断言界面使用“数据范围配置”“字段/结构映射配置”，且切换函数不再清空或删除任一配置。

- [ ] **Step 2: Run test to verify it fails**

Run: `node --test src/views/MesFlow.data-ranges.test.cjs`

- [ ] **Step 3: Implement the mode switch**

新增旧工程自动识别，切换只设置 `payload.mode` 并初始化缺失数组；两种编辑区按当前模式显示。

- [ ] **Step 4: Run frontend tests and checks**

Run: `node --test src/views/MesFlow.data-ranges.test.cjs src/utils/mesDataRanges.test.cjs`

Run: `npm run typecheck`

Run: `npm run build`

### Task 4: Full verification

**Files:**
- Verify only.

**Interfaces:**
- Consumes: Tasks 1-3 completed implementation.
- Produces: release-ready verified source tree.

- [ ] **Step 1: Run focused backend tests**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "MesDataRangeModelTests|MesDataRangeValidationTests|MesRangePayloadTests"`

- [ ] **Step 2: Run focused frontend tests**

Run: `node --test src/views/MesFlow.data-ranges.test.cjs src/utils/mesDataRanges.test.cjs src/components/MesDataRangeDialog.ui.test.cjs src/utils/reactiveClone.test.cjs`

- [ ] **Step 3: Run typecheck and production build**

Run: `npm run typecheck && npm run build`
