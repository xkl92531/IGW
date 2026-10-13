namespace IGW.Abstractions;

/// <summary>自动字段映射的字典路由；普通 JSON 名称与 PLC 路径各自保留原意。</summary>
public static class MesParameterRouting
{
    public sealed record Route(string Key, string TagId, double Scale);

    public static bool HasExplicitTemplate(MesPayload payload, MesHttp http) =>
        !string.IsNullOrWhiteSpace(http.JsonTemplate) ||
        !MesPayloadModes.IsDataRanges(payload) && !string.IsNullOrWhiteSpace(http.BodyTemplate);

    /// <summary>流程节点有各自的 Payload/Http；线性步骤共用任务 Payload，任一步自动生成就须检查路由。</summary>
    public static bool UsesAutomaticTaskPayload(MesTask task) => task.Flow is not { Nodes.Count: > 0 } &&
        (task.Steps.Count == 0 || task.Steps.Any(step => !HasExplicitTemplate(task.Payload, step.Http)));

    public static bool IsCovered(string path, IReadOnlyList<MesStruct> structs) => structs.Any(item =>
        !string.IsNullOrWhiteSpace(item.BaseGroup) && (path == item.BaseGroup ||
        path.StartsWith(item.BaseGroup + ".", StringComparison.Ordinal) ||
        path.StartsWith(item.BaseGroup + "[", StringComparison.Ordinal) ||
        path.StartsWith(item.BaseGroup + "_", StringComparison.Ordinal)));

    public static bool HasRoutes(MesPayload payload) => !MesPayloadModes.IsDataRanges(payload) &&
        (payload.Fields.Any(f => !string.IsNullOrWhiteSpace(f.ParameterCode) && !IsCovered(f.TagId, payload.Structs)) || payload.Structs.Any(HasRoutes));

    private static bool HasRoutes(MesStruct item) => item.AsDictionary ||
        item.Members.Any(f => !string.IsNullOrWhiteSpace(f.ParameterCode)) || item.Structs.Any(HasRoutes);

    public static IEnumerable<string> FixedElementPaths(MesStruct item, string path) =>
        Enumerable.Range(item.StartIndex, Math.Max(0, item.FixedCount)).Select(index => $"{path}[{index}]");

    public static IEnumerable<Route> Routes(MesPayload payload, Func<MesStruct, string, IEnumerable<string>>? elements = null)
    {
        if (MesPayloadModes.IsDataRanges(payload)) yield break;
        elements ??= FixedElementPaths;
        foreach (var field in payload.Fields)
            if (!string.IsNullOrWhiteSpace(field.ParameterCode) && !IsCovered(field.TagId, payload.Structs))
                yield return new(field.ParameterCode, field.TagId, field.Scale);
        foreach (var item in payload.Structs)
            if (HasRoutes(item))
                foreach (var route in StructRoutes(item, item.BaseGroup, false, elements)) yield return route;
    }

    private static IEnumerable<Route> StructRoutes(MesStruct item, string path, bool dictionary,
        Func<MesStruct, string, IEnumerable<string>> elements)
    {
        dictionary |= item.AsDictionary;
        var paths = item.Kind is "Object" or "List" ? new[] { path } : elements(item, path);
        foreach (string element in paths)
        {
            foreach (var member in item.Members)
            {
                string target = element + "." + member.TagId;
                if (dictionary || !string.IsNullOrWhiteSpace(member.ParameterCode))
                    yield return new(dictionary ? target : member.ParameterCode!, target, member.Scale);
            }
            foreach (var child in item.Structs)
                if (dictionary || HasRoutes(child))
                    foreach (var route in StructRoutes(child, element + "." + child.BaseGroup, dictionary, elements)) yield return route;
        }
    }

    public static void Validate(IEnumerable<Route> routes)
    {
        var seen = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (seen.TryGetValue(route.Key, out var previous) &&
                (previous.TagId != route.TagId || previous.Scale != route.Scale))
                throw new InvalidDataException($"MES parameterDic 参数码重复或目标冲突：{route.Key} ({previous.TagId} / {route.TagId})");
            seen[route.Key] = route;
        }
    }
}
