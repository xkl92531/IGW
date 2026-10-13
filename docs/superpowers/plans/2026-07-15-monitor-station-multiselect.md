# 监控中心工位多选实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将监控中心工位筛选从单选改为无需组合键的卡片多选，并提供清晰的勾选、数量和清空反馈。

**Architecture:** 把选择切换、异常 OR 过滤和失效选择清理提取到纯 TypeScript 工具，`Monitor.vue` 只保存响应式 `Set` 并渲染。沿用现有后台静默轮询，不修改服务端异常接口。

**Tech Stack:** Vue 3、TypeScript、Element Plus、Node.js 内置测试运行器、Vite。

## Global Constraints

- 未选择任何工位时表示显示全部。
- 卡片整块和右上角复选框都可切换选择，不依赖 Ctrl/Shift。
- 健康状态颜色继续表达设备状态，蓝色描边和复选框只表达筛选选择。
- 工位名称搜索不得清除仍存在的选择。

---

### Task 1: 工位多选状态与过滤纯函数

**Files:**
- Create: `src/IGW.WebUi/src/utils/monitorStations.ts`
- Create: `src/IGW.WebUi/src/utils/monitorStations.test.cjs`

**Interfaces:**
- Produces: `toggleStationSelection(current: ReadonlySet<string>, name: string): Set<string>`
- Produces: `filterEntriesByStations(entries: any[], selected: ReadonlySet<string>): any[]`
- Produces: `retainExistingStations(current: ReadonlySet<string>, channels: Array<{name:string}>): Set<string>`

- [ ] **Step 1: 写失败测试**

测试连续选择 A、B 后集合包含两项，再点 A 只取消 A；选择 A/B 时异常列表按 OR 语义保留直接命中和折叠子项命中；工位删除后清理失效选择。

- [ ] **Step 2: 验证测试失败**

Run: `node --test src/utils/monitorStations.test.cjs`
Expected: FAIL，因为 `monitorStations.ts` 尚不存在。

- [ ] **Step 3: 实现最小纯函数**

实现时始终返回新 `Set`，空集合直接返回全部异常，折叠条目同时检查自身 `channel` 和 `impacted[].channel`。

- [ ] **Step 4: 验证测试通过**

Run: `node --test src/utils/monitorStations.test.cjs`
Expected: 所有工位筛选测试 PASS。

### Task 2: 监控中心多选界面

**Files:**
- Modify: `src/IGW.WebUi/src/views/Monitor.vue`
- Modify: `src/IGW.WebUi/src/views/Monitor.ui.test.cjs`

**Interfaces:**
- Consumes: Task 1 的三个纯函数。
- Produces: `selectedChannels: Ref<Set<string>>`、`selectedCount`、卡片复选框和清空筛选入口。

- [ ] **Step 1: 更新界面失败测试**

断言模板包含 `el-checkbox`、`已选`、`清空筛选`，并且不再依赖单个 `chFilter`。

- [ ] **Step 2: 验证界面测试失败**

Run: `node --test src/views/Monitor.ui.test.cjs`
Expected: FAIL，提示缺少复选框或仍存在单选状态。

- [ ] **Step 3: 接入多选状态和模板**

把 `chFilter` 替换为 `selectedChannels`；卡片与复选框调用同一切换函数；顶部显示 `已选 N 个` 和 `清空筛选`；异常列表用 OR 过滤；数据刷新时清理已经删除的工位。

- [ ] **Step 4: 增强选中视觉**

选中卡片保留健康色状态条，增加蓝色双层描边、复选框和选中数量；所选工位无异常时显示专用空状态文案。

- [ ] **Step 5: 运行完整验证**

Run: `node --test $(rg --files src | Where-Object { $_ -like '*.test.cjs' })`
Expected: 全部 PASS。

Run: `pnpm typecheck`
Expected: exit 0。

Run: `pnpm build`
Expected: exit 0，并生成 `src/IGW.Runtime/wwwroot/assets/index-*.js`。

### Task 3: 部署与现场校验

**Files:**
- Copy build output: `src/IGW.Runtime/wwwroot` → `D:/Program Files/IGW/wwwroot`

**Interfaces:**
- Consumes: Task 2 的生产构建。
- Produces: 当前运行 IGW 可访问的新前端资源。

- [ ] **Step 1: 同步生产资源**

复制新 `index.html` 和构建资源到安装目录，不停止 IGW 服务。

- [ ] **Step 2: 校验部署哈希与 HTTP 引用**

比较源码构建和安装目录的 SHA-256，并确认 `http://127.0.0.1:48020/` 引用了新 bundle。

