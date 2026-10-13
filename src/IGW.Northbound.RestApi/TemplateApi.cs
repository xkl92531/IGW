using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

/// <summary>模板化配置 REST:模板列表/提取/套用/导出/导入。改工程一律走「克隆当前工程 → 改动 → ReloadAsync(校验+保存+增量热重载)」。</summary>
public static class TemplateApi
{
    public static void MapTemplateApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        var jobs = new ValidationJobService();   // 验证作业内存态(应用生命周期,保留最近 N 份报告)

        // 发起验证作业:体 { devices:["通道.设备",...] } → { jobId }。异步执行,不阻塞。
        app.MapPost("/api/v1/validation/jobs", (ValidateBody body) =>
        {
            var devices = (body.Devices ?? new()).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (devices.Count == 0) return Results.BadRequest(new { error = "未指定要验证的设备。" });
            return Results.Ok(new { jobId = jobs.Start(host, devices) });
        });

        // 查询作业进度/结果
        app.MapGet("/api/v1/validation/jobs/{id}", (string id) =>
        {
            var snap = jobs.Snapshot(id);
            return snap is null ? Results.NotFound(new { error = "作业不存在或已过期。" }) : Results.Ok(snap);
        });

        // 收编 extra 点:勾选的真机多出点写入实例 Delta.Adds(设备根层),再热重载
        app.MapPost("/api/v1/validation/adopt", async (AdoptBody body) =>
        {
            var points = (body.Points ?? new())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new TagDef
                {
                    Name = p.Name!,
                    Address = p.Address ?? "",
                    DataType = Enum.TryParse<IgwDataType>(p.DataType, ignoreCase: true, out var dt) ? dt : IgwDataType.Int16,
                }).ToList();
            int adopted = 0;
            return await MutateAsync(host,
                proj => TemplateOps.AdoptExtras(proj, body.Channel ?? "", body.Device ?? "", points, out adopted),
                () => new { status = "adopted", count = adopted });
        });

        // 模板列表:名称/驱动/描述/点数/引用实例数(供管理板块;无模板时前端折叠空态)
        app.MapGet("/api/v1/templates", () =>
            Results.Ok(host.Project.Templates.Select(t => new
            {
                t.Name, t.DriverId, t.Description,
                tagCount = CountTags(t.Tags, t.Groups),
                instanceCount = CountInstances(host.Project, t.Name),
            })));

        // 删除模板(仍有实例引用则拒绝)
        app.MapDelete("/api/v1/templates/{name}", async (string name) =>
            await MutateAsync(host, proj => TemplateOps.Delete(proj, name), () => new { status = "deleted", template = name }));

        // 更新模板内容(体=DeviceTemplate JSON,用 ProjectStore.Options 含枚举字符串)。改点经热重载传播到全部引用实例。
        app.MapPut("/api/v1/templates/{name}", async (string name, HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            DeviceTemplate? tmpl;
            try { tmpl = JsonSerializer.Deserialize<DeviceTemplate>(json, ProjectStore.Options); }
            catch (Exception ex) { return Results.BadRequest(new { error = "模板 JSON 解析失败:" + ex.Message }); }
            if (tmpl is null) return Results.BadRequest(new { error = "空模板" });
            return await MutateAsync(host, proj => TemplateOps.Update(proj, name, tmpl), () => new { status = "updated", template = name });
        });

        // 导出单模板为 JSON(下载;用 ProjectStore.Options 保证枚举字符串化,可再导入)
        app.MapGet("/api/v1/templates/{name}/export", (string name) =>
        {
            var t = host.Project.Templates.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (t is null) return Results.NotFound(new { error = $"模板不存在:{name}" });
            return Results.Text(JsonSerializer.Serialize(t, ProjectStore.Options), "application/json", System.Text.Encoding.UTF8);
        });

        // 提取模板:从 channel.device 生成模板(剥离端点等实例设置);源设备不变;重名拒绝
        app.MapPost("/api/v1/templates/extract", async (ExtractBody body) =>
            await MutateAsync(host,
                proj => TemplateOps.Extract(proj, body.Channel ?? "", body.Device ?? "", body.TemplateName ?? ""),
                () => new { status = "extracted", template = body.TemplateName }));

        // 套用创建:模板名+通道+设备名+端点/设置 → 新增模板实例设备;驱动不匹配/重名拒绝
        app.MapPost("/api/v1/templates/apply", async (ApplyBody body) =>
            await MutateAsync(host,
                proj => TemplateOps.Apply(proj, body.TemplateName ?? "", body.Channel ?? "", body.DeviceName ?? "",
                    body.Endpoint ?? "", body.Settings, body.Description),
                () => new { status = "applied", device = body.DeviceName }));

        // 导入模板 JSON(重名拒绝,用户改名重试)。用 ProjectStore.Options 读体(含枚举字符串)。
        app.MapPost("/api/v1/templates/import", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            DeviceTemplate? tmpl;
            try { tmpl = JsonSerializer.Deserialize<DeviceTemplate>(json, ProjectStore.Options); }
            catch (Exception ex) { return Results.BadRequest(new { error = "模板 JSON 解析失败:" + ex.Message }); }
            if (tmpl is null) return Results.BadRequest(new { error = "空模板" });
            return await MutateAsync(host, proj => TemplateOps.Import(proj, tmpl), () => new { status = "imported", template = tmpl.Name });
        });

        // ---- MES 流程模板(印章语义:套用即拷贝断开) ----

        // 模板面板只读取可套用边界，不把百万点工程下载到浏览器。
        app.MapGet("/api/v1/mes/flow-template-targets", () =>
            Results.Ok(BuildFlowTemplateTargets(host.Project)));

        // 列表:名称/描述/源组/节点数(供画布模板面板)
        app.MapGet("/api/v1/mes/flow-templates", () =>
            Results.Ok((host.Project.MesUpload?.FlowTemplates ?? new()).Select(t => new
            {
                t.Name, t.Description, t.SourceGroupRef,
                nodeCount = t.Task.Flow?.Nodes.Count ?? 0,
                fieldCount = t.Task.Flow?.Nodes.Sum(n => n.Payload.Fields.Count + n.Payload.Structs.Count) ?? 0,
            })));

        // 提取:画布当前任务 → 流程模板(重名拒绝;源任务不变)
        app.MapPost("/api/v1/mes/flow-templates/extract", async (FlowExtractBody body) =>
            await MutateAsync(host,
                proj => MesFlowTemplateOps.Extract(proj, body.Task ?? "", body.Name ?? "", body.Description),
                () => new { status = "extracted", template = body.Name }));

        // 套用:模板+目标主标记组 → 新任务(默认停用)+ 报告(missing/external,不阻断)
        app.MapPost("/api/v1/mes/flow-templates/apply", async (FlowApplyBody body) =>
        {
            FlowApplyReport? report = null;
            var result = await MutateAsync(host,
                proj => MesFlowTemplateOps.Apply(proj, body.TemplateName ?? "", body.TargetGroup ?? "", body.TaskName,
                    body.ReplaceExisting, out report),
                () => new { status = "applied", task = report!.TaskName, report });
            return result;
        });

        // 批量套用:全部目标校验通过才一次写入(原子);任一非法 → 400 逐组错误
        app.MapPost("/api/v1/mes/flow-templates/apply-bulk", async (FlowApplyBulkBody body) =>
        {
            var proj = ProjectStore.Clone(host.Project);
            var (errors, reports) = MesFlowTemplateOps.ApplyBulk(proj, body.TemplateName ?? "", body.TargetGroups ?? new());
            if (errors.Count > 0)
                return Results.BadRequest(new { error = "批量套用未执行:存在非法目标组。", groups = errors });
            try { await host.ReloadAsync(proj); }
            catch (Exception ex) { return Results.Json(new { error = "应用失败:" + ex.Message }, statusCode: 500); }
            return Results.Ok(new { status = "applied", count = reports.Count, reports });
        });

        // 删除流程模板(印章语义:已套用的任务不受影响)
        app.MapDelete("/api/v1/mes/flow-templates/{name}", async (string name) =>
            await MutateAsync(host, proj =>
            {
                var list = proj.MesUpload?.FlowTemplates;
                var t = list?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (t is null) return $"流程模板不存在:{name}。";
                list!.Remove(t);
                return null;
            }, () => new { status = "deleted", template = name }));

        // 导出单模板 JSON(可再导入)
        app.MapGet("/api/v1/mes/flow-templates/{name}/export", (string name) =>
        {
            var t = (host.Project.MesUpload?.FlowTemplates ?? new()).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (t is null) return Results.NotFound(new { error = $"流程模板不存在:{name}" });
            return Results.Text(JsonSerializer.Serialize(t, ProjectStore.Options), "application/json", System.Text.Encoding.UTF8);
        });

        // 导入模板 JSON(重名拒绝)
        app.MapPost("/api/v1/mes/flow-templates/import", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            MesFlowTemplate? tmpl;
            try { tmpl = JsonSerializer.Deserialize<MesFlowTemplate>(json, ProjectStore.Options); }
            catch (Exception ex) { return Results.BadRequest(new { error = "模板 JSON 解析失败:" + ex.Message }); }
            if (tmpl is null || string.IsNullOrWhiteSpace(tmpl.Name)) return Results.BadRequest(new { error = "模板缺少名称" });
            return await MutateAsync(host, proj =>
            {
                var mes = proj.MesUpload ??= new MesUploadSettings();
                if (mes.FlowTemplates.Any(x => string.Equals(x.Name, tmpl.Name, StringComparison.OrdinalIgnoreCase)))
                    return $"流程模板名已存在:{tmpl.Name}(请改名后重试)。";
                mes.FlowTemplates.Add(tmpl);
                return null;
            }, () => new { status = "imported", template = tmpl.Name });
        });

        // CSV 样例模板下载
        app.MapGet("/api/v1/templates/import-csv/sample", () =>
            Results.Text(CsvImport.SampleCsv(), "text/csv", System.Text.Encoding.UTF8));

        // CSV 批量导入:体=CSV 文本(UTF-8)。全量校验通过→原子写入并热重载;任一行错→400 + 逐行错误报告(不写入任何设备)。
        app.MapPost("/api/v1/templates/import-csv", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body, System.Text.Encoding.UTF8);
            var csv = await reader.ReadToEndAsync();
            var result = CsvImport.Build(host.Project, csv);
            if (!result.Ok)
                return Results.BadRequest(new { error = "导入未执行:清单存在错误,请修正后重试。", rows = result.Errors.Select(e => new { line = e.Line, message = e.Message }) });
            try { await host.ReloadAsync(result.Project!); }
            catch (Exception ex) { return Results.Json(new { error = "应用失败:" + ex.Message }, statusCode: 500); }
            return Results.Ok(new { status = "imported", count = result.Count, devices = result.Devices });
        });
    }

    // 克隆当前工程 → 改动(op 返回 null=成功/非 null=错误)→ 成功则 ReloadAsync(校验+保存+热重载)
    internal static async Task<IResult> MutateAsync(IGatewayHost host, Func<IgwProject, string?> op, Func<object> ok)
    {
        var proj = ProjectStore.Clone(host.Project);
        var err = op(proj);
        if (err != null) return Results.BadRequest(new { error = err });
        try { await host.ReloadAsync(proj); }
        catch (Exception ex) { return Results.Json(new { error = "应用失败:" + ex.Message }, statusCode: 500); }
        return Results.Ok(ok());
    }

    private static int CountTags(List<TagDef> tags, List<TagGroupDef> groups) =>
        tags.Count + groups.Sum(g => CountTags(g.Tags, g.SubGroups));

    private static int CountInstances(IgwProject proj, string tmplName) =>
        proj.Channels.Sum(c => c.Devices.Count(d => string.Equals(d.TemplateRef, tmplName, StringComparison.OrdinalIgnoreCase)));

    internal static FlowTemplateTargetCatalog BuildFlowTemplateTargets(IgwProject project)
    {
        Dictionary<string, MesTask> tasks = (project.MesUpload?.Tasks ?? [])
            .Where(task => !string.IsNullOrWhiteSpace(task.GroupRef))
            .Select(task => new { Key = task.GroupRef!, Task = task })
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Task, StringComparer.OrdinalIgnoreCase);
        var targets = new List<FlowTemplateTarget>();
        foreach (ChannelDef channel in project.Channels)
        foreach (DeviceDef configured in channel.Devices)
        {
            DeviceDef device = TemplateExpander.ExpandInProject(project, configured);
            string devicePath = channel.Name + "." + device.Name;
            foreach (TagGroupDef group in device.Groups)
            {
                var totals = new Dictionary<TagGroupDef, long>();
                CountSubtree(group, totals);
                AddTargets(group, devicePath, channel.DriverId, configured.TemplateRef, 0, false, false, totals, tasks, targets);
            }
        }
        return new FlowTemplateTargetCatalog(
            targets,
            project.Templates.Select(template => template.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static long CountSubtree(TagGroupDef group, Dictionary<TagGroupDef, long> totals)
    {
        long total = group.Tags.Count;
        foreach (TagGroupDef child in group.SubGroups) total += CountSubtree(child, totals);
        totals[group] = total;
        return total;
    }

    private static void AddTargets(
        TagGroupDef group,
        string parentPath,
        string driverId,
        string? templateRef,
        int depth,
        bool ancestorHasDirectTags,
        bool parentIsPrimary,
        IReadOnlyDictionary<TagGroupDef, long> totals,
        IReadOnlyDictionary<string, MesTask> tasks,
        List<FlowTemplateTarget> output)
    {
        string path = parentPath + "." + group.Name;
        bool hasDirectTags = group.Tags.Count > 0;
        bool primary = totals[group] > 0 && (driverId == "OpcUa.Client"
            ? depth == 0
            : hasDirectTags && !ancestorHasDirectTags);
        if (primary || parentIsPrimary)
        {
            tasks.TryGetValue(path, out MesTask? task);
            output.Add(new FlowTemplateTarget(
                path,
                task is not null,
                task is not null && ((task.Flow?.Nodes.Count ?? 0) > 0 || (task.Flow?.Edges.Count ?? 0) > 0),
                templateRef));
        }
        foreach (TagGroupDef child in group.SubGroups)
            AddTargets(child, path, driverId, templateRef, depth + 1,
                ancestorHasDirectTags || hasDirectTags, primary, totals, tasks, output);
    }

    public sealed record FlowTemplateTarget(string Path, bool HasTask, bool HasFlow, string? TemplateRef);
    public sealed record FlowTemplateTargetCatalog(
        IReadOnlyList<FlowTemplateTarget> Groups,
        IReadOnlyList<string> DeviceTemplates);

    public sealed record ExtractBody(string? Channel, string? Device, string? TemplateName);
    public sealed record ApplyBody(string? TemplateName, string? Channel, string? DeviceName, string? Endpoint,
        Dictionary<string, string>? Settings, string? Description);
    public sealed record FlowExtractBody(string? Task, string? Name, string? Description);
    public sealed record FlowApplyBody(string? TemplateName, string? TargetGroup, string? TaskName,
        bool ReplaceExisting = false);
    public sealed record FlowApplyBulkBody(string? TemplateName, List<string>? TargetGroups);
    public sealed record ValidateBody(List<string>? Devices);
    public sealed record AdoptBody(string? Channel, string? Device, List<AdoptPoint>? Points);
    public sealed record AdoptPoint(string? Name, string? Address, string? DataType);
}
