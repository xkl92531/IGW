# 设计:MES 流程模板

## Context

- 任务模型(`IGW.Abstractions/MesModel.cs`):`MesTask { GroupRef(绑定主标记组), Trigger, Payload(Fields/Structs/Append), Flow(Nodes/Edges), Steps, Heartbeat, Handshake, … }`。点引用是**绝对路径**(`通道.设备.组[.子组].点`),散布在 Trigger.TagId、Node.Conditions/SwitchTag/Payload/WriteBack/Branches、Struct.BaseGroup/CountTagId、Heartbeat/Handshake 等键。
- 任务无独立 REST 端点:随工程 `PUT /project` 全量下发热重载;画布保存即 `putProject`。
- **前缀重写有先例**:组改路径时按"键名深度重写"(`Project.vue:1087` 起,`tagId/groupRef/baseGroup/…` 键的值按前缀替换,与退役桌面版 MigrateMesRefs 同源语义)——套用模板 = 同一语义(源组前缀 → 目标组前缀),把这套规则下沉到 Core 做成可单测纯函数。
- MES 任务只挂主标记组(本轮刚修的 bug 钉死此语义);同类设备经设备模板/CSV 批量生成后,各实例的组结构同构——这是"套用即可用"的前提。

## Goals / Non-Goals

**Goals:**

- 画布上配好的任务一键存为模板;套用到任意主标记组一键生成任务,配 80 个同构组 ≈ 配 1 个 + 点 80 下(或一次批量)。
- 套用透明可信:缺什么点、带了哪些组外引用,报告一次说清;产物与手工任务完全等价(引擎无感)。
- 与设备模板闭环:按"设备模板实例"圈选目标组批量套用。

**Non-Goals:**

- 不做模板与任务的"活关联"(改模板全场跟进)——任务个性化(触发值/工位号/报文)是常态,diff/传播语义对流程图过重;v1 = 印章式拷贝,断开关联。
- 不做跨工程中央模板库(导出/导入单文件 JSON 作轻量通道,与设备模板同惯例)。
- 不改 MES 执行引擎与任一运行时行为。

## Decisions

### D1 模板体 = 完整 MesTask + 源组路径,不发明相对路径语法

- `MesFlowTemplate { Name(唯一), Description?, SourceGroupRef, Task(MesTask 深拷贝,Id/Enabled 置空/false) }`,存 `MesUploadSettings.FlowTemplates`。
- 套用 = 深拷贝 Task → 所有以 `SourceGroupRef.` 开头的引用改前缀为目标组 → 新 Id/名字/GroupRef/DeviceRef。
- **备选**:模板内存相对路径(剥前缀)——多一套语法与序列化格式,且组外引用无法表达;前缀重写已有成熟语义;否。

### D2 重写器:按键名深度重写,Core 纯函数

- `MesFlowTemplateOps.RewriteRefs(task, oldPrefix, newPrefix)`:递归遍历任务对象树,对**引用键白名单**(`TagId/GroupRef/DeviceRef/BaseGroup/CountTagId/CopyFromTagId/SwitchTag/MesFlagTagId/PlcFlagTagId`)的字符串值做前缀替换(`oldPrefix.` → `newPrefix.`;DeviceRef 按前两段);其余键不动。与前端组改路径的键集合对齐(实现时以代码为准盘点全集)。
- 组外引用(不匹配前缀)原样保留,收集进报告的"外部引用"列表。
- **备选**:前端 JS 重写(已有类似代码)——无单测基建、REST 批量套用也要用;下沉 Core 一份,前端薄化;否。

### D3 套用报告:缺失保留 + 外部引用提示,不做静默剔除

- 重写后逐引用对照目标组展开点集(模板实例经 `TemplateExpander` 展开后同口径):`missing`(目标组无此点,字段保留、报告标示,画布可改)/ `external`(组外引用,原样带走,人工确认)。
- 缺失不阻断套用(同构组偶有差异是常态,引擎对缺失点给坏质量,画布随时可改);报告在套用响应里返回,前端展示。
- **备选**:缺失即剔除字段——静默改配置,违背"不静默"原则;否。

### D4 REST 面与批量:沿用「克隆→改→ReloadAsync」

- 端点:`POST /api/v1/mes/flow-templates/extract`(taskId+模板名)、`POST …/apply`(模板名+目标组[+任务名])、`POST …/apply-bulk`(模板名+目标组列表→逐组结果+报告汇总,全量校验通过才一次写入,对齐 CSV 原子惯例)、`GET …/{name}/export` + `POST …/import`、`GET /api/v1/mes/flow-templates`(列表)。
- 批量目标组圈选在前端完成(按设备模板实例过滤同名组),后端只收组列表——保持端点纯粹。

### D5 前端:入口全在画布,新 UI 拆组件

- `MesFlow.vue` 工具栏:「存为模板…」(命名对话框)+「从模板新建…」(选模板→选目标组(树,仅主标记组)→套用→报告);「批量套用…」多选组;模板小面板(列表/删/导出导入)。
- 报告组件独立 `.vue`(missing/external 分组列表,点击跳画布对应节点/字段)。

## Risks / Trade-offs

- [同构假设不成立(目标组结构差异大)] → 报告把 missing 全列出且不阻断;画布改完即可用;不追求自动对齐。
- [组外引用(心跳/握手指向公共设备)语义歧义:该跟着换还是保留] → v1 保留原引用并显式列出;强行猜测会写错生产点位,宁可人工确认。
- [印章式与设备模板"活关联"心智不一致] → proposal 明示裁决与理由;发版说明写清"流程模板套用后与模板无关联"。
- [重写键名白名单漏键] → 单测覆盖全模型引用键盘点(对 MesModel 反射断言:含 TagId/Ref 类字符串键都在白名单或明确豁免),防加字段后遗漏。

## Migration Plan

1. 模型+校验+Ops(Abstractions/Core,含重写器单测)——独立可合;
2. REST 端点;
3. 前端(存为模板 → 套用+报告 → 批量);
4. 回滚:新字段无消费者即死数据;含流程模板的工程旧版打开会丢模板字段(与设备模板同性质的单向门,发版说明并入既有条目)。

## Open Questions

- 批量套用时任务命名规则:默认 `组名`(与自动建任务一致),冲突时后缀序号——是否需要模板内占位符(如 `{device}-上报`)?v1 先默认规则,现场反馈再加。
- 「按设备模板实例圈选」放前端过滤是否够用(2000 实例的树多选性能)——不行再加后端按 templateRef 查询组的辅助端点。
