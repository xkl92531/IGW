using IGW.Abstractions;

namespace IGW.Northbound.RestApi;

internal sealed class DcsTagAliasResolver
{
    private sealed record DeviceAlias(string LogicalPrefix, string PhysicalPrefix);

    private readonly Func<string, string?> _resolvePhysical;
    private readonly List<DeviceAlias> _aliases = [];
    private readonly Dictionary<string, string> _groupBare = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _groupBareAmbiguous = new(StringComparer.OrdinalIgnoreCase);    private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

    public DcsTagAliasResolver(IgwProject project, IEnumerable<string> physicalTagIds)
        : this(project, BuildPhysicalResolver(physicalTagIds))
    {
    }

    public DcsTagAliasResolver(IgwProject project, Func<string, bool> tagExists)
        : this(project, tagId => tagExists(tagId) ? tagId : null)
    {
    }

    private DcsTagAliasResolver(IgwProject project, Func<string, string?> resolvePhysical)
    {
        _resolvePhysical = resolvePhysical;
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelDef channel in project.Channels ?? [])
        {
            foreach (DeviceDef device in channel.Devices ?? [])
            {
                if (device.Settings?.TryGetValue(DcsTagMetadata.LogicalDevice, out string? logical) != true
                    || string.IsNullOrWhiteSpace(logical)) continue;
                string logicalPrefix = $"通用.{logical.Trim()}.";
                string physicalPrefix = $"{channel.Name}.{device.Name}.";
                if (_ambiguous.Contains(logicalPrefix)) continue;
                if (mappings.TryGetValue(logicalPrefix, out string? existing)
                    && !existing.Equals(physicalPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    mappings.Remove(logicalPrefix);
                    _ambiguous.Add(logicalPrefix);
                }
                else
                {
                    mappings[logicalPrefix] = physicalPrefix;
                }
            }
        }
        _aliases.AddRange(mappings.Select(item => new DeviceAlias(item.Key, item.Value)));
        _aliases.Sort((left, right) => right.LogicalPrefix.Length.CompareTo(left.LogicalPrefix.Length));

        // 2026-08-26 标记组兼容索引：组内点位同时登记无组名裸别名（通用.{logical}.{tag.Name}），
        // 旧别名（不带组段）仍可命中组内点位；跨组同名或与根层同名视为歧义，不参与兜底。
        foreach (ChannelDef channel in project.Channels ?? [])
        {
            foreach (DeviceDef device in channel.Devices ?? [])
            {
                if (device.Settings?.TryGetValue(DcsTagMetadata.LogicalDevice, out string? logical) != true
                    || string.IsNullOrWhiteSpace(logical)) continue;
                string barePrefix = $"通用.{logical.Trim()}.";
                foreach ((string path, TagDef tag) in EnumerateGroupedTags(device.Groups, ""))
                {
                    string bareKey = barePrefix + tag.Name;
                    string physical = $"{channel.Name}.{device.Name}.{path}.{tag.Name}";
                    if (_groupBareAmbiguous.Contains(bareKey)) continue;
                    if (_groupBare.TryGetValue(bareKey, out string? existing)
                        && !existing.Equals(physical, StringComparison.OrdinalIgnoreCase))
                    {
                        _groupBare.Remove(bareKey);
                        _groupBareAmbiguous.Add(bareKey);
                    }
                    else if (!_groupBare.ContainsKey(bareKey))
                    {
                        _groupBare[bareKey] = physical;
                    }
                }
            }
        }
    }

    public bool TryResolve(string? requested, out string physical, out string error)
    {
        string key = requested?.Trim() ?? "";
        if (key.Length == 0)
        {
            physical = "";
            error = "点位名称不能为空";
            return false;
        }

        string? native = _resolvePhysical(key);
        if (native is not null)
        {
            physical = native;
            error = "";
            return true;
        }

        string? ambiguousPrefix = _ambiguous.FirstOrDefault(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (ambiguousPrefix is not null)
        {
            physical = "";
            error = $"DCS 点位别名不唯一:{key}";
            return false;
        }

        DeviceAlias? alias = _aliases.FirstOrDefault(candidate =>
            key.StartsWith(candidate.LogicalPrefix, StringComparison.OrdinalIgnoreCase));
        if (alias is not null)
        {
            string candidate = alias.PhysicalPrefix + key[alias.LogicalPrefix.Length..];
            string? resolved = _resolvePhysical(candidate);
            if (resolved is not null)
            {
                physical = resolved;
                error = "";
                return true;
            }
        }

        if (_groupBare.TryGetValue(key, out string? groupedPhysical))
        {
            string? resolved = _resolvePhysical(groupedPhysical);
            if (resolved is not null)
            {
                physical = resolved;
                error = "";
                return true;
            }
        }

        physical = "";
        error = $"点位不存在:{key}";
        return false;
    }

    private static IEnumerable<(string Path, TagDef Tag)> EnumerateGroupedTags(IEnumerable<TagGroupDef> groups, string prefix)
    {
        foreach (TagGroupDef group in groups ?? [])
        {
            string path = prefix.Length == 0 ? group.Name : $"{prefix}.{group.Name}";
            foreach (TagDef tag in group.Tags) yield return (path, tag);
            foreach ((string subPath, TagDef subTag) in EnumerateGroupedTags(group.SubGroups, path)) yield return (subPath, subTag);
        }
    }

    private static Func<string, string?> BuildPhysicalResolver(IEnumerable<string> physicalTagIds)
    {
        var physical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in physicalTagIds)
        {
            string tagId = raw?.Trim() ?? "";
            if (tagId.Length > 0) physical.TryAdd(tagId, tagId);
        }
        return tagId => physical.TryGetValue(tagId, out string? canonical) ? canonical : null;
    }
}
