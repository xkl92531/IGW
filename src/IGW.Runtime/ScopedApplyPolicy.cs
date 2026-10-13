using System.Text.Encodings.Web;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Runtime;

/// <summary>
/// 局部修改(界面局部保存与批量注册点位共用的 <see cref="ProjectPatch"/>)的运行态范围决策：
/// 除受影响设备外，还有哪些北向组件必须重建。组件按名解析点位、按句柄订阅：
/// 同路径点位复用 Hub 条目时订阅继续有效；点位被删除/改名、被新登记(之前解析不到)或条目被重建(停设备排空超时)时，
/// 引用它们的组件必须重建才能重新绑定。模板实例设备(点位由模板展开)仍退回整体应用。
/// </summary>
public static class ScopedApplyPolicy
{
    /// <summary>逐个点位做子串检查的上限；超过时按设备前缀判断(大批量注册下保持 O(段大小 × 设备数))。</summary>
    internal const int MaxExactTagProbes = 2000;

    // 通道/设备/点名在这个客户群里常年是中文(如"线体.电芯上料")。ProjectStore.Options 没配 Encoder，
    // System.Text.Json 默认把非 ASCII 转成 \uXXXX 转义序列，导致 Contains(deviceFullName + ".") 在
    // 序列化文本里永远找不到原样的中文子串。这里用一份只在本类内使用的选项副本，放开转义
    // (只做子串匹配，不回写文件，不必担心 XSS 转义场景)。
    private static readonly JsonSerializerOptions SerializationOptions = new(ProjectStore.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>受影响的设备(修改前或修改后)是模板实例时必须退回整体应用。</summary>
    public static bool RequiresFullApply(IgwProject before, IgwProject after, ProjectPatchSummary summary)
    {
        var names = AffectedDevices(summary);
        if (names.Count == 0) return false;
        return AnyDevice(before, names, static device => !string.IsNullOrEmpty(device.TemplateRef))
            || AnyDevice(after, names, static device => !string.IsNullOrEmpty(device.TemplateRef));
    }

    /// <summary>
    /// 应用设备之前就能确定的重建组件：设置段本身变化(OPC UA/历史/MES)；受影响设备(前后任一)带报警；
    /// 组件引用了被删除/改名的设备或点/组路径。
    /// </summary>
    public static NorthboundComponents ComponentsToRestart(IgwProject before, IgwProject after, ProjectPatchSummary summary)
    {
        NorthboundComponents restart = NorthboundComponents.None;
        if (summary.OpcUaChanged) restart |= NorthboundComponents.OpcUa;
        if (summary.HistoryChanged) restart |= NorthboundComponents.History;
        if (summary.MesChanged) restart |= NorthboundComponents.Mes;
        var names = AffectedDevices(summary);
        if (names.Count > 0 && (AnyDevice(before, names, HasAnyAlarm) || AnyDevice(after, names, HasAnyAlarm)))
            restart |= NorthboundComponents.Alarms;
        var removed = summary.RemovedDevices.Concat(summary.RemovedPaths)
            .Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (removed.Length > 0) restart |= SectionsReferencing(after, removed);
        return restart;
    }

    /// <summary>
    /// 应用设备之后按引擎实际注销/新登记的点位补充判断：被组件引用的点位条目消失、重建或新出现，都要重建该组件。
    /// 点位过多时退化为按变化设备前缀判断(宁可多重建，不漏)。
    /// </summary>
    public static NorthboundComponents ComponentsReferencingTags(IgwProject project,
        IReadOnlyCollection<string> removedTagIds, IReadOnlyCollection<string> addedTagIds, IReadOnlyCollection<string> changedDevices)
    {
        int total = removedTagIds.Count + addedTagIds.Count;
        if (total == 0) return NorthboundComponents.None;
        IReadOnlyCollection<string> probes = total <= MaxExactTagProbes
            ? removedTagIds.Concat(addedTagIds).Distinct(StringComparer.Ordinal).ToArray()
            : changedDevices;
        return SectionsReferencing(project, probes);
    }

    private static NorthboundComponents SectionsReferencing(IgwProject project, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return NorthboundComponents.None;
        NorthboundComponents result = NorthboundComponents.None;
        if (References(project.History, paths)) result |= NorthboundComponents.History;
        if (References(project.ComputedTags, paths)) result |= NorthboundComponents.Computed;
        // MES 只看正在运行的任务(与 MES 引擎实际绑定的范围一致)
        var runningMes = project.MesUpload?.Tasks.Where(static task => task.Enabled && task.FlowAvailable).ToList();
        if (runningMes is { Count: > 0 } && References(runningMes, paths)) result |= NorthboundComponents.Mes;
        if (References(project.MqttPublish, paths)) result |= NorthboundComponents.Mqtt;
        if (References(project.Schedules, paths)) result |= NorthboundComponents.Scheduler;
        return result;
    }

    // 这几段体量小(不含全量点位)，序列化后做子串匹配即可(命名规则统一是"通道.设备(.组)(.点)")：
    // ① 文本里出现该路径本身——引用了它或它下面的点(含嵌在计算表达式里的引用)；
    // ② 某个引用值恰好等于它的上级组/设备路径(如 MES GroupRef="通道.设备.组")——上级下面的点增删同样要重新绑定。
    // 宁可误判重建，不漏。
    private static bool References<T>(T? section, IReadOnlyCollection<string> paths)
    {
        if (section is null) return false;
        string json = JsonSerializer.Serialize(section, SerializationOptions);
        foreach (string path in paths)
        {
            if (json.Contains(path, StringComparison.Ordinal)) return true;
            for (int dot = path.LastIndexOf('.'); dot > 0; dot = path.LastIndexOf('.', dot - 1))
            {
                string parent = path[..dot];
                if (parent.IndexOf('.') < 0) break;   // 只到「通道.设备」为止，不拿单独的通道名做匹配
                if (json.Contains("\"" + parent + "\"", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static HashSet<string> AffectedDevices(ProjectPatchSummary summary) =>
        summary.ChangedDevices.Concat(summary.RemovedDevices).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool AnyDevice(IgwProject project, HashSet<string> names, Func<DeviceDef, bool> predicate)
    {
        foreach (var channel in project.Channels)
            foreach (var device in channel.Devices)
                if (names.Contains(channel.Name + "." + device.Name) && predicate(device)) return true;
        return false;
    }

    private static bool HasAnyAlarm(DeviceDef device)
    {
        if (device.Tags.Exists(static tag => tag.Alarm is not null)) return true;
        foreach (var group in device.Groups)
            if (GroupHasAlarm(group)) return true;
        return false;
    }

    private static bool GroupHasAlarm(TagGroupDef group)
    {
        if (group.Tags.Exists(static tag => tag.Alarm is not null)) return true;
        foreach (var sub in group.SubGroups)
            if (GroupHasAlarm(sub)) return true;
        return false;
    }
}
