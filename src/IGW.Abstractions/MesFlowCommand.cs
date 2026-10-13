namespace IGW.Abstractions;

/// <summary>保存与运行共用的流程条件校验。不读设备、不执行动作。</summary>
public static class MesFlowCommandValidation
{
    public static void Validate(MesTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        // 含本版本不支持配置的任务在运行时停用；这里不拒绝，否则整份旧工程无法加载。
        if (MesUnsupportedConfig.Describe(task) is not null) return;
        var conditions = task.Steps.SelectMany(s => s.Conditions).Concat(task.Flow?.Nodes
            .Where(n => n.Type == "Condition").SelectMany(n => n.Branches).SelectMany(b => b.Conditions) ?? []);
        foreach (var condition in conditions)
        {
            if (condition.Source is not ("Tag" or "Input" or "Mapped" or "Response" or "Variable"))
                throw new InvalidDataException("条件来源无效：" + condition.Source);
            if (condition.Source != "Tag" && string.IsNullOrWhiteSpace(condition.Path))
                throw new InvalidDataException("条件来源路径不能为空：" + condition.Source);
        }
    }
}

/// <summary>
/// 识别本版本不认识的任务配置：触发方式、节点类型、条件或附加字段的取值来源。
/// 旧工程可能带着已下线功能的配置，这类任务整体停用并说明原因，不能跳过认不出的部分继续执行，
/// 否则会在缺少前置动作时照常上报、回写，或把事件触发的任务当成周期任务反复上报。
/// </summary>
public static class MesUnsupportedConfig
{
    private static readonly HashSet<string> TriggerModes = new(StringComparer.OrdinalIgnoreCase) { "", "OnChange", "Interval", "Always", "InterfaceEvent" };
    private static readonly HashSet<string> NodeTypes = new(StringComparer.Ordinal)
        { "Trigger", "Condition", "Upload", "Branch", "Switch", "Writeback", "ResponseWriteback", "Delay", "FixedData" };
    private static readonly HashSet<string> ConditionSources = new(StringComparer.Ordinal) { "Tag", "Input", "Mapped", "Response", "Variable" };
    private static readonly HashSet<string> ExtraFieldSources = new(StringComparer.Ordinal) { "Fixed", "Tag", "Input" };

    /// <summary>任务能正常运行时返回 null，否则返回停用原因。</summary>
    public static string? Describe(MesTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var unsupported = new List<string>();
        if (!TriggerModes.Contains(task.Trigger?.Mode ?? "")) unsupported.Add("触发方式 " + task.Trigger!.Mode);
        var nodes = task.Flow?.Nodes ?? [];
        unsupported.AddRange(nodes.Select(n => n.Type).Where(type => !NodeTypes.Contains(type)).Distinct().Select(type => "节点类型 " + type));
        var conditions = task.Steps.SelectMany(s => s.Conditions)
            .Concat(nodes.SelectMany(n => n.Conditions.Concat(n.Branches.SelectMany(b => b.Conditions))));
        var fields = nodes.Select(n => n.Payload).Append(task.Payload).SelectMany(p => p?.ExtraFields ?? []);
        unsupported.AddRange(conditions.Select(c => c.Source).Where(source => !ConditionSources.Contains(source))
            .Concat(fields.Select(f => f.Source).Where(source => !ExtraFieldSources.Contains(source)))
            .Distinct().Select(source => "取值来源 " + source));
        return unsupported.Count == 0 ? null : $"含本版本不支持的配置({string.Join("、", unsupported)})，任务已停用";
    }
}
