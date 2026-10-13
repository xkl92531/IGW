using IGW.Abstractions;

namespace IGW.Northbound.RestApi;

/// <param name="Path">请求的完整路径（通道.设备.组….点）。</param>
/// <param name="Found">工程里是否存在。</param>
/// <param name="Kind">节点类型：tag / group / device / channel。</param>
/// <param name="Id">节点 ID，移动/删除等工程变更按它定位。</param>
/// <param name="ParentId">父节点 ID（点位=所在组或设备；组=上级组或设备）。</param>
/// <param name="ParentPath">父节点完整路径，界面上显示「现在在哪」。</param>
/// <param name="ParentKind">父节点类型。</param>
public sealed record ProjectPathMatch(
    string Path,
    bool Found,
    string? Kind,
    string? Id,
    string? ParentId,
    string? ParentPath,
    string? ParentKind);

/// <summary>
/// 把点位/组的完整路径解析成节点 ID。
///
/// 监控中心的点位异常只知道路径（通道.设备.组.点），要把配错设备的点位挪到正确的设备上，
/// 就得先拿到节点 ID 和它现在的父节点。名字里可以带点号（现场有 <c>DB110_User.Cmd.Flag</c>
/// 这样的点名），所以不能简单按点号切分，只能沿着工程树逐层做最长前缀匹配。
/// </summary>
public static class ProjectPathResolver
{
    public static ProjectPathMatch Resolve(IgwProject project, string path)
    {
        if (project is null || string.IsNullOrWhiteSpace(path)) return NotFound(path);

        foreach (ChannelDef channel in project.Channels)
        {
            if (!StartsWithSegment(path, channel.Name, out string afterChannel))
                continue;
            if (afterChannel.Length == 0)
                return new ProjectPathMatch(path, true, "channel", channel.Id, null, null, "project");

            foreach (DeviceDef device in channel.Devices)
            {
                if (!StartsWithSegment(afterChannel, device.Name, out string afterDevice))
                    continue;
                string devicePath = channel.Name + "." + device.Name;
                if (afterDevice.Length == 0)
                    return new ProjectPathMatch(path, true, "device", device.Id, channel.Id, channel.Name, "channel");

                var match = InDevice(path, device, devicePath, afterDevice);
                if (match is not null) return match;
            }
        }
        return NotFound(path);
    }

    private static ProjectPathMatch? InDevice(string path, DeviceDef device, string devicePath, string rest)
    {
        foreach (TagDef tag in device.Tags)
            if (string.Equals(rest, tag.Name, StringComparison.Ordinal))
                return new ProjectPathMatch(path, true, "tag", tag.Id, device.Id, devicePath, "device");

        return InGroups(path, device.Groups, device.Id, devicePath, "device", rest);
    }

    private static ProjectPathMatch? InGroups(
        string path, IReadOnlyList<TagGroupDef> groups, string parentId, string parentPath, string parentKind, string rest)
    {
        foreach (TagGroupDef group in groups)
        {
            if (!StartsWithSegment(rest, group.Name, out string afterGroup)) continue;
            string groupPath = parentPath + "." + group.Name;
            if (afterGroup.Length == 0)
                return new ProjectPathMatch(path, true, "group", group.Id, parentId, parentPath, parentKind);

            foreach (TagDef tag in group.Tags)
                if (string.Equals(afterGroup, tag.Name, StringComparison.Ordinal))
                    return new ProjectPathMatch(path, true, "tag", tag.Id, group.Id, groupPath, "group");

            var nested = InGroups(path, group.SubGroups, group.Id, groupPath, "group", afterGroup);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>路径是否以某一段开头；是则给出去掉该段和点号之后的剩余部分。</summary>
    private static bool StartsWithSegment(string path, string segment, out string rest)
    {
        rest = "";
        if (segment.Length == 0 || !path.StartsWith(segment, StringComparison.Ordinal)) return false;
        if (path.Length == segment.Length) return true;
        if (path[segment.Length] != '.') return false;
        rest = path[(segment.Length + 1)..];
        return true;
    }

    private static ProjectPathMatch NotFound(string path) => new(path, false, null, null, null, null, null);
}
