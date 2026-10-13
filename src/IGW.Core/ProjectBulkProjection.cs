namespace IGW.Core;

using IGW.Abstractions;

/// <summary>批量注册后的写时复制：只替换一台设备，其余拓扑对象引用复用（不克隆、不序列化全工程）。</summary>
public static class ProjectBulkProjection
{
    /// <summary>
    /// 把读回的设备投影进当前工程，并给出与界面局部保存同形的 <see cref="ProjectPatch"/>
    /// (ChangedDevices = 该设备；新建通道时 ChannelsAddedOrRemoved)，交给协调器唯一的局部应用回调。
    /// 读回的设备对象是新建的(尚未被其它线程读取)：先做内存压缩，让大批量点名/元数据共用字符串。
    /// </summary>
    public static ProjectPatch ToPatch(IgwProject current, ProjectDeviceSnapshot snapshot, out string deviceFullName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        ProjectMemoryCompactor.CompactDevice(snapshot.Device);
        bool newChannel = !current.Channels.Exists(c => c.Id == snapshot.Channel.Id);
        IgwProject next = WithDevice(current, snapshot, out deviceFullName);
        var summary = new ProjectPatchSummary { ChannelsAddedOrRemoved = newChannel };
        summary.ChangedDevices.Add(deviceFullName);
        return new ProjectPatch(next, summary);
    }

    /// <summary>
    /// 把部分读回的设备(只含 <paramref name="rootGroupNames"/> 这些根组，及 <paramref name="includeRootTags"/> 时的根下点)
    /// 与当前工程里的同一设备合并成完整设备：未涉及的根组与根下点复用内存里的对象，涉及的根组按 Id 原位替换，
    /// 被删除的去掉，新建的按读回顺序(排序号最大)追加到末尾——与整台设备读回的结果一致。
    /// 当前工程里还没有该设备时返回 null(调用方改为整台读回)。
    /// </summary>
    public static ProjectDeviceSnapshot? MergePartial(
        IgwProject current, ProjectDeviceSnapshot partial, IReadOnlyCollection<string> rootGroupNames, bool includeRootTags)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(partial);
        DeviceDef? old = current.Channels.SelectMany(channel => channel.Devices).FirstOrDefault(device => device.Id == partial.Device.Id);
        if (old is null) return null;
        var touched = new HashSet<string>(rootGroupNames.Select(FoldAscii), StringComparer.Ordinal);
        var loaded = partial.Device.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var groups = new List<TagGroupDef>(old.Groups.Count + partial.Device.Groups.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (TagGroupDef group in old.Groups)
        {
            if (!touched.Contains(FoldAscii(group.Name))) groups.Add(group);
            else if (loaded.TryGetValue(group.Id, out TagGroupDef? fresh)) { groups.Add(fresh); used.Add(fresh.Id); }
            // 否则：该根组已被 replaceGroups 删除(可能以新 Id 重建，下面追加)
        }
        foreach (TagGroupDef group in partial.Device.Groups)
            if (!used.Contains(group.Id)) groups.Add(group);
        partial.Device.Groups = groups;
        if (!includeRootTags) partial.Device.Tags = old.Tags;
        return partial;
    }

    /// <summary>与 SQLite COLLATE NOCASE 一致：只折叠 ASCII 字母。</summary>
    private static string FoldAscii(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });

    public static IgwProject WithDevice(IgwProject current, ProjectDeviceSnapshot snapshot, out string deviceFullName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        IgwProject next = current.ShallowCopy();
        next.Channels = [.. current.Channels];
        int channelIndex = next.Channels.FindIndex(c => c.Id == snapshot.Channel.Id);
        ChannelDef channel;
        if (channelIndex < 0)
        {
            channel = snapshot.Channel.ShallowCopy();
            channel.Devices = [snapshot.Device];
            next.Channels.Add(channel);
        }
        else
        {
            channel = next.Channels[channelIndex].ShallowCopy();
            channel.Devices = [.. channel.Devices];
            int deviceIndex = channel.Devices.FindIndex(d => d.Id == snapshot.Device.Id);
            if (deviceIndex < 0) channel.Devices.Add(snapshot.Device);
            else channel.Devices[deviceIndex] = snapshot.Device;
            next.Channels[channelIndex] = channel;
        }
        deviceFullName = channel.Name + "." + snapshot.Device.Name;
        return next;
    }
}
