# 数据面单机 100 万点(变化总线分片 + 订阅倒转 + 整型句柄)

## Why

规模路线的三面墙中,墙①(配置文档模型)已由 `device-template-config` 解决——工程文件与设备数解耦,20 模板 × 2000 实例(百万点)工程随手可建。下一面墙是数据面本身:当前 DataHub 变化总线是**单 dispatcher + 字符串分发 + DropOldest 静默丢 + 每条变化装箱**,在 1M 点、十万级变化/秒下,分发线程成为全局瓶颈,GC 稳态产生垃圾,且丢数据无人知晓(与"告警不静默"的产品原则冲突)。模板落地后基准工程已可得,现在是补数据面的时机。

## What Changes

- **整型句柄贯穿**:点表改为槽位数组 + 整型句柄,采集→总线→订阅全热路径只携带句柄;字符串 TagId 仅存在于边界(注册、REST/OPC UA 查询)。现有 `GetHandle/UpdateByHandle` 句柄 API 的自然延伸,seqlock 点表保留。
- **变体值存储**:`DataValue` 的数值不再每条变化装箱(变体 struct 承载常用数值类型),消除稳态 GC 垃圾(当前 ~64B/变化)。
- **总线分片**:变化总线按句柄哈希分 N 片,每片独立有界队列 + 独立 pump——分发吞吐随核数扩展,单片拥塞不拖全局。
- **订阅倒转**:由"每条变化 offer 给所有订阅、订阅回调内自行过滤"倒转为"订阅者按点注册兴趣,分发只投递给该点的订阅者"。Alarms/ComputedEngine/OPC UA(只关心 告警点/表达式输入/被钉点)改为点注册;DataLogger/MQTT 保留全量订阅通道。**BREAKING(仓库内部 API)**:`IDataHub.Subscribe` 签名演进,五个内部消费方随改;对外 REST/OPC UA 行为不变。
- **背压可观测**:每片/每订阅的丢弃与滞后计数,暴露到 `/health` 与监控中心——丢可以,静默不行。
- **基准台架**:Simulator 驱动 + 模板工程(2000 设备 × 500 点 = 1M)的可复跑基准:吞吐、分发延迟、丢弃数、GC、内存足迹;小档位可进常规测试。

不做(明确出圈):OPC UA 地址空间 1M 节点(墙③,另立 change);驱动接口与采集循环结构(方向已对,不推倒);历史存储扩容。

## Capabilities

### New Capabilities

- `datahub-handle-storage`: 槽位点表与整型句柄、变体值存储——1M 点的内存足迹与零装箱热路径。
- `datahub-sharded-dispatch`: 分片变化总线与订阅倒转——分发吞吐可扩展,点注册订阅与全量订阅并存。
- `datahub-backpressure-observability`: 背压与丢弃可观测——每片/每订阅计数,不静默丢。
- `dataplane-benchmark`: 1M 点基准台架——可复跑的规模验证与指标基线。

### Modified Capabilities

(无——现有 11 个能力(mes-*/template-*)均不涉及变化总线的规格级行为。)

## Impact

- **`IGW.Abstractions`**:`IDataHub`/`TagChange` 演进(句柄化、订阅 API);`DataValue` 增变体存储。仓库内部 API,无对外协议影响。
- **`IGW.Core`**:`DataHub`(槽位表/分片/倒转,改动核心)、`Engine`(绑定期句柄注册,publish 路径微调)、`Alarms`/`ComputedEngine`(改点注册订阅)、`DataLogger`(全量通道)。
- **`IGW.Northbound.Mqtt`**:发布器改全量通道;**`IGW.Northbound.OpcUaServer`**:推送订阅改按钉点注册。
- **新增**:基准台架(`tools/` 下独立工程或测试内基准)+ `/health` 背压字段 + 监控中心展示。
- **不影响**:驱动实现、采集循环结构、工程模型/模板、REST 读写端点行为、MES 上报(读缓存不订阅)。
