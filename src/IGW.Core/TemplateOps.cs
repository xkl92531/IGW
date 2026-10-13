using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 模板操作(纯逻辑,就地改动传入的工程副本;REST 端点先 <see cref="ProjectStore.Clone"/> 再调用,成功后 ReloadAsync)。
/// 各方法返回 null=成功、非 null=错误信息(端点转 400)。提取/套用/导入的一致性规则集中于此,便于单测。
/// </summary>
public static class TemplateOps
{
    // 提取模板时不带入实例专属的端点类设置(Endpoint 是独立字段,secondaryEndpoint 是设置里的实例专属项)
    private static readonly string[] InstanceOnlySettingKeys = { "secondaryEndpoint" };

    /// <summary>从 <paramref name="channel"/>.<paramref name="device"/> 提取模板并加入 <c>proj.Templates</c>。源设备保持不变。</summary>
    public static string? Extract(IgwProject proj, string channel, string device, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return "模板名不能为空。";
        var ch = FindChannel(proj, channel);
        if (ch is null) return $"通道不存在:{channel}。";
        var dev = ch.Devices.FirstOrDefault(d => string.Equals(d.Name, device, StringComparison.OrdinalIgnoreCase));
        if (dev is null) return $"通道 {channel} 下设备不存在:{device}。";
        if (TemplateExists(proj, templateName)) return $"模板名已存在:{templateName}(请改名后重试)。";

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in dev.Settings)
            if (!InstanceOnlySettingKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                defaults[kv.Key] = kv.Value;

        proj.Templates.Add(new DeviceTemplate
        {
            Name = templateName,
            DriverId = ch.DriverId,             // 记录源设备的驱动类型
            Description = dev.Description,
            DefaultSettings = defaults,          // 剥离端点类实例设置
            Tags = Clone(dev.Tags),              // 深拷贝:提取后改模板不影响源设备
            Groups = Clone(dev.Groups),
        });
        return null;
    }

    /// <summary>套用模板创建新设备并加入 <paramref name="channel"/>。驱动不匹配/重名/找不到 → 返回错误。</summary>
    public static string? Apply(IgwProject proj, string templateName, string channel, string deviceName, string endpoint,
        IReadOnlyDictionary<string, string>? settings, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "设备名不能为空。";
        var tmpl = FindTemplate(proj, templateName);
        if (tmpl is null) return $"模板不存在:{templateName}。";
        var ch = FindChannel(proj, channel);
        if (ch is null) return $"通道不存在:{channel}。";
        if (!string.Equals(tmpl.DriverId, ch.DriverId, StringComparison.OrdinalIgnoreCase))
            return $"驱动不匹配:模板 {templateName}({tmpl.DriverId})与通道 {channel}({ch.DriverId})驱动不同。";
        if (ch.Devices.Any(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase)))
            return $"通道 {channel} 下设备名已存在:{deviceName}。";

        // 实例设置 = 模板默认设置 叠加 调用方提供的设置(调用方优先)
        var merged = new Dictionary<string, string>(tmpl.DefaultSettings, StringComparer.Ordinal);
        if (settings != null) foreach (var kv in settings) merged[kv.Key] = kv.Value;

        ch.Devices.Add(new DeviceDef
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = deviceName,
            Endpoint = endpoint ?? "",
            Description = description,
            Settings = merged,
            TemplateRef = tmpl.Name,             // 与模板保持关联;点位来自模板+差异(运行时展开)
            Delta = new DeviceDelta(),
        });
        return null;
    }

    /// <summary>收编 extra 点:把验证报告里勾选的真机多出点写入该实例的 <c>Delta.Adds</c>(设备根层);已存在同名追加则跳过。返回收编条数经 out 参数。</summary>
    public static string? AdoptExtras(IgwProject proj, string channel, string device, IReadOnlyList<TagDef> points, out int adopted)
    {
        adopted = 0;
        var ch = FindChannel(proj, channel);
        if (ch is null) return $"通道不存在:{channel}。";
        var dev = ch.Devices.FirstOrDefault(d => string.Equals(d.Name, device, StringComparison.OrdinalIgnoreCase));
        if (dev is null) return $"通道 {channel} 下设备不存在:{device}。";
        if (string.IsNullOrWhiteSpace(dev.TemplateRef)) return $"设备 {device} 不是模板实例,无法收编。";
        if (points is null || points.Count == 0) return "未选择要收编的点。";

        dev.Delta ??= new DeviceDelta();
        var existing = new HashSet<string>(dev.Delta.Adds.Where(a => string.IsNullOrEmpty(a.GroupPath))
            .Select(a => a.Tag.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in points)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || !existing.Add(p.Name)) continue;   // 去重(同根层同名只收一次)
            dev.Delta.Adds.Add(new TagAdd { GroupPath = "", Tag = p });
            adopted++;
        }
        return adopted == 0 ? "所选点均已存在,无新增收编。" : null;
    }

    /// <summary>导入模板到工程(重名拒绝,由用户改名重试)。</summary>
    public static string? Import(IgwProject proj, DeviceTemplate tmpl)
    {
        if (tmpl is null || string.IsNullOrWhiteSpace(tmpl.Name)) return "模板缺少名称。";
        if (string.IsNullOrWhiteSpace(tmpl.DriverId)) return $"模板 {tmpl.Name} 缺少 driverId。";
        if (TemplateExists(proj, tmpl.Name)) return $"模板名已存在:{tmpl.Name}(请改名后重试)。";
        proj.Templates.Add(Clone(tmpl));
        return null;
    }

    /// <summary>更新模板内容(名称不变,按 <paramref name="name"/> 定位):替换驱动/说明/默认设置/点位。改点会经 DeviceSig 传播到全部引用实例(下次应用时重启)。</summary>
    public static string? Update(IgwProject proj, string name, DeviceTemplate updated)
    {
        var existing = FindTemplate(proj, name);
        if (existing is null) return $"模板不存在:{name}。";
        if (updated is null) return "模板内容为空。";
        if (string.IsNullOrWhiteSpace(updated.DriverId)) return "模板缺少 driverId。";
        existing.DriverId = updated.DriverId;
        existing.Description = updated.Description;
        existing.DefaultSettings = updated.DefaultSettings ?? new();
        existing.Tags = updated.Tags ?? new();
        existing.Groups = updated.Groups ?? new();
        return null;
    }

    /// <summary>删除模板:仍有实例引用则拒绝(否则实例悬空);无引用则移除。</summary>
    public static string? Delete(IgwProject proj, string templateName)
    {
        var tmpl = FindTemplate(proj, templateName);
        if (tmpl is null) return $"模板不存在:{templateName}。";
        int refs = proj.Channels.Sum(c => c.Devices.Count(d => string.Equals(d.TemplateRef, tmpl.Name, StringComparison.OrdinalIgnoreCase)));
        if (refs > 0) return $"模板 {tmpl.Name} 仍被 {refs} 台设备引用,不能删除(请先改用其他模板或删除这些设备)。";
        proj.Templates.Remove(tmpl);
        return null;
    }

    private static ChannelDef? FindChannel(IgwProject proj, string name) =>
        proj.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static DeviceTemplate? FindTemplate(IgwProject proj, string name) =>
        proj.Templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool TemplateExists(IgwProject proj, string name) => FindTemplate(proj, name) != null;

    private static T Clone<T>(T v) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, ProjectStore.Options), ProjectStore.Options)!;
}
