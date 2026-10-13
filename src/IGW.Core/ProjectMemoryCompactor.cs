using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 压缩工程模型在内存里的重复内容：点名、单位、元数据键和常见取值在大工程里成千上万次重复
/// (例如每个 OPC UA 点都带 "array.sourceFormat"="OPC UA"、"opcua.valueRank"="-1")，
/// 反序列化后每个点各持一份。这里让相同内容共用同一个字符串，元数据里与地址相同的值直接引用地址，
/// 并按实际条目数重建元数据字典。只改变对象引用，不改变任何取值和行为。
/// 必须在工程交给运行时各组件之前调用(对象尚未被其他线程读取)。
/// </summary>
public static class ProjectMemoryCompactor
{
    private const int MaxPooledLength = 256;

    public static void Compact(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ChannelDef channel in project.Channels)
            foreach (DeviceDef device in channel.Devices)
                CompactDevice(device, pool);
        foreach (DeviceTemplate template in project.Templates)
        {
            CompactTags(template.Tags, pool);
            CompactGroups(template.Groups, pool);
        }
    }

    /// <summary>
    /// 只压缩一台新读回的设备(批量注册点位后投影进工程前调用;字符串只在该设备内部共用)。
    /// 必须在设备交给运行时各组件之前调用。
    /// </summary>
    public static void CompactDevice(DeviceDef device)
    {
        ArgumentNullException.ThrowIfNull(device);
        CompactDevice(device, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static void CompactDevice(DeviceDef device, Dictionary<string, string> pool)
    {
        CompactTags(device.Tags, pool);
        CompactGroups(device.Groups, pool);
    }

    private static void CompactGroups(List<TagGroupDef> groups, Dictionary<string, string> pool)
    {
        foreach (TagGroupDef group in groups)
        {
            group.Name = Pool(group.Name, pool);
            CompactTags(group.Tags, pool);
            CompactGroups(group.SubGroups, pool);
        }
    }

    private static void CompactTags(List<TagDef> tags, Dictionary<string, string> pool)
    {
        foreach (TagDef tag in tags)
        {
            tag.Name = Pool(tag.Name, pool);
            tag.Address = Pool(tag.Address, pool);
            if (tag.Description is { Length: 0 }) tag.Description = string.Empty;
            else if (tag.Description is not null) tag.Description = Pool(tag.Description, pool);
            if (tag.EngUnit is not null) tag.EngUnit = Pool(tag.EngUnit, pool);
            if (tag.Metadata is { Count: > 0 } metadata)
            {
                var compact = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
                foreach (var (key, value) in metadata)
                    compact[Pool(key, pool)] = string.Equals(value, tag.Address, StringComparison.Ordinal)
                        ? tag.Address
                        : Pool(value, pool);
                tag.Metadata = compact;
            }
            if (tag.KepProperties is { Count: > 0 } kep)
            {
                var compact = new Dictionary<string, System.Text.Json.JsonElement>(kep.Count, StringComparer.Ordinal);
                foreach (var (key, value) in kep) compact[Pool(key, pool)] = value;
                tag.KepProperties = compact;
            }
        }
    }

    private static string Pool(string value, Dictionary<string, string> pool)
    {
        if (value is null || value.Length == 0) return value ?? "";
        if (value.Length > MaxPooledLength) return value;
        if (pool.TryGetValue(value, out string? shared)) return shared;
        pool[value] = value;
        return value;
    }
}
