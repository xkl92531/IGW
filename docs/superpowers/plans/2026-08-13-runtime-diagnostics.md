# IGW Runtime Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在工程树和 MES 画布中显示可定位的 PLC、MES 网络与业务异常，并将 P95 改为最近 60 秒口径。

**Architecture:** 后端使用结构化诊断代码作为唯一事实来源，驱动保留底层错误，MES 状态详情携带失败类别；Web 只负责本地化和展示。设备周期统计使用固定 60 槽秒级环形窗口，保持热路径无持续分配。

**Tech Stack:** .NET 8/C#, xUnit, Vue 3/TypeScript, Node test runner

## Global Constraints

- 不改变现有工程树和 MES 画布整体布局。
- MES 接口失败不得修改 PLC 数据质量。
- 诊断保留原始错误，同时提供稳定的结构化分类。
- 不覆盖工作区已有未提交修改。

---

### Task 1: 最近 60 秒周期指标

**Files:**
- Modify: `src/IGW.Core/DeviceTimingMetrics.cs`
- Test: `tests/IGW.Tests/DeviceTimingMetricsTests.cs`

- [ ] 先写窗口淘汰和 P95 桶边界测试并确认失败。
- [ ] 使用固定秒槽实现最近 60 秒平均值、P95、P99 和点/秒。
- [ ] 运行相关测试并确认通过。

### Task 2: 后端异常分类

**Files:**
- Modify: `src/IGW.Abstractions/MesStatus.cs`
- Modify: `src/IGW.Northbound.Mes/MesUploadEngine.cs`
- Modify: `src/IGW.Drivers.IoTClient/IoTPlcDriver.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensClassicTransport.cs`
- Modify: `src/IGW.Drivers.IoTClient/SiemensHybridDeviceDriver.cs`
- Test: `tests/IGW.Tests/MesUploadFlowTests.cs`
- Test: `tests/IGW.Tests/SiemensHybridDeviceDriverTests.cs`

- [ ] 写 MES 无响应、业务 NG 和 Classic DB 错误诊断测试并确认失败。
- [ ] 添加结构化失败类别，保留 Classic 读取原始错误和受影响 DB。
- [ ] 运行相关测试并确认通过。

### Task 3: Web 状态呈现

**Files:**
- Modify: `src/IGW.WebUi/src/utils/deviceRuntime.ts`
- Modify: `src/IGW.WebUi/src/components/MesNodeDetail.vue`
- Modify: `src/IGW.WebUi/src/views/MesFlow.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Test: `src/IGW.WebUi/src/utils/deviceRuntime.test.cjs`
- Test: `src/IGW.WebUi/src/components/MesNodeDetail.data-quality.test.cjs`
- Test: `src/IGW.WebUi/src/views/MesFlow.runtime-state.test.cjs`

- [ ] 写工程树和画布三类状态测试并确认失败。
- [ ] 显示网络异常、DB/协议不匹配、MES 无响应和业务 NG。
- [ ] 运行 Node 测试、类型检查和生产构建。

### Task 4: 完整回归

**Files:**
- Verify only

- [ ] 运行全部后端测试。
- [ ] 运行全部 Web 测试、类型检查和构建。
- [ ] 检查 git diff，确认只包含本次新增和既有未提交修改。
