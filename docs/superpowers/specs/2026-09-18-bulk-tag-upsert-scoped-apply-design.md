# 点位批量注册接口 + 按设备范围应用（百万点注册）设计

日期：2026-09-18
分支：`feat/bulk-tag-upsert`（基于 1.6.72 / 3a7a7dc）
调用方：HSJM_AI_PLC「PLC 程序上传后自动注册 IGW 拓扑」（子项目 B，另立 spec）

## 1. 背景与问题

HSJM 需要把 PLC 程序里的 DB 变量、IO 标签、GRAPH 工步变量自动注册到 IGW，规模是单工程几十万到上百万点，要求注册快、注册过程中 IGW 对其它设备的采集和读取不中断。

现有两条写入路径都达不到：

| 路径 | 瓶颈（源码位置） | 百万点后果 |
|---|---|---|
| `POST /Tag/AddTags` | 每次调用：整工程克隆 ×3（DcsTagProjectUpdater.cs:106、ProjectConfigurationCoordinator.cs:65、ProjectSnapshotWriter.cs:25）、校验 ×2、整库读回 ×2（ProjectSnapshotWriter.cs:40、Coordinator:150）、全点签名 ×2（ProjectSnapshotWriter.Delta.cs）；设备内逐项线性匹配 O(批量×设备点数)（DcsTagProjectUpdater.cs:217-221）；南向有变化即 `NorthboundComponents.All`（ProjectRuntimeChanges.cs:19-20），OPC UA/MES/历史/报警全部重启，`Reloading=true` 期间 DCS/REST 读写 503；**无法指定通道/设备**，同 IP 的 S7 Classic 与 S7 Plus 点被塞进同一设备 → `SiemensHybridDeviceDriver` 构造抛异常、整台设备不启动（SiemensHybridDeviceDriver.cs:535-556，Engine.cs:3409-3413），`ProjectStore.Check` 不拦 | 5000/批需 200 次调用，约 2–4 小时；每次数十秒到数分钟 503；峰值内存 8–12GB |
| `POST /api/v1/project/mutations` | 行级 SQL 真增量，但一次只能写一个实体，每次都 `LoadRuntimeProjectAsync` 整库读回 + 整工程运行态应用（Coordinator:101-128） | 每点 30–80 秒，不可行 |
| 工程导入 `import/commit` | 整工程替换（ProjectJsonImporter.cs:85-88） | 会覆盖其它内容；100 万点 62s、峰值 9.6GB |

IGW 自己的百万点设计（`2026-07-16-million-point-arbitrary-cycle-collection-design.md`）要求「配置修改只重编译受影响的设备」「点位批量导入在服务端解析并以批次写入」，现有代码尚未落地。本设计就是把这两条落地到一个面向外部系统的批量接口上。

## 2. 目标与非目标

**目标**

1. 新增批量注册接口：一次请求把一批点写进**指定通道 / 指定设备 / 指定嵌套标记组**，通道和设备不存在时按调用方给的定义创建。
2. 写库是行级批量 UPSERT，一次请求只提升一次工程修订号；成本 O(本次涉及设备的点数 + 本批点数)，与工程总点数无关。
3. 运行态**只应用受影响的设备**：其它设备不停、不断连；北向组件不整体重启；不进入 `Reloading` 窗口；OPC UA 地址空间只替换受影响设备的子树。
4. 支持「替换标记组」语义，让调用方能清掉 PLC 里已删除的变量（先删指定根组子树，再写入新点）。
5. 同一设备内 S7 Classic 绝对地址与 S7 Plus 符号地址混用时拒绝写入，并给出明确错误。
6. 性能目标（NVMe、Release 构建）：服务端单请求 10 万点 ≤ 5 秒（不含设备重连）；已有 100 万点工程上追加 10 万点，耗时与在空工程上相当（±30%）；100 万点分 10 次请求端到端 ≤ 90 秒；注册全程其它设备的 DCS/REST 读取无 503。

**非目标**

- 不改 `/Tag/AddTags` 的现有语义（其它调用方继续可用；它的 O(B×M) 匹配问题另行处理）。
- 不做设备内「热追加点位不重连」：受影响设备允许重建一次（沿用 `reuseHubEntries` 保住已有点的 Hub 句柄）。
- 不做 S7 Plus 原生层符号解析缓存（子项目 C，另立 spec）。
- 不改 Web UI。

## 3. 接口

`POST /api/v1/project/bulk/tags`

- 与 `/api/v1/project/*` 其它写接口相同的鉴权策略。
- `If-Match` 可选：带上则按修订号做乐观并发（不一致返回 409 `revision_conflict`）；不带则在协调器锁内直接执行（与 AddTags 一致，方便自动化调用方串行写入）。
- 请求体上限按 `ProjectExchangeApi.LiftRequestBodyLimit` 同样的方式抬高到 512MB；服务端流式反序列化（`JsonSerializer.DeserializeAsyncEnumerable` 读 `items`），不整体缓冲。建议调用方每请求 ≤ 10 万项。

请求体：

```json
{
  "channel": {
    "name": "泰兴PLC1",
    "driverId": "Siemens.S7",
    "settings": { "wordOrder": "ABCD", "timeoutMs": "2000", "...": "..." }
  },
  "device": {
    "name": "S7Plus",
    "endpoint": "10.11.38.20:102",
    "settings": { "model": "S7-1500", "rack": "0", "slot": "1", "dcsLogicalDevice": "S1500_10.11.38.20", "...": "..." }
  },
  "replaceGroups": [ ["DB490_OP100Alarm"], ["DB161_HomeGraph"] ],
  "items": [
    {
      "groupPath": ["DB490_OP100Alarm", "Type", "Errors"],
      "name": "Error1",
      "address": "DB490_OP100Alarm.Type.Errors.Error1",
      "dataType": "bool",
      "access": "readWrite",
      "scanRateMs": 100,
      "description": "",
      "array": null,
      "metadata": { "source": "hsjm" }
    }
  ]
}
```

规则：

- **通道**：按名称（不区分大小写）找。找不到则按请求里的 `driverId/settings` 创建；找到时 `driverId` 必须一致，否则 400 `channel_driver_mismatch`；已有通道的 `settings` **不覆盖**（手工在 UI 调过的参数保留）。
- **设备**：在该通道下按名称找。找不到则按请求创建；找到时只做两件事：`endpoint` 与请求不同则更新；请求 `settings` 里有而设备上**没有**的键补上（如 `dcsLogicalDevice`）。已有键不覆盖。
- **标记组路径**用字符串数组 `groupPath` 表达，每段一个组，逐级查找或创建。空数组 = 设备根层。
- **名称规则**（与 IGW 界面一致，`Project.vue:1820`）：组名与点名非空、不含 `.`、首尾无空白。`canonical_path` 与运行态点位 id 都以 `.` 拼接，名字含 `.` 会产生歧义，违反则整请求 400 `invalid_items`。
- **点**：按 `(所属组, name)` 唯一定位；已存在则更新地址/类型/访问/周期等字段，不存在则新建。`name` 为空、组名或点名重复（同一请求内）时整请求 400 `invalid_items`，并返回前 50 条出错项的下标和原因。
- **replaceGroups**：每项是一个根组路径（相对设备根）。事务里先删除这些组的整棵子树（级联删点），再写入 `items`。`items` 为空时相当于「删除这些组」。
- **dataType / access** 用 IGW 原生枚举（`IgwDataType` / `TagAccess`，camelCase 字符串），不走 DCS 数字枚举。数组点给 `array`（`ArrayDef`，与工程 JSON 同构）。
- **协议混用校验**：Siemens.S7 通道的设备，写入后设备内全部点（含未变动的旧点）的地址必须同属 Classic（`SiemensAddressDescriptor` 判定为 DB 绝对地址 / I/Q/M/V 区域地址）或同属 Symbolic，否则整请求 400 `mixed_siemens_address_mode`，错误信息给出两类各一个示例地址。
- 同一请求只针对一个设备。调用方要写多个设备就发多个请求（天然把运行态影响范围限定为一台设备）。

响应（200）：

```json
{
  "applied": true,
  "revision": "project-1043",
  "channelId": "…", "deviceId": "…",
  "created": 98211, "updated": 1789, "deleted": 5203,
  "deviceRestarted": true,
  "timing": "解析 820ms；写库 2310ms；加载设备 190ms；应用运行态 640ms"
}
```

失败：400（参数/校验）、409（修订号冲突）、500 `runtime_apply_failed`（数据库已提交，运行态应用失败——与现有 mutation 语义一致，进入降级态，可 `RetryRuntimeApplyAsync`）。

## 4. 架构与数据流

```
BulkTagApi (Northbound.RestApi)
  └─ 流式解析 + 请求级校验
     └─ GatewayHost.ApplyBulkTagsAsync(request, expected?)
        └─ ProjectConfigurationCoordinator.ExecuteBulkAsync      ← 同一把 _gate
           ├─ IProjectRepository.BulkUpsertTagsAsync             ← 单事务，行级批量 SQL，修订号 +1
           │    返回 (revision, channelId, deviceId, 统计)
           ├─ IProjectRepository.LoadDeviceAsync(deviceId)        ← 只读这一台设备的组与点
           ├─ 构造新工程：对 HealthyProject 写时复制，只替换/插入这台 DeviceDef
           └─ applyRuntime(newProject, ProjectApplyScope.Devices([deviceFullName]))
              └─ GatewayHost.ApplyScopedRuntimeAsync
                 ├─ Engine.ApplyDevicesAsync(changed)             ← 只停/起这台设备，复用已有点 Hub 句柄
                 ├─ OpcUa.ReplaceDeviceSubtree(channel, device)   ← 只替换该设备文件夹
                 ├─ 实时流：句柄复用，默认不动；仅删除的点正被订阅时整体重同步（§5.3）
                 └─ 北向其它组件：见 §5.4 判定
```

### 4.1 持久化：`BulkUpsertTagsAsync`

放在 `IGW.Persistence.Sqlite`，新文件 `ProjectBulkTagWriter.cs`，不改 `ProjectSnapshotWriter`（1.6.71/1.6.72 正在迭代增量保存，避免冲突）。

- 一个 `BEGIN IMMEDIATE` 事务：修订号检查 → 通道/设备 upsert → `replaceGroups` 删除 → 组路径建立 → 点 UPSERT → 修订号 +1 → 提交。
- 只查目标设备的 `tag_groups` 与 `tags`，建 `(parentGroupId, name) → groupId`、`(groupId, name) → tagRow` 字典，成本 O(M+B)。
- 复用预编译 `SqliteCommand`（参数对象复用，每行只改值），tags 与 tag_options 两表各一条 UPSERT；`sort_order` 在内存里按组递增分配，不逐行查 `MAX`。
- 事务内 `PRAGMA defer_foreign_keys=ON`，删除子树按「点 → 组（自底向上）」顺序执行，不依赖级联时序（吸取 974faee 的教训）。
- 百万行单事务在 WAL 下可接受；调用方已被约束为按设备、按 ≤10 万项分请求，单事务行数有上界。

### 4.2 只加载一台设备：`LoadDeviceAsync`

`ProjectSnapshotReader` 增加按 `device_id` 过滤的读取：设备行 + 该设备的组 + 该设备的点，组装成 `DeviceDef`（与全量读取共用映射代码，保证结果与 `LoadRuntimeProjectAsync` 中该设备部分逐字段一致——用往返测试锁住）。

### 4.3 协调器：`ExecuteBulkAsync`

- 与 `ExecuteAsync` 共用 `_gate`，保证与 UI 保存、mutation、AddTags 串行。
- 写时复制：`HealthyProject` 浅拷贝工程对象与 `Channels` 列表；目标通道浅拷贝并替换 `Devices` 列表中的目标设备（或追加新设备 / 新通道）。其余 `ChannelDef/DeviceDef` 对象引用复用——不克隆、不序列化全工程。
- 运行态应用失败：沿用 `SetDegraded` 语义，`_pendingProject` = 新工程，`_pendingScope` = Devices 范围，`RetryRuntimeApplyAsync` 按范围重试。
- 按设备应用走协调器新增的构造参数 `applyDevicesRuntime(project, deviceFullNames, ct)`（与现有 `applyRuntime` 并列）；`ProjectApplyScope` 增加 `Devices` 成员，仅用于降级态记录，现有调用点语义不变。

## 5. 运行态：按设备应用

### 5.1 引擎 `ApplyDevicesAsync`

- 在 `_deviceLifecycleGate` 内执行，只针对给定设备全名：
  - 计算该设备新签名（`DeviceSig`），与运行中的签名相同则跳过；
  - 否则 `StopDeviceAsync(..., unregisterTags: false, drainTimeout: ApplyStopGrace)`，再 `StartDevice(..., reuseHubEntries: true)`（与 `HardReconnectChannelCoreAsync` 相同的句柄保留方式）；
  - 旧有新无的点：从 Hub 注销；新有旧无的点：注册。
- `UpdateProjectMetadata` 改为可按设备更新，避免 O(N) 全量刷新。
- 不触碰其它设备、不计算其它设备签名。

### 5.2 OPC UA

`IgwNodeManager` 增加 `ReplaceDeviceSubtree(ChannelDef ch, DeviceDef dev)`：在 `Lock` 内删除该设备文件夹下全部节点（同时从 `_vars/_defs` 移除），按现有 `CreateFolder/BuildGroup/CreateTag` 重建并 `AddPredefinedNode`；通道是新建时同时建通道文件夹。已订阅被删节点的监控项收到 `BadNodeIdUnknown`，其余会话与订阅不受影响。OPC UA 未启用时跳过。

### 5.3 实时流

`RealtimeProjectRevision` 是全工程 `JsonHash`（O(N)），变化即让全部流退役。按设备应用时不走它：

- 引擎按设备重建时复用已有点的 Hub 句柄（`reuseHubEntries`），已订阅这些点的流无需任何动作即可继续收到新值；新增点尚未被任何流订阅。
- 仅当**被删除的点**正被某个流订阅时（`RealtimeStreamCoordinator` 新增 `HasRoutesForTags(IEnumerable<string> tagIds)` 判定），才执行一次现有的整体退役 `RetireGenerationAsync`，新 revision 仍按 `RealtimeProjectRevision.Compute` 计算。HSJM 的替换组场景下这种情况只在「删除正被监控的变量」时出现，频率低。
- 未退役时 `RealtimeRevision` 保持不变。

### 5.4 其它北向组件判定

| 组件 | 按设备应用时的处理 |
|---|---|
| 报警 | 受影响设备新旧点里有任一点配置了 `alarm`，才重建报警引擎；否则不动 |
| 历史 | 同上，按点的历史配置 / 历史订阅判断 |
| 计算点、计划任务、MES | 若其引用的点位 id 落在受影响设备范围内，回退为现有 Full 应用（保证正确性优先）；否则不动 |
| MQTT | 发布规则按点/前缀匹配到受影响设备时重建 MQTT；否则不动 |
| DCS 别名解析 | 解析器缓存按工程对象引用失效，O(DCS 设备点数) 重建；改为后台构建后原子替换，重建期间继续用旧解析器服务读请求 |

HSJM 注册的点不带报警/历史/计算引用，正常情况下全部走「不动」分支。

## 6. 错误处理

- 请求级校验失败（格式、重复名、协议混用、通道驱动不符）：400，数据库不动。
- 事务内任何 SQL 失败：回滚，400 `persistence_failed`，数据库不动。
- 数据库已提交、运行态失败：500 `runtime_apply_failed`，进入降级态（与现有 mutation 一致），日志与 `/health` 可见，`RetryRuntimeApplyAsync` 按范围重试；不会回退已提交的数据。
- 修订号冲突：409。
- 响应与日志附带分段耗时（沿用 `工程保存耗时` 日志关键字）。

## 7. 测试与验收

**单元 / 集成测试（tests/IGW.Tests）**

- `ProjectBulkTagWriterTests`：新建通道+设备+嵌套组+点；已有通道/设备 settings 不被覆盖、缺键补齐；点按 (组,名) 更新；`replaceGroups` 删除子树后写入；同请求重名拒绝；协议混用拒绝（含「新点 Symbolic + 库里旧点 Classic」）；修订号只 +1；失败回滚。
- `LoadDeviceAsync` 往返：与 `LoadRuntimeProjectAsync` 取出的同一设备逐字段相等。
- `ProjectConfigurationCoordinatorTests`：Bulk 后 `HealthyProject` 只替换目标设备（其它设备对象引用不变）；运行态失败进入降级、按范围重试成功。
- 引擎：两台设备运行中，对设备 A 执行按设备应用，设备 B 的 `DeviceRuntime` 实例不变、连接不断；A 旧点句柄复用、删除点注销。
- OPC UA：`ReplaceDeviceSubtree` 后新点可浏览、删除点消失、其它设备节点与订阅不受影响。
- API：请求体流式解析、If-Match 冲突、400/409/500 响应格式。
- 回归：现有 1796 项后端测试全绿；`/Tag/AddTags`、mutations、导入行为不变。

**性能基准（benchmarks/IGW.ProjectStorage.Performance 新增场景）**

- 空工程写入 10 万点（单请求）；
- 已有 100 万点（100 台设备 × 1 万）的工程上，对一台新设备写入 10 万点；
- 100 万点分 10 次请求写入 10 台设备，记录总耗时与峰值工作集；
- 写入期间另一线程持续 `ReadTagsAsList` 读其它设备，统计 503/失败次数（目标 0）。

**验收标准**：§2 目标 6 的数值全部达成，且回归全绿。

## 8. 发布与兼容

- 新接口纯增量，不改现有接口契约；老版本 HSJM 不受影响。
- 版本号随下一个 IGW 版本发布，release notes 写明新接口与性能数据。
- HSJM 侧（子项目 B）通过探测 `POST /api/v1/project/bulk/tags` 是否存在（405/404）判断 IGW 版本，不存在时明确报错「IGW 版本过低」，不回退到 AddTags。
