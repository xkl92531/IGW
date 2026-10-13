using System.Text.Json.Serialization;

namespace IGW.Licensing;

/// <summary>授权状态。</summary>
public enum LicenseStatus
{
    Valid,          // 正常
    Trial,          // 首次安装 7 天试用期
    ExpiringSoon,   // 临期(默认到期前 30 天):仍可用,提醒续费
    Grace,          // 已过期但在宽限期内:仍可用,强提醒
    Expired,        // 过期且宽限期已过:停采集/北向
    NotActivated,   // 未激活(无 license.key)
    Invalid,        // 授权码损坏/签名无效
    MachineMismatch,// 授权码绑的是别的机器
    Tampered,       // 检测到系统时间被回拨
    TrialExpired,   // 本机试用期已结束，重复安装不重新计时
}

/// <summary>授权内容(签名保护;字段紧凑以缩短授权码)。</summary>
public sealed class LicensePayload
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("issued")] public DateTime IssuedUtc { get; set; }
    [JsonPropertyName("expiry")] public DateTime ExpiryUtc { get; set; }
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";   // 绑定机器码(规范化 16 位);空=不绑机
    [JsonPropertyName("grace")] public int GraceDays { get; set; } = 7;
    [JsonPropertyName("edition")] public string Edition { get; set; } = "Standard";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    // 授权的驱动/功能模块(取值见 LicenseCatalog)。null = 不限制：旧授权码没有这两项，按全部可用处理
    [JsonPropertyName("drivers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Drivers { get; set; }
    [JsonPropertyName("features"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Features { get; set; }
}

/// <summary>验证结果(供 UI/引擎读)。</summary>
public sealed class LicenseState
{
    public LicenseStatus Status { get; init; }
    public LicensePayload? Payload { get; init; }
    public string Message { get; init; } = "";
    public DateTime? ExpiryUtc => Payload?.ExpiryUtc;
    public string Customer => Payload?.Customer ?? "";
    public int DaysLeft { get; init; }              // 距到期天数(过期为负)
    public string MachineId => Licensing.MachineId.Current;

    /// <summary>是否应停止采集/北向(过期且过宽限、无效、机器不符、时间回拨)。临期/宽限仍放行。</summary>
    public bool Blocked => Status is LicenseStatus.Expired or LicenseStatus.TrialExpired or LicenseStatus.NotActivated
        or LicenseStatus.Invalid or LicenseStatus.MachineMismatch or LicenseStatus.Tampered;

    /// <summary>是否需要在界面上显著提醒(临期/宽限/已被拦截)。</summary>
    public bool NeedsAttention => Status != LicenseStatus.Valid;

    /// <summary>授权里是否包含这个驱动。试用、旧授权码(没列驱动)、模拟器一律放行。</summary>
    public bool AllowsDriver(string driverId) => LicenseCatalog.Allows(Payload?.Drivers, driverId, LicenseCatalog.AlwaysAllowedDrivers);

    /// <summary>授权里是否包含这个功能模块。试用、旧授权码(没列功能)一律放行；不在目录里的功能不受授权限制。</summary>
    public bool AllowsFeature(string featureId) =>
        !LicenseCatalog.Features.Any(item => string.Equals(item.Id, featureId, StringComparison.OrdinalIgnoreCase))
        || LicenseCatalog.Allows(Payload?.Features, featureId, []);
}

/// <summary>授权可以控制的驱动和功能模块。发码工具按这个列勾选项，网关按这个判断。</summary>
public static class LicenseCatalog
{
    public sealed record Item(string Id, string Name);

    /// <summary>可授权的驱动(Id 与驱动注册的 DriverId 一致)。</summary>
    public static readonly IReadOnlyList<Item> Drivers =
    [
        new("Modbus.Tcp", "Modbus TCP"),
        new("Siemens.S7", "西门子 S7"),
        new("Mitsubishi.Mc", "三菱 MC"),
        new("Omron.Fins", "欧姆龙 FINS"),
        new("Omron.Cip", "欧姆龙 CIP"),
        new("AB.Eip", "罗克韦尔 AB"),
        new("OpcUa.Client", "OPC UA 客户端"),
        new("Mqtt.Client", "MQTT 客户端"),
    ];

    /// <summary>可授权的功能模块。</summary>
    public static readonly IReadOnlyList<Item> Features =
    [
        new(Mes, "MES 采集上报"),
        new(OpcUaServer, "OPC UA 服务"),
        new(MqttPublish, "MQTT 发布"),
        new(History, "历史存储"),
    ];

    public const string Mes = "Mes";
    public const string OpcUaServer = "OpcUaServer";
    public const string MqttPublish = "MqttPublish";
    public const string History = "History";

    /// <summary>不受授权限制的驱动：模拟器只用于演示和测试。</summary>
    public static readonly IReadOnlyList<string> AlwaysAllowedDrivers = ["Simulator"];

    /// <summary>授权码里的驱动/功能清单转成中文名称(发码工具显示用)；旧授权码没有清单 = 不限制。</summary>
    public static string Describe(List<string>? ids, IReadOnlyList<Item> catalog) =>
        ids is null ? "全部(旧授权码不限制)"
        : ids.Count == 0 ? "无"
        : string.Join("、", ids.Select(id => catalog.FirstOrDefault(item => item.Id == id)?.Name ?? id));

    internal static bool Allows(List<string>? granted, string id, IReadOnlyList<string> always) =>
        granted is null
        || always.Contains(id, StringComparer.OrdinalIgnoreCase)
        || granted.Contains(id, StringComparer.OrdinalIgnoreCase);
}
