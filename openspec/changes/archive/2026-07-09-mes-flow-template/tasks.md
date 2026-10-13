## 1. 模型与校验(Abstractions/Core)

- [x] 1.1 `MesFlowTemplate` 模型(Name/Description/SourceGroupRef/Task 深拷贝),`MesUploadSettings.FlowTemplates`;旧工程兼容(缺省空)
- [x] 1.2 `ProjectStore.Check` 扩展:流程模板名唯一(错误);序列化回环与旧工程兼容单测

## 2. 提取/套用核心(Core:MesFlowTemplateOps,纯函数)

- [x] 2.1 引用键盘点与深度重写器(JSON 树按键名深度重写,白名单 tagId/baseGroup/countTagId/copyFromTagId/switchTag/mesFlagTagId/plcFlagTagId;groupRef/deviceRef 任务级显式重置;前缀带点边界防误伤同名前缀组)
- [x] 2.2 `Extract`:深拷贝任务→剥 Id/Enabled→存模板(重名拒绝,源任务不变;按 Id/名匹配)
- [x] 2.3 `Apply`:校验(模板存在/目标为主标记组=3 段且在展开后设备直属组/无既有任务)→重写→默认停用+组名命名(重名自动后缀)→missing(对照展开点集,baseGroup 对照组集)/external 报告
- [x] 2.4 `ApplyBulk`:全部目标预校验(含选择内重复)任一非法整体不写;全过逐组套用汇总报告
- [x] 2.5 单元测试 16 例:反射盘点引用键全集(加模型字段漏白名单即红)、全键重写、印章语义、前缀边界、缺失/外部报告、目标为设备模板实例(按展开点集)、批量原子——128 全绿

## 3. REST(RestApi)

- [x] 3.1 端点:GET 列表 / POST extract / POST apply(带报告)/ POST apply-bulk(原子,400 逐组错)/ GET {name}/export / POST import(沿用 克隆→改→ReloadAsync)

## 4. 前端(MesFlow.vue 加入口,新 UI 拆组件)

- [x] 4.1 画布「模板 ▾」下拉并入「💾 当前任务存为流程模板…」(dirty 先保存;成功后重载防 409;重名走后端拒绝提示)
- [x] 4.2 FlowTemplatePanel:选模板→选目标主标记组(候选=各设备直属组,模板实例设备取设备模板的组;已有任务置灰)→套用→报告(missing/external 徽标+说明);套用完自动选中新任务
- [x] 4.3 批量:多选目标组 + 按设备模板过滤 + 一键全选无任务组→原子提交(400 逐组错误弹窗)→逐组报告
- [x] 4.4 模板面板:列表(节点/字段数/源组)/删除(印章语义提示)/导出下载/导入 JSON;后端补 DELETE 端点与设备模板对称

## 5. 验证与收尾

- [x] 5.1 活体 E2E 16/16(临时工程+真实 REST):提取/重名拒/批量原子(一好一坏整体不写)/套用重写无残留源引用/同构零缺失/重复目标拒/子组拒/导出/删除后任务仍在(印章);全量回归 128 用例全绿
- [x] 5.2 发版说明:印章语义与设备模板的刻意差异、默认停用、单向门并入 `docs/RELEASE_NOTES.md`
