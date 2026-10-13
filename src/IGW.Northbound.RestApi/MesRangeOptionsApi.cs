using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

public sealed record MesRangeMemberDto(
    string Path,
    string JsonName,
    string DataType,
    string Access,
    string? ShapeKind,
    int[]? Dimensions,
    int[]? LowerBounds);

public sealed record MesRangeOptionDto(
    string SourcePath,
    string DevicePath,
    string Name,
    string Scope,
    int TagCount,
    string DefaultOutputPath,
    string? ShapeKind,
    int[] Dimensions,
    int[] LowerBounds,
    long ExistingElementCount,
    IReadOnlyList<MesRangeMemberDto> Members,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record MesRangeOptionsResponse(
    IReadOnlyList<MesRangeOptionDto> Items,
    string? NextCursor,
    int Total,
    IReadOnlyList<ArrayRecognitionDiagnostic> Diagnostics);

/// <summary>
/// Projects the server-side MES catalog into small, pageable UI records. Expanded physical
/// array elements are never returned as individual rows.
/// </summary>
public static class MesRangeOptionsApi
{
    public static async Task<MesRangeOptionsResponse> SearchAsync(
        IProjectRepository repository,
        string? scopeGroup,
        string? query,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string scope = scopeGroup?.Trim().Trim('.') ?? "";
        if (scope.Length == 0)
            throw new ArgumentException("MES 范围查询必须提供 scopeGroup，避免扫描完整工程。", nameof(scopeGroup));

        IgwProject scopedProject = await LoadScopedProjectAsync(repository, scope, cancellationToken).ConfigureAwait(false);
        return Search(scopedProject, scope, query, cursor, requestedLimit, cancellationToken);
    }

    /// <summary>只读出 scope 下的组和点，拼成一个小工程(不加载整个工程)。</summary>
    public static async Task<IgwProject> LoadScopedProjectAsync(
        IProjectRepository repository, string scope, CancellationToken cancellationToken)
    {
        List<ProjectNodeSearchRow> groups = await ReadAllAsync(
            repository, new ProjectNodeSearchQuery(scope, null, ProjectNodeKind.Group, null, false, 0, 10_000),
            cancellationToken).ConfigureAwait(false);
        List<ProjectNodeSearchRow> tags = await ReadAllAsync(
            repository, new ProjectNodeSearchQuery(scope, null, ProjectNodeKind.Tag, null, false, 0, 10_000),
            cancellationToken).ConfigureAwait(false);
        return BuildScopedProject(scope, groups, tags);
    }

    private static async Task<List<ProjectNodeSearchRow>> ReadAllAsync(
        IProjectRepository repository,
        ProjectNodeSearchQuery first,
        CancellationToken cancellationToken)
    {
        var result = new List<ProjectNodeSearchRow>();
        int offset = 0;
        while (true)
        {
            ProjectPage<ProjectNodeSearchRow> page = await repository.SearchNodesAsync(
                first with { Offset = offset }, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total) return result;
        }
    }

    private static IgwProject BuildScopedProject(
        string scope,
        IReadOnlyList<ProjectNodeSearchRow> groupRows,
        IReadOnlyList<ProjectNodeSearchRow> tagRows)
    {
        ProjectNodeSearchRow? sample = groupRows.FirstOrDefault() ?? tagRows.FirstOrDefault();
        if (sample is null) return new IgwProject();
        string devicePath = sample.DevicePath;
        int separator = devicePath.IndexOf('.');
        if (separator < 1 || separator == devicePath.Length - 1)
            throw new InvalidDataException("工程仓储返回了无效的设备实际路径：" + devicePath);
        var device = new DeviceDef { Name = devicePath[(separator + 1)..] };
        var channel = new ChannelDef { Name = devicePath[..separator], Devices = [device] };
        var byPath = new Dictionary<string, TagGroupDef>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectNodeSearchRow row in groupRows.OrderBy(row => row.ActualPath.Count(ch => ch == '.')))
        {
            var group = new TagGroupDef { Id = row.Id, Name = row.Name, Description = row.Description };
            byPath[row.ActualPath] = group;
            if (string.Equals(row.ParentPath, devicePath, StringComparison.OrdinalIgnoreCase))
                device.Groups.Add(group);
            else if (byPath.TryGetValue(row.ParentPath, out TagGroupDef? parent))
                parent.SubGroups.Add(group);
        }
        foreach (ProjectNodeSearchRow row in tagRows)
        {
            var tag = new TagDef
            {
                Id = row.Id,
                Name = row.Name,
                Address = row.Address ?? "",
                DataType = row.DataType ?? IgwDataType.Int16,
                Access = row.Access ?? TagAccess.Read,
                Description = row.Description,
                EngUnit = row.EngUnit,
                StringLength = row.StringLength,
                IsArray = row.Array is not null,
                Array = row.Array,
                Metadata = row.Metadata is null ? null : new Dictionary<string, string>(row.Metadata),
            };
            if (string.Equals(row.ParentPath, devicePath, StringComparison.OrdinalIgnoreCase)) device.Tags.Add(tag);
            else if (byPath.TryGetValue(row.ParentPath, out TagGroupDef? parent)) parent.Tags.Add(tag);
        }
        return new IgwProject { Channels = [channel] };
    }

    public static MesRangeOptionsResponse Search(
        IgwProject project,
        string? scopeGroup,
        string? query,
        string? cursor,
        int? requestedLimit,
        CancellationToken cancellationToken = default)
    {
        int limit = requestedLimit ?? 200;
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(requestedLimit), "limit 必须在 1..500 之间。");
        int offset = ParseCursor(cursor);
        string scope = scopeGroup?.Trim() ?? "";
        string q = query?.Trim() ?? "";
        var catalog = MesDataRangeCatalog.Build(project);
        var logicalByPath = catalog.LogicalArrays.ToDictionary(
            item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
        var diagnostics = catalog.RecognitionDiagnostics
            .Where(item => InScope(item.SourcePath, scope))
            .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new Dictionary<string, MesRangeOptionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in catalog.Roots)
            CollectGroup(root, catalog, logicalByPath, entries, cancellationToken);
        foreach (var logical in catalog.LogicalArrays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries[logical.SourcePath] = ToDto(logical, diagnostics);
        }

        var filtered = entries.Values
            .Where(item => InScope(item.SourcePath, scope))
            .Where(item => q.Length == 0 || SearchText(item).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (offset > filtered.Length) offset = filtered.Length;
        var page = filtered.Skip(offset).Take(limit).ToArray();
        string? next = offset + page.Length < filtered.Length
            ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new MesRangeOptionsResponse(page, next, filtered.Length, diagnostics);
    }

    private static void CollectGroup(
        MesCatalogGroup group,
        MesDataRangeCatalog catalog,
        IReadOnlyDictionary<string, MesCatalogLogicalArray> logicalByPath,
        IDictionary<string, MesRangeOptionDto> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (logicalByPath.TryGetValue(group.SourcePath, out var exact) &&
            exact.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
            return;

        entries[group.SourcePath] = new MesRangeOptionDto(
            group.SourcePath,
            group.DevicePath,
            group.Name,
            group.SourcePath.Count(ch => ch == '.') == 2 ? "Group" : "Struct",
            group.TagCount,
            group.DefaultOutputPath,
            null,
            [],
            [],
            0,
            [],
            catalog.RecognitionDiagnostics.Where(item =>
                    string.Equals(item.SourcePath, group.SourcePath, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        var boundLeaves = catalog.GetLogicalChildren(group.SourcePath)
            .SelectMany(array => array.Descriptor.Leaves)
            .Select(leaf => leaf.TagId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in group.Children)
        {
            if (boundLeaves.Count > 0 && GroupTagIds(child).Any(boundLeaves.Contains)) continue;
            CollectGroup(child, catalog, logicalByPath, entries, cancellationToken);
        }
    }

    private static MesRangeOptionDto ToDto(
        MesCatalogLogicalArray array,
        IReadOnlyList<ArrayRecognitionDiagnostic> diagnostics)
    {
        var descriptor = array.Descriptor;
        long existing = descriptor.ShapeKind == LogicalArrayShapeKind.Rectangular
            ? descriptor.ElementCount
            : descriptor.ExistingIndices.Count;
        return new MesRangeOptionDto(
            array.SourcePath,
            array.DevicePath,
            array.Name,
            "Array",
            array.TagCount,
            array.DefaultOutputPath,
            descriptor.ShapeKind.ToString(),
            [.. descriptor.Dimensions],
            [.. descriptor.LowerBounds],
            existing,
            FlattenMembers(descriptor.Members).ToArray(),
            diagnostics.Where(item => string.Equals(item.SourcePath, array.SourcePath, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<MesRangeMemberDto> FlattenMembers(
        IReadOnlyList<LogicalArrayMember> members,
        string prefix = "")
    {
        foreach (var member in members)
        {
            string path = prefix.Length == 0 ? member.Path : prefix + "." + member.Path;
            yield return new MesRangeMemberDto(
                path,
                member.JsonName,
                member.DataType.ToString(),
                member.Access.ToString(),
                member.NestedArray?.ShapeKind.ToString(),
                member.NestedArray is null ? null : [.. member.NestedArray.Dimensions],
                member.NestedArray is null ? null : [.. member.NestedArray.LowerBounds]);
            if (member.NestedArray is not null)
                foreach (var nested in FlattenMembers(member.NestedArray.Members, path)) yield return nested;
        }
    }

    private static IEnumerable<string> GroupTagIds(MesCatalogGroup group)
    {
        foreach (var tag in group.Tags) yield return tag.TagId;
        foreach (var child in group.Children)
        foreach (string tagId in GroupTagIds(child))
            yield return tagId;
    }

    private static bool InScope(string path, string scope) => scope.Length == 0 ||
        string.Equals(path, scope, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase);

    private static string SearchText(MesRangeOptionDto item) =>
        string.Join(' ', item.SourcePath, item.Name, item.Scope, item.ShapeKind,
            string.Join(' ', item.Members.Select(member => member.Path + " " + member.JsonName)));

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (!int.TryParse(cursor, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) || value < 0)
            throw new ArgumentException("cursor 无效。", nameof(cursor));
        return value;
    }
}
