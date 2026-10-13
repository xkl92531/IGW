namespace IGW.Abstractions;

/// <summary>批量注册的目标通道：不存在时按此定义创建；已存在时 settings 不覆盖。</summary>
public sealed record ProjectBulkChannelSpec(string Name, string DriverId, Dictionary<string, string>? Settings);

/// <summary>批量注册的目标设备：不存在时创建；已存在时只更新 endpoint 并补齐缺失的 settings 键。</summary>
public sealed record ProjectBulkDeviceSpec(string Name, string Endpoint, Dictionary<string, string>? Settings);

/// <summary>
/// 批量注册写入设备 settings 的归属标记。带此标记的设备由 <c>POST /api/v1/project/bulk/tags</c> 管理：
/// DCS <c>/Tag/AddTags</c> 选择通道/设备时忽略它们，并拒绝向同名逻辑设备（dcsLogicalDevice）追加点位。
/// </summary>
public static class ProjectBulkTagMetadata
{
    public const string ManagedByKey = "dcsManagedBy";
    public const string ManagedByBulk = "bulk";

    public static bool IsBulkManaged(DeviceDef? device) =>
        device?.Settings is { } settings
        && settings.TryGetValue(ManagedByKey, out string? value)
        && string.Equals(value, ManagedByBulk, StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条待写入的点位。GroupPath 每段一个标记组，空 = 设备根层。</summary>
public sealed class ProjectBulkTagItem
{
    public List<string> GroupPath { get; set; } = [];
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public IgwDataType DataType { get; set; } = IgwDataType.Int16;
    public TagAccess Access { get; set; } = TagAccess.Read;
    public int ScanRateMs { get; set; } = 100;
    public string? Description { get; set; }
    public ArrayDef? Array { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public int? StringLength { get; set; }
    public string? EngUnit { get; set; }
}

public sealed class ProjectBulkTagRequest
{
    public ProjectBulkChannelSpec Channel { get; set; } = new("", "", null);
    public ProjectBulkDeviceSpec Device { get; set; } = new("", "", null);
    /// <summary>写入前整棵删除的根组路径（相对设备根）。用于清理 PLC 中已删除的变量。</summary>
    public List<List<string>> ReplaceGroups { get; set; } = [];
    public List<ProjectBulkTagItem> Items { get; set; } = [];
}

public sealed record ProjectBulkTagResult(
    bool Applied,
    bool Conflict,
    ProjectRevision Revision,
    string? ChannelId,
    string? DeviceId,
    int Created,
    int Updated,
    int Deleted,
    string? ErrorCode,
    string? Error,
    bool Retryable = false);

/// <summary>Index=-1 表示通道/设备/replaceGroups 层面的错误；否则为 Items 下标。</summary>
public sealed record ProjectBulkValidationError(int Index, string Reason);

/// <summary>单设备读取结果：Device 为完整设备子树（组/点/已还原密钥）；Channel 只有其自身字段，不含 Devices（改批量写入时用来读回本次改动的一个通道+设备，不必加载整个工程）。</summary>
public sealed record ProjectDeviceSnapshot(ChannelDef Channel, DeviceDef Device);

public static class ProjectBulkTagValidator
{
    /// <summary>groupPath / replaceGroups 每项的最大段数（组嵌套层数）。更深的嵌套会让运行态签名（JSON 深度 64）失败。</summary>
    public const int MaxGroupDepth = 16;
    /// <summary>通道/设备/组/点名最大字符数。</summary>
    public const int MaxNameLength = 128;
    /// <summary>点位地址最大字符数。</summary>
    public const int MaxAddressLength = 512;
    /// <summary>单次请求最多点位数；超出时接口返回 413 too_many_items。</summary>
    public const int MaxItems = 200_000;
    /// <summary>单次请求最多 replaceGroups 条数；超出时接口返回 413 too_many_items。</summary>
    public const int MaxReplaceGroups = 10_000;

    private const string NameRule = "不能为空、不能含 \".\"、首尾不能有空白、不能含控制字符、不超过 128 个字符";

    /// <summary>数量上限检查（在逐项校验之前做，超出即整单拒绝）。超出时返回中文说明，否则 null。</summary>
    public static string? CheckLimits(ProjectBulkTagRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int items = request.Items?.Count ?? 0;
        if (items > MaxItems)
            return $"单次请求最多 {MaxItems} 个点位（本次 {items} 个），请拆分为多次请求";
        int replace = request.ReplaceGroups?.Count ?? 0;
        if (replace > MaxReplaceGroups)
            return $"单次请求最多 {MaxReplaceGroups} 条 replaceGroups（本次 {replace} 条），请拆分为多次请求";
        return null;
    }

    /// <summary>
    /// 与 IGW 界面一致：非空、不含 "."、首尾无空白（canonical_path 与点位 id 均以 "." 拼接）；
    /// 另外不允许控制字符（OPC UA 浏览名/日志/重复键拼接都不接受）且长度不超过 <see cref="MaxNameLength"/>。
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        if (name.Trim().Length != name.Length) return false;
        foreach (char c in name)
            if (c == '.' || char.IsControl(c)) return false;
        return true;
    }

    private static bool IsValidGroupPath(List<string>? path) =>
        path is not null && path.Count <= MaxGroupDepth && path.All(IsValidName);

    public static IReadOnlyList<ProjectBulkValidationError> Validate(ProjectBulkTagRequest request, int maxErrors = 50)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ProjectBulkValidationError>();
        void Add(int index, string reason)
        {
            if (errors.Count < maxErrors) errors.Add(new ProjectBulkValidationError(index, reason));
        }

        if (!IsValidName(request.Channel?.Name)) Add(-1, $"通道名无效（{NameRule}）");
        if (string.IsNullOrWhiteSpace(request.Channel?.DriverId)) Add(-1, "通道 driverId 不能为空");
        if (!IsValidName(request.Device?.Name)) Add(-1, $"设备名无效（{NameRule}）");
        foreach (var path in request.ReplaceGroups ?? [])
            if (path is null || path.Count == 0 || !IsValidGroupPath(path))
                Add(-1, $"replaceGroups 含无效组路径（每项 1～{MaxGroupDepth} 段，每段须为有效名称）");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = request.Items ?? [];
        for (int i = 0; i < items.Count && errors.Count < maxErrors; i++)
        {
            var item = items[i];
            if (item is null) { Add(i, "点位为空"); continue; }
            if ((item.GroupPath?.Count ?? 0) > MaxGroupDepth) { Add(i, $"组路径超过 {MaxGroupDepth} 层"); continue; }
            if (!IsValidGroupPath(item.GroupPath ?? [])) { Add(i, $"组路径含无效名称（{NameRule}）"); continue; }
            if (!IsValidName(item.Name)) { Add(i, $"点名无效（{NameRule}）"); continue; }
            if (string.IsNullOrWhiteSpace(item.Address)) { Add(i, "地址不能为空"); continue; }
            if (item.Address.Length > MaxAddressLength) { Add(i, $"地址超过 {MaxAddressLength} 个字符"); continue; }
            if (!Enum.IsDefined(item.DataType)) { Add(i, "dataType 无效：" + (int)item.DataType); continue; }
            if (!Enum.IsDefined(item.Access)) { Add(i, "access 无效：" + (int)item.Access); continue; }
            if (item.ScanRateMs <= 0) { Add(i, "scanRateMs 必须大于 0"); continue; }
            // 名称已排除控制字符，用  拼接不会与名称内容冲突
            string key = string.Join('', (item.GroupPath ?? []).Append(item.Name));
            if (!seen.Add(key)) Add(i, "同一组内点名重复：" + item.Name);
        }
        return errors;
    }
}
