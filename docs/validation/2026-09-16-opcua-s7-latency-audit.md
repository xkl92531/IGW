# OPC UA / S7 速度与稳定性复查

审查基线：master `a63ca0be9353b881f9f758bd5077f67b0b53db3d`（1.6.59）。
范围：DeviceLoop、OPC UA 客户端、S7 Classic、S7 Plus 托管适配及 Rust 桥接。

后续进展：A、B、C 已进行代码修复，见 [2026-09-17 修复与验证](2026-09-17-opcua-latency-fixes.md)。本文保留修复前审查证据，不代表当前修复状态。

## 结论

1.6.59 已减少一部分订阅重建和固定等待，但仍有 IGW 自身放大故障等待的路径。不能把“BadRequestInterrupted 重试间隔改成 100ms”表述为“100ms 内恢复”。本轮没有修改生产驱动，也没有连接现场 PLC；以下是实际代码上的本地故障注入及静态审查结果，不是现场根因的逐条归因，也不是修复完成证明。

## 已复现的六项问题

|编号 / 优先级|触发与实测结果|影响 / 修复方向|
|---|---|---|
|A / P0|2 点订阅，服务器只接受 1 点；需求保持不变，3 次 reconciliation 发生 3 次创建、2 次释放。|正常订阅也被反复拆掉。分开记录请求集合与接受集合；拒绝点单独退避、保留正常监控项；后续支持增量修改。|
|B / P0|首次 Subscribe 抛 BadRequestInterrupted；注入一轮 2500ms 读取，实际下一次订阅尝试在约 **2568ms** 后才发生。|100ms 只是时间戳。慢速兜底轮询仍抢先占用设备循环。恢复操作需有独立调度优先级、物理请求边界让出和总预算；从失败完成时计时。|
|C / P0|8 点持续返回 BadRequestTimeout，一次逻辑读取发生 **11 次协议请求**，规模为 8、4、2、1×8；窗口降到 1。每次注入延迟仅 5ms。|超时被当成容量不足，在同一调用里重复拆分、重试。超时与明确容量拒绝分开分类，限制总尝试/总时间，并交回调度器退避；不能把这次 5ms 注入时长当现场耗时。|
|D / P0|S7 Plus 数组 768 元素，第一片结束后取消；仍完成 **3 片**并返回 Good。|数组内部不接收取消令牌，停止/重载仍被后续请求拖住。把取消传到每个物理请求边界；已完成结果与未完成结果明确区分。|
|E / P1|S7 Plus 数组发生 TransportFailure，容量窗口从 **64 降为 32**。|网络断连污染容量学习，后续请求数可能增多。仅明确容量错误降低窗口；补齐计划版本、真实成功耗时及本地扩容重读的训练规则。|
|F / P1|S7 Classic 同一个不存在的 DB，8 个连续点，返回相同地址错误；一次逻辑读取共 **15 次请求**。|递归二分会放大整段失败。区分 DB/权限级失败与局部地址失败，设置隔离探测预算、缓存确认的坏范围；继续保障其它 DB。不能完全去掉二分，合法点仍需隔离恢复。|

源码定位（行号对应审查基线）：

- A：`src/IGW.Core/Engine.cs:1448,1457,1484`；`src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs:416`。比较的是候选全集与实际接受集合。
- B：`Engine.cs:762,846,1521`。订阅检查之后进入读取循环；100ms 截止时间没有独立唤醒/抢占语义，且使用进入检查时的 nowMs。
- C：`OpcUaClientDeviceDriver.cs:560,998,1016`。TimeoutException / BadRequestTimeout 都进入容量分类。
- D：`src/IGW.Drivers.IoTClient/SiemensPlusTransport.cs:179,197,234`；`SiemensSymbolicArrayCodec.cs:21`。外层检查令牌，内部数组循环只检查优先写入；Task.Run 使用 CancellationToken.None。
- E：`SiemensSymbolicArrayCodec.cs:54,64,107`。断连收缩窗口，写入成功训练使用固定 1ms；不同于标量路径的训练保护。
- F：`src/IGW.Drivers.IoTClient/SiemensClassicBlockCodec.cs:159`。范围失败递归分成两半；点位错误退避在整个读取返回后才记录。

## 静态审查发现：需补回归用例后修复

### P0：OPC UA 写超时可能重复发送

`OpcUaClientDeviceDriver.cs:768` 写路径复用 IsCapacityError；多点写超时也缩小窗口后重新发送。超时不证明 PLC 没有执行写入，不能无条件拆分重放。应返回明确的“结果未确认”，阻断隐式重放；只有能够确定未执行的容量拒绝才允许受限重试。此项已确认代码路径，尚未在真实会话上复现重复写入。

### P1：订阅首帧补读仍串行阻塞，且缺少普通读取已有的保护

`Engine.cs:1507,1570` 在订阅建立过程中直接 await 所有缺失首值批次。100–500ms grace 只限制等通知的时间，不限制随后的补读。pending 在循环前固定，后续已经收到通知的点也可能被重复读取。

普通读取有物理请求计时与 writeEpoch 防旧值覆盖；PrimeRealtimeSubscriptionAsync 直接 ReadAsync / Publish(samples)，没有传入 writeEpoch。需要把首帧补读纳入统一调度、重新检查缺失集合、记录完整耗时并复用写入一致性保护。旧值覆盖属于静态风险，本轮未构造并发写入复现。

### P1：S7 Plus 本地缓冲区探测重复执行浏览/读取

`SiemensPlusNative.cs:82,111,160`：Browse 先用空缓冲区调用，得知大小后再调用一次；Rust `native/igw-s7plus/src/lib.rs:374` 每次都执行 browse_vars 再复制输出。依赖 s7commplus 0.1.0 的 browse_vars 每次调用 prefetch_type_container（远端 Explore），还会再次遍历、展开变量；这不是单纯复制已取得的数据。已有类型缓存不能消除这次显式预取。

ReadBuffered 本地输出容量不足时，也再次执行真实 PLC 读取。应缓存同一次操作的序列化结果，通过结果句柄/受限缓存复制到目标缓冲区，再释放；不能为了扩容重新采样。大工程浏览应做按范围加载及结果量限制，缓存绑定会话/程序版本。本轮未实测该路径的毫秒数。

### P1：重试与资源关闭缺乏统一的总时间预算

- S7 Classic `IoTPlcDriver.cs:171` 默认允许 2 次内部重试；一次调用可能包含多次超时、重新建连和延迟，上层看到的是合并耗时。
- S7 Hybrid `SiemensHybridDeviceDriver.cs:342` 串行保护会话是必要的；但队列等待需要独立计时、取消及公平调度。
- OPC UA `DisconnectAsync` 使用 CancellationToken.None；订阅删除的 250/500ms 令牌不等于同步 Dispose 有硬时间上限。
- S7 原生同步调用当前由 Task.Run 包裹；不能以 Task.WhenAny 超时后直接重用或释放仍在执行的同一个原生句柄。应停止追加请求，依照底层 socket 超时退出；必要时按会话代次隔离失效对象。

这些是边界设计风险，未在本轮模拟中证明每项都会卡数秒。

### P2：让出读取与诊断完整性

优先写入触发 DriverReadYieldException 时，之前读到的前缀结果可能没有返回给调度器，重试又从头开始；Engine.cs:1013 还把这次计时标记为不保留。应设计携带已完成结果及剩余游标的让出方式、保证尾部点公平性，并记录被打断的实际请求耗时。不能只展示成功批次而漏掉最慢的取消/首帧/恢复阶段。

## 建议实施顺序与验收

### 第一批：阻止等待放大和错误恢复

处理 A–D、写超时重放、首帧调度。以每个逻辑操作的总预算统一约束重试，保留健康订阅，不让一个拒绝点导致全组重建。

必须增加自动化验收：

1. 部分订阅拒绝后需求不变，正常监控项持续存活；10 次检查不会重建整组。
2. BadRequestInterrupted 后不得先启动一整轮长兜底扫描。模拟底层可立即响应时，重试符合配置截止时间及调度容差；底层已有请求阻塞时分别记录阻塞时间与 IGW 额外延迟。
3. 普通超时不能触发二分容量重试；请求数和总预算有明确断言。单点故障不阻断其它正常设备。
4. 写超时注入“服务端已执行、回复丢失”，断言客户端不自动再次发送。
5. S7 数组第一片后取消，不再发送第二片；不得把未完成数据报为 Good。覆盖读取、写入、缓冲区扩容重读。
6. 首帧期间并发写入，旧读取不能覆盖新值；通知已补齐的点不再重复补读。

### 第二批：减少重复工作、补齐稳定性与诊断

处理 E–F、S7 浏览/缓冲区复制、Classic 重连预算、让出续读与窗口训练。补齐队列、建连、订阅修改、首值、实际请求、退避、关闭各阶段计时与请求数，关联设备/任务/会话代次。

验收至少包括：不存在 DB 的受限探测；断线不收缩容量；缓冲区扩容不新增 PLC 请求；持续优先写入时尾部点不饥饿；反复断连/重载后任务、会话、原生句柄和内存不持续增长。

“IGW 不额外拖慢”要以这些有边界的调度和失败注入指标验证，不能只把所有协议超时改短。配置 100/200ms 不代表全部点在所有 PLC 负载下都能完成；超过设备吞吐量时需要明确超期和背压，不堆积历史扫描任务。

## 本轮证据与运行方式

仓库根目录运行（.NET 10）：

```powershell
dotnet run --project docs/validation/driver-latency-audit/Audit.csproj --verbosity quiet
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --filter "FullyQualifiedName~DeviceLoopSubscriptionTests|FullyQualifiedName~OpcUaReadRecoveryTests|FullyQualifiedName~SiemensSymbolicArrayCodecTests|FullyQualifiedName~SiemensClassicReadPlannerTests|FullyQualifiedName~SiemensHybridSessionLifecycleTests"
```

复现程序只使用模拟驱动/注入回调，不访问 PLC。它输出当前行为的观测值，退出码 0 只表示探针跑完，**不代表上述风险通过验收**。代码使用真实 DeviceLoop 和驱动内部路径，其中私有方法通过反射调用，后续重构时需要更新探针。固定基线保存在 [baseline-results.json](driver-latency-audit/baseline-results.json)。

本轮已有定向回归 **58/58 通过**，同时六项问题仍能复现，说明现有用例没有覆盖这些边界。未运行完整发布套件；未改生产代码、未重打安装包，也未进行 S7-1515/1516/1517 实机压力和长时间稳定性验收。
