# 设计:数据面单机 100 万点

## Context

- 现状 `DataHub`(`IGW.Core/DataHub.cs`):点表 `ConcurrentDictionary<string, Entry>`,Entry 内联 seqlock 保护的 `DataValue`(读零分配、不撕裂);变化总线 = **单**有界 Channel(100k,DropOldest)+ **单** dispatcher 扇出;每订阅独立队列(10k,DropOldest)+ 独立 pump。
- 五个订阅方全部 `Subscribe(OnChange)` 全量收:Alarms(只关心带告警定义的点,回调里 `TryGetDef` 后丢弃)、ComputedEngine(只关心表达式输入点,`_inputMap.TryGetValue` 后丢弃)、OPC UA(只关心被钉点,`IsPinned` 后丢弃)、DataLogger/MQTT(真全量)。1M 点、十万级变化/秒下,前三家每秒做十万次字典查找再扔掉。
- `TagChange(string TagId, DataValue)`:分发按字符串;`DataValue.Value` 是 `object?` → 数值每条变化装箱(~64B),总线+订阅队列高水位时可滞留百万级箱体。
- 采集侧已句柄化:`DeviceLoop` 绑定期解析 `GetHandle`,`UpdateByHandle` 热路径零字典查找——底子方向正确,本 change 是把句柄贯穿到"总线→订阅"这后半程,不推倒采集循环。
- 基准工程可由模板生成(`device-template-config` 已落地):2000 设备 × 500 点 = 1M,Simulator 驱动。

## Goals / Non-Goals

**Goals:**

- 1M 点、≥10 万变化/秒稳态:分发不成为单线程瓶颈,稳态热路径零装箱、零字典查找。
- 只关心子集的订阅方(告警/计算/OPC UA 推送)成本 ∝ 自己关心的点数,而非全局变化率。
- 丢弃(背压)可见:每片/每订阅计数暴露,不再静默。
- 可复跑的 1M 基准台架,产出吞吐/延迟/丢弃/GC/内存基线数据。

**Non-Goals:**

- OPC UA 地址空间 1M 节点与推送锁(墙③,另立 change;本次仅记录其瓶颈证据)。
- 驱动接口/解码管线零分配(驱动产出的短命装箱保留,属采集侧后续)。
- 跨点变化顺序保证(从未承诺,继续不承诺;单点顺序保留)。
- 历史存储(DataLogger 的 SQLite 写入能力)扩容。

## Decisions

### D1 句柄贯穿:TagChange 携带条目引用 + 槽位索引,字符串只在边界

- 点表增加**槽位数组**(分段 `Entry[]` 块,句柄 = int 槽位号),`ConcurrentDictionary<string,int>` 仅做边界的名字→句柄解析(注册、REST 读、MES GroupRef);Entry 保留 TagId 字符串引用供反向输出。
- `TagChange` 改为携带 `(int Handle, Entry 引用, DataValue)`,对消费方暴露 `TagId`(取 Entry 里的字符串引用,零分配)——分发全程无字符串哈希、无字典查找。
- 防 ABA 不引入代际位:句柄身份以 Entry 对象为准(与今日 `object handle` 语义一致),设备卸载后旧循环写孤儿 Entry 无害(GC 兜底),槽位复用只影响新注册。
- **备选**:纯 int 句柄 + 代际计数——多一套失效协议,而 Entry 引用天然安全;否。

### D2 变体值存储:总线与缓存零装箱,驱动侧短命箱保留

- `DataValue` 增变体承载:内联 `double/long/bool`(union 语义)+ `object?` 仅用于 string/数组;`Publish` 处把驱动产出的 object 一次规范化进变体。
- 效果:Entry 常驻值、总线队列、订阅队列全部无箱体滞留;GC 压力只剩驱动解码的短命 gen0。
- **备选**:驱动管线泛型化全程免箱——动驱动接口,出圈;否。

### D3 总线分片:按句柄哈希分 N 片,每片独立队列 + pump

- N 默认 `min(处理器数, 8)`(基准后定,留配置);同句柄恒定同片 → **单点顺序保留**(每点本就单写者)。
- 每片:有界 Channel(容量 = 现 100k/N 起步)+ 独立 dispatcher。分发吞吐随核扩展,单片拥塞不拖全局。
- **备选**:单队列多消费者工作窃取——破坏单点顺序;否。

### D4 订阅倒转:点注册订阅 + 全量订阅并存

- Entry 挂 `PointSubs`(copy-on-write 不可变数组,注册/注销罕见);每片另有 `wildcard` 订阅列表。分发一条变化 = 投递给 `Entry.PointSubs ∪ 该片 wildcard`。
- API 演进(**仓库内部 BREAKING**):`SubscribeAll(cb)`(DataLogger/MQTT)+ `SubscribePoints(cb)` 返回可 `Add(handle)/Remove(handle)` 的订阅句柄(Alarms 按告警点集注册、Computed 按输入点注册、OPC UA 在 Pin/Unpin 时注册/反注册——与既有钉点生命周期天然对齐)。
- 每订阅仍保留独立队列 + 单 pump(慢消费者隔离不变)。**单写者前提守住**:计算点只被 Computed 的单 pump 写,告警状态只被 Alarms 的单 pump 改——倒转不引入并发写。
- **备选**:保留全量 offer + 订阅侧位图速滤——仍是 N 订阅 × 全变化率的投递成本;否。

### D5 背压可观测:计数暴露,不加告警噪音

- 计数:每片 总线丢弃数/队列深度,每订阅 丢弃数/滞后深度;`DataHub.Stats()` 快照 → `/health` 增 `dataplane` 字段,监控中心加一张卡。
- DropOldest 策略保留(有界内存是底线),只是**必须可见**;不弹 toast、不进告警(噪音零容忍),异常放监控中心。

### D6 基准台架:模板生成 1M 工程 + Simulator,指标进文档

- `tools/DataplaneBench`(独立控制台):用 `TemplateExpander` 生成 20 模板 × 2000 实例(1M 点)内存工程 → Simulator 驱动起引擎 → 采样 稳态变化/秒、发布→消费延迟 p50/p99、丢弃计数(目标负载下必须为 0)、GC 代数、工作集。档位可调(`--devices/--tags/--rate`)。
- 单元测试只做**功能**断言(分发正确性、倒转投递面、计数准确),不做性能断言(本机波动会假红);性能基线数字记入 bench 输出与发版说明。

## Risks / Trade-offs

- [变体使 `DataValue` 变宽(≈+8B)× 1M 常驻] → 仍远小于装箱(64B 堆箱 + 引用);净省且消灭 GC 扫描面。
- [倒转后 Alarms/Computed 需要显式点集] → 二者本就从工程定义构建(每次热重载重建),把"隐式过滤"变"显式注册"反而消掉每秒十万次无效查找;注册遗漏风险由功能测试兜(投递面断言)。
- [OPC UA 推送在 1M 订阅点下 NodeManager 全局锁成为新瓶颈] → 本 change 只把"进入推送前"的成本降到 ∝ 钉点数;锁问题记录进墙③的立项输入,台架顺带出证据。
- [MQTT/DataLogger 全量通道在 1M 下自身跟不上] → 有界队列 + 丢弃计数使其可见;扩容属各自组件的后续,不在数据面。
- [分片数选错] → 留配置 + 基准数据定默认;错了只是吞吐次优,不是正确性问题。
- [内部 API BREAKING 波及五个消费方] → 同一 change 内全部随改 + 全量测试;仓库外无消费者。

## Migration Plan

1. **存储先行**(可独立合入):槽位表 + 变体 DataValue + TagChange 句柄化,分发暂保持单片全量——行为等价,测试全绿即可发。
2. **分片 + 倒转**:总线分片、订阅 API 演进、五个消费方随改(同一提交,内部 API 不留过渡层)。
3. **观测 + 台架**:计数暴露、监控中心卡、DataplaneBench 与基线数据。
4. 回滚:纯内存行为,无 `.igwproj`/协议/持久化格式变更——直接 revert 对应提交即可,无单向门。

## Open Questions

- 分片数与各队列容量的默认值——由台架数据定(先按 `min(核,8)`、100k/N 起步)。
- 延迟采样方式:TagChange 内嵌时间戳(+8B/条)还是采样探针点——倾向探针(仅台架注入少量带戳点),不给热路径加常驻负担。
- 台架是否顺带出墙③(OPC UA 1M 订阅推送)的瓶颈证据——若便宜就做,作为下一 change 的立项输入。
