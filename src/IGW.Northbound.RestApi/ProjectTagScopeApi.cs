using System.Globalization;
using System.Text;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

public sealed record ProjectTagGroupDto(string Name, string Path, string Kind, string? Description);

public sealed record ProjectTagDefinitionDto(
    string Id,
    string Name,
    string Address,
    string DataType,
    string Access,
    string? Description,
    string? EngUnit,
    int? StringLength,
    ArrayDef? Array);

public sealed record ProjectTagScopeResponse(
    string ProjectRevision,
    string RealtimeRevision,
    string Root,
    IReadOnlyList<ProjectTagGroupDto> Groups,
    IReadOnlyList<ProjectTagDefinitionDto> Definitions,
    string? NextCursor,
    int Total);

/// <summary>
/// Projects one exact project node at a time. Definitions are paged and only direct children
/// are returned, so a Quick Client never downloads or materializes a million-point project.
/// All path comparisons are ordinal because the runtime tag id is an exact identity.
/// </summary>
public static class ProjectTagScopeApi
{
    public static void MapProjectTagScopeApi(this IEndpointRouteBuilder app, IGatewayHost host) =>
        app.MapGet("/api/v1/project/tag-scope", async (
            string? root,
            string? q,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Json(host.ProjectRepository is { } repository
                    ? await SearchAsync(repository, host.ProjectRevision, host.RealtimeRevision,
                        root, q, cursor, limit, cancellationToken).ConfigureAwait(false)
                    : Search(host.Project, host.ProjectRevision, host.RealtimeRevision,
                        root, q, cursor, limit, cancellationToken), ProjectStore.Options);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

    public static async Task<ProjectTagScopeResponse> SearchAsync(
        IProjectRepository repository,
        string projectRevision,
        string realtimeRevision,
        string? root,
        string? query,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        int limit = requestedLimit ?? 1_000;
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(requestedLimit), "limit 必须在 1..1000 之间。");
        int offset = DecodeCursor(cursor, realtimeRevision);
        string exactRoot = root?.Trim() ?? "";
        ProjectNodeRef node = await LocateAsync(repository, exactRoot, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectTreeNode> children = await repository.GetChildrenAsync(node, cancellationToken)
            .ConfigureAwait(false);
        ProjectTagGroupDto[] groups = children
            .Where(child => child.Kind != ProjectNodeKind.Tag)
            .OrderBy(child => child.ActualPath, StringComparer.Ordinal)
            .Select(child => new ProjectTagGroupDto(
                child.Name, child.ActualPath, child.Kind.ToString(), child.Description))
            .ToArray();

        ProjectPage<ProjectNodeSearchRow> tags = node.Kind is ProjectNodeKind.Device or ProjectNodeKind.Group
            ? await repository.SearchNodesAsync(
                new ProjectNodeSearchQuery(exactRoot, query, ProjectNodeKind.Tag, null, true, offset, limit),
                cancellationToken).ConfigureAwait(false)
            : new ProjectPage<ProjectNodeSearchRow>([], 0, offset, limit);
        string? next = offset + tags.Items.Count < tags.Total
            ? EncodeCursor(offset + tags.Items.Count, realtimeRevision)
            : null;
        return new ProjectTagScopeResponse(
            projectRevision,
            realtimeRevision,
            exactRoot,
            groups,
            tags.Items.Select(ToDefinition).ToArray(),
            next,
            checked((int)Math.Min(tags.Total, int.MaxValue)));
    }

    public static Task<ProjectTagScopeResponse> SearchAsync(
        IProjectRepository repository,
        string projectRevision,
        string realtimeRevision,
        string? root,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default) =>
        SearchAsync(repository, projectRevision, realtimeRevision, root, null, cursor, requestedLimit, cancellationToken);

    private static async Task<ProjectNodeRef> LocateAsync(
        IProjectRepository repository,
        string root,
        CancellationToken cancellationToken)
    {
        ProjectTreeNode current = await repository.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (root.Length == 0) return new ProjectNodeRef(ProjectNodeKind.Project, current.Id);
        if (root.Contains("..") || root.EndsWith('.')) throw Missing(root);
        // 设备/组名可以含点(如 S1500_10.11.38.10,名字里带 IP),不能按点切分;
        // 逐层用「ActualPath 精确命中优先,否则最长 "路径." 前缀」贪心下降。
        ProjectNodeRef parent = new(ProjectNodeKind.Project, current.Id);
        while (true)
        {
            IReadOnlyList<ProjectTreeNode> children = await repository.GetChildrenAsync(parent, cancellationToken)
                .ConfigureAwait(false);
            ProjectTreeNode? match = children.FirstOrDefault(child =>
                string.Equals(child.ActualPath, root, StringComparison.Ordinal))
                ?? children.Where(child => root.StartsWith(child.ActualPath + ".", StringComparison.Ordinal))
                    .OrderByDescending(child => child.ActualPath.Length)
                    .FirstOrDefault();
            if (match is null) throw Missing(root);
            if (string.Equals(match.ActualPath, root, StringComparison.Ordinal))
                return new ProjectNodeRef(match.Kind, match.Id);
            parent = new ProjectNodeRef(match.Kind, match.Id);
        }
    }

    private static ProjectTagDefinitionDto ToDefinition(ProjectNodeSearchRow row) => new(
        row.ActualPath,
        row.Name,
        row.Address ?? "",
        row.DataType?.ToString() ?? IgwDataType.Int16.ToString(),
        row.Access?.ToString() ?? TagAccess.Read.ToString(),
        row.Description,
        row.EngUnit,
        row.StringLength,
        row.Array);

    public static ProjectTagScopeResponse Search(
        IgwProject project,
        string projectRevision,
        string realtimeRevision,
        string? root,
        string? query,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default)
    {
        int limit = requestedLimit ?? 1_000;
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(requestedLimit), "limit 必须在 1..1000 之间。");
        int offset = DecodeCursor(cursor, realtimeRevision);
        string exactRoot = root?.Trim() ?? "";
        ScopeNode node = Locate(project, exactRoot);

        var groups = node.Children
            .OrderBy(child => child.Path, StringComparer.Ordinal)
            .Select(child => new ProjectTagGroupDto(child.Name, child.Path, child.Kind, child.Description))
            .ToArray();
        string search = query?.Trim() ?? "";
        IReadOnlyList<TagDef> matching = search.Length == 0
            ? node.Tags
            : node.Tags.Where(tag => Matches(tag, node.Path, search)).ToArray();
        int total = matching.Count;
        if (offset > total) offset = total;
        cancellationToken.ThrowIfCancellationRequested();
        // Project lists are already stable for one revision. Slice before DTO projection so a
        // million-tag group allocates only one page, rather than one million temporary records.
        var page = matching.Skip(offset).Take(limit).Select(tag => ToDefinition(node.Path, tag)).ToArray();
        string? next = offset + page.Length < total
            ? EncodeCursor(offset + page.Length, realtimeRevision)
            : null;
        return new ProjectTagScopeResponse(projectRevision, realtimeRevision, exactRoot, groups, page, next, total);
    }

    public static ProjectTagScopeResponse Search(
        IgwProject project,
        string projectRevision,
        string? root,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default) =>
        Search(project, projectRevision, projectRevision, root, null, cursor, requestedLimit, cancellationToken);

    public static ProjectTagScopeResponse Search(
        IgwProject project,
        string projectRevision,
        string realtimeRevision,
        string? root,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default) =>
        Search(project, projectRevision, realtimeRevision, root, null, cursor, requestedLimit, cancellationToken);

    private static bool Matches(TagDef tag, string parentPath, string search) =>
        tag.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || tag.Address.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (tag.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (parentPath + "." + tag.Name).Contains(search, StringComparison.OrdinalIgnoreCase);

    private static ScopeNode Locate(IgwProject project, string root)
    {
        if (root.Length == 0)
            return new ScopeNode("", "", "Root", null, [], project.Channels.Select(channel =>
                new ScopeNode(channel.Name, channel.Name, "Channel", null, [], channel.Devices.Select(device =>
                    new ScopeNode(device.Name, channel.Name + "." + device.Name, "Device", device.Description, [], [])).ToArray())).ToArray());

        if (root.Contains("..") || root.EndsWith('.')) throw Missing(root);
        // 与 LocateAsync 一致:名字可含点(设备名常带 IP),逐层贪心匹配而不是按点切分。
        var channel = project.Channels
            .Where(item => string.Equals(root, item.Name, StringComparison.Ordinal)
                || root.StartsWith(item.Name + ".", StringComparison.Ordinal))
            .OrderByDescending(item => item.Name.Length)
            .FirstOrDefault() ?? throw Missing(root);
        if (string.Equals(root, channel.Name, StringComparison.Ordinal))
            return new ScopeNode(channel.Name, root, "Channel", null, [], channel.Devices.Select(device =>
                new ScopeNode(device.Name, root + "." + device.Name, "Device", device.Description, [], [])).ToArray());

        string remaining = root[(channel.Name.Length + 1)..];
        var configured = channel.Devices
            .Where(item => string.Equals(remaining, item.Name, StringComparison.Ordinal)
                || remaining.StartsWith(item.Name + ".", StringComparison.Ordinal))
            .OrderByDescending(item => item.Name.Length)
            .FirstOrDefault() ?? throw Missing(root);
        DeviceDef device = TemplateExpander.ExpandInProject(project, configured);
        if (string.Equals(remaining, device.Name, StringComparison.Ordinal))
            return FromDevice(device, root);

        remaining = remaining[(device.Name.Length + 1)..];
        TagGroupDef? group = null;
        IReadOnlyList<TagGroupDef> level = device.Groups;
        while (remaining.Length > 0)
        {
            group = level.FirstOrDefault(item => string.Equals(remaining, item.Name, StringComparison.Ordinal))
                ?? level.Where(item => remaining.StartsWith(item.Name + ".", StringComparison.Ordinal))
                    .OrderByDescending(item => item.Name.Length)
                    .FirstOrDefault()
                ?? throw Missing(root);
            if (string.Equals(remaining, group.Name, StringComparison.Ordinal))
            {
                remaining = "";
                break;
            }
            remaining = remaining[(group.Name.Length + 1)..];
            level = group.SubGroups;
        }
        return FromGroup(group!, root);
    }

    private static ScopeNode FromDevice(DeviceDef device, string path) =>
        new(device.Name, path, "Device", device.Description, device.Tags,
            device.Groups.Select(group => Child(group, path)).ToArray());

    private static ScopeNode FromGroup(TagGroupDef group, string path) =>
        new(group.Name, path, "Group", group.Description, group.Tags,
            group.SubGroups.Select(child => Child(child, path)).ToArray());

    private static ScopeNode Child(TagGroupDef group, string parent) =>
        new(group.Name, parent + "." + group.Name, "Group", group.Description, [], []);

    private static ProjectTagDefinitionDto ToDefinition(string parent, TagDef tag) => new(
        parent.Length == 0 ? tag.Name : parent + "." + tag.Name,
        tag.Name,
        tag.Address,
        tag.DataType.ToString(),
        tag.Access.ToString(),
        tag.Description,
        tag.EngUnit,
        tag.StringLength,
        tag.Array);

    private static ArgumentException Missing(string root) =>
        new($"找不到精确工程路径: {root}", nameof(root));

    private static string EncodeCursor(int offset, string projectRevision)
    {
        string text = "v1|" + offset.ToString(CultureInfo.InvariantCulture) + "|" + projectRevision;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int DecodeCursor(string? cursor, string projectRevision)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            string value = cursor.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            string[] parts = decoded.Split('|', 3, StringSplitOptions.None);
            if (parts.Length != 3 || !string.Equals(parts[0], "v1", StringComparison.Ordinal)
                || !string.Equals(parts[2], projectRevision, StringComparison.Ordinal)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
                || offset < 0) throw new FormatException();
            return offset;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("cursor 无效。", nameof(cursor));
        }
    }

    private sealed record ScopeNode(
        string Name,
        string Path,
        string Kind,
        string? Description,
        IReadOnlyList<TagDef> Tags,
        IReadOnlyList<ScopeNode> Children);
}
