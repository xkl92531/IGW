## 1. 数据模型与校验(Abstractions/Core)

- [x] 1.1 `DeviceTemplate` 模型(Name/DriverId/Tags/Groups/DefaultSettings,复用 TagDef/TagGroupDef),`IgwProject.Templates`
- [x] 1.2 `DeviceDef` 新增可选 `TemplateRef` 与 `Delta`(Overrides 仅存改动字段/Adds/Masks),JSON 序列化向后兼容(旧工程缺省加载)
- [x] 1.3 `ProjectStore.Validate` 扩展:模板名唯一、引用存在、驱动与通道一致、悬空差异→警告、追加重名→错误;校验结果区分 错误(阻断)/警告(放行)
- [x] 1.4 单元测试:模型序列化回环、旧工程兼容、各校验分支

## 2. 运行时展开与热重载(Core)

- [x] 2.1 `TemplateExpander.Expand(template, delta) → 有效点集`(纯函数):覆盖→屏蔽→追加顺序,悬空差异忽略
- [x] 2.2 接入 `RuntimeEngine`:StartDevice/BindDevice 前对模板实例展开;确认 DataHub 注册、扫描组、写映射、MES GroupRef 全链路对展开结果透明
- [x] 2.3 `DeviceSig` 纳入所引用模板内容哈希:改模板→引用实例签名变;改单实例差异→仅该实例签名变
- [x] 2.4 Apply 分批启动(如每批 100 台、批间短间隔),防改模板引发 2000 台重连风暴
- [x] 2.5 单元测试:展开顺序、悬空忽略、签名传播范围、增量重载只动受影响设备

## 3. 提取与套用(Core + RestApi)

- [x] 3.1 提取模板:设备定义→模板(剥离端点等实例设置),REST 端点 + 重名拒绝
- [x] 3.2 套用创建:REST 端点(模板名+通道+设备名+端点/设置→新增模板实例设备),驱动不匹配拒绝
- [x] 3.3 模板导出/导入单文件 JSON 端点(导入重名拒绝)

## 4. 套用验证作业(Core + RestApi)

- [x] 4.1 验证作业框架:发起→作业 Id→进度/结果查询;内存保留最近 N 份报告;统一报告结构(Path/Address/Result/Detail)
- [x] 4.2 可浏览驱动匹配:BrowseAsync 结果与展开点集按 点名/路径 匹配,产出 matched/missing/extra
- [x] 4.3 extra 收编端点:勾选的 extra 点写入实例 Delta.Adds
- [x] 4.4 不可浏览驱动试读:对展开点集一轮真实读(复用设备读路径),产出 readOk/readFail+错误摘要
- [x] 4.5 集成测试:仿真通道跑通 套用→验证→报告→收编/改地址→复验(闭环逻辑;活引擎试读路径并入 7.1)

## 5. 批量导入(RestApi)

- [x] 5.1 CSV 解析(UTF-8,容错 BOM;行级错误定位)与样例模板文件下载端点
- [x] 5.2 全量校验+原子提交:任一行错→整体不写+逐行错误报告(行号+原因);全过→一次写入工程
- [x] 5.3 集成测试:2000 行导入性能、错误行报告、修正后重导成功

## 6. 前端(全部新组件文件,Project.vue 只加挂载点)

- [x] 6.1 模板管理板块:模板列表/编辑(复用点位编辑组件)/导出导入;无模板时折叠空态(TemplateManager + TemplateEditorDialog;编辑器为聚焦式就地编辑,非复用巨型内联编辑器)
- [x] 6.2 设备右键「提取为模板」+ 命名对话框
- [x] 6.3 套用向导:选模板→填端点/设置→保存并应用→验证报告页(missing/extra/readFail 高亮,extra 勾选收编,失败点跳转编辑)(ApplyTemplateDialog + ValidationReport;跳转为提示定位,v1 未做树内导航)
- [x] 6.4 差异徽章与 diff 面板:「与模板差异 N 处」、逐条 模板值 vs 实例值、单条/整体还原、失效差异标示
- [x] 6.5 批量导入向导:上传 CSV→校验错误报告→确认导入→验证作业进度→逐台报告入口
- [x] 6.6 应用前提示:「本次将重启 N 台设备」(模板修改波及面预告)(模板编辑保存前 + diff 整体还原前确认)

## 7. 验证与收尾

- [x] 7.1 端到端:提取→套用→(仿真)验证→加差异→改模板→增量应用只重启引用实例、差异保留(活引擎 `TemplateEngineTests`:展开→DataHub 注册、改模板传播+差异保留、手工共存;套用/验证/收编/复验闭环见 `ValidationTests`;只重启引用实例由签名单测证;UI 全流程点验留待实机)
- [x] 7.2 规模验证:20 模板 × 2000 实例工程的 加载耗时/文件体积/应用耗时 基准记录(`TemplateScaleTests`:10 万展开点、文件 <2MB 体积解耦、加载回环;活网关应用耗时属实机基准)
- [x] 7.3 兼容验证:旧工程零改动加载;含模板工程在旧版本不可打开的单向门写入发版说明(`docs/RELEASE_NOTES.md`)
- [x] 7.4 用户文档:模板工作流(提取/套用/差异/导入)与 CSV 格式说明(`docs/设备模板.md`)
