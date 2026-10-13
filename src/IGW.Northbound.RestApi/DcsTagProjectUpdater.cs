using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

internal static class DcsTagProjectUpdater
{
    private sealed record CommunicationMapping(
        string DriverId,
        int DefaultPort,
        string? Model = null,
        string? SettingKey = null,
        string? SettingValue = null);

    private sealed record ValidatedItem(
        DcsTagUpdateModel Source,
        CommunicationMapping Communication,
        TagDef Definition,
        string LogicalDevice,
        string Endpoint);

    public static bool TryBuild(
        IgwProject current,
        IReadOnlyList<DcsTagUpdateModel>? items,
        out IgwProject updated,
        out string error)
    {
        if (items is not { Count: > 0 })
        {
            updated = current;
            error = "请输入点位！";
            return false;
        }

        var validated = new List<ValidatedItem>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            DcsTagUpdateModel? item = items[index];
            if (item is null)
            {
                updated = current;
                error = $"索引 {index} 的点位为空";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.Ip))
            {
                updated = current;
                error = $"索引 {index} 的 IP 不能为空";
                return false;
            }
            if (item.Port is < 0 or > 65535)
            {
                updated = current;
                error = $"索引 {index} 的端口超出范围";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.DbAddress))
            {
                updated = current;
                error = $"索引 {index} 的 PLC 地址不能为空";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.TagName))
            {
                updated = current;
                error = $"索引 {index} 的点位名称不能为空";
                return false;
            }
            if (!TryMapCommunication(item.CommunicationType, out var communication, out error))
            {
                updated = current;
                error = $"索引 {index}:{error}";
                return false;
            }
            if (!DcsTagValueConverter.TryMapDefinition(item.DataType, out TagDef definition, out error))
            {
                updated = current;
                error = $"索引 {index}:{error}";
                return false;
            }
            if (definition.IsArray && !TryApplyArrayDefinition(item, definition, out error))
            {
                updated = current;
                error = $"索引 {index}:{error}";
                return false;
            }

            string ip = item.Ip.Trim();
            string logicalDevice = $"{item.CommunicationType}_{ip}";
            // 批量注册（/api/v1/project/bulk/tags）管理的逻辑设备不接受 AddTags：
            // 另建同名逻辑设备会让 通用.{逻辑设备}.… 别名变成歧义，改写批量设备又会被下次批量注册覆盖
            if (FindBulkManagedLogicalDevice(current, logicalDevice) is { } bulkOwned)
            {
                updated = current;
                error = $"索引 {index}:逻辑设备 {logicalDevice} 由批量注册管理（{bulkOwned}），请使用 /api/v1/project/bulk/tags";
                return false;
            }
            int port = item.Port == 0 ? communication.DefaultPort : item.Port;
            string endpoint = item.CommunicationType switch
            {
                DcsCommunicationType.MemoryCache => "",
                DcsCommunicationType.OpcUa => $"opc.tcp://{ip}:{port}",
                _ => $"{ip}:{port}",
            };
            validated.Add(new ValidatedItem(
                item,
                communication,
                definition,
                logicalDevice,
                endpoint));
        }

        try
        {
            IgwProject clone = ProjectStore.Clone(current);
            foreach (ValidatedItem item in validated)
            {
                ChannelDef channel = FindOrCreateChannel(clone, item);
                DeviceDef device = FindOrCreateDevice(channel, item);
                if (!TryAddOrUpdateTag(device, item, out error))
                {
                    updated = current;
                    return false;
                }
            }
            ProjectStore.Validate(clone);
            updated = clone;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            updated = current;
            error = $"工程配置校验失败:{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// AddTags 只在自己管理的设备所在通道里落点：批量注册建的工程通道（如 泰兴线_PLC1）里的设备也带
    /// dcsLogicalDevice，但带 dcsManagedBy=bulk，不参与通道选择与设备匹配。
    /// </summary>
    private static ChannelDef FindOrCreateChannel(IgwProject project, ValidatedItem item)
    {
        bool opcUa = item.Communication.DriverId.Equals("OpcUa.Client", StringComparison.OrdinalIgnoreCase);
        ChannelDef? channel = opcUa
            ? project.Channels.FirstOrDefault(candidate =>
                candidate.DriverId.Equals(item.Communication.DriverId, StringComparison.OrdinalIgnoreCase)
                && !candidate.Devices.Any(ProjectBulkTagMetadata.IsBulkManaged)
                && (SameOpcEndpoint(candidate.Settings.GetValueOrDefault("url", ""), item.Endpoint)
                    || candidate.Devices.Any(device => IsAddTagsManaged(device)
                        && SameOpcEndpoint(device.Endpoint, item.Endpoint))))
            : project.Channels.FirstOrDefault(candidate =>
                candidate.DriverId.Equals(item.Communication.DriverId, StringComparison.OrdinalIgnoreCase)
                && candidate.Devices.Any(IsAddTagsManaged));
        if (!opcUa)
            channel ??= project.Channels.FirstOrDefault(candidate =>
                candidate.Name.Equals("通用", StringComparison.OrdinalIgnoreCase)
                && candidate.DriverId.Equals(item.Communication.DriverId, StringComparison.OrdinalIgnoreCase));
        if (channel is not null)
        {
            if (opcUa) channel.Settings["url"] = item.Endpoint;
            return channel;
        }

        string preferred = project.Channels.All(candidate => !candidate.Name.Equals("通用", StringComparison.OrdinalIgnoreCase))
            ? "通用"
            : $"通用_{item.Source.CommunicationType}";
        string name = opcUa
            ? UniqueUnusedChannelName(project, preferred)
            : UniqueChannelName(project, preferred, item.Communication.DriverId);
        channel = new ChannelDef
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            DriverId = item.Communication.DriverId,
        };
        if (opcUa) channel.Settings["url"] = item.Endpoint;
        project.Channels.Add(channel);
        return channel;
    }

    private static bool SameOpcEndpoint(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b))
            return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static string UniqueUnusedChannelName(IgwProject project, string preferred)
    {
        if (project.Channels.All(channel => !channel.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            return preferred;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{preferred}_{suffix}";
            if (project.Channels.All(channel => !channel.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static DeviceDef FindOrCreateDevice(ChannelDef channel, ValidatedItem item)
    {
        DeviceDef? device = channel.Devices.FirstOrDefault(candidate =>
            IsAddTagsManaged(candidate)
            && candidate.Settings.TryGetValue(DcsTagMetadata.LogicalDevice, out string? logical)
            && logical.Equals(item.LogicalDevice, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            device = new DeviceDef
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = item.LogicalDevice,
            };
            channel.Devices.Add(device);
        }

        device.Endpoint = item.Endpoint;
        device.Settings[DcsTagMetadata.LogicalDevice] = item.LogicalDevice;
        device.Settings[DcsTagMetadata.CommunicationType] = item.Source.CommunicationType.ToString();
        if (item.Communication.Model is not null) device.Settings["model"] = item.Communication.Model;
        if (item.Communication.SettingKey is not null)
            device.Settings[item.Communication.SettingKey] = item.Communication.SettingValue ?? "";
        return device;
    }

    private static bool TryAddOrUpdateTag(DeviceDef device, ValidatedItem item, out string error)
    {
        List<TagDef> all = EnumerateTags(device).ToList();
        TagDef? byAddress = all.FirstOrDefault(tag =>
            tag.Address.Equals(item.Source.DbAddress.Trim(), StringComparison.OrdinalIgnoreCase));
        TagDef? byName = all.FirstOrDefault(tag =>
            tag.Name.Equals(item.Source.TagName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byAddress is not null && byName is not null && !ReferenceEquals(byAddress, byName))
        {
            error = $"点位名称和 PLC 地址分别匹配到不同点位，存在冲突:{item.Source.TagName}/{item.Source.DbAddress}";
            return false;
        }

        // 2026-08-26 DCS tag-group: non-empty Group places new tags into the group; existing tags are moved idempotently
        string groupPath = (item.Source.Group ?? string.Empty).Trim();
        List<string>? groupSegments = null;
        if (groupPath.Length > 0)
        {
            string[] segments = groupPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || segments.Any(static segment => segment.Length > 100) || groupPath.Length > 300)
            {
                error = $"标记组名称不合法:{groupPath}";
                return false;
            }
            groupSegments = segments.ToList();
        }

        TagDef tag = byAddress ?? byName ?? new TagDef { Id = Guid.NewGuid().ToString("N") };
        if (byAddress is null && byName is null)
        {
            List<TagDef> container = groupSegments is null ? device.Tags : FindOrCreateTagGroup(device, groupSegments).Tags;
            container.Add(tag);
        }
        else if (groupSegments is not null)
        {
            MoveTagIntoGroup(device, tag, groupSegments);
        }
        tag.Name = item.Source.TagName.Trim();
        tag.Address = item.Source.DbAddress.Trim();
        tag.DataType = item.Definition.DataType;
        tag.IsArray = item.Definition.IsArray;
        tag.Array = item.Definition.Array;
        tag.Access = TagAccess.ReadWrite;
        tag.ScanRateMs = 100;
        tag.Metadata = new Dictionary<string, string>(item.Definition.Metadata!, StringComparer.OrdinalIgnoreCase);
        error = "";
        return true;
    }

    private static TagGroupDef FindOrCreateTagGroup(DeviceDef device, List<string> segments)
    {
        List<TagGroupDef> level = device.Groups;
        TagGroupDef current = null!;
        foreach (string name in segments)
        {
            TagGroupDef? found = level.FirstOrDefault(group =>
                group.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                found = new TagGroupDef { Id = Guid.NewGuid().ToString("N"), Name = name };
                level.Add(found);
            }
            current = found;
            level = found.SubGroups;
        }
        return current;
    }

    private static void MoveTagIntoGroup(DeviceDef device, TagDef tag, List<string> segments)
    {
        TagGroupDef target = FindOrCreateTagGroup(device, segments);
        List<TagDef>? owner = FindTagOwner(device, tag);
        if (owner is null)
        {
            target.Tags.Add(tag);
            return;
        }
        if (ReferenceEquals(owner, target.Tags)) return;
        owner.Remove(tag);
        target.Tags.Add(tag);
    }

    private static List<TagDef>? FindTagOwner(DeviceDef device, TagDef tag)
    {
        if (device.Tags.Contains(tag)) return device.Tags;
        return FindTagOwnerInGroups(device.Groups, tag);
    }

    private static List<TagDef>? FindTagOwnerInGroups(IEnumerable<TagGroupDef> groups, TagDef tag)
    {
        foreach (TagGroupDef group in groups)
        {
            if (group.Tags.Contains(tag)) return group.Tags;
            List<TagDef>? hit = FindTagOwnerInGroups(group.SubGroups, tag);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static bool TryApplyArrayDefinition(DcsTagUpdateModel source, TagDef definition, out string error)
    {
        if (source.Dimensions is not { Length: > 0 })
        {
            error = "数组维度 Dimensions 不能为空；绝对地址协议无法从单个地址推断数组长度";
            return false;
        }
        if (source.Dimensions.Any(static dimension => dimension <= 0))
        {
            error = "数组维度必须全部大于零";
            return false;
        }

        int[] lowerBounds = source.LowerBounds is { Length: > 0 }
            ? source.LowerBounds.ToArray()
            : new int[source.Dimensions.Length];
        if (lowerBounds.Length != source.Dimensions.Length)
        {
            error = "数组维度与下界数量不一致";
            return false;
        }

        definition.Array = new ArrayDef
        {
            ElementType = definition.DataType,
            Dimensions = source.Dimensions.ToArray(),
            LowerBounds = lowerBounds,
            ElementStrideBytes = source.ElementStrideBytes ?? 0,
            ElementStrideBits = source.ElementStrideBits ?? 0,
            StringLength = source.StringLength,
            Struct = source.Struct,
            SourceShape = "DCS AddTags",
        };
        error = "";
        return true;
    }

    private static IEnumerable<TagDef> EnumerateTags(DeviceDef device)
    {
        foreach (TagDef tag in device.Tags) yield return tag;
        foreach (TagDef tag in EnumerateGroups(device.Groups)) yield return tag;
    }

    private static IEnumerable<TagDef> EnumerateGroups(IEnumerable<TagGroupDef> groups)
    {
        foreach (TagGroupDef group in groups)
        {
            foreach (TagDef tag in group.Tags) yield return tag;
            foreach (TagDef tag in EnumerateGroups(group.SubGroups)) yield return tag;
        }
    }

    /// <summary>由 AddTags 管理的 DCS 设备：有 dcsLogicalDevice 且不是批量注册管理的设备。</summary>
    private static bool IsAddTagsManaged(DeviceDef device) =>
        device.Settings.ContainsKey(DcsTagMetadata.LogicalDevice)
        && !ProjectBulkTagMetadata.IsBulkManaged(device);

    /// <summary>工程里由批量注册管理、逻辑设备名为 <paramref name="logicalDevice"/> 的设备路径（通道.设备）；没有则 null。</summary>
    private static string? FindBulkManagedLogicalDevice(IgwProject project, string logicalDevice)
    {
        foreach (ChannelDef channel in project.Channels ?? [])
            foreach (DeviceDef device in channel.Devices ?? [])
                if (ProjectBulkTagMetadata.IsBulkManaged(device)
                    && device.Settings.TryGetValue(DcsTagMetadata.LogicalDevice, out string? logical)
                    && logical.Trim().Equals(logicalDevice, StringComparison.OrdinalIgnoreCase))
                    return $"{channel.Name}.{device.Name}";
        return null;
    }

    private static string UniqueChannelName(IgwProject project, string preferred, string driverId)
    {
        ChannelDef? existing = project.Channels.FirstOrDefault(channel =>
            channel.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (existing is null || existing.DriverId.Equals(driverId, StringComparison.OrdinalIgnoreCase)) return preferred;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{preferred}_{suffix}";
            if (project.Channels.All(channel => !channel.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static bool TryMapCommunication(
        DcsCommunicationType type, out CommunicationMapping mapping, out string error)
    {
        mapping = type switch
        {
            DcsCommunicationType.OpcUa => new("OpcUa.Client", 4840),
            DcsCommunicationType.S1200 => new("Siemens.S7", 102, "S7-1200"),
            DcsCommunicationType.S300 => new("Siemens.S7", 102, "S7-300"),
            DcsCommunicationType.S400 => new("Siemens.S7", 102, "S7-400"),
            DcsCommunicationType.S1500 => new("Siemens.S7", 102, "S7-1500"),
            DcsCommunicationType.S200Smart => new("Siemens.S7", 102, "S7-200Smart"),
            DcsCommunicationType.S200 => new("Siemens.S7", 102, "S7-200"),
            DcsCommunicationType.FX5U => new("Mitsubishi.Mc", 6000, SettingKey: "mcVersion", SettingValue: "Qna_3E"),
            DcsCommunicationType.Omr_NJ => new("Omron.Cip", 44818, SettingKey: "path", SettingValue: ""),
            DcsCommunicationType.Omr_Fins => new("Omron.Fins", 9600),
            DcsCommunicationType.MemoryCache => new("Simulator", 0),
            _ => new("", 0),
        };
        if (type == DcsCommunicationType.Inovance_H5U)
        {
            error = "暂不支持 Inovance_H5U 通讯类型";
            return false;
        }
        if (mapping.DriverId.Length == 0)
        {
            error = $"暂不支持的通讯类型:{type}";
            return false;
        }
        error = "";
        return true;
    }
}
