# IGW 工业网关 架构与流程分析

> 本文档基于源码梳理 IGW 的设计目标、整体架构、业务/数据/控制流程。所有流程图用 Mermaid 描述，可在支持 Mermaid 的 Markdown 查看器中渲染。

## 一、项目概览

### 1.1 设计目标

IGW（Industrial Gateway，工业网关）对标 KEPServerEX，定位为一款**跨平台、可热重载、支持百万点位的工业数据中转网关**。核心职责是把南向 PLC / 设备 / MQTT 设备的数据采集到本地缓存（DataHub），再通过多种北向协议（OPC UA Server、MQTT、REST、MES HTTP）统一对外发布，并提供配置、监控、报警、计算、历史记录、计划任务等工厂网关标准能力。

设计目标要点（源自代码注释与目录命名）：

- **对标 KEP**：OPC UA Server、IoT/MQTT 发布、Advanced Tags 计算、Data Logger、Scheduler、User Manager/Security、报警等功能均对标 KEP。
- **热重载**：`GatewayHost` 支持下发新配置后整体或增量重建，南向不变时只重建北向，设备不断线、缓存不丢。
- **百万点位**：DataHub 采用按句柄寻址、按点注册订阅、active-tag 按需采集，避免穿透 PLC。
- **跨平台**：Windows 服务（SCM）与 Linux systemd 同一套代码；Web 配置界面内嵌 Vue 产物。
- **授权控制**：未授权/过期时只保留 REST 以便激活，不采集、不北向。
- **现场可用性**：熔断、退避重连、自动降级、主备路径冗余回切、通道级自动重连。

### 1.2 技术栈

- 后端：.NET（`IGW.Runtime` 目标 `.net10.0-windows`，含 WPF 托盘/配置工具），ASP.NET Core Minimal API + Swagger。
- 驱动依赖：IoTClient（Modbus/Siemens/Omron FINS/AB/Mitsubishi）、libplctag（EtherNet/IP）、OPC.Ua.Client/SDK、MQTTnet。
- 前端：Vue 3 + Vite + TypeScript + Pinia + Element Plus。
- 数据存储：SQLite（历史/MES 日志/凭证）、`%ProgramData%\IGW` 作为可写数据根。
- 服务化：`UseWindowsService` + `UseSystemd`，同一可执行文件双形态运行。

### 1.3 解决方案结构（IGW.slnx）

| 层 | 项目 | 职责 |
|----|------|------|
| 抽象 | IGW.Abstractions | 接口契约 + 工程数据模型 + DataHub 抽象 |
| 核心 | IGW.Core | 采集引擎、设备循环、驱动注册表、地址块合并、报警/计算/计划/日志引擎 |
| 运行时 | IGW.Runtime | 宿主编排、授权、REST 挂载、入口 Program |
| 南向驱动 | IGW.Drivers.IoTClient / EtherNetIP / OpcUaClient / Mitsubishi / Mqtt / Simulator | 各协议设备接入 |
| 北向 | IGW.Northbound.RestApi / OpcUaServer / Mqtt / Mes | 对外发布与上报 |
| 应用 | IGW.WebUi(Vue) / IGW.Licensing / IGW.DbSetup / IGW.DataMover | Web 管理、授权、数据库配置、数据迁移 |

---

## 二、整体分层架构

```mermaid
flowchart TB
    subgraph APP["应用与运维层"]
        WEB["IGW.WebUi<br/>(Vue3 Web 界面)"]
        LIC["IGW.Licensing<br/>(机器码/授权)"]
    end

    subgraph NB["北向接口层 (IGW.Northbound.*)"]
        REST["RestApi<br/>/api/v1 + /iotgateway"]
        OPCUA["OpcUaServer<br/>opc.tcp :4840"]
        MQTTP["MqttPublisher<br/>topic 发布"]
        MES["MesUploadEngine<br/>HTTP 上报"]
        ALM["AlarmEngine"]
        CMP["ComputedEngine"]
        SCH["ScheduleEngine"]
        LOG["DataLogger (SQLite)"]
    end

    subgraph CORE["核心层 (IGW.Core)"]
        HOST["GatewayHost<br/>宿主编排/热重载/授权调和"]
        ENG["RuntimeEngine<br/>设备级生命周期"]
        LOOP["DeviceLoop<br/>采集循环/熔断/重连/降级/主备"]
        REG["DriverRegistry<br/>驱动工厂注册表"]
        AB["AddressBlockOptimizer<br/>读块合并"]
    end

    subgraph HUB["数据中枢 (IGW.Abstractions)"]
        DH["DataHub<br/>缓存/订阅/active-tag"]
        SD["ScanDemandRegistry<br/>按需采集租约"]
    end

    subgraph DRV["南向驱动层 (IGW.Drivers.*)"]
        D1["Modbus/IoTClient"]
        D2["Siemens/IoTClient"]
        D3["Omron FINS/IoTClient"]
        D4["AB/IoTClient"]
        D5["Mitsubishi"]
        D6["EtherNet/IP(libplctag)"]
        D7["OPC UA Client"]
        D8["MQTT(南向订阅)"]
        D9["Simulator"]
    end

    subgraph DEV["现场设备"]
        PLC1["PLC / 仪表"]
        PLC2["OPC UA Server"]
        MQTTDEV["MQTT 设备"]
    end

    APP -->|"HTTP REST / 工程下发"| REST
    APP -->|"机器码/激活"| LIC
    LIC -->|"LicenseState.Blocked?"| HOST

    WEB --> REST

    NB -->|"订阅/读缓存"| DH
    MES -->|"上报需数据"| DH
    MES -->|"写回"| ENG
    REST -->|"读缓存/Touch/写"| DH
    OPCUA -->|"PushValue"| DH

    HOST --> ENG
    ENG --> LOOP
    ENG --> REG
    LOOP --> AB
    LOOP -->|"Update(samples)"| DH
    LOOP -->|"ScanDemand"| SD
    SD --> DH

    REG --> DRV
    DRV --> DEV

    REST -.->|"内嵌 wwwroot"| WEB
```

> 关键说明：`DataHub` 是唯一的数据中枢，南向只写、北向只读/订阅，避免任何北向穿透 PLC。`GatewayHost` 是编排核心，统一持有 Hub/Engine 与各北向组件。

---

## 三、核心数据模型

工程文件 `.igwproj` 的层级结构与运行时中枢：

```mermaid
classDiagram
    class IgwProject {
        +SchemaVersion
        +Name
        +Settings: ProjectSettings
        +OpcUa: OpcUaSettings
        +History: HistorySettings
        +MqttPublish: MqttPublishSettings
        +MesUpload: MesUploadSettings
        +ComputedTags
        +Schedules
        +Security
        +Templates
        +Channels
    }
    class ChannelDef {
        +Id/Name
        +DriverId
        +Settings
        +Devices
    }
    class DeviceDef {
        +Id/Name
        +Endpoint
        +TemplateRef
        +Delta
        +Groups/Tags
    }
    class DeviceTemplate {
        +Name
        +DriverId
        +DefaultSettings
        +Groups/Tags
    }
    class DeviceDelta {
        +Overrides
        +Adds
        +Masks
    }
    class TagGroupDef {
        +Name
        +SubGroups
        +Tags
    }
    class TagDef {
        +Name
        +Address
        +DataType/IsArray
        +Access
        +ScanRateMs
        +Scaling
        +Deadband/DeadbandType
        +Alarm
    }

    class DataHub {
        <<interface IDataHub>>
        +Register(tagId, def)
        +Update(tagId, value)
        +TryGet(tagId)
        +SubscribeAll(onChange)
        +SubscribePoints(onChange)
        +Touch/Pin/Unpin/IsActive
    }

    IgwProject "1" o-- "*" ChannelDef
    ChannelDef "1" o-- "*" DeviceDef
    DeviceDef --> "0..1" DeviceTemplate : TemplateRef
    DeviceDef --> "0..1" DeviceDelta : Delta
    DeviceDef "1" o-- "*" TagGroupDef
    TagGroupDef "1" o-- "*" TagDef
    DeviceTemplate "1" o-- "*" TagGroupDef
```

> 层级：**Project → Channel → Device → (Group*) → Tag**。Tag 是北向【分组路径】↔ 南向【PLC 地址】的绑定。模板实例通过 `Delta`（覆盖/追加/屏蔽）运行时展开，不落全量拷贝。

---

## 四、业务流程

### 4.1 网关启动流程

```mermaid
flowchart TD
    A["Program.Main 启动"] --> B["IgwPaths.MigrateFromLegacy<br/>迁移旧用户数据到 ProgramData"]
    B --> C["WebApplication 构建<br/>UseWindowsService + UseSystemd"]
    C --> D["加载/生成 project.igwproj"]
    D --> E["注册 DriverRegistry<br/>Modbus/OpcUa/Siemens/FINS/AB/Mitsubishi/EIP/MQTT"]
    E --> F["new GatewayHost(project, registry, ...)"]
    F --> G{"LicenseManager.Evaluate<br/>Blocked?"}
    G -- "是:未授权/过期/机器不符/时间回拨" --> G1["只起 REST 便于激活<br/>不采集不北向"]
    G -- "否(或仅临期告警)" --> H["StartSouthAsync<br/>建 DataHub + ScanDemandRegistry + RuntimeEngine"]
    H --> I["StartNorthboundAsync<br/>报警/计算/计划/历史/OPC UA/MQTT/MES"]
    I --> J["MapIgwApi / MapDcsTagCompatibilityApi<br/>挂载 REST + Swagger + 内嵌 Web UI"]
    J --> K["app.RunAsync<br/>监听 RestPort"]
    G1 --> J
```

入口：[Program.cs:14-87](file:///d:/AI/AIProject/IGW/src/IGW.Runtime/Program.cs)。授权闸门：[GatewayHost.cs:134-149](file:///d:/AI/AIProject/IGW/src/IGW.Runtime/GatewayHost.cs)。

### 4.2 设备采集循环（含熔断/重连/降级/主备）

`DeviceLoop` 是每设备一条后台任务的主循环，集连接管理、扫描计划、熔断器、退避重连、自动降级、主备冗余于一体：

```mermaid
flowchart TD
    START(["loop.Start(devCts.Token)"]) --> LOOP{"while !ct.IsCancellationRequested"}

    LOOP --> M{"手动重连请求?"}
    M -- 是 --> M1["ResetTransport + 复位熔断/退避/降级<br/>MarkAllBad"]
    M -- 否 --> D{"有采集需求 HasDemand?<br/>(MES/QC/OPCUA/活动点)"}

    LOOP --> C{"CircuitState == Open?"}
    C -- "是且未到期" --> C1["MarkAllBad + 丢弃待写<br/>WaitWorkOrDelay(剩余)"]
    C -- "是且已到期" --> C2["→ HalfOpen<br/>清扫描计划，立即探测"]

    LOOP --> DM{"已启用降级且在降级期?"}
    DM -- 是 --> DM1["停止采集<br/>待写直接失败<br/>DelaySafe(0.5s)"]

    LOOP --> SP{"在备用路径 & 到探测点?"}
    SP -- 是 --> SP1["ProbePrimaryAsync<br/>主恢复→切回主、断备"]

    LOOP --> CONN{"_active.State == Connected?"}
    CONN -- 否 --> CONN1["ConnectAsync<br/>失败→退避抖动 1~30s 重试<br/>HandleFailureAsync(切备/降级)"]
    CONN -- 是 --> CONN2["CompleteManualReconnect(true)"]

    CONN2 --> IDLE{"有需求?"}
    IDLE -- 否 --> IDLE1["仅保持会话不读<br/>WaitWorkOrDelay(idle)"]
    IDLE -- 是 --> PLAN["RebuildPlanIfNeeded<br/>按 ScanRate 建读块截止时间"]

    PLAN --> HALF{"HalfOpen 且无读块?"}
    HALF -- 是 --> HALF1["最小读取探测 _bindings[0]"]
    HALF -- 否 --> READ["循环取到期读块 (≤64/趟)<br/>driver.ReadAsync(block.Tags)"]

    READ --> PUB["Publish(samples, block)<br/>→ Hub.Update"]
    PUB --> OK{"有有效样本?"}
    OK -- "否" --> RT["失败计数++<br/>该块指数退避单独复探<br/>MarkBlockBad"]
    OK -- "是" --> OK2["清零失败计数<br/>关闭熔断<br/>RecordCompletion"]

    READ --> ERR{"读取异常?"}
    ERR -- 是 --> ERR1["ReportDiagnostic<br/>ResetTransport<br/>connectionLost=true"]
    ERR1 --> HF["HandleFailureAsync<br/>切备/熔断/降级"]

    OK2 --> W["DrainWritesAsync 排空写队列"]
    W --> NEXT["DelayUntilNext<br/>睡到下个到期块/被写唤醒"]
    NEXT --> LOOP
```

关键源码：[Engine.cs:267-500](file:///d:/AI/AIProject/IGW/src/IGW.Core/Engine.cs)（DeviceLoop 主循环）。

控制要点：
- **会话与扫描解耦**：设备启用即建立连接（反映真实在线/离线），需求只决定是否读取点位。
- **熔断器**：连续失败达阈值 → Open（阻塞 + 丢弃待写），到期 → HalfOpen 单次探测，成功 → Closed。
- **退避抖动**：1~5s 范围 ×(0.7~1.3) 抖动，避免全场锁步；读成功才复位退避。
- **自动降级**：连续故障达 `demoteAfter` → 降级期停采集（对齐 KEP「故障时降级」）。
- **主备冗余**：备用路径时定期探测主路径，恢复则切回（对齐 KEP「尽快返回至主要设备」）。
- **active-tag**：没人引用的点不去轮设备，由 `Touch/Pin/IsActive` 控制。

### 4.3 南向设备建立流程（StartDevice）

```mermaid
flowchart TD
    A["Engine.StartAsync<br/>遍历 Channels → Devices"] --> B["ResolveFactory(ch)<br/>DriverRegistry.TryGet"]
    B --> C["EffectiveDevice<br/>模板实例→展开(模板+Delta)"]
    C --> D["NetworkBindingPlan.Prepare<br/>网卡/源地址绑定"]
    D --> E["factory.CreateDevice(channel, device)<br/>创建 IDeviceDriver"]
    E --> F["BindDevice<br/>AddressParser 解析地址→TagBinding"]
    F --> G["ScanDemands.RefreshDevice<br/>重绑 MES 租约防 ABA"]
    G --> H{"secondaryEndpoint 配置?"}
    H -- 是 --> H1["创建备用驱动实例"]
    H -- 否 --> I["new DeviceLoop(...)"]
    H1 --> I
    I --> J{"dataCollection == 禁用?"}
    J -- 是 --> J1["只登记点位+标记 Bad<br/>不启动循环"]
    J -- 否 --> K["写映射 _writeMap[TagId]=(loop,binding)"]
    K --> L["loop.Start(devCts.Token)<br/>每设备独立 CTS(链全局)"]
```

源码：[Engine.cs:938-1074](file:///d:/AI/AIProject/IGW/src/IGW.Core/Engine.cs)。

### 4.4 北向上报流程（四通道）

各北向组件统一订阅 DataHub，但触发方式不同：

```mermaid
flowchart TD
    UP["DeviceLoop.Publish(samples)"] --> HUB["DataHub.Update(tagId, value)"]
    HUB --> |"变化事件 TagChange"| FANOUT

    subgraph FANOUT["DataHub 订阅扇出"]
        SA["SubscribeAll: MQTT/历史"]
        SP["SubscribePoints: 报警/计算/OPC UA 推送"]
    end

    SA --> MQTT["MqttPublisher.OnChange<br/>立即 PublishAsync(topicPrefix/tagId)<br/>或 IntervalLoop 全量"]
    SA --> HIS["DataLogger<br/>变化写 SQLite(批量 flush)"]

    SP --> ALM2["AlarmEngine<br/>越限判定→报警"]
    SP --> CMP2["ComputedEngine<br/>表达式重算→回写 Hub"]
    SP --> OPC["OpcUaServerHost<br/>有会话→NodeManager.PushValue<br/>无会话跳过(避锁)"]

    HUB -. "REST 按需读缓存" .-> REST["IgwRestApi<br/>GET /api/v1/tags/{id}/value<br/>TryGet 缓存 + Touch 激活"]
    HUB -. "MES 轮询缓存" .-> MES["MesUploadEngine<br/>任务循环按周期轮询 Hub<br/>条件触发→HTTP POST 上报<br/>响应解析→写回 PLC"]
```

源码：MQTT [MqttPublisher.cs:38-90](file:///d:/AI/AIProject/IGW/src/IGW.Northbound.Mqtt/MqttPublisher.cs)；OPC UA [OpcUaServerHost.cs:58-65](file:///d:/AI/AIProject/IGW/src/IGW.Northbound.OpcUaServer/OpcUaServerHost.cs)；MES [MesUploadEngine.cs:141-189](file:///d:/AI/AIProject/IGW/src/IGW.Northbound.Mes/MesUploadEngine.cs)；REST [IgwRestApi.cs:57-215](file:///d:/AI/AIProject/IGW/src/IGW.Northbound.RestApi/IgwRestApi.cs)。

MES 任务循环细化：

```mermaid
flowchart TD
    A["构造 MesUploadEngine"] --> B["Precompile(payload)<br/>预编译 JSON 模板"]
    B --> C["RegisterScanDemands<br/>按任务/Flow/条件/写回计算需求点位<br/>向 ScanDemandRegistry 租约"]
    C --> D["为每个 enabled 任务起 TaskLoopAsync"]
    D --> E{"按采集周期轮询 Hub<br/>条件上升沿?"}
    E -- 否 --> E
    E -- 是 --> F["组装 JSON payload<br/>(字段/结构体/数据范围平铺)"]
    F --> G["HTTP POST 上报 MES<br/>Bearer Token + 单节点超时"]
    G --> H{"成功?"}
    H -- 是 --> H1["UploadsOk++<br/>写回(FromResponse→PLC)"]
    H -- 否 --> H2["UploadsErr++/LastError<br/>MesLog(SQLite) 留痕"]
```

### 4.5 写入流程（北向下发 → PLC）

```mermaid
flowchart TD
    A["REST: POST /api/v1/tags/{id}/value<br/>或 OPC UA Write / MES 写回"] --> B["IgwRestApi/Engine.WriteAsync(tagId, value)"]
    B --> C{"_writeMap 含 TagId?"}
    C -- 否 --> C1["直接返回 Bad(无设备/禁用采集)"]
    C -- 是 --> D["loop.EnqueueWrite(binding, value)<br/>进设备写队列"]
    D --> E["DeviceLoop 主循环 DrainWritesAsync"]
    E --> F["driver.WriteAsync(writes)"]
    F --> G{"写成功?"}
    G -- 是 --> G1["返回 Good"]
    G -- 否 --> G2["按通信故障 ResetTransport + 重连<br/>FailPendingWrites(Bad)"]
```

写优化：与读取共用单连接串行执行，无并发竞争；写请求可提前唤醒退避中的循环。

### 4.6 热重载流程（下发新配置）

`GatewayHost.ReloadAsync` 是配置生效的核心，体现"南向不变增量、北向总重建"策略：

```mermaid
flowchart TD
    A["REST PUT /project<br/>下发新 IgwProject"] --> B["MesTaskTopology.Reconcile"]
    B --> C["ProjectStore.Validate<br/>非法直接拒绝(老配置原样跑)"]
    C --> D["_gate 闸门 + Reloading=true<br/>(数据端点 503)"]
    D --> E{"License.Blocked?"}
    E -- 是 --> E1["仅保存工程不碰引擎<br/>激活后生效"]
    E -- 否 --> F["ProjectStore.Save 原子落盘"]
    F --> G["StopNorthboundAsync<br/>停 OPC/MQTT/MES/历史/计划/报警"]
    G --> H["southSame = ChannelsSig 相同?"]
    H -- "是(南向未变)" --> H1["不碰 Engine/Hub<br/>设备不断线缓存不丢"]
    H -- "否(南向有变)" --> H2["Engine.ApplyAsync<br/>设备级增量:按签名停/起变化设备<br/>分批 100 台避免重连风暴"]
    H1 --> I["WaitPortFreeAsync(opc.tcp)"]
    H2 --> I
    I --> J["StartNorthboundAsync<br/>重建全部北向"]
    J --> K["Reloading=false 释放闸门"]
    K --> L{"途中异常?"}
    L -- 是 --> L1["回退整体重建<br/>StopNorthbound + Engine.Stop + Hub.Dispose<br/>+ StartSouth + StartNorthbound"]
    L -- 否 --> L1OK["配置已应用并生效"]
```

源码：[GatewayHost.cs:284-337](file:///d:/AI/AIProject/IGW/src/IGW.Runtime/GatewayHost.cs)；增量应用 [Engine.cs:1077-1115](file:///d:/AI/AIProject/IGW/src/IGW.Core/Engine.cs)。

### 4.7 授权生命周期

```mermaid
flowchart TD
    A["LicenseManager.Evaluate<br/>读 license.key + 验签 + 验机器码 + 防回拨"] --> B{"Blocked?"}
    B -- "是(未授权/过期过宽限/机器不符/回拨)" --> B1["启动只起 REST<br/>采集/北向停止"]
    B -- "否" --> C["全量启动采集与北向"]
    B -- "临期/宽限" --> C2["全量启动 + 持续告警(去重)"]

    D["ActivateAsync(code)"] --> D1["验签+机器码<br/>通过才写 license.key"]
    D1 --> D2["ReconcileRunAsync 按落盘状态调和:<br/>恢复→起;失效→停"]
    D2 --> E["返回本次尝试状态(坏码不破坏现授)"]

    F["定时器每 10min 复查"] --> G{"跨过到期/宽限边界?"}
    G -- "Blocked 且在跑" --> G1["ReconcileRunAsync 停采集北向"]
    G -- "恢复且未跑" --> G2["ReconcileRunAsync 起采集北向"]
    G -- "仅临期" --> G3["去重告警(状态变或>24h)"]
```

源码：[GatewayHost.cs:339-415](file:///d:/AI/AIProject/IGW/src/IGW.Runtime/GatewayHost.cs)；[LicenseManager.cs](file:///d:/AI/AIProject/IGW/src/IGW.Licensing/LicenseManager.cs)。

---

## 五、数据流程（端到端）

南向采集 → DataHub 缓存 → 北向多通道发布的完整数据通路：

```mermaid
flowchart LR
    subgraph SOUTH["南向"]
        PLC["PLC/设备"]
        DRV["IDeviceDriver.ReadAsync"]
        AB2["AddressBlockOptimizer<br/>合并邻近点位为读块"]
        SAMPLE["TagSample[]"]
    end

    subgraph CENTER["数据中枢"]
        DH2["DataHub<br/>缓存最新值 + 订阅分发"]
        SD2["ScanDemandRegistry<br/>active-tag 租约"]
        WM["_writeMap<br/>TagId→设备循环"]
    end

    subgraph NORTH["北向"]
        MQTT2["MQTT 发布"]
        OPC2["OPC UA Server 推送"]
        REST2["REST 读缓存"]
        MES2["MES HTTP 上报"]
        HIS2["历史 SQLite"]
        ALM2b["报警/计算/计划"]
    end

    PLC -->|协议帧| DRV
    DRV --> AB2
    AB2 --> SAMPLE
    SAMPLE -->|Publish/Update| DH2
    SD2 -.需求.-> DRV
    DH2 --> MQTT2
    DH2 --> OPC2
    DH2 --> HIS2
    DH2 --> ALM2b
    DH2 -.按需读.-> REST2
    DH2 -.轮询.-> MES2
    MES2 -.写回.-> WM
    WM -.写请求.-> DRV
    REST2 -.写.-> WM
```

关键不变量：
- **单写者**：每个点位只被其所属设备循环写入，无锁。
- **零穿透**：北向读全走缓存，绝不直接读 PLC。
- **按需采集**：活动点（被 REST 读/OPC UA 监控/MES 租约引用）才纳入扫描计划。
- **共享 Hub**：南向与北向通过同一个 `DataHub` 解耦，热重载北向时不影响设备缓存。

---

## 六、模块交互与依赖

### 6.1 项目依赖方向

```mermaid
flowchart TD
    ABS["IGW.Abstractions"]
    CORE["IGW.Core"]
    DRV["IGW.Drivers.*"]
    NB["IGW.Northbound.*"]
    RT["IGW.Runtime"]

    DRV --> ABS
    NB --> ABS
    CORE --> ABS
    RT --> CORE
    RT --> NB
    RT --> DRV
```

> 运行时（IGW.Runtime）是依赖汇聚点，引用 Core + 全部北向 + 全部驱动；驱动与北向只依赖 Abstractions，互不耦合。

### 6.2 启动时序编排

```mermaid
sequenceDiagram
    participant P as Program
    participant H as GatewayHost
    participant L as LicenseManager
    participant E as RuntimeEngine
    participant DH as DataHub
    participant NB as Northbound组件

    P->>H: new GatewayHost(project, registry, lf, dataDir)
    P->>H: StartAsync()
    H->>L: Evaluate()
    alt Blocked
        H-->>P: 只保留 REST
    else Licensed
        H->>H: StartSouthAsync()
        H->>DH: new DataHub()
        H->>E: new RuntimeEngine(...) + StartAsync()
        E->>E: 遍历 Channels/Devices → StartDevice
        H->>H: StartNorthboundAsync()
        H->>NB: AlarmEngine/Computed/Scheduler/DataLogger
        H->>NB: OpcUaServerHost.Start(失败→60s重试)
        H->>NB: MqttPublisher(SubscribeAll)
        H->>NB: MesUploadEngine(RegisterScanDemands+TaskLoop)
    end
    P->>P: MapIgwApi + 内嵌 Web UI + app.Run
```

---

## 七、控制流程关键逻辑小结

| 控制点 | 机制 | 位置 |
|--------|------|------|
| 授权闸门 | Evaluate → Blocked 只起 REST；10min 复查调和起停 | GatewayHost.cs |
| 热重载闸门 | `_gate` 信号量 + `Reloading` 标志（数据端点 503） | GatewayHost.cs |
| 南向增量签名 | `ChannelsSig` 哈希比对，南向不变跳过设备重建 | GatewayHost.cs / Engine.cs |
| 设备级 CTS | 每设备链到全局，可单独停 | Engine.cs |
| 熔断器 | Closed→Open(阻塞)→HalfOpen(单探)→Closed | Engine.cs DeviceLoop |
| 退避抖动 | 1~5s ×(0.7~1.3) 抖动，读成功才复位 | Engine.cs |
| 自动降级 | 连续故障达阈值进入降级期，停采集 | Engine.cs |
| 主备冗余 | 备用路径定期探测主，恢复切回 | Engine.cs |
| 通道自动重连 | AutomaticChannelReconnectLoopAsync | Engine.cs |
| active-tag | Touch/Pin/IsActive 决定是否扫描 | DataHub |
| OPC UA 重试 | 启动失败 60s 后台重试直到成功 | GatewayHost.cs |
| 北向停止超时 | 8s 兜底，避免卡住配置保存 | GatewayHost.cs |
| 证书临期巡检 | 24h 巡检，提前 30 天告警 | GatewayHost.cs |
| 连接重试风暴抑制 | 增量起设备分批 100 台 + 间隔 | Engine.cs |

---

## 八、结论

IGW 采用经典的**「南向驱动 → 内存缓存中枢 → 北向多协议发布」三层架构**，以 `DataHub` 为唯一数据交汇点、以 `GatewayHost` 为运行时编排核心。其工程模型（Project→Channel→Device→Tag）与功能集（OPC UA/IoT/Advanced Tags/Data Logger/Scheduler/Security/Alarm）对标 KEPServerEX，但在热重载（南向增量签名）、百万点位（按点订阅+active-tag）、现场可用性（熔断/降级/主备/退避抖动）三方面做了面向工业长跑场景的工程化加固。整体控制流程围绕"采集循环"与"热重载闸门"两条主线展开，授权作为顶层开关贯穿启动、激活、复查、调和四个环节。
