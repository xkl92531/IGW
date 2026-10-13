# SQLite 工程主存储与 KEP 兼容 JSON 交换设计

状态：待实施计划评审  
日期：2026-09-01  
适用范围：IGW 工程配置、运行时配置装载、JSON 导入导出、KEP 工程兼容

## 1. 背景与目标

当前 IGW 以完整 JSON 工程对象作为持久化和前端编辑单位。编辑一个属性时，前端和后端仍可能加载、克隆、序列化并保存整个工程；在单工程达到几十万到一百万点时，这种方式会放大内存、GC、传输、锁等待和保存延迟，也使 `Project.vue`、`ProjectStore` 等模块承担过多职责。

本次改造采用以下不可变目标：

1. SQLite 是工程配置唯一的主存储和事实来源，不再自动双写 JSON。
2. 工程配置支持百万点规模下的按需读取、分页编辑、局部保存和批量导入。
3. 用户界面只保留“导入 JSON”和“导出 JSON”，不提供 `.igwproj` 入口，也不增加独立“导出 KEP”入口。
4. 导出的单个 JSON 文件同时包含：
   - KEP 可识别的原生 `project` 工程结构；
   - IGW 用于无损迁移的 `igw` 扩展结构。
5. 将同一 JSON 导回 IGW 后，连接、层级、点位、采集任务、MES 流程、映射、回写、数组、模板及其他工程设置必须无损恢复。
6. 对有等价 KEP 驱动的配置，导入 KEP 后必须得到相同协议、通道、设备、标记组和标记；不能等价表达的配置必须在导出报告中明确指出，不能静默伪装为兼容。
7. 通过分层模块和稳定接口逐步拆解现有大文件，避免以一次性重写方式破坏正在使用的 PLC 读写、SSE 和 MES 行为。

## 2. 明确不在本次范围内的内容

- 不改变 `/Tag`、`/iotgateway` 等现有内网业务接口的匿名 HTTP 使用方式，不强制鉴权或 HTTPS。
- 不改变 PLC 扫描、订阅、写入、写后校验、重试和故障隔离的既有业务语义。
- 不把运行日志、历史数据、实时缓存、安装包、Git 历史或机器许可证写入工程 JSON。
- 不将 `.igwproj` 作为新格式继续维护；它只允许作为旧安装首次迁移时的内部兼容输入。
- 不把所有低基数字段过度拆成关系表，也不在第一阶段同时重写 Engine、MES 引擎和全部驱动。
- 不以模拟器测试代替真实 KEP 或真实 PLC 验收。

## 3. 总体架构

工程配置分为四层：

```text
Web/WPF 配置界面
        |
        v
工程应用服务（查询、局部命令、版本冲突、导入导出）
        |
        +--------------------------+
        |                          |
        v                          v
IProjectRepository           IProjectExchangeService
        |                          |
        v                          +--> KEP 编解码器
SQLiteProjectRepository            +--> IGW 扩展编解码器
        |
        v
SQLite 工程数据库  --->  版本化运行时快照  --->  PLC/MES/SSE 运行引擎
```

建议新增两个职责清晰的项目：

- `IGW.Persistence.Sqlite`：SQLite 模式、迁移、仓储、事务、分页、批量写入和备份切换。
- `IGW.ProjectExchange`：KEP JSON 编解码、IGW 扩展、流式导入导出、兼容性报告和迁移密码封装。

`IGW.Abstractions` 只放稳定合同和 DTO；`IGW.Core` 负责用例协调，不引用具体 SQLite 类型；驱动项目提供自己的 KEP 属性编解码器，不让一个中央文件包含所有协议分支。

核心接口建议为：

```csharp
public interface IProjectRepository
{
    Task<ProjectSummary> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TreeNodeSummary>> GetChildrenAsync(
        ProjectNodeRef parent, CancellationToken cancellationToken);
    Task<Page<TagRecord>> GetTagsAsync(
        TagQuery query, CancellationToken cancellationToken);
    Task<ProjectEntity> GetEntityAsync(
        ProjectEntityRef entity, CancellationToken cancellationToken);
    Task<ProjectRevision> ExecuteAsync(
        ProjectCommand command, long expectedRevision, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeConfigBatch> StreamRuntimeConfigAsync(
        long revision, CancellationToken cancellationToken);
}

public interface IProjectExchangeService
{
    Task<ProjectImportPreview> InspectAsync(Stream json, CancellationToken cancellationToken);
    Task<ProjectImportResult> ImportAsync(
        Stream json, ProjectImportOptions options, CancellationToken cancellationToken);
    Task ExportAsync(
        Stream output, ProjectExportOptions options, CancellationToken cancellationToken);
}
```

应用层命令必须表达具体变更，如修改一个点、批量移动点、替换一个 MES 流程或切换一个采集任务，禁止继续把“完整 Project 对象”当作常规保存命令。

## 4. SQLite 数据模型

### 4.1 高基数连接层级

百万点的连接层级使用关系表，避免查找一个节点时反序列化整个工程：

| 表 | 主要字段 | 说明 |
|---|---|---|
| `project_meta` | `project_id`, `name`, `schema_version`, `revision`, `runtime_generation`, `created_at`, `updated_at` | 单工程元数据和全局修订号 |
| `channels` | `id`, `name`, `description`, `driver_id`, `enabled`, `settings_json`, `kep_properties_json`, `sort_order`, `row_version` | 通道及协议属性 |
| `devices` | `id`, `channel_id`, `name`, `description`, `enabled`, `settings_json`, `kep_properties_json`, `sort_order`, `row_version` | 设备及驱动属性 |
| `tag_groups` | `id`, `device_id`, `parent_group_id`, `name`, `description`, `canonical_path`, `flow_enabled`, `sort_order`, `row_version` | 任意深度标记组 |
| `tags` | `id`, `device_id`, `group_id`, `name`, `description`, `canonical_path`, `address`, `data_type`, `access`, `scan_rate_ms`, `string_length`, `text_encoding`, `eng_unit`, `sort_order`, `row_version` | 普通点与数组根点 |
| `tag_options` | `tag_id`, `is_array`, `array_json`, `scaling_json`, `deadband_json`, `alarm_json`, `write_confirmation_json`, `metadata_json`, `igw_properties_json`, `kep_properties_json` | 所有点位的完整扩展，不按“有特殊字段”才创建 |

关键约束和索引：

- `channels(name)`、`devices(channel_id, name)`、`tag_groups(device_id, parent_group_id, name)` 和同组 `tags` 名称建立唯一约束。
- `tag_groups(device_id, parent_group_id, sort_order)`、`tags(device_id, group_id, sort_order)`、`tags(canonical_path)`、`tags(address)` 建立查询索引。
- 所有实体使用稳定 GUID/ULID，名称或路径变化不改变 ID。
- `canonical_path` 是显示、检索和旧数据匹配辅助字段，不是主键。
- 数组和嵌套数组保留结构描述，只保存一个逻辑数组根；运行时展开由统一数组服务按需完成，禁止为每种格式建立重复数组树。

### 4.2 低基数和复杂配置

不需要高频跨实体查询的复杂配置以版本化 JSON 文档保存，避免为每个流程节点类型建立易碎表结构：

| 表 | 主要字段 | 说明 |
|---|---|---|
| `project_sections` | `section_key`, `schema_version`, `document_json`, `row_version` | 工程级安全、历史、SSE、北向服务等设置 |
| `mes_tasks` | `id`, `group_id`, `enabled`, `name`, `template_id`, `flow_document_json`, `row_version` | 一组一个 MES 任务及完整流程图 |
| `mes_task_index` | `task_id`, `node_type`, `referenced_tag_id`, `referenced_path` | 用于查找跨 DB 点、删除影响分析和校验 |
| `flow_templates` | `id`, `name`, `template_document_json`, `row_version` | 流程模板 |
| `device_templates` | `id`, `driver_id`, `name`, `template_document_json`, `row_version` | 设备模板 |
| `secret_entries` | `owner_type`, `owner_id`, `secret_name`, `ciphertext`, `nonce`, `tag`, `key_version` | 本机静态加密后的可逆凭据 |

MES 流程文档必须保留节点 ID、位置、端口、连线、条件、上报映射、响应映射、固定值/复制值、`isSuccess` 拦截策略和所有版本化设置。`mes_task_index` 由保存服务原子重建，不能由前端直接维护。

### 4.3 SQLite 运行参数

- 启用 `foreign_keys=ON`、WAL、合理的 `busy_timeout` 和显式事务。
- 小型局部保存使用短事务；百万点导入使用预编译语句和批量事务，不逐行提交。
- 数据库模式由顺序迁移管理；启动时只能向前迁移，失败时保留原数据库和失败诊断。
- 数据库、WAL 和 SHM 的一致备份使用 SQLite backup API 或完成 checkpoint 后的受控切换，不能只复制主文件。
- 每次成功命令增加工程 `revision`；会影响运行时的命令同时增加 `runtime_generation`。

## 5. 读取、编辑和运行时装载

### 5.1 配置端 API

新增面向大工程的服务端接口：

- 工程摘要、通道列表、设备列表；
- 树节点按父节点延迟加载；
- 点位按组分页、排序和搜索；
- 单实体属性读取和局部更新；
- 批量复制、剪切、粘贴、启停、删除和导入；
- MES 任务列表按当前设备/当前通道分页筛选；
- JSON 导入预检、提交和导出流。

所有写请求携带 `expectedRevision` 或实体 `rowVersion`。版本不一致返回 409 和当前版本，不能以后到请求覆盖先到请求。批量命令在一个事务中计算完整选中集合，解决界面选中多个但只处理一个的问题。

旧的完整工程 GET/PUT 在过渡期只作为兼容入口保留，标记为 deprecated，并设置明确的体积限制；新 Web UI 和 WPF 配置器不得继续调用它们。完成调用方迁移后再删除，避免百万点工程通过旧接口进入浏览器。

### 5.2 前端状态

- `Project.vue` 只负责页面布局和路由协调，拆分为树、列表、属性编辑、导入预检和批量操作组件。
- `useProjectTree` 管理懒加载树；`useTagPage` 管理分页点位；`useProjectCommands` 管理局部命令和冲突；`useSelection` 维护稳定 ID 集合。
- 页面只让当前树分支、当前页和当前属性表单进入 Vue 响应式系统，禁止把百万点完整工程做成深层 reactive 对象。
- 导出由浏览器直接下载后端响应流；导入先上传临时文件，前端只显示摘要、警告和进度，不解析完整 JSON。
- 行选择使用稳定 ID，点击复制、剪切、粘贴时先冻结选择快照，避免异步刷新改变选中项。

### 5.3 运行时快照

SQLite 是配置事实来源，不代表 PLC 每次扫描都查询 SQLite。工程启用或修订发布时，运行时按通道/设备批量流式装载不可变配置快照并建立所需索引；采集、写入和 SSE 使用该快照。

局部保存流程为：

1. 在 SQLite 事务中校验并保存变更；
2. 提交新 revision/generation；
3. 构造受影响通道、设备、点位或 MES 任务的增量；
4. 通过统一协调器原子发布；
5. 只重建受影响运行组件；
6. 若运行组件重建失败，数据库仍保留已保存配置，但健康状态明确显示“配置已保存、运行时未应用”，并允许重试应用。

工程点表整体替换时，生成新 generation，删除旧点的运行时缓存和 SSE 保留状态，并向客户端发送 generation reset/tombstone，不能让旧点继续显示。

## 6. 单 JSON 文件合同

### 6.1 根结构

导出文件使用：

```json
{
  "project": {
    "common.ALLTYPES_NAME": "ProjectName",
    "channels": []
  },
  "igw": {
    "format": "IGW.JSON.Configuration",
    "schemaVersion": 4,
    "exportedAt": "2026-09-01T00:00:00Z",
    "project": {},
    "connectivity": {},
    "mes": {},
    "secrets": {},
    "compatibility": {}
  }
}
```

硬性规则：

- `project` 必须按目标 KEP Config API 的原生命名、层级、数据类型和驱动属性生成，不能放 IGW 自定义字段。
- `igw` 是附加扩展，不替代 `project`，也不能要求 KEP 理解 IGW 流程。
- 导入没有 `igw` 的原生 KEP JSON 时，IGW 根据 `project` 创建完整连接层级并为 IGW 专有字段使用明确默认值。
- 导入包含 `igw` 的 IGW JSON 时，以 `project` 重建 KEP 可表达层，以 `igw` 恢复 IGW 专有层；两部分冲突时中止导入并给出路径级错误，不能静默选择一边。
- 普通点也必须写入必要的 IGW 扩展。稳定 ID、缩放、死区、报警、工程单位、写确认、字符串长度/编码、数组描述和元数据不能因字段看似“普通”而遗漏。

### 6.2 KEP 原生层

`project` 至少表达：

- 工程名和 KEP 工程级可表示设置；
- 通道名称、说明、KEP 驱动标识和完整驱动属性；
- 设备名称、说明、设备 ID/远程地址及完整设备属性；
- 任意层级标记组；
- 标记名称、说明、地址、数据类型、读写权限、扫描周期及 KEP 可表示的缩放/死区属性；
- 从 KEP 原始 JSON 导入但 IGW 暂不理解的合法属性原样保留，以保证再次导出不丢失。

驱动映射采用插件式合同：

```csharp
public interface IKepDriverCodec
{
    string IgwDriverId { get; }
    IReadOnlyCollection<string> KepDriverNames { get; }
    KepCompatibilityResult Inspect(ChannelRecord channel);
    void WriteChannel(Utf8JsonWriter writer, ChannelRecord channel);
    void WriteDevice(Utf8JsonWriter writer, DeviceRecord device);
    DriverImportResult ReadChannel(ref Utf8JsonReader reader);
    DeviceImportResult ReadDevice(ref Utf8JsonReader reader);
}
```

Simulator、Siemens S7、OPC UA Client、Modbus TCP、Omron、EtherNet/IP、Mitsubishi 和 MQTT 等协议分别实现编解码器。每个编解码器使用对应 KEP 版本的真实导出样例作为 golden fixture。

若 IGW 协议、驱动模式或属性在目标 KEP 版本没有等价表示：

1. `igw` 中仍完整保存原配置；
2. `compatibility` 中记录实体路径、属性、原因和严重级别；
3. 若缺失会导致连接到错误 PLC、使用错误协议或错误地址，默认阻止“KEP 兼容导出成功”结论；
4. 绝不能用相似驱动或默认值伪装为等价配置。

### 6.3 IGW 扩展层

`igw` 保存 KEP 不能表达但 IGW 迁移必须保留的全部内容：

- 工程级服务、SSE、历史、北向接口、用户和运行策略；
- 通道/设备/组/点稳定 ID、IGW 驱动 ID、启停状态和 IGW 专属属性；
- 数组和嵌套数组的逻辑结构、维度、元素类型及识别元数据；
- 字符串声明长度、编码、远端/本地长度诊断配置；
- 缩放、死区、报警、工程单位和元数据；
- 写入权限、写后读取验证、确认点、重试次数和超时策略；
- 采集流程开关、采集上报开关和运行开关的独立状态；
- MES 任务、完整流程图、模板、条件、HTTP 映射、响应字段、固定/复制回写和 `isSuccess` 拦截选项；
- 设备模板、流程模板和其他迁移后继续开发所需的工程设置。

扩展引用同时携带稳定 ID 和规范路径。还原匹配优先级为：稳定 ID、规范路径、结构指纹；任何一层出现一对多歧义都中止并列出候选，不能只按名称自动匹配。

### 6.4 凭据与可迁移性

JSON 不得明文包含 PLC、MQTT、MES 或其他可逆凭据。导出工程存在可逆凭据时，界面要求输入迁移密码，并将凭据写入 `igw.secrets` 的版本化 AES-GCM 信封；密钥由 PBKDF2-SHA256、随机 salt 和足够迭代次数从迁移密码派生。每条凭据使用随机 nonce 并进行完整性认证。

- API 用户密码以已有强哈希形式迁移，不导出明文。
- 导入时迁移密码错误必须在写入数据库前失败。
- 本机主密钥、机器绑定许可证和不可迁移私钥不导出。
- OPC UA 客户端证书信任和私钥属于机器信任边界，默认在新机器重新生成/信任；JSON 中只保存端点、安全策略和证书引用设置。
- 没有可逆凭据的工程允许不输入迁移密码。

## 7. 流式导入、验证与原子切换

### 7.1 导入

导入不得把百万点 JSON 整体读取为字符串或 `JsonDocument`。服务端将上传保存到受控临时文件，使用 `Utf8JsonReader` 流式解析，并写入独立 staging SQLite 数据库。

导入步骤：

1. 校验根结构、格式版本、JSON 深度、大小和数量限制；
2. 解析 `project`，建立 KEP 层级、原始属性袋和规范路径；
3. 解析 `igw`，解密凭据并恢复扩展；
4. 执行 ID/路径/指纹合并和冲突检测；
5. 校验引用、地址、权限、流程连线、跨 DB 点位、模板、数组和驱动兼容性；
6. 返回只读预检摘要，包括数量、警告、错误、协议兼容情况和预计替换范围；
7. 用户确认后，在工程更新协调器内暂停新配置写命令，保持当前运行快照继续工作；
8. 完成 staging checkpoint，关闭 staging 连接，备份当前数据库并原子替换；
9. 打开新数据库、发布新 generation、重建运行快照并发送 SSE 重置；
10. 任一步骤失败都恢复原数据库和原运行快照。

预检与提交绑定上传文件哈希和一次性 token，防止确认期间文件内容变化。

### 7.2 导出

导出在一个只读 SQLite 快照事务中执行，通过 `Utf8JsonWriter` 直接写 HTTP 响应流：

1. 写 `project` KEP 原生层；
2. 写完整 `igw` 扩展和加密凭据；
3. 写兼容性摘要和导出 schema 版本；
4. 完成后记录数量、耗时、输出字节数和不兼容项数量，不记录业务值和秘密。

浏览器不构造或复制完整工程对象。导出期间的后续配置保存进入下一 revision，不混入当前文件。

## 8. KEP 直接导入验证

PTC KEP Config API 的工程导入入口要求包含完整 `project` 对象。目标文件同时存在顶层 `igw` 时，不同 KEP 版本是否忽略未知顶层字段必须通过实际版本验证，不能仅凭本地序列化测试宣称兼容。

提供独立的 KEP 往返验证工具：

1. 读取 IGW 导出的同一个 JSON；
2. 调用测试 KEP 的 `JsonProjectLoad` 导入；
3. 等待 KEP 应用配置并读取诊断；
4. 从 KEP 再导出工程；
5. 比较驱动、通道、设备、标记组、点名、地址、类型、权限、扫描周期和驱动关键属性；
6. 输出机器可读 JSON 报告和人员可读摘要。

该工具默认只允许对明确指定的测试 KEP 实例运行，执行前备份其工程。没有用户明确授权时不得替换现场正在运行的 KEP 工程。

验收分两层：

- “结构生成通过”：本地 schema/golden fixture 测试通过，只证明生成结构符合已知样例。
- “目标 KEP 往返通过”：目标 KEP 接受同一个含 `igw` 的文件，且再导出比较一致，才证明该 KEP 版本可直接使用。

如果目标 KEP 拒绝未知顶层 `igw`，当前“单文件同时直导 KEP 和无损迁移 IGW”的约束在该版本上不可实现。此时必须把实际错误和版本信息提交给用户重新选择承载方式；不得暗中改成两个文件、丢弃 `igw` 或虚报兼容。

## 9. 旧工程迁移与回滚

- 首次启动发现没有 SQLite 工程库但存在旧 JSON/旧内部工程文件时，使用同一导入管线生成 staging 数据库。
- 迁移成功并完成运行时装载后才切换 SQLite 为主存储；原文件重命名为带时间戳的只读备份，不删除。
- SQLite 已存在后，启动、保存和运行时都只使用 SQLite，旧 JSON 不再自动更新。
- 每次数据库 schema 升级和整工程导入前生成一致备份，并执行保留数量策略。
- 回滚旧程序时使用迁移前备份；新程序的 SQLite 变更不反向写入旧 JSON。
- 迁移日志记录表数量、点数、任务数、revision 和错误路径，不记录密码、token 或业务值。

## 10. 模块化清理路线

### 阶段 A：工程存储和交换

- 引入仓储合同、SQLite 模式和旧工程迁移。
- 将 KEP/IGW JSON 编解码从 Web 前端迁到后端。
- 实现分页配置 API、局部命令和 Web 懒加载。
- 完成百万点导入、编辑、导出和重启一致性验证。

### 阶段 B：运行引擎拆分

- 将 `Engine.cs` 拆为拓扑生命周期、扫描调度、读批处理、写协调、连接健康、运行快照应用和遥测模块。
- 驱动异常只通过统一结果合同影响点或批次；只有连接健康判定失败才关闭会话。
- 保持各协议现有能力和公开 API 不变。

### 阶段 C：MES 模块拆分

- 将 `MesUploadEngine` 拆为触发器、条件求值、请求构造、HTTP 执行、响应映射、PLC 回写和执行状态模块。
- 将 `MesFlow.vue` 和 WPF 流程窗口拆为任务列表、画布、节点属性、点位选择器和运行状态订阅组件。
- 前后端共享版本化流程 DTO 和校验规则，避免同一规则多处漂移。

### 阶段 D：驱动公共能力

- 抽取批量限制协商、读写分片、重试、写后验证、字符串长度、日志字段和连接故障分类。
- 各驱动只实现协议传输与能力报告，通用策略由运行时协调器负责。
- KEP 编解码器与 PLC 驱动分离；导出 KEP 配置不参与实际 PLC 通信。

每个阶段都先补行为测试，再做小步替换；禁止只按行数机械拆文件或创建没有业务边界的 `Utils`/`Helpers` 集合。

## 11. 测试与验收

### 11.1 数据和迁移

- 旧工程迁移后逐实体计数和关键字段一致。
- 迁移失败、磁盘满、进程中断和 schema 升级失败均能保留可启动的旧工程。
- SQLite 外键、唯一约束、事务回滚、并发 revision 冲突和备份恢复测试通过。
- 重启后读取的 revision、任务启停、流程图和凭据状态一致。

### 11.2 JSON 往返

- 原生 KEP JSON（无 `igw`）可以导入 IGW。
- IGW 导出后再导入，所有支持字段和稳定 ID 无损。
- 普通点、结构体、数组、嵌套数组、字符串长度/编码、缩放、报警、写确认和 MES 回写均有往返测试。
- 每个支持协议都有真实 KEP 导出 fixture，未知属性袋能够保留。
- 冲突、重复路径、缺失引用、错误密码和损坏 JSON 在替换数据库前失败。
- 目标 KEP 往返报告逐项证明协议和拓扑一致；未执行真实 KEP 时只能标记“未验证”。

### 11.3 百万点性能

使用单工程一百万点基准验证：

- 后端树节点和点位分页查询不随总点数线性传输；
- 前端首次打开工程、切换组、搜索、选择和局部保存不加载完整点表；
- 单点保存和小批量保存不序列化整个工程；
- JSON 导入/导出为有界内存流式处理；
- 导入替换期间现有 PLC 运行快照保持服务，切换时间和重建时间分别记录；
- 测试报告记录机器配置、数据规模、耗时、峰值工作集、GC 和数据库大小，不用单一平均值宣称产线 SLA。

### 11.4 回归边界

- 后端完整测试、前端组件测试、类型检查、生产构建和 Release 解决方案构建全部通过。
- PLC 读写、SSE、MES 流程和各驱动回归分开报告。
- 模拟器通过只证明软件路径；真实 KEP 和真实 PLC 必须作为独立现场证据。

## 12. 完成定义

只有同时满足以下条件，阶段 A 才算完成：

1. SQLite 成为唯一工程主存储，旧工程自动迁移且可回滚。
2. 常规配置编辑不再传输或保存完整 Project 对象。
3. 单 JSON 能无损完成 IGW 导出再导入。
4. `project` 使用每个支持驱动的 KEP 原生属性，不能等价映射的项目有明确报告。
5. 同一含 `project` 和 `igw` 的 JSON 在目标 KEP 版本完成真实导入/再导出验证后，才标记“可直接导入 KEP”。
6. 采集任务、流程图、点位实际路径、数组、权限、长度、映射、回写、模板和所有工程设置经自动往返测试无丢失。
7. 一百万点测试证明查询、编辑、导入和导出使用有界内存并满足已定义的验收阈值。
8. 现有内网业务 API、PLC 运行语义和 SSE 实时链路没有未经授权的行为变化。

本规格取代 `2026-08-12-kepserver-json-project-exchange-design.md` 中“后台保存机制保持不变”和仅覆盖少量驱动属性的部分；旧文档仍作为历史设计记录保留。
