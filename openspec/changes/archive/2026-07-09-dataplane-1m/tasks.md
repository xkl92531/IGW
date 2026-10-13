## 1. 存储与句柄(行为等价,可独立合入)

- [x] 1.1 槽位点表:分段 `Entry[]` + 整型句柄分配/回收(free-list,孤儿条目由 Entry 引用语义兜底);名字字典仅边界按名解析(实现取 `<string,Entry>` 单跳,Entry 携 Handle);Entry 保留 TagId 字符串引用
- [x] 1.2 `DataValue` 变体承载:数值保留原始 CLR 宽度(北向类型不漂移)+ `object?` 仅引用型;构造即规范化(驱动零改动);变体版 缩放/死区/值等/非规范归零
- [x] 1.3 `TagChange` 句柄化(Handle + TagId 引用 + Value,零分配);总线改携带条目引用,pump 构造一次 TagChange 扇出,旧 Subscribe 行为等价(101 测试全绿)
- [x] 1.4 单元测试:句柄回收/重复注册/旧句柄写孤儿无害;变体 12 类型回环+ulong 极值;seqlock 并发读写不撕裂;写路径 10 万次更新 <1MB 分配(零装箱断言)

## 2. 分片与订阅倒转

- [x] 2.1 总线分片:N 片独立有界队列+pump(默认 `min(核,8)`、容量可配),句柄哈希恒定路由;单点顺序保留
- [x] 2.2 订阅 API 演进:`SubscribeAll` + `SubscribePoints`(动态 Add/Remove 且计数化——同点多监控项安全;Remove 按注册时条目摘除,槽位复用不误伤);COW 点级订阅表;每订阅独立队列+pump 保留
- [x] 2.3 子集消费方迁移:Alarms 按告警点集注册(hub.Snapshot 挑点)、ComputedEngine 按表达式输入注册、OPC UA 推送随监控项建立/删除 Add/Remove(pushSub 经 IgwServer 传入 NodeManager)
- [x] 2.4 全量消费方迁移:DataLogger("history")、MqttPublisher("mqtt")改 `SubscribeAll`;旧 `Subscribe` 已删除
- [x] 2.5 单元测试:投递面/动态增删/计数注册、单点顺序、单片拥塞不拖全局、慢订阅不拖快订阅、行为等价(告警触发清除/计算重算)——109 全绿(钉点推送路径与 2.3 同构,活 UA 会话留实机)

## 3. 背压可观测

- [x] 3.1 计数与快照:每片 丢弃/队列深度,每订阅 名称/类别/注册点数/滞后/丢弃;`DataHub.Stats()` 近似读快照
- [x] 3.2 暴露:`/health` 增 `dataplane` 字段;监控中心数据面条(常驻展示,丢弃标红但不产生告警/弹窗)
- [x] 3.3 单元测试:冻结 pump 精确断言总线丢弃(64 容量写 200 → 恰丢 136);慢订阅丢弃归属自己、快订阅为 0;快照结构(名称/类别/注册数)——112 全绿

## 4. 基准台架

- [x] 4.1 `tools/DataplaneBench`:参数化(--devices/--tags/--active-pct/--speed/--scan-ms/--seconds/--shards/--smoke)模板展开生成工程 → Simulator 真实引擎 → 吞吐/延迟 p50 p99/各级丢弃/GC/工作集
- [x] 4.2 通过判定与报告:稳态窗增量判定(启动突发=全点坏→好的合法有界丢弃,单列报告不计罚);未过明示瓶颈(哪片/哪订阅/实测吞吐)
- [x] 4.3 冒烟档 8s(1 万点,稳态 gen0=0 零装箱实证);参考机(i7-10750H/32GB)1M 档**通过**:稳态 99,878 条/秒(100%)、稳态零丢弃、p99 80.6ms、工作集 ~1GB;默认 8 分片/100k÷N 容量定档

## 5. 验证与收尾

- [x] 5.1 全量回归 112 用例全绿 + `obs-demo.igwproj` 实跑无回归:/health(dataplane 字段)/值读写/异常中心/MES 正常;OPC UA 新配线经备用端口工程验证 running + opcua-push 订阅出现(4840 被本机已装 IGW 服务占用属环境)
- [x] 5.2 基线数据与墙③立项输入(CreateAddressSpace 1M 节点构建/PushValue 全局锁/Publish 队列)记入 `docs/RELEASE_NOTES.md`
- [x] 5.3 发版说明:对外零变化(值类型逐位不变/工程格式不变/**无单向门可回退**)、仓库内部 API 变更清单、丢弃不再静默
