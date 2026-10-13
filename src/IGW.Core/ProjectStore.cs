using System.Text.Json;
using System.Text.Json.Serialization;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>工程文件(.igwproj)加载/保存。JSON,枚举存字符串,camelCase。</summary>
public static class ProjectStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IgwProject Load(string path) =>
        Load(path, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "igw.masterkey"));

    /// <summary>使用显式旧工程主密钥加载，供跨目录迁移时解密后再用新安装密钥落库。</summary>
    public static IgwProject Load(string path, string masterKeyPath)
    {
        var json = File.ReadAllText(path);
        var proj = JsonSerializer.Deserialize<IgwProject>(json, Options)
                   ?? throw new InvalidDataException("工程文件为空或无法解析。");
        RetiredProjectContent.RemoveOpenProtocolChannels(proj);
        var protector = new InstallationSecretProtector(Path.GetFullPath(masterKeyPath));
        ProjectSecrets.TransformReversibleSecrets(proj, protector.Unprotect);
        MesTaskTopology.Reconcile(proj);
        Validate(proj);
        return proj;
    }

    public static void Save(IgwProject project, string path)
    {
        Validate(project);
        var persisted = Clone(project);
        ProjectSecrets.HashPendingApiPasswords(persisted);
        var protector = ProtectorFor(path);
        ProjectSecrets.TransformReversibleSecrets(persisted, protector.Protect);
        Validate(persisted);

        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string? backupJson = ExistingEncryptedBackup(path, protector);
        File.WriteAllText(tmp, JsonSerializer.Serialize(persisted, Options));
        try
        {
            // 备份必须先准备成功；否则不能先替换主文件再向调用方报告保存失败，
            // 那会造成磁盘工程已变化而运行内存仍保持旧版本。
            if (backupJson is not null) WriteBackup(path + ".bak", backupJson);
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        ProjectSecrets.CopyApiPasswordHashes(persisted, project);
    }

    public static string ToJson(IgwProject project) => JsonSerializer.Serialize(project, Options);

    /// <summary>深拷贝(JSON 回环):REST 端点改工程前先克隆当前工程,避免就地改动 live 对象。</summary>
    public static IgwProject Clone(IgwProject project) =>
        JsonSerializer.Deserialize<IgwProject>(JsonSerializer.Serialize(project, Options), Options)!;

    private static InstallationSecretProtector ProtectorFor(string projectPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        return new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"));
    }

    private static string? ExistingEncryptedBackup(string path, InstallationSecretProtector protector)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var existing = JsonSerializer.Deserialize<IgwProject>(File.ReadAllText(path), Options);
            if (existing is null) return null;
            ProjectSecrets.TransformReversibleSecrets(existing, protector.Unprotect);
            ProjectSecrets.HashPendingApiPasswords(existing);
            ProjectSecrets.TransformReversibleSecrets(existing, protector.Protect);
            return JsonSerializer.Serialize(existing, Options);
        }
        catch
        {
            // 主工程仍以原子替换保存；无法安全解析的旧文件不复制成可能含明文密钥的备份。
            return null;
        }
    }

    private static void WriteBackup(string path, string json)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>
    /// 创建只替换 MES 配置的工程快照。未变化的工程拓扑沿用现有只读对象，避免 MES 画布保存时
    /// 对几十万/上百万点位做一次无意义的深拷贝；传入的 MES 配置来自独立请求体，不与 live 对象共享。
    /// </summary>
    public static IgwProject WithMesUpload(IgwProject current, MesUploadSettings mesUpload) => new()
    {
        SchemaVersion = current.SchemaVersion,
        Name = current.Name,
        Description = current.Description,
        Settings = current.Settings,
        OpcUa = current.OpcUa,
        History = current.History,
        MqttPublish = current.MqttPublish,
        ComputedTags = current.ComputedTags,
        Schedules = current.Schedules,
        Security = current.Security,
        MesUpload = mesUpload,
        Templates = current.Templates,
        Channels = current.Channels,
    };

    /// <summary>MES 专用保存只比较 MES 自身数量，不遍历未变化的百万点工程拓扑。</summary>
    public static IReadOnlyList<string> MesDestructiveChanges(MesUploadSettings current, MesUploadSettings next)
    {
        var changes = new List<string>(2);
        if ((next.Tasks?.Count ?? 0) < (current.Tasks?.Count ?? 0))
            changes.Add($"MES 任务 {current.Tasks?.Count ?? 0}→{next.Tasks?.Count ?? 0}");
        if ((next.FlowTemplates?.Count ?? 0) < (current.FlowTemplates?.Count ?? 0))
            changes.Add($"流程模板 {current.FlowTemplates?.Count ?? 0}→{next.FlowTemplates?.Count ?? 0}");
        return changes;
    }

    /// <summary>
    /// 返回新工程相对当前工程减少的关键配置数量。后台自动保存遇到减少时必须停下，
    /// 由用户通过显式“保存并应用”确认，避免陈旧页面静默覆盖整批通道或点位。
    /// </summary>
    public static IReadOnlyList<string> DestructiveChanges(IgwProject current, IgwProject next)
    {
        static (int Devices, int Groups, int Tags) Topology(IgwProject p)
        {
            int devices = 0, groups = 0, tags = 0;
            void WalkGroups(IEnumerable<TagGroupDef>? source)
            {
                foreach (var group in source ?? Array.Empty<TagGroupDef>())
                {
                    groups++;
                    tags += group.Tags?.Count ?? 0;
                    WalkGroups(group.SubGroups);
                }
            }
            foreach (var channel in p.Channels ?? new())
                foreach (var device in channel.Devices ?? new())
                {
                    devices++;
                    tags += device.Tags?.Count ?? 0;
                    WalkGroups(device.Groups);
                }
            return (devices, groups, tags);
        }

        var before = Topology(current);
        var after = Topology(next);
        var changes = new List<string>();
        static void AddIfLess(List<string> output, string label, int oldCount, int newCount)
        {
            if (newCount < oldCount) output.Add($"{label} {oldCount}→{newCount}");
        }
        AddIfLess(changes, "通道", current.Channels?.Count ?? 0, next.Channels?.Count ?? 0);
        AddIfLess(changes, "设备", before.Devices, after.Devices);
        AddIfLess(changes, "标记组", before.Groups, after.Groups);
        AddIfLess(changes, "点位", before.Tags, after.Tags);
        AddIfLess(changes, "MES 任务", current.MesUpload?.Tasks?.Count ?? 0, next.MesUpload?.Tasks?.Count ?? 0);
        AddIfLess(changes, "设备模板", current.Templates?.Count ?? 0, next.Templates?.Count ?? 0);
        AddIfLess(changes, "流程模板", current.MesUpload?.FlowTemplates?.Count ?? 0, next.MesUpload?.FlowTemplates?.Count ?? 0);
        return changes;
    }

    /// <summary>校验结果:错误(阻断保存/加载)与警告(放行,仅提示,如悬空模板差异)。</summary>
    public sealed record ValidationReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool Ok => Errors.Count == 0;
        public static readonly ValidationReport Empty = new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>校验并在有错误时抛出(阻断);警告不阻断(由 <see cref="Check"/> 暴露给上层提示)。</summary>
    public static void Validate(IgwProject p)
    {
        var r = Check(p);
        if (!r.Ok) throw new InvalidDataException(string.Join(" ", r.Errors));
    }

    /// <summary>全量校验:收集所有错误与警告(不抛异常)。通道/设备命名唯一性 + 模板引用/驱动/差异一致性。</summary>
    public static ValidationReport Check(IgwProject p)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        p.Channels ??= new();
        p.Templates ??= new();
        if (p.Settings.LogRetentionDays is < 1 or > 3650)
            errors.Add("日志保留天数必须在 1 到 3650 天之间。");
        CheckTagStreamSettings(p.Settings.TagStream ??= new TagStreamSettings(), errors);

        try
        {
            ArrayProjectValidator.NormalizeAndValidate(p);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(ex.Message);
        }

        // ---- 模板:名称唯一、命名/驱动非空 ----
        var tmplByName = new Dictionary<string, DeviceTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in p.Templates)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) { errors.Add("存在未命名的模板。"); continue; }
            if (string.IsNullOrWhiteSpace(t.DriverId))
                errors.Add($"模板 {t.Name} 未指定 driverId。");
            if (!tmplByName.TryAdd(t.Name, t))
                errors.Add($"模板名重复:{t.Name}(模板名必须唯一)。");
        }

        // ---- MES 流程模板:名称唯一(印章语义,任务不引用模板,无需引用校验) ----
        var flowTplSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in p.MesUpload?.FlowTemplates ?? new())
        {
            if (string.IsNullOrWhiteSpace(ft.Name)) { errors.Add("存在未命名的流程模板。"); continue; }
            if (!flowTplSeen.Add(ft.Name))
                errors.Add($"流程模板名重复:{ft.Name}(流程模板名必须唯一)。");
        }

        // ---- 通道/设备:命名唯一 + 模板实例引用/驱动/差异 ----
        var chSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in p.Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Name)) { errors.Add("存在未命名的通道。"); continue; }
            if (string.IsNullOrWhiteSpace(ch.DriverId))
                errors.Add($"通道 {ch.Name} 未指定 driverId。");
            if (!chSeen.Add(ch.Name))
                errors.Add($"通道名重复:{ch.Name}(通道名必须唯一)。");
            var devSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in ch.Devices ?? new())
            {
                if (string.IsNullOrWhiteSpace(d.Name)) { errors.Add($"通道 {ch.Name} 下存在未命名的设备。"); continue; }
                if (!devSeen.Add(d.Name))
                    errors.Add($"通道 {ch.Name} 下设备名重复:{d.Name}(同通道设备名必须唯一)。");
                if (!string.IsNullOrWhiteSpace(d.TemplateRef))
                    CheckTemplateInstance(ch, d, tmplByName, errors, warnings);
            }
        }
        CheckWriteConfirmations(p, errors);
        CheckMesTaskEnablement(p.MesUpload, errors);
        MesDataRangeValidator.Check(p, errors);
        foreach (var task in p.MesUpload?.Tasks ?? [])
            CheckMesExtraFields(p, task, $"MES 任务 {task.Name}", errors);
        foreach (var template in p.MesUpload?.FlowTemplates ?? [])
            CheckMesExtraFields(p, template.Task, $"MES 流程模板 {template.Name}", errors);
        return new ValidationReport(errors, warnings);
    }

    private static void CheckMesExtraFields(IgwProject project, MesTask task, string owner, List<string> errors)
    {
        MesDataRangeCatalog? routingCatalog = null;
        IEnumerable<string> Elements(MesStruct item, string path)
        {
            if (item.CountMode == "Enum")
            {
                routingCatalog ??= MesDataRangeCatalog.Build(project);
                if (routingCatalog.TryGetIndexedArray(path, out var array))
                    return array.Elements.Where(element => element.Index >= item.StartIndex &&
                        (long)element.Index < (long)item.StartIndex + Math.Max(0, item.FixedCount)).Select(element => element.Group.SourcePath);
                return [];
            }
            return MesParameterRouting.FixedElementPaths(item, path);
        }
        // 含本版本不支持配置的旧任务由 MES 引擎停用并给出原因，校验不拒绝，保证旧工程能加载。
        if (MesUnsupportedConfig.Describe(task) is not null) return;
        try { MesFlowCommandValidation.Validate(task); }
        catch (InvalidDataException ex) { errors.Add($"{owner}: {ex.Message}"); }
        CheckPayload(task.Payload, owner, MesParameterRouting.UsesAutomaticTaskPayload(task));
        foreach (var node in task.Flow?.Nodes ?? [])
            if (string.Equals(node.Type, "Upload", StringComparison.OrdinalIgnoreCase))
                CheckPayload(node.Payload, $"{owner} 的上报节点 {node.Title}", !MesParameterRouting.HasExplicitTemplate(node.Payload, node.Http));

        void CheckPayload(MesPayload payload, string label, bool automatic)
        {
            try
            {
                MesFieldKeyModes.Validate(payload, Elements, automatic);
                MesExtraFieldValidation.Validate(payload, automatic);
            }
            catch (InvalidDataException ex) { errors.Add($"{label}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 设备级校验：<see cref="Check"/> 中针对单台设备的部分（数组点位规范化/布局、纯写点确认方式、确认点引用与循环）。
    /// 供批量注册在写库前校验候选设备，保证写入后整个工程仍能通过 <see cref="Validate"/>。
    /// 设备须为已展开的有效定义（非模板实例，或由调用方展开）。收集全部错误（不在第一个错误处停止）。
    /// 数组定义会像全量校验一样就地规范化：调用方须传入不与运行中工程共享的点/数组对象。
    /// </summary>
    public static IReadOnlyList<string> CheckDevice(ChannelDef channel, DeviceDef device)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(device);
        var errors = new List<string>();
        ArrayProjectValidator.CollectDeviceErrors(channel, device, errors);
        CheckDeviceWriteConfirmations(channel, device, device, errors);
        return errors;
    }

    private static void CheckWriteConfirmations(IgwProject project, List<string> errors)
    {
        foreach (var channel in project.Channels ?? [])
        foreach (var device in channel.Devices ?? [])
            CheckDeviceWriteConfirmations(channel, device, TemplateExpander.ExpandInProject(project, device), errors);
    }

    private static void CheckDeviceWriteConfirmations(
        ChannelDef channel, DeviceDef device, DeviceDef effective, List<string> errors)
    {
        // 没有纯写点也没有确认点引用时不会产生任何错误：跳过按路径建索引（十万点设备上省一次全量字符串拼接）
        if (!AnyTag(effective, static tag => tag.Access == TagAccess.Write
                || tag.WriteConfirmation?.Mode == WriteConfirmationMode.ConfirmationTag))
            return;
        var tags = EnumerateDeviceTags(effective, $"{channel.Name}.{device.Name}")
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Tag, StringComparer.Ordinal);
        var confirmationEdges = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, tag) in tags.Select(pair => (pair.Key, pair.Value)))
        {
            WriteConfirmationDef? confirmation = tag.WriteConfirmation;
            if (tag.Access == TagAccess.Write &&
                (confirmation is null || confirmation.Mode == WriteConfirmationMode.ReadBack))
            {
                errors.Add($"纯写点 {path} 必须配置确认方式（确认点或仅协议确认）。");
                continue;
            }

            if (confirmation?.Mode != WriteConfirmationMode.ConfirmationTag) continue;
            string target = confirmation.ConfirmationTagId?.Trim() ?? "";
            if (target.Length == 0)
            {
                errors.Add($"点位 {path} 的确认点路径不能为空。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(confirmation.ExpectedValue))
                errors.Add($"点位 {path} 的确认点期望值不能为空。");
            if (string.Equals(path, target, StringComparison.Ordinal))
            {
                errors.Add($"点位 {path} 的确认点不能指向自身。");
                continue;
            }
            if (!tags.TryGetValue(target, out TagDef? targetTag))
            {
                errors.Add($"点位 {path} 引用的确认点不存在或不在同一设备:{target}。");
                continue;
            }
            if (targetTag.Access == TagAccess.Write)
                errors.Add($"点位 {path} 引用的确认点不可读取:{target}。");
            confirmationEdges[path] = target;
        }

        foreach (string origin in confirmationEdges.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = origin;
            while (confirmationEdges.TryGetValue(current, out string? next))
            {
                if (!visited.Add(current))
                {
                    errors.Add($"设备 {channel.Name}.{device.Name} 的写入确认点存在循环引用:{origin}。");
                    break;
                }
                current = next;
            }
        }
    }

    private static bool AnyTag(DeviceDef device, Func<TagDef, bool> predicate)
    {
        foreach (TagDef tag in device.Tags ?? [])
            if (tag is not null && predicate(tag)) return true;
        return AnyGroupTag(device.Groups, predicate);
    }

    private static bool AnyGroupTag(IEnumerable<TagGroupDef>? groups, Func<TagDef, bool> predicate)
    {
        foreach (TagGroupDef group in groups ?? [])
        {
            foreach (TagDef tag in group.Tags ?? [])
                if (tag is not null && predicate(tag)) return true;
            if (AnyGroupTag(group.SubGroups, predicate)) return true;
        }
        return false;
    }

    private static IEnumerable<(string Path, TagDef Tag)> EnumerateDeviceTags(
        DeviceDef device,
        string prefix)
    {
        foreach (TagDef tag in device.Tags ?? [])
            if (!string.IsNullOrWhiteSpace(tag.Name))
                yield return ($"{prefix}.{tag.Name}", tag);
        foreach (var item in EnumerateGroupTags(device.Groups, prefix))
            yield return item;
    }

    private static IEnumerable<(string Path, TagDef Tag)> EnumerateGroupTags(
        IEnumerable<TagGroupDef>? groups,
        string prefix)
    {
        foreach (TagGroupDef group in groups ?? [])
        {
            string groupPath = $"{prefix}.{group.Name}";
            foreach (TagDef tag in group.Tags ?? [])
                if (!string.IsNullOrWhiteSpace(tag.Name))
                    yield return ($"{groupPath}.{tag.Name}", tag);
            foreach (var item in EnumerateGroupTags(group.SubGroups, groupPath))
                yield return item;
        }
    }

    private static void CheckTagStreamSettings(TagStreamSettings settings, List<string> errors)
    {
        if (settings.MaxTagsPerStream is < 1 or > 10_000)
            errors.Add("SSE 单流点位上限必须在 1 到 10000 之间。");
        if (settings.MaxStreams is < 1 or > 1_000)
            errors.Add("SSE 实时连接上限必须在 1 到 1000 之间。");
        if (settings.HeartbeatSeconds is < 5 or > 30)
            errors.Add("SSE 心跳秒数必须在 5 到 30 之间。");
        if (settings.SnapshotIntervalSeconds is < 15 or > 3_600)
            errors.Add("SSE 快照周期必须在 15 到 3600 秒之间。");
        if (settings.MaxConcurrentSnapshots is < 1 or > 32)
            errors.Add("SSE 并发快照数必须在 1 到 32 之间。");
        if (settings.DeltaBatchWindowMs is < 1 or > 100)
            errors.Add("SSE 增量批窗口必须在 1 到 100 毫秒之间。");
        if (settings.CoordinatorQueueCapacity is < 1_024 or > 16_777_216)
            errors.Add("SSE 协调器队列容量必须在 1024 到 16777216 之间。");
    }

    private static void CheckMesTaskEnablement(MesUploadSettings? mesUpload, List<string> errors)
    {
        var enabled = (mesUpload?.Tasks ?? new())
            .Where(task => task.FlowAvailable && task.Enabled && !string.IsNullOrWhiteSpace(task.GroupRef))
            .ToArray();
        for (int left = 0; left < enabled.Length; left++)
        for (int right = left + 1; right < enabled.Length; right++)
        {
            string leftRef = enabled[left].GroupRef!.Trim();
            string rightRef = enabled[right].GroupRef!.Trim();
            if (!IsAncestorGroup(leftRef, rightRef) && !IsAncestorGroup(rightRef, leftRef)) continue;
            errors.Add($"MES 父子采集任务不能同时启用:{leftRef} 与 {rightRef}。请停用其中一个任务。");
        }
    }

    private static bool IsAncestorGroup(string ancestor, string descendant) =>
        descendant.StartsWith(ancestor + ".", StringComparison.OrdinalIgnoreCase);

    // 模板实例:引用存在(错)、驱动与通道一致(错)、悬空覆盖/屏蔽(警告)、追加与模板点同名/重复(错)
    private static void CheckTemplateInstance(ChannelDef ch, DeviceDef d,
        Dictionary<string, DeviceTemplate> tmplByName, List<string> errors, List<string> warnings)
    {
        if (!tmplByName.TryGetValue(d.TemplateRef!, out var tmpl))
        {
            errors.Add($"设备 {ch.Name}.{d.Name} 引用的模板不存在:{d.TemplateRef}。");
            return;   // 模板缺失,无法解析差异路径,跳过后续差异校验
        }
        if (!string.Equals(tmpl.DriverId, ch.DriverId, StringComparison.OrdinalIgnoreCase))
            errors.Add($"设备 {ch.Name}.{d.Name} 的模板 {tmpl.Name} 驱动({tmpl.DriverId})与通道驱动({ch.DriverId})不一致。");

        var tmplPaths = new HashSet<string>(TemplateTagPaths(tmpl), StringComparer.OrdinalIgnoreCase);
        var delta = d.Delta;
        if (delta is null) return;

        foreach (var o in delta.Overrides)
            if (!string.IsNullOrWhiteSpace(o.Path) && !tmplPaths.Contains(o.Path))
                warnings.Add($"设备 {ch.Name}.{d.Name} 的覆盖指向模板中不存在的点:{o.Path}(已失效,展开时忽略)。");
        foreach (var m in delta.Masks)
            if (!string.IsNullOrWhiteSpace(m) && !tmplPaths.Contains(m))
                warnings.Add($"设备 {ch.Name}.{d.Name} 的屏蔽指向模板中不存在的点:{m}(已失效,展开时忽略)。");

        var maskSet = new HashSet<string>(delta.Masks.Where(m => !string.IsNullOrWhiteSpace(m)), StringComparer.OrdinalIgnoreCase);
        var addSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in delta.Adds)
        {
            if (a.Tag is null || string.IsNullOrWhiteSpace(a.Tag.Name)) continue;
            string path = string.IsNullOrWhiteSpace(a.GroupPath) ? a.Tag.Name : a.GroupPath + "." + a.Tag.Name;
            // 与未被屏蔽的模板点同名 → 错误(屏蔽后再追加同名 = 替换,允许)
            if (tmplPaths.Contains(path) && !maskSet.Contains(path))
                errors.Add($"设备 {ch.Name}.{d.Name} 的追加点与模板点同名:{path}(追加点不得与模板点重名)。");
            if (!addSeen.Add(path))
                errors.Add($"设备 {ch.Name}.{d.Name} 存在重复的追加点:{path}。");
        }
    }

    // 模板内全部点位路径(组.子组.点;根层点为点名),用于差异路径一致性校验
    private static IEnumerable<string> TemplateTagPaths(DeviceTemplate t)
    {
        foreach (var tag in t.Tags)
            if (!string.IsNullOrWhiteSpace(tag.Name)) yield return tag.Name;
        foreach (var path in GroupTagPaths(t.Groups, "")) yield return path;
    }

    private static IEnumerable<string> GroupTagPaths(List<TagGroupDef> groups, string parent)
    {
        foreach (var g in groups)
        {
            string gp = parent.Length == 0 ? g.Name : parent + "." + g.Name;
            foreach (var tag in g.Tags)
                if (!string.IsNullOrWhiteSpace(tag.Name)) yield return gp + "." + tag.Name;
            foreach (var path in GroupTagPaths(g.SubGroups, gp)) yield return path;
        }
    }
}
