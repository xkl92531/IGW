using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Runtime;

/// <summary>
/// 批量注册写库前的候选设备构造（只用于写库前的预检：工程级设备校验、西门子地址模式混用、同级组/点重名）：
/// 以内存健康工程里的同名设备为底，去掉 replaceGroups 指定的子树，再按「组路径 + 点名」覆盖/追加请求点。
/// 覆盖已有点时与仓储写入一致：写确认/缩放/死区/报警等现场配置保留，请求里为 null 的描述/单位/字符串长度/元数据保留原值。
/// 设备 endpoint 以请求为准（非空时），settings 只补缺失键。
/// 通道/设备/组/点名按忽略大小写匹配（与仓储 COLLATE NOCASE 一致的保守近似）。
/// 只新建被触及的列表与组对象；现有非数组 <see cref="TagDef"/> 引用复用，数组点（及其 <see cref="ArrayDef"/>）复制一份——
/// 设备级校验会就地规范化数组定义，不能改到运行中的工程。候选设备可放心交给 <see cref="ProjectStore.CheckDevice"/>，绝不修改传入工程。
/// </summary>
public static class BulkTagCandidateBuilder
{
    public static (ChannelDef Channel, DeviceDef Device) Build(IgwProject project, ProjectBulkTagRequest request) =>
        Build(project, request, null);

    /// <param name="conflicts">
    /// 非 null 时收集本次请求引入的同级组/点重名（OPC UA NodeId 区分大小写，按 Ordinal 比较）：
    /// 只检查本次新建的组与本次新增/改名的点，已有的重名（历史数据）不报。
    /// </param>
    public static (ChannelDef Channel, DeviceDef Device) Build(
        IgwProject project, ProjectBulkTagRequest request, List<string>? conflicts)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);

        ChannelDef? channel = project.Channels.FirstOrDefault(c =>
            string.Equals(c.Name, request.Channel.Name, StringComparison.OrdinalIgnoreCase));
        DeviceDef? existing = channel?.Devices.FirstOrDefault(d =>
            string.Equals(d.Name, request.Device.Name, StringComparison.OrdinalIgnoreCase));
        channel ??= new ChannelDef
        {
            Name = request.Channel.Name,
            DriverId = request.Channel.DriverId,
            Settings = request.Channel.Settings ?? new(),
        };

        var settings = new Dictionary<string, string>(existing?.Settings ?? new());
        foreach ((string key, string value) in request.Device.Settings ?? [])
            settings.TryAdd(key, value);
        settings[ProjectBulkTagMetadata.ManagedByKey] = ProjectBulkTagMetadata.ManagedByBulk; // 与写库一致
        var replaced = new HashSet<string>(
            (request.ReplaceGroups ?? []).Where(path => path is { Count: > 0 }).Select(PathKey),
            StringComparer.OrdinalIgnoreCase);
        var candidate = new DeviceDef
        {
            Id = existing?.Id ?? "",
            Name = existing?.Name ?? request.Device.Name,
            Endpoint = string.IsNullOrEmpty(request.Device.Endpoint) ? existing?.Endpoint ?? "" : request.Device.Endpoint,
            Settings = settings,
            TemplateRef = existing?.TemplateRef,
            Delta = existing?.Delta is null ? null : Clone(existing.Delta),
            Tags = CopyTags(existing?.Tags ?? []),
            Groups = CopyGroups(existing?.Groups ?? [], "", replaced),
        };

        var overlay = new Overlay(candidate, conflicts);
        foreach (ProjectBulkTagItem item in request.Items ?? [])
        {
            if (item is null) continue;
            overlay.Put(item.GroupPath ?? [], item);
        }
        return (channel, candidate);
    }

    private static string PathKey(IEnumerable<string> path) => string.Join('.', path);

    private static List<TagGroupDef> CopyGroups(List<TagGroupDef> groups, string parentPath, HashSet<string> replaced)
    {
        var result = new List<TagGroupDef>(groups.Count);
        foreach (TagGroupDef group in groups)
        {
            string path = parentPath.Length == 0 ? group.Name : parentPath + "." + group.Name;
            if (replaced.Contains(path)) continue;
            result.Add(new TagGroupDef
            {
                Id = group.Id,
                Name = group.Name,
                Description = group.Description,
                KepProperties = group.KepProperties,
                Tags = CopyTags(group.Tags),
                SubGroups = CopyGroups(group.SubGroups, path, replaced),
            });
        }
        return result;
    }

    /// <summary>非数组点复用引用；数组点深拷贝（校验会规范化 IsArray/LowerBounds/结构体字段）。</summary>
    private static List<TagDef> CopyTags(List<TagDef> tags)
    {
        var result = new List<TagDef>(tags.Count);
        foreach (TagDef tag in tags)
            result.Add(tag is not null && (tag.Array is not null || tag.IsArray) ? Clone(tag) : tag!);
        return result;
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, ProjectStore.Options), ProjectStore.Options)!;

    /// <summary>请求点 → 候选点；<paramref name="existing"/> 非 null 时按仓储 UPDATE 语义保留现场配置与留空字段。</summary>
    private static TagDef ToTag(ProjectBulkTagItem item, TagDef? existing) => new()
    {
        Id = existing?.Id ?? "",
        Name = item.Name,   // 仓储 UPDATE 同样以请求的点名（大小写）为准
        Address = item.Address,
        DataType = item.DataType,
        Access = item.Access,
        ScanRateMs = item.ScanRateMs,
        Description = item.Description ?? existing?.Description,
        Array = item.Array,
        IsArray = item.Array is not null,
        Metadata = item.Metadata ?? existing?.Metadata,
        StringLength = item.StringLength ?? existing?.StringLength,
        EngUnit = item.EngUnit ?? existing?.EngUnit,
        Scaling = existing?.Scaling,
        Deadband = existing?.Deadband ?? 0,
        DeadbandType = existing?.DeadbandType ?? DeadbandType.None,
        Alarm = existing?.Alarm,
        KepProperties = existing?.KepProperties,
        TextEncoding = existing?.TextEncoding,
        WriteConfirmation = existing?.WriteConfirmation,
    };

    /// <summary>候选树上的覆盖器：组/点名索引按列表懒建立，十万级请求点也保持 O(M+B)。</summary>
    private sealed class Overlay(DeviceDef device, List<string>? conflicts)
    {
        private readonly Dictionary<List<TagGroupDef>, Dictionary<string, TagGroupDef>> _groups =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<List<TagDef>, Dictionary<string, int>> _tags =
            new(ReferenceEqualityComparer.Instance);

        public void Put(List<string> groupPath, ProjectBulkTagItem item)
        {
            List<TagGroupDef> groups = device.Groups;
            List<TagDef> tags = device.Tags;
            string path = device.Name;
            foreach (string segment in groupPath)
            {
                Dictionary<string, TagGroupDef> byName = GroupIndex(groups);
                if (!byName.TryGetValue(segment, out TagGroupDef? group))
                {
                    if (conflicts is not null && TagIndex(tags).TryGetValue(segment, out int clash)
                        && string.Equals(tags[clash].Name, segment, StringComparison.Ordinal))
                        conflicts.Add($"组 {path}.{segment} 与同级点位同名");
                    group = new TagGroupDef { Name = segment };
                    groups.Add(group);
                    byName[segment] = group;
                }
                path += "." + group.Name;
                groups = group.SubGroups;
                tags = group.Tags;
            }

            Dictionary<string, int> tagIndex = TagIndex(tags);
            bool found = tagIndex.TryGetValue(item.Name, out int index);
            // 同名（Ordinal）点此前已在：重名要么是历史数据、要么已在前一项报过，不再重复报
            bool sameNameBefore = found && string.Equals(tags[index].Name, item.Name, StringComparison.Ordinal);
            if (conflicts is not null && !sameNameBefore
                && GroupIndex(groups).TryGetValue(item.Name, out TagGroupDef? sibling)
                && string.Equals(sibling.Name, item.Name, StringComparison.Ordinal))
                conflicts.Add($"点位 {path}.{item.Name} 与同级组同名");
            if (found) tags[index] = ToTag(item, tags[index]);
            else
            {
                tagIndex[item.Name] = tags.Count;
                tags.Add(ToTag(item, null));
            }
        }

        private Dictionary<string, TagGroupDef> GroupIndex(List<TagGroupDef> groups)
        {
            if (_groups.TryGetValue(groups, out var index)) return index;
            index = new Dictionary<string, TagGroupDef>(StringComparer.OrdinalIgnoreCase);
            foreach (TagGroupDef group in groups) index.TryAdd(group.Name, group);
            _groups[groups] = index;
            return index;
        }

        private Dictionary<string, int> TagIndex(List<TagDef> tags)
        {
            if (_tags.TryGetValue(tags, out var index)) return index;
            index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tags.Count; i++) index.TryAdd(tags[i].Name, i);
            _tags[tags] = index;
            return index;
        }
    }
}
