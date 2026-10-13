using System.Text.Json;
using System.Text.Json.Nodes;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>套用报告单条:missing=目标组缺此点(字段保留,画布可改)/ external=组外引用(原样保留,人工确认)。</summary>
public sealed record FlowApplyIssue(string Kind, string Key, string Ref);

/// <summary>一次套用的报告(目标组 + 生成的任务名 + 问题清单;缺失不阻断)。</summary>
public sealed record FlowApplyReport(string Group, string TaskName, IReadOnlyList<FlowApplyIssue> Issues);

/// <summary>批量套用的单组错误。</summary>
public sealed record FlowGroupError(string Group, string Message);

/// <summary>
/// MES 流程模板操作(纯逻辑,改动落在传入的工程副本上;REST 先 Clone 再调用,成功后 ReloadAsync)。
/// 印章语义:套用=深拷贝任务体 + 把所有以源组为前缀的点/组引用「按键名深度重写」为目标组前缀(与组改路径的重写同源语义),
/// 字段映射上报会再按目标 DB 的真实点位重新识别字段/结构体，并按相对路径保留模板里的 JSON 名称、参数码、缩放和格式，
/// 生成后与模板无关联。返回 null=成功、非 null=错误信息(端点转 400)。
/// </summary>
public static class MesFlowTemplateOps
{
    // 引用键白名单(JSON camelCase,大小写不敏感):MesModel 中所有承载 点/组路径 的字符串键。
    // groupRef/deviceRef 是任务级绑定,套用时显式重置,不进白名单。加新引用键时同步维护(单测有反射盘点兜底)。
    internal static readonly HashSet<string> RefKeys = new(StringComparer.OrdinalIgnoreCase)
    { "tagId", "baseGroup", "countTagId", "copyFromTagId", "switchTag", "mesFlagTagId", "plcFlagTagId", "sourcePath", "arrayLengthTagIds" };

    /// <summary>从任务提取流程模板(源任务不变;重名拒绝)。<paramref name="taskIdOrName"/> 先按 Id 再按名匹配。</summary>
    public static string? Extract(IgwProject proj, string taskIdOrName, string templateName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return "模板名不能为空。";
        var mes = proj.MesUpload ??= new MesUploadSettings();
        if (mes.FlowTemplates.Any(t => string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase)))
            return $"流程模板名已存在:{templateName}(请改名后重试)。";
        var task = mes.Tasks.FirstOrDefault(t => t.Id == taskIdOrName)
                ?? mes.Tasks.FirstOrDefault(t => string.Equals(t.Name, taskIdOrName, StringComparison.OrdinalIgnoreCase));
        if (task is null) return $"任务不存在:{taskIdOrName}。";
        if (string.IsNullOrWhiteSpace(task.GroupRef)) return $"任务 {task.Name} 未绑定标记组,无法提取。";

        var body = Clone(task);
        body.Id = ""; body.Enabled = false;   // 模板体不带运行身份
        mes.FlowTemplates.Add(new MesFlowTemplate
        {
            Name = templateName,
            Description = description,
            SourceGroupRef = task.GroupRef!,
            Task = body,
        });
        return null;
    }

    /// <summary>套用模板到目标标记组(含子组),生成默认停用的新任务;缺失/外部引用进 <paramref name="report"/>(缺失不阻断)。</summary>
    public static string? Apply(IgwProject proj, string templateName, string targetGroup, string? taskName, out FlowApplyReport? report) =>
        Apply(proj, templateName, targetGroup, taskName, false, out report);

    /// <summary>
    /// 套用模板到目标标记组。<paramref name="replaceExisting"/> 为 true 时原子替换该组已有任务，
    /// 保留任务身份与绑定并强制停用，等待用户审核后重新启用。
    /// </summary>
    public static string? Apply(IgwProject proj, string templateName, string targetGroup, string? taskName,
        bool replaceExisting, out FlowApplyReport? report)
    {
        report = null;
        var mes = proj.MesUpload ??= new MesUploadSettings();
        var tmpl = mes.FlowTemplates.FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase));
        if (tmpl is null) return $"流程模板不存在:{templateName}。";
        var err = ValidateTarget(proj, targetGroup);
        if (err != null) return err;
        var existingIndex = mes.Tasks.FindIndex(t => string.Equals(t.GroupRef, targetGroup, StringComparison.OrdinalIgnoreCase));
        var existing = existingIndex >= 0 ? mes.Tasks[existingIndex] : null;
        if (existing is not null && !replaceExisting)
            return $"目标组已有采集任务:{targetGroup}(一个组只允许一个任务)。";

        // 任务名:显式指定 或 目标组名;与既有任务重名则后缀序号
        var seg = targetGroup.Split('.');
        string name = existing?.Name ?? (string.IsNullOrWhiteSpace(taskName) ? seg[^1] : taskName.Trim());
        string unique = name;
        for (int i = 2; existing is null && mes.Tasks.Any(t => string.Equals(t.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
            unique = $"{name}-{i}";

        // 深拷贝 + 按键名深度重写(源组前缀 → 目标组前缀),同时收集 组内引用(查缺失)与 组外引用(external)
        var task = Clone(tmpl.Task);
        var node = JsonSerializer.SerializeToNode(task, ProjectStore.Options)!;
        var inGroup = new List<(string Key, string Ref)>();
        var external = new List<(string Key, string Ref)>();
        string oldPrefix = tmpl.SourceGroupRef, dot = oldPrefix + ".";
        Walk(node, (key, val) =>
        {
            if (val == oldPrefix) { inGroup.Add((key, targetGroup)); return targetGroup; }
            if (val.StartsWith(dot, StringComparison.OrdinalIgnoreCase))
            {
                string nv = targetGroup + val[oldPrefix.Length..];
                inGroup.Add((key, nv));
                return nv;
            }
            external.Add((key, val));   // 组外引用(心跳/握手等):原样保留,报告提示
            return null;
        });
        task = node.Deserialize<MesTask>(ProjectStore.Options)!;
        foreach (var upload in task.Flow?.Nodes.Where(n => string.Equals(n.Type, "Upload", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            if (!MesPayloadModes.IsDataRanges(upload.Payload) &&
                (upload.Payload.Fields.Count > 0 || upload.Payload.Structs.Count > 0))
                RebuildFieldMappingFromTarget(proj, targetGroup, upload.Payload);
            foreach (var range in upload.Payload.DataRanges)
            {
                if (!string.Equals(range.OutputPathMode, "Auto", StringComparison.OrdinalIgnoreCase)) continue;
                range.OutputPath = string.Join('.', range.SourcePath.Split('.').Skip(1));
            }
        }
        PruneReplacedPayloadReferences(task, inGroup, external);
        task.Id = Guid.NewGuid().ToString("N");
        task.Name = unique;
        task.GroupRef = targetGroup;
        task.DeviceRef = string.Join('.', seg.Take(2));
        task.FlowAvailable = true;
        task.Enabled = false;   // 默认停用:用户审核流程后再开(与画布自动建任务惯例一致)

        // 缺失检查:对照目标设备「运行时展开后」的点/组集合(模板实例设备的组来自设备模板展开)
        var (tagSet, groupSet) = TargetSets(proj, targetGroup);
        var issues = new List<FlowApplyIssue>();
        foreach (var (key, r) in inGroup.DistinctBy(x => (x.Key, x.Ref)))
        {
            bool ok = string.Equals(key, "baseGroup", StringComparison.OrdinalIgnoreCase)
                ? IsExistingGroupOrIndexedBase(groupSet, r) || string.Equals(r, targetGroup, StringComparison.OrdinalIgnoreCase)
                : string.Equals(key, "sourcePath", StringComparison.OrdinalIgnoreCase)
                ? groupSet.Contains(r) || tagSet.Contains(r) || string.Equals(r, targetGroup, StringComparison.OrdinalIgnoreCase)
                : tagSet.Contains(r);
            if (!ok) issues.Add(new FlowApplyIssue("missing", key, r));
        }
        foreach (var (key, r) in external.DistinctBy(x => (x.Key, x.Ref)))
            issues.Add(new FlowApplyIssue("external", key, r));

        if (existing is null)
        {
            mes.Tasks.Add(task);
        }
        else
        {
            task.Id = existing.Id;
            task.Name = existing.Name;
            task.GroupRef = existing.GroupRef;
            task.DeviceRef = existing.DeviceRef;
            task.FlowAvailable = true;
            task.Enabled = false;
            mes.Tasks[existingIndex] = task;
        }
        report = new FlowApplyReport(task.GroupRef ?? targetGroup, task.Name, issues);
        return null;
    }

    private static bool HasConfiguredFlow(MesTask task) =>
        (task.Flow?.Nodes.Count ?? 0) > 0 || (task.Flow?.Edges.Count ?? 0) > 0;

    /// <summary>批量套用:全部目标先校验(完整组路径存在/无非空流程/选择内不重复),任一非法整体不写;全过后逐组套用并汇总报告。</summary>
    public static (IReadOnlyList<FlowGroupError> Errors, IReadOnlyList<FlowApplyReport> Reports) ApplyBulk(
        IgwProject proj, string templateName, IReadOnlyList<string> targetGroups)
    {
        var errors = new List<FlowGroupError>();
        var reports = new List<FlowApplyReport>();
        var mes = proj.MesUpload ??= new MesUploadSettings();
        if (targetGroups is null || targetGroups.Count == 0)
            return (new[] { new FlowGroupError("", "未选择目标组。") }, reports);
        if (!mes.FlowTemplates.Any(t => string.Equals(t.Name, templateName, StringComparison.OrdinalIgnoreCase)))
            return (new[] { new FlowGroupError("", $"流程模板不存在:{templateName}。") }, reports);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in targetGroups)
        {
            if (!seen.Add(g)) { errors.Add(new(g, "选择中重复出现。")); continue; }
            var err = ValidateTarget(proj, g);
            var existing = mes.Tasks.FirstOrDefault(t => string.Equals(t.GroupRef, g, StringComparison.OrdinalIgnoreCase));
            if (err is null && existing is not null && HasConfiguredFlow(existing))
                err = "目标组已有采集任务。";
            if (err != null) errors.Add(new(g, err));
        }
        if (errors.Count > 0) return (errors, reports);   // 原子:任一非法整体不写

        foreach (var g in targetGroups)
        {
            var existing = mes.Tasks.FirstOrDefault(t => string.Equals(t.GroupRef, g, StringComparison.OrdinalIgnoreCase));
            var err = Apply(proj, templateName, g, null, existing is not null, out var rep);
            if (err != null) { errors.Add(new(g, err)); continue; }   // 理论到不了(已预检),兜底
            reports.Add(rep!);
        }
        return (errors, reports);
    }

    // 套用不限制层级，只校验完整路径对应的目标组确实存在。
    private static string? ValidateTarget(IgwProject proj, string targetGroup)
    {
        var seg = (targetGroup ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (seg.Length < 3) return $"目标必须是标记组完整路径(通道.设备.组[.子组…]):{targetGroup}。";
        var ch = proj.Channels.FirstOrDefault(c => string.Equals(c.Name, seg[0], StringComparison.OrdinalIgnoreCase));
        if (ch is null) return $"通道不存在:{seg[0]}。";
        var dev = ch.Devices.FirstOrDefault(d => string.Equals(d.Name, seg[1], StringComparison.OrdinalIgnoreCase));
        if (dev is null) return $"设备不存在:{seg[0]}.{seg[1]}。";
        if (FindTargetGroup(proj, targetGroup!) is null)
            return $"设备 {seg[0]}.{seg[1]} 下不存在标记组:{string.Join('.', seg.Skip(2))}。";
        return null;
    }

    // 目标设备展开后的 全部点完整路径 与 全部组完整路径(供缺失检查)
    private static (HashSet<string> Tags, HashSet<string> Groups) TargetSets(IgwProject proj, string targetGroup)
    {
        var seg = targetGroup.Split('.');
        var ch = proj.Channels.First(c => string.Equals(c.Name, seg[0], StringComparison.OrdinalIgnoreCase));
        var dev = ch.Devices.First(d => string.Equals(d.Name, seg[1], StringComparison.OrdinalIgnoreCase));
        var eff = TemplateExpander.ExpandInProject(proj, dev);
        string devPath = $"{ch.Name}.{eff.Name}";

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _, _) in TemplateExpander.EffectiveTags(eff))
            tags.Add($"{devPath}.{path}");
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Rec(List<TagGroupDef> gs, string pp)
        {
            foreach (var g in gs)
            {
                string gp = $"{pp}.{g.Name}";
                groups.Add(gp);
                Rec(g.SubGroups, gp);
            }
        }
        Rec(eff.Groups, devPath);
        return (tags, groups);
    }

    /// <summary>
    /// 将模板上报映射重新绑定到目标 DB 的实际结构。模板仅提供接口契约(JSON 名称/参数码/缩放/格式)，
    /// PLC 点位和结构体成员一律以目标 DB 为准，因而目标新增点会出现、模板遗留点会消失。
    /// </summary>
    private static void RebuildFieldMappingFromTarget(IgwProject proj, string targetGroup, MesPayload payload)
    {
        var root = FindTargetGroup(proj, targetGroup);
        if (root is null) return; // ValidateTarget 已保证存在；保留兜底避免损坏模板内容

        var oldFields = payload.Fields
            .GroupBy(field => RelativePath(field.TagId, targetGroup), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var oldStructs = payload.Structs
            .GroupBy(item => RelativePath(item.BaseGroup, targetGroup), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var fields = new List<MesField>();
        CollectTargetFields(root, targetGroup, fields);
        foreach (var field in fields)
            if (oldFields.TryGetValue(RelativePath(field.TagId, targetGroup), out var old))
                CopyFieldContract(old, field);

        var indexed = new Dictionary<string, IndexedStructCandidate>(StringComparer.OrdinalIgnoreCase);
        var plain = new Dictionary<string, PlainStructCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            string relative = RelativePath(field.TagId, targetGroup);
            var segments = relative.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) continue; // DB 直属点保持平铺

            int indexedAt = -1;
            string indexedBase = "";
            int indexedNumber = 0;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!MesIndexedGroupName.TryParse(segments[i], out indexedBase, out indexedNumber)) continue;
                indexedAt = i;
                break;
            }
            if (indexedAt >= 0)
            {
                string scopeRelative = string.Join('.', segments.Take(indexedAt));
                string baseRelative = string.IsNullOrEmpty(scopeRelative) ? indexedBase : $"{scopeRelative}.{indexedBase}";
                var candidate = indexed.GetValueOrDefault(baseRelative);
                if (candidate is null)
                {
                    candidate = new IndexedStructCandidate(baseRelative, indexedBase);
                    indexed.Add(baseRelative, candidate);
                }
                candidate.Indices.Add(indexedNumber);
                candidate.CoveredFields.Add(field);
                string member = string.Join('.', segments.Skip(indexedAt + 1));
                if (!candidate.Members.Contains(member, StringComparer.OrdinalIgnoreCase)) candidate.Members.Add(member);
                continue;
            }

            int lastDot = field.TagId.LastIndexOf('.');
            string parent = field.TagId[..lastDot];
            var plainCandidate = plain.GetValueOrDefault(parent);
            if (plainCandidate is null)
            {
                plainCandidate = new PlainStructCandidate(parent);
                plain.Add(parent, plainCandidate);
            }
            plainCandidate.Fields.Add(field);
        }

        var structs = new List<MesStruct>();
        var covered = new HashSet<MesField>();
        foreach (var candidate in indexed.Values)
        {
            if (candidate.Indices.Count < 2) continue;
            var item = new MesStruct
            {
                Kind = "Array",
                BaseGroup = $"{targetGroup}.{candidate.BaseRelative}",
                AliasName = candidate.BaseName,
                CountMode = "Enum",
                StartIndex = candidate.Indices.Min(),
                FixedCount = candidate.Indices.Max() - candidate.Indices.Min() + 1,
                Members = candidate.Members.Select(member => new MesField { TagId = member, FieldName = "" }).ToList(),
            };
            PreserveStructContract(item, oldStructs, targetGroup);
            structs.Add(item);
            foreach (var field in candidate.CoveredFields) covered.Add(field);
        }
        foreach (var candidate in plain.Values)
        {
            if (candidate.Fields.Count < 2 || candidate.Fields.Any(covered.Contains)) continue;
            var item = new MesStruct
            {
                Kind = "List",
                BaseGroup = candidate.BaseGroup,
                AliasName = candidate.BaseGroup[(candidate.BaseGroup.LastIndexOf('.') + 1)..],
                CountMode = "Fixed",
                StartIndex = 0,
                FixedCount = candidate.Fields.Count,
                Members = candidate.Fields.Select(field => new MesField
                {
                    TagId = field.TagId[(field.TagId.LastIndexOf('.') + 1)..],
                    FieldName = "",
                }).ToList(),
            };
            PreserveStructContract(item, oldStructs, targetGroup);
            structs.Add(item);
            foreach (var field in candidate.Fields) covered.Add(field);
        }

        payload.Fields = fields.Where(field => !covered.Contains(field)).ToList();
        payload.Structs = structs;
    }

    private static TagGroupDef? FindTargetGroup(IgwProject proj, string targetGroup)
    {
        var seg = targetGroup.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (seg.Length < 3) return null;
        var channel = proj.Channels.FirstOrDefault(item => string.Equals(item.Name, seg[0], StringComparison.OrdinalIgnoreCase));
        var device = channel?.Devices.FirstOrDefault(item => string.Equals(item.Name, seg[1], StringComparison.OrdinalIgnoreCase));
        if (device is null) return null;
        var effective = TemplateExpander.ExpandInProject(proj, device);
        var groups = effective.Groups;
        TagGroupDef? target = null;
        foreach (var name in seg.Skip(2))
        {
            target = groups.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is null) return null;
            groups = target.SubGroups;
        }
        return target;
    }

    private static void CollectTargetFields(TagGroupDef group, string groupPath, List<MesField> fields)
    {
        foreach (var tag in group.Tags)
            fields.Add(new MesField { TagId = $"{groupPath}.{tag.Name}", FieldName = tag.Name });
        foreach (var child in group.SubGroups)
            CollectTargetFields(child, $"{groupPath}.{child.Name}", fields);
    }

    private static string RelativePath(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return "";
        string prefix = root + ".";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }

    private static void CopyFieldContract(MesField source, MesField target)
    {
        target.FieldName = source.FieldName;
        target.ParameterCode = source.ParameterCode;
        target.Scale = source.Scale;
        target.Format = source.Format;
    }

    private static void PreserveStructContract(MesStruct target, Dictionary<string, MesStruct> oldStructs, string targetGroup)
    {
        if (!oldStructs.TryGetValue(RelativePath(target.BaseGroup, targetGroup), out var old)) return;
        if (!string.IsNullOrWhiteSpace(old.AliasName)) target.AliasName = old.AliasName;
        target.TakeIndex = old.TakeIndex;
        var oldMembers = old.Members
            .GroupBy(member => member.TagId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var member in target.Members)
            if (oldMembers.TryGetValue(member.TagId, out var oldMember)) CopyFieldContract(oldMember, member);
    }

    private sealed class IndexedStructCandidate(string baseRelative, string baseName)
    {
        public string BaseRelative { get; } = baseRelative;
        public string BaseName { get; } = baseName;
        public HashSet<int> Indices { get; } = [];
        public List<string> Members { get; } = [];
        public List<MesField> CoveredFields { get; } = [];
    }

    private sealed class PlainStructCandidate(string baseGroup)
    {
        public string BaseGroup { get; } = baseGroup;
        public List<MesField> Fields { get; } = [];
    }

    // 字段映射重建后，报告必须只检查最终任务里仍存在的引用；被目标 DB 淘汰的模板旧字段不能再报“缺失”。
    // MesStruct.Members.TagId 是相对 BaseGroup 的成员路径，不是组外完整引用，也不应报 external。
    private static void PruneReplacedPayloadReferences(MesTask task,
        List<(string Key, string Ref)> inGroup, List<(string Key, string Ref)> external)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taskNode = JsonSerializer.SerializeToNode(task, ProjectStore.Options);
        Walk(taskNode, (key, value) =>
        {
            present.Add(ReferenceToken(key, value));
            return null;
        });
        inGroup.RemoveAll(item => !present.Contains(ReferenceToken(item.Key, item.Ref)));
        external.RemoveAll(item => !present.Contains(ReferenceToken(item.Key, item.Ref)));

        var relativeMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddMembers(MesPayload payload)
        {
            foreach (var member in payload.Structs.SelectMany(item => item.Members))
                if (!string.IsNullOrWhiteSpace(member.TagId)) relativeMembers.Add(member.TagId);
        }
        AddMembers(task.Payload);
        foreach (var node in task.Flow?.Nodes ?? []) AddMembers(node.Payload);
        external.RemoveAll(item => string.Equals(item.Key, "tagId", StringComparison.OrdinalIgnoreCase) &&
            relativeMembers.Contains(item.Ref));
    }

    private static string ReferenceToken(string key, string value) => $"{key}\u001f{value}";

    private static bool IsExistingGroupOrIndexedBase(HashSet<string> groups, string path)
    {
        if (groups.Contains(path)) return true;
        int dot = path.LastIndexOf('.');
        string scope = dot >= 0 ? path[..(dot + 1)] : "";
        string expectedBase = dot >= 0 ? path[(dot + 1)..] : path;
        foreach (var group in groups)
        {
            if (!group.StartsWith(scope, StringComparison.OrdinalIgnoreCase)) continue;
            string remainder = group[scope.Length..];
            if (remainder.Contains('.')) continue;
            if (MesIndexedGroupName.TryParse(remainder, out var candidateBase, out _) &&
                string.Equals(candidateBase, expectedBase, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 按键名深度重写:遍历任务 JSON 树,白名单键的非空字符串值交给 onRef(返回替换值或 null=不动)
    private static void Walk(JsonNode? node, Func<string, string, string?> onRef)
    {
        switch (node)
        {
            case JsonObject o:
                // ExtraFields.Path is a project reference only for Tag sources. Input/Job
                // JSON paths and fixed strings are business contracts, not project names.
                bool tagSource = o.Any(property => string.Equals(property.Key, "source", StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue source && source.TryGetValue<string>(out var text) &&
                    string.Equals(text, "Tag", StringComparison.OrdinalIgnoreCase));
                foreach (var kv in o.ToList())
                {
                    // Mapping sourcePath is a backend JSON path, not a PLC reference.
                    if (string.Equals(kv.Key, "sourcePath", StringComparison.OrdinalIgnoreCase) &&
                        o.Any(property => string.Equals(property.Key, "targetPath", StringComparison.OrdinalIgnoreCase))) continue;
                    if (string.Equals(kv.Key, "tagId", StringComparison.OrdinalIgnoreCase) &&
                        o.Any(property => string.Equals(property.Key, "source", StringComparison.OrdinalIgnoreCase)) && !tagSource) continue;
                    bool isReference = RefKeys.Contains(kv.Key) || (tagSource && string.Equals(kv.Key, "path", StringComparison.OrdinalIgnoreCase));
                    if (isReference && kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    {
                        var repl = onRef(kv.Key, s);
                        if (repl != null) o[kv.Key] = repl;
                    }
                    else if (RefKeys.Contains(kv.Key) && kv.Value is JsonArray refs)
                    {
                        for (int i = 0; i < refs.Count; i++)
                            if (refs[i] is JsonValue item && item.TryGetValue<string>(out var path) && !string.IsNullOrEmpty(path))
                            {
                                var repl = onRef(kv.Key, path);
                                if (repl != null) refs[i] = repl;
                            }
                    }
                    else Walk(kv.Value, onRef);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, onRef);
                break;
        }
    }

    private static MesTask Clone(MesTask t) =>
        JsonSerializer.Deserialize<MesTask>(JsonSerializer.Serialize(t, ProjectStore.Options), ProjectStore.Options)!;
}
