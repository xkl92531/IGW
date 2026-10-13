using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

/// <summary>百万点工程的按需树、分页列表和乐观并发局部修改接口。</summary>
public static class ProjectConfigurationApi
{
    internal const long FullProjectTagLimit = 50_000;

    public static void MapProjectConfigurationApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        IProjectRepository Repository() => host.ProjectRepository
            ?? throw new InvalidOperationException("当前宿主没有配置工程仓储。");

        app.MapGet("/api/v1/project/summary", async (HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                ProjectTreeNode root = await repository.GetRootAsync(ct).ConfigureAwait(false);
                long totalTagCount = await repository.GetTagCountAsync(ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Json(new
                {
                    root,
                    totalTagCount,
                    fullProjectSupported = totalTagCount <= FullProjectTagLimit,
                    configurationDegraded = host.ConfigurationDegraded,
                    host.ConfigurationDegradedMessage,
                    scopedApplyFallbacks = host.ScopedApplyFallbacks,
                }, ProjectStore.Options);
            }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapGet("/api/v1/project/settings", async (HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                ProjectDocument settings = await repository.GetDocumentAsync(
                    ProjectDocumentKind.ProjectSection, "settings", ct).ConfigureAwait(false);
                ProjectDocument opcUa = await repository.GetDocumentAsync(
                    ProjectDocumentKind.ProjectSection, "opcUa", ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Json(new
                {
                    settings = JsonSerializer.Deserialize<JsonElement>(settings.DocumentJson),
                    opcUa = JsonSerializer.Deserialize<JsonElement>(opcUa.DocumentJson),
                }, ProjectStore.Options);
            }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        // 大工程按需加载:只给通道/设备骨架(不含组和点位),组和点位在展开/选中时再取。
        app.MapGet("/api/v1/project/shell", (HttpResponse response) =>
        {
            IgwProject live = host.Project;
            string revision = host.ProjectRevision;
            var lazy = new Dictionary<string, LazyCounts>(StringComparer.Ordinal);
            var channels = new List<ChannelDef>(live.Channels.Count);
            foreach (ChannelDef channel in live.Channels)
            {
                var devices = new List<DeviceDef>(channel.Devices.Count);
                foreach (DeviceDef device in channel.Devices)
                {
                    devices.Add(DeviceHeader(device));
                    lazy[device.Id] = new LazyCounts(device.Groups.Count, device.Tags.Count,
                        device.Tags.Count + device.Groups.Sum(TotalTags));
                }
                channels.Add(new ChannelDef
                {
                    Id = channel.Id, Name = channel.Name, DriverId = channel.DriverId, Description = channel.Description,
                    Settings = new Dictionary<string, string>(channel.Settings), KepProperties = channel.KepProperties,
                    Devices = devices,
                });
            }
            var shallow = new IgwProject
            {
                SchemaVersion = live.SchemaVersion, Name = live.Name, Description = live.Description,
                KepProperties = live.KepProperties, Settings = live.Settings, OpcUa = live.OpcUa, History = live.History,
                MqttPublish = live.MqttPublish, ComputedTags = live.ComputedTags, Schedules = live.Schedules,
                Security = live.Security, MesUpload = live.MesUpload, Templates = live.Templates, Channels = channels,
            };
            response.Headers.ETag = revision;
            return Results.Json(new { project = ProjectConfigurationRedactor.Create(shallow), lazy }, ProjectStore.Options);
        });

        // 设备或组的直接下级:子组只给表头(下级再按需取)+ 本层全部点位。
        // deep=true 返回完整子树(复制/剪切/导入等需要整块内容的操作)。
        app.MapGet("/api/v1/project/children/{kind}/{id}", (string kind, string id, bool? deep, HttpResponse response) =>
        {
            if (!TryParseNodeKind(kind, out ProjectNodeKind nodeKind) || nodeKind is not (ProjectNodeKind.Device or ProjectNodeKind.Group))
                return Results.BadRequest(new { error = "invalid_node_kind" });
            IgwProject live = host.Project;
            string revision = host.ProjectRevision;
            (List<TagGroupDef> Groups, List<TagDef> Tags) children;
            if (nodeKind == ProjectNodeKind.Device
                && live.Channels.SelectMany(c => c.Devices).FirstOrDefault(d => d.Id == id) is { } device)
                children = (device.Groups, device.Tags);
            else if (nodeKind == ProjectNodeKind.Group && FindGroup(live, id) is { } group)
                children = (group.SubGroups, group.Tags);
            else
                return Results.NotFound(new { error = $"节点 {id} 不存在或已被删除。" });
            var lazy = new Dictionary<string, LazyCounts>(StringComparer.Ordinal);
            if (deep != true)
                foreach (TagGroupDef sub in children.Groups)
                    lazy[sub.Id] = new LazyCounts(sub.SubGroups.Count, sub.Tags.Count, TotalTags(sub));
            response.Headers.ETag = revision;
            return Results.Json(new
            {
                groups = deep == true ? children.Groups : children.Groups.Select(GroupHeader).ToList(),
                tags = children.Tags,
                lazy,
            }, ProjectStore.Options);
        });

        app.MapGet("/api/v1/project/tree/{kind}/{id}/children", async (
            string kind, string id, HttpResponse response, CancellationToken ct) =>
        {
            if (!TryParseNodeKind(kind, out ProjectNodeKind nodeKind))
                return Results.BadRequest(new { error = "invalid_node_kind" });
            try
            {
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                IReadOnlyList<ProjectTreeNode> children = await repository.GetChildrenAsync(
                    new ProjectNodeRef(nodeKind, id), ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Json(children, ProjectStore.Options);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapGet("/api/v1/project/tags", async (
            string? groupId, string? deviceId, string? q, int? offset, int? limit, HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                var query = new ProjectTagQuery(groupId, q, offset ?? 0, limit ?? 200, deviceId).Validate();
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                ProjectPage<ProjectTagRow> page = await repository.GetTagsAsync(query, ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Json(page, ProjectStore.Options);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapGet("/api/v1/project/mes-tasks", async (
            string? scope, string? deviceId, string? channelId, string? q,
            int? offset, int? limit, bool? enabled, HttpResponse response, CancellationToken ct) =>
        {
            if (!TryParseMesScope(scope, out ProjectMesTaskScope parsedScope))
                return Results.BadRequest(new { error = "scope 只能是 all、device 或 channel。" });
            try
            {
                var query = new ProjectMesTaskQuery(
                    parsedScope, deviceId, channelId, q, offset ?? 0, limit ?? 200, enabled).Validate();
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                ProjectPage<ProjectMesTaskRow> page = await repository.GetMesTasksAsync(query, ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Json(page, ProjectStore.Options);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapGet("/api/v1/project/mes-tasks/{id}", async (
            string id, HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                ProjectDocument document = await repository.GetDocumentAsync(
                    ProjectDocumentKind.MesTask, id, ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Text(document.DocumentJson, "application/json", System.Text.Encoding.UTF8);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapGet("/api/v1/project/entity/{kind}/{id}", async (
            string kind, string id, HttpResponse response, CancellationToken ct) =>
        {
            if (!TryParseNodeKind(kind, out ProjectNodeKind nodeKind))
                return Results.BadRequest(new { error = "invalid_node_kind" });
            try
            {
                IProjectRepository repository = Repository();
                ProjectRevision revision = await repository.GetRevisionAsync(ct).ConfigureAwait(false);
                string json = await repository.GetEntityDocumentAsync(
                    new ProjectNodeRef(nodeKind, id), ct).ConfigureAwait(false);
                SetRevision(response, revision);
                return Results.Text(json, "application/json", System.Text.Encoding.UTF8);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return RepositoryFailure(ex); }
        });

        app.MapPost("/api/v1/project/mutations", async (
            HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            string? expected = request.Headers.IfMatch.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(expected))
                return Results.Json(new { error = "if_match_required" }, statusCode: StatusCodes.Status428PreconditionRequired);
            if (!ProjectRevision.TryParse(expected, out _))
                return Results.BadRequest(new { error = "invalid_if_match" });

            ProjectMutation? mutation;
            try
            {
                mutation = await JsonSerializer.DeserializeAsync<ProjectMutation>(
                    request.Body, ProjectStore.Options, ct).ConfigureAwait(false);
            }
            catch (JsonException ex) { return Results.BadRequest(new { error = "invalid_mutation", detail = ex.Message }); }
            if (mutation is null) return Results.BadRequest(new { error = "empty_mutation" });

            ProjectUpdateResult result;
            try
            {
                mutation = ProjectConfigurationRedactor.RestoreMutationSecrets(host.Project, mutation);
                result = await host.ApplyProjectMutationAsync(mutation, expected, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = "mutation_failed", detail = ex.Message }, statusCode: 500); }

            response.Headers.ETag = result.Revision;
            if (result.Conflict)
                return Results.Json(new { error = "revision_conflict", revision = result.Revision }, statusCode: 409);
            if (!result.Applied)
                return Results.BadRequest(new { error = result.Error ?? "mutation_rejected", detail = result.Detail, revision = result.Revision });
            // mesTasksReconciled：局部修改删掉了 MES 任务绑定的组，服务端已清理失效任务，客户端应重新取 MES 配置
            return Results.Ok(new { applied = true, revision = result.Revision, mesTasksReconciled = result.MesTasksReconciled });
        });
    }

    private sealed record LazyCounts(int Groups, int Tags, long TotalTags);

    private static long TotalTags(TagGroupDef group)
    {
        long total = group.Tags.Count;
        foreach (TagGroupDef sub in group.SubGroups) total += TotalTags(sub);
        return total;
    }

    private static TagGroupDef? FindGroup(IgwProject project, string id)
    {
        foreach (ChannelDef channel in project.Channels)
            foreach (DeviceDef device in channel.Devices)
                if (FindGroup(device.Groups, id) is { } found) return found;
        return null;
    }

    private static TagGroupDef? FindGroup(List<TagGroupDef> groups, string id)
    {
        foreach (TagGroupDef group in groups)
        {
            if (group.Id == id) return group;
            if (FindGroup(group.SubGroups, id) is { } found) return found;
        }
        return null;
    }

    private static TagGroupDef GroupHeader(TagGroupDef group) => new()
    {
        Id = group.Id, Name = group.Name, Description = group.Description, KepProperties = group.KepProperties,
    };

    private static DeviceDef DeviceHeader(DeviceDef device) => new()
    {
        Id = device.Id, Name = device.Name, Endpoint = device.Endpoint, Description = device.Description,
        Settings = new Dictionary<string, string>(device.Settings), KepProperties = device.KepProperties,
        TemplateRef = device.TemplateRef, Delta = device.Delta,
    };

    private static void SetRevision(HttpResponse response, ProjectRevision revision) =>
        response.Headers.ETag = revision.ETag;

    private static bool TryParseNodeKind(string value, out ProjectNodeKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind);

    private static bool TryParseMesScope(string? value, out ProjectMesTaskScope scope)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            scope = ProjectMesTaskScope.All;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out scope);
    }

    private static IResult RepositoryFailure(Exception exception) =>
        Results.Json(new { error = "project_repository_unavailable", detail = exception.Message }, statusCode: 503);
}
