namespace IGW.Abstractions;

/// <summary>网关工程(序列化进 .igwproj)。层级:Project → Channel → Device → (Group*) → Tag。</summary>
public sealed class IgwProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "项目";
    public string? Description { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? KepProperties { get; set; }
    public ProjectSettings Settings { get; set; } = new();
    public OpcUaSettings OpcUa { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public MqttPublishSettings MqttPublish { get; set; } = new();
    public List<ComputedTagDef> ComputedTags { get; set; } = new();
    public List<ScheduleJobDef> Schedules { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public MesUploadSettings MesUpload { get; set; } = new();
    public List<DeviceTemplate> Templates { get; set; } = new();   // 设备模板库(套用/批量实例化);无模板时为空,旧工程缺省
    public List<ChannelDef> Channels { get; set; } = new();

    /// <summary>浅拷贝：所有属性引用复用（写时复制投影用，调用方负责替换需要变更的列表）。</summary>
    public IgwProject ShallowCopy() => (IgwProject)MemberwiseClone();
}

/// <summary>设备模板:一份可复用的设备"骨架"——点位/分组/扫描率/缩放/数据类型完整定义,不含实例专属端点。
/// 工程内 <see cref="Name"/> 唯一;绑定 <see cref="DriverId"/>(套用时目标通道须同驱动)。实例只存对它的引用+差异,不落全量拷贝。</summary>
public sealed class DeviceTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";                 // 工程内唯一
    public string DriverId { get; set; } = "";             // 绑定驱动类型
    public string? Description { get; set; }
    public Dictionary<string, string> DefaultSettings { get; set; } = new();  // 套用时预填的设备默认设置(实例可覆盖)
    public List<TagGroupDef> Groups { get; set; } = new();
    public List<TagDef> Tags { get; set; } = new();
}

/// <summary>模板实例相对模板的差异:覆盖(改字段)/追加(实例专有点)/屏蔽(模板点在此实例不生效)。
/// 差异存实例上、不改模板;模板更新时各实例差异保持有效(覆盖压在新模板值之上)。</summary>
public sealed class DeviceDelta
{
    public List<TagOverride> Overrides { get; set; } = new();  // 对模板点的字段覆盖(只存改动字段)
    public List<TagAdd> Adds { get; set; } = new();            // 实例专有追加点
    public List<string> Masks { get; set; } = new();           // 屏蔽的模板点路径(组.子组.点;不含通道/设备)
}

/// <summary>对模板某点的覆盖:<see cref="Path"/> 定位模板内点(组.子组.点),仅非 null 字段生效(只存改动)。</summary>
public sealed class TagOverride
{
    public string Path { get; set; } = "";         // 模板内点路径(组.子组.点,不含通道/设备)
    public string? Address { get; set; }
    public int? ScanRateMs { get; set; }
    public ScalingDef? Scaling { get; set; }
    public IgwDataType? DataType { get; set; }
    public TagAccess? Access { get; set; }
    public string? Description { get; set; }
    public string? EngUnit { get; set; }
}

/// <summary>实例追加的专有点:<see cref="Tag"/> 为点定义,<see cref="GroupPath"/> 为目标组路径(组.子组;空=设备根层)。</summary>
public sealed class TagAdd
{
    public string GroupPath { get; set; } = "";    // 目标组路径(组.子组;空=设备根层)
    public TagDef Tag { get; set; } = new();
}

/// <summary>安全 / 用户管理(对标 KEP「User Manager / Security Policies」):REST 访问鉴权 + 按用户的读写/分支权限。</summary>
public sealed class SecuritySettings
{
    public bool Enabled { get; set; }                  // 启用后 REST 需 HTTP Basic 鉴权并按权限放行
    public List<UserDef> Users { get; set; } = new();
}

/// <summary>用户及权限。</summary>
public sealed class UserDef
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PasswordHash { get; set; } = "";          // PBKDF2 版本化哈希；旧工程明文在下次成功保存时迁移
    public bool CanWrite { get; set; } = true;             // 是否允许写(写点/改配置)
    public List<string> ReadBranches { get; set; } = new(); // 可读分支前缀(空=全部可读)
}

/// <summary>计划任务(对标 KEP「Scheduler」):按周期对某点定时读或写。</summary>
public sealed class ScheduleJobDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Write";   // Read / Write
    public string TagId { get; set; } = "";        // 完整点路径
    public string Value { get; set; } = "";        // Write 时下发的值
    public int IntervalMs { get; set; } = 1000;    // 周期
    public bool Enabled { get; set; } = true;
}

/// <summary>北向 IoT 发布(对标 KEP「IoT Gateway / MQTT」):把缓存数据主动发布到 MQTT broker。</summary>
public sealed class MqttPublishSettings
{
    public bool Enabled { get; set; }
    public string BrokerHost { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;
    public string ClientId { get; set; } = "";          // 留空自动
    public string TopicPrefix { get; set; } = "igw";    // 主题前缀,完整=前缀/通道/设备/.../点
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int Qos { get; set; }                        // 0/1/2
    public bool Retain { get; set; }
    public int PublishIntervalMs { get; set; }          // 0=变化即发;>0=周期全量发布
}

/// <summary>计算 / 派生标记(对标 KEP「Advanced Tags」):值=对其它点的算术表达式,输入变化时自动重算。</summary>
public sealed class ComputedTagDef
{
    public string Name { get; set; } = "";       // 完整路径,如 Computed.AvgTemp
    public string Expression { get; set; } = "";  // 用 {通道.设备.组.点} 引用输入,如 ({A}+{B})/2
    public IgwDataType DataType { get; set; } = IgwDataType.Double;
    // 运算类型(对标 KEP Advanced Tags):Expression(默认表达式)/ Average / Sum / Max / Min / MovingAverage
    public string Operation { get; set; } = "Expression";
    public int WindowSize { get; set; } = 10;     // MovingAverage 的样本窗口
    public string? Description { get; set; }
    public string? EngUnit { get; set; }
}

/// <summary>历史记录 / 数据记录(对标 KEP「Data Logger」):变化时写入 SQLite。</summary>
public sealed class HistorySettings
{
    public bool Enabled { get; set; }
    public string DbPath { get; set; } = "history.db";   // 相对程序目录或绝对路径
    public int FlushIntervalMs { get; set; } = 2000;      // 批量落盘间隔
    public int RetentionRows { get; set; } = 1_000_000;   // 每点保留上限(超出删旧)
}

/// <summary>OPC UA Server 设置(北向)。改后经 PUT /project 热重载生效。</summary>
public sealed class OpcUaSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4840;
    public bool DiagnosticsEnabled { get; set; }
    public bool AllowAnonymous { get; set; } = true;
    public int MaxSessions { get; set; } = 128;
    public int MinSessionTimeoutSec { get; set; } = 15;
    public int MaxSessionTimeoutSec { get; set; } = 60;
    public int TagCacheTimeoutSec { get; set; } = 5;
    public bool ReturnTagProperties { get; set; }
    public bool ReturnAddressHints { get; set; }
    public int MaxDataQueueSize { get; set; } = 2;
    public int MaxRetransmitQueueSize { get; set; } = 10;
    public int MaxNotificationsPerPublish { get; set; } = 65536;
    public bool AllowEncryption { get; set; } = true;   // 提供 Basic256Sha256 Sign/SignAndEncrypt 端点
    public List<OpcUaUser> Users { get; set; } = new();  // 用户名/密码鉴权(AllowAnonymous=false 时生效)
}

/// <summary>OPC UA 服务端账号(用户名/密码)。</summary>
public sealed class OpcUaUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ProjectSettings
{
    public int RestPort { get; set; } = 48020;
    public int OpcUaPort { get; set; } = 4840;
    public int ManagementPort { get; set; } = 48010;
    public int LogRetentionDays { get; set; } = 30;
    /// <summary>Runtime 日志目录；空字符串使用网关数据目录下的 logs。</summary>
    public string LogStorageDirectory { get; set; } = "";
    public TagStreamSettings TagStream { get; set; } = new();
}

/// <summary>通用只读实时流设置。旧工程缺少字段时使用生产安全默认值，限制始终受服务端硬上限约束。</summary>
public sealed class TagStreamSettings
{
    /// <summary>
    /// 早期试验版兼容字段。SSE 成为 QC/运行状态正式数据面后不再作为运行门禁；
    /// 保留它是为了旧工程和 KEP 兼容 JSON 可以无损往返。
    /// </summary>
    public bool Enabled { get; set; } = true;
    public int MaxTagsPerStream { get; set; } = 10_000;
    public int MaxStreams { get; set; } = 100;
    public int HeartbeatSeconds { get; set; } = 10;
    public int SnapshotIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentSnapshots { get; set; } = 4;
    public int DeltaBatchWindowMs { get; set; } = 1;
    public int CoordinatorQueueCapacity { get; set; } = 262_144;
}

/// <summary>通道:一条逻辑通讯链路(决定用哪个驱动 + 链路级参数)。</summary>
public sealed class ChannelDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
    public Dictionary<string, System.Text.Json.JsonElement>? KepProperties { get; set; }
    public List<DeviceDef> Devices { get; set; } = new();

    /// <summary>浅拷贝：所有属性引用复用（写时复制投影用，调用方负责替换 Devices 列表）。</summary>
    public ChannelDef ShallowCopy() => (ChannelDef)MemberwiseClone();
}

/// <summary>设备:通道下的一个从站 / PLC。手工设备直接持有 <see cref="Tags"/>/<see cref="Groups"/>;
/// 模板实例设备则 <see cref="TemplateRef"/> 非空,点位来自模板+<see cref="Delta"/> 运行时展开(自身 Tags/Groups 应为空)。</summary>
public sealed class DeviceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = ""; // "192.168.1.10:502" 等
    public string? Description { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
    public Dictionary<string, System.Text.Json.JsonElement>? KepProperties { get; set; }
    public string? TemplateRef { get; set; }       // 非空=模板实例,引用 DeviceTemplate.Name;旧工程/手工设备缺省 null
    public DeviceDelta? Delta { get; set; }          // 实例相对模板的差异(仅模板实例有意义)
    public List<TagGroupDef> Groups { get; set; } = new();
    public List<TagDef> Tags { get; set; } = new();
}

/// <summary>点位分组(逻辑文件夹,对标 KEP Tag Group,可递归嵌套)。</summary>
public sealed class TagGroupDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement>? KepProperties { get; set; }
    public List<TagGroupDef> SubGroups { get; set; } = new();
    public List<TagDef> Tags { get; set; } = new();
}

/// <summary>点位。Tag = 北向【分组+名字/路径】↔ 南向【Address(PLC 地址)】的绑定。</summary>
public sealed class TagDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public IgwDataType DataType { get; set; } = IgwDataType.Int16;
    public bool IsArray { get; set; }
    public ArrayDef? Array { get; set; }
    public TagAccess Access { get; set; } = TagAccess.Read;
    public int ScanRateMs { get; set; } = 100;   // 默认扫描速率 100ms
    public ScalingDef? Scaling { get; set; }
    public double Deadband { get; set; }
    public DeadbandType DeadbandType { get; set; } = DeadbandType.None;
    public string? Description { get; set; }
    public string? EngUnit { get; set; }
    public Dictionary<string, string>? Metadata { get; set; } // 外部协议扩展元数据;null 时不增加百万点工程体积
    public Dictionary<string, System.Text.Json.JsonElement>? KepProperties { get; set; }
    public AlarmDef? Alarm { get; set; }
    public int? StringLength { get; set; }   // S7 String 为字节容量，WString 为字符容量；缺省 254
    public PlcTextEncoding? TextEncoding { get; set; } // null=自动；仅普通 String 使用，WString 固定 UTF-16
    public WriteConfirmationDef? WriteConfirmation { get; set; }
}

/// <summary>写入完成的通用确认方式；不依赖 MESFlag/PLCFlag 等项目专用命名。</summary>
public sealed class WriteConfirmationDef
{
    public WriteConfirmationMode Mode { get; set; } = WriteConfirmationMode.ReadBack;
    /// <summary>ConfirmationTag 模式使用的完整实际点位路径。</summary>
    public string? ConfirmationTagId { get; set; }
    /// <summary>确认点必须达到的值；按确认点自身的数据类型转换后比较。</summary>
    public string? ExpectedValue { get; set; }
}

public enum WriteConfirmationMode
{
    ReadBack = 0,
    ConfirmationTag = 1,
    ProtocolAcknowledged = 2,
}

/// <summary>点位报警限值(对标 KEP「报警」):四级限值 + 回差防抖。值越限→产生报警,回落超过回差→清除。</summary>
public sealed class AlarmDef
{
    public bool Enabled { get; set; }
    public double? HiHi { get; set; }   // 高高
    public double? Hi { get; set; }     // 高
    public double? Lo { get; set; }     // 低
    public double? LoLo { get; set; }   // 低低
    public double Deadband { get; set; } // 回差(防抖):清除需越过 限值±回差
}

/// <summary>缩放:raw_lo..raw_hi → eng_lo..eng_hi(线性;SquareRoot=true 时为平方根)。</summary>
public sealed class ScalingDef
{
    public bool Enabled { get; set; }
    public bool SquareRoot { get; set; } // true=平方根缩放,false=线性
    public double RawLow { get; set; }
    public double RawHigh { get; set; }
    public double EngLow { get; set; }
    public double EngHigh { get; set; }
    public bool Clamp { get; set; }
}
