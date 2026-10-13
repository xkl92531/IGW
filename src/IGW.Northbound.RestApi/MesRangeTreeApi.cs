using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;

namespace IGW.Northbound.RestApi;

/// <summary>「配置数据」树里的一项：组、结构体、数组或单个点。</summary>
public sealed record MesRangeNodeDto(
    string SourcePath,
    string Name,
    string Scope,
    string? DataType,
    int TagCount,
    bool HasChildren,
    string DefaultOutputPath,
    string? ShapeKind,
    int[] Dimensions,
    int[] LowerBounds,
    IReadOnlyList<MesRangeMemberDto> Members);

public sealed record MesRangeChildrenResponse(IReadOnlyList<MesRangeNodeDto> Items, string? NextCursor, int Total);

/// <summary>
/// 报文结构预览里的一项。Key 是结构路径(按原名)，改名、不上报都按它记：
/// Excludable 是数据块/结构体下的普通点(记在 Excludes，连采集一起省掉)，Omittable 是数组元素里的成员(记在 Omit，只影响报文)。
/// </summary>
public sealed record MesPayloadNodeDto(
    string Key,
    string Name,
    string Kind,
    string? TagId,
    string? DataType,
    int? Length,
    bool Excludable,
    IReadOnlyList<MesPayloadNodeDto> Children,
    bool Omittable = false);

public sealed record MesPayloadRangePreview(
    string SourcePath,
    string OutputPath,
    string Scope,
    MesPayloadNodeDto Root,
    int TagCount);

public sealed record MesPayloadPreviewResponse(IReadOnlyList<MesPayloadRangePreview> Ranges, string? Error);

public sealed record MesPayloadPreviewRequest(string? ScopeGroup, List<MesDataRange>? Ranges);

/// <summary>「配置数据」选中的范围生成的旧版字段/结构映射。</summary>
public sealed record MesFieldMappingResponse(IReadOnlyList<MesField> Fields, IReadOnlyList<MesStruct> Structs);

/// <summary>
/// 「配置数据」按层取子项(含单个点)与报文结构预览。目录按「工程版本 + 数据块」缓存，
/// 在一个上万点的数据块里展开、翻页、搜索时不再每次把整块重新读一遍、重建一遍。
/// </summary>
public static class MesRangeTreeApi
{
    private const int CacheSize = 8;
    private static readonly ConcurrentDictionary<string, Lazy<Task<MesDataRangeCatalog>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> CacheOrder = new();

    /// <summary>数据块路径：通道.设备.组(任务可能绑定到更深的二级组，目录按它所在的数据块建)。</summary>
    public static string BlockOf(string path)
    {
        string[] segments = (path ?? "").Trim().Trim('.').Split('.');
        return string.Join('.', segments.Take(Math.Min(3, segments.Length)));
    }

    public static async Task<MesDataRangeCatalog> CatalogAsync(
        IProjectRepository? repository, IgwProject? project, string scope, CancellationToken cancellationToken)
    {
        string block = BlockOf(scope);
        if (block.Split('.').Length < 3)
            throw new ArgumentException("MES 范围查询必须指定到数据块(通道.设备.组)。", nameof(scope));
        if (repository is null) return MesDataRangeCatalog.Build(project ?? new IgwProject());
        ProjectRevision revision = await repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
        string key = revision.Value + "|" + block;
        var entry = Cache.GetOrAdd(key, added =>
        {
            CacheOrder.Enqueue(added);
            while (CacheOrder.Count > CacheSize && CacheOrder.TryDequeue(out string? old)) Cache.TryRemove(old, out _);
            return new Lazy<Task<MesDataRangeCatalog>>(async () =>
                MesDataRangeCatalog.Build(await MesRangeOptionsApi.LoadScopedProjectAsync(repository, block, CancellationToken.None)
                    .ConfigureAwait(false)));
        });
        try { return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (entry.Value.IsFaulted) { Cache.TryRemove(key, out _); throw; }
    }

    /// <summary>
    /// parent 的直接子项(组、结构体、数组、点)，分页；q 不为空时在整个 scopeGroup 里按名称/路径搜索(不分层)。
    /// </summary>
    public static MesRangeChildrenResponse Children(
        MesDataRangeCatalog catalog, string scopeGroup, string? parent, string? query, string? cursor, int? requestedLimit)
    {
        int limit = requestedLimit ?? 200;
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(requestedLimit), "limit 必须在 1..500 之间。");
        int offset = ParseCursor(cursor);
        string scope = scopeGroup.Trim().Trim('.');
        string q = query?.Trim() ?? "";
        IReadOnlyList<MesRangeNodeDto> items;
        if (q.Length > 0)
        {
            var found = new List<MesRangeNodeDto>();
            if (catalog.TryGet(scope, out var root)) Search(catalog, root, q, found);
            items = found;
        }
        else
        {
            string path = string.IsNullOrWhiteSpace(parent) ? scope : parent.Trim().Trim('.');
            if (!InScope(path, scope)) throw new ArgumentException("parent 不在 scopeGroup 范围内。", nameof(parent));
            if (string.IsNullOrWhiteSpace(parent))
                items = catalog.TryGet(scope, out var self) ? [GroupNode(self, scope)] : [];
            // 按下标命名的子组(UDT 数组展开成的 名字[下标] 组)：展开后是每个元素，元素里再按结构一层层展开
            else if (catalog.TryGetIndexedArray(path, out var indexedArray))
                items = indexedArray.Elements.Select(element => GroupNode(element.Group, path)).ToArray();
            else items = catalog.TryGet(path, out var group) ? ChildrenOf(catalog, group) : [];
        }
        if (offset > items.Count) offset = items.Count;
        var page = items.Skip(offset).Take(limit).ToArray();
        string? next = offset + page.Length < items.Count
            ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new MesRangeChildrenResponse(page, next, items.Count);
    }

    /// <summary>
    /// 数据块以上的层级：通道 → 设备 → 数据块(通道.设备.组)，让「配置数据」也能选别的设备、别的 DB 里的点。
    /// parent 为空取通道，是通道取设备，是设备取数据块；数据块里面再按 <see cref="Children"/> 分层取。
    /// </summary>
    public static MesRangeChildrenResponse ProjectLevels(IgwProject project, string? parent, string? cursor, int? requestedLimit)
    {
        int limit = requestedLimit ?? 200;
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(requestedLimit), "limit 必须在 1..500 之间。");
        int offset = ParseCursor(cursor);
        string path = parent?.Trim().Trim('.') ?? "";
        string[] segments = path.Length == 0 ? [] : path.Split('.');
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        ChannelDef? channel = segments.Length >= 1 ? project.Channels.FirstOrDefault(c => Same(c.Name, segments[0])) : null;
        DeviceDef? device = segments.Length >= 2 ? channel?.Devices.FirstOrDefault(d => Same(d.Name, segments[1])) : null;
        IReadOnlyList<MesRangeNodeDto> items = segments.Length switch
        {
            0 => project.Channels.Select(c => LevelNode(c.Name, c.Name, "Channel", c.Devices.Sum(DeviceTagCount), c.Devices.Count > 0)).ToArray(),
            1 => channel?.Devices.Select(d => LevelNode(path + "." + d.Name, d.Name, "Device", DeviceTagCount(d), d.Groups.Count > 0)).ToArray() ?? [],
            2 => device?.Groups.Select(g => LevelNode(path + "." + g.Name, g.Name, "Group", GroupTagCount(g), g.Tags.Count + g.SubGroups.Count > 0)).ToArray() ?? [],
            _ => throw new ArgumentException("parent 已在数据块内，请按 scopeGroup 查询。", nameof(parent)),
        };
        if (offset > items.Count) offset = items.Count;
        var page = items.Skip(offset).Take(limit).ToArray();
        string? next = offset + page.Length < items.Count
            ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new MesRangeChildrenResponse(page, next, items.Count);
    }

    private static MesRangeNodeDto LevelNode(string path, string name, string scope, int tagCount, bool hasChildren) =>
        new(path, name, scope, null, tagCount, hasChildren, name, null, [], [], []);

    // 只算组里的点：直接挂在设备下的点不属于任何数据块，「配置数据」选不到
    private static int DeviceTagCount(DeviceDef device) => device.Groups.Sum(GroupTagCount);

    private static int GroupTagCount(TagGroupDef group) => group.Tags.Count + group.SubGroups.Sum(GroupTagCount);

    private static List<MesRangeNodeDto> ChildrenOf(MesDataRangeCatalog catalog, MesCatalogGroup group)
    {
        // 与报文编译(MesRangePayloadCompiler.CompileGroupObject)同样的顺序和归并规则：先点，再识别出的数组，再子组
        var result = new List<MesRangeNodeDto>();
        foreach (var tag in group.Tags)
            result.Add(tag.Array is null ? TagNode(tag, group) : ArrayTagNode(catalog, tag, group));
        var consumed = new HashSet<int>();
        foreach (var logical in catalog.GetLogicalChildren(group.SourcePath)
                     .Where(child => child.Descriptor.BindingKind != ArrayBindingKind.NativeTag))
        {
            var bound = logical.Descriptor.Leaves.Select(leaf => leaf.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < group.Children.Count; i++)
                if (GroupTagIds(group.Children[i]).Any(bound.Contains)) consumed.Add(i);
            result.Add(LogicalNode(logical));
        }
        var indexedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < group.Children.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            var child = group.Children[i];
            if (MesIndexedGroupName.TryParse(child.Name, out string arrayName, out _) &&
                catalog.TryGetIndexedArray(group.SourcePath + "." + arrayName, out var indexed))
            {
                if (indexedSeen.Add(indexed.SourcePath)) result.Add(IndexedNode(indexed));
                continue;
            }
            if (catalog.TryGetIndexedArray(child.SourcePath, out var numeric)) { result.Add(IndexedNode(numeric)); continue; }
            result.Add(GroupNode(child, group.SourcePath));
        }
        return result;
    }

    private static void Search(MesDataRangeCatalog catalog, MesCatalogGroup group, string q, List<MesRangeNodeDto> found)
    {
        foreach (var node in ChildrenOf(catalog, group))
        {
            if (found.Count >= 5000) return;   // 搜索结果只给前 5000 条，提示用户输入更具体的关键字
            if (node.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                node.SourcePath.Contains(q, StringComparison.OrdinalIgnoreCase)) found.Add(node);
            if (node.Scope is "Group" or "Struct" && catalog.TryGet(node.SourcePath, out var child)) Search(catalog, child, q, found);
        }
    }

    private static MesRangeNodeDto GroupNode(MesCatalogGroup group, string scopeRoot) => new(
        group.SourcePath, group.Name, group.SourcePath.Count(ch => ch == '.') == 2 ? "Group" : "Struct",
        null, group.TagCount, group.Tags.Count + group.Children.Count > 0, group.DefaultOutputPath, null, [], [], []);

    private static MesRangeNodeDto TagNode(MesCatalogTag tag, MesCatalogGroup parent) => new(
        tag.TagId, tag.Name, "Tag", tag.DataType.ToString(), 1, false,
        parent.DefaultOutputPath + "." + tag.Name, null, [], [], []);

    private static MesRangeNodeDto ArrayTagNode(MesDataRangeCatalog catalog, MesCatalogTag tag, MesCatalogGroup parent)
    {
        var array = tag.Array!;
        var members = array.Struct?.Fields.Select(field => new MesRangeMemberDto(
            field.Name, field.JsonName ?? field.Name, field.DataType.ToString(), tag.Access.ToString(),
            field.Array is null ? null : "Rectangular", field.Array?.Dimensions, field.Array?.LowerBounds)).ToArray() ?? [];
        string output = catalog.TryGetArray(tag.TagId, out var registered) ? registered.DefaultOutputPath : parent.DefaultOutputPath + "." + tag.Name;
        return new MesRangeNodeDto(tag.TagId, tag.Name, "Array", array.ElementType.ToString(),
            checked((int)array.Shape.ElementCount), false, output, "Rectangular",
            [.. array.Dimensions], [.. array.LowerBounds], members);
    }

    private static MesRangeNodeDto LogicalNode(MesCatalogLogicalArray logical) => new(
        logical.SourcePath, logical.Name, "Array", null, logical.TagCount, false, logical.DefaultOutputPath,
        logical.Descriptor.ShapeKind.ToString(), [.. logical.Descriptor.Dimensions], [.. logical.Descriptor.LowerBounds],
        logical.Descriptor.Members.Select(member => new MesRangeMemberDto(member.Path, member.JsonName,
            member.DataType.ToString(), member.Access.ToString(), member.NestedArray?.ShapeKind.ToString(),
            member.NestedArray is null ? null : [.. member.NestedArray.Dimensions],
            member.NestedArray is null ? null : [.. member.NestedArray.LowerBounds])).ToArray());

    private static MesRangeNodeDto IndexedNode(MesCatalogIndexedArray indexed)
    {
        var sample = indexed.Elements.FirstOrDefault()?.Group;
        var members = sample?.Tags.Select(tag => new MesRangeMemberDto(tag.Name, tag.Name, tag.DataType.ToString(),
            tag.Access.ToString(), null, null, null)).ToArray() ?? [];
        return new MesRangeNodeDto(indexed.SourcePath, indexed.Name, "Array", null, indexed.TagCount, indexed.Elements.Count > 0,
            indexed.DefaultOutputPath, "Rectangular", [indexed.Length], [indexed.LowerBound], members);
    }

    /// <summary>
    /// 把配置的数据范围编译成报文结构(按原名，含被排除的项)，供「报文高级设置」逐项改名、选择是否上报；
    /// 同时按实际配置(改名、排除)完整编译一遍，有重名等问题时在 Error 里返回。
    /// </summary>
    public static MesPayloadPreviewResponse Preview(Func<string, MesDataRangeCatalog> catalogFor, IReadOnlyList<MesDataRange> ranges)
    {
        var result = new List<MesPayloadRangePreview>();
        string? error = null;
        foreach (var range in ranges)
        {
            try
            {
                var catalog = catalogFor(range.SourcePath);
                var bare = new MesDataRange
                {
                    SourcePath = range.SourcePath, Scope = range.Scope, OutputPath = range.OutputPath,
                    OutputPathMode = range.OutputPathMode, ArrayStart = range.ArrayStart, ArrayLengths = range.ArrayLengths,
                    ArrayLengthMode = "Fixed", ArrayMemberPath = range.ArrayMemberPath,
                };
                var compiled = MesRangePayloadCompiler.Compile(new MesPayload { Mode = MesPayloadModes.DataRanges, DataRanges = [bare] }, catalog)
                    .Ranges.Single();
                bool groupSource = catalog.TryGet(range.SourcePath, out _) &&
                    !catalog.TryGetIndexedArray(range.SourcePath, out _) && !catalog.TryGetLogicalArray(range.SourcePath, out _);
                string name = compiled.OutputSegments[^1];
                var root = Describe(compiled.Source, "", name, catalog, groupSource);
                result.Add(new MesPayloadRangePreview(range.SourcePath, string.Join('.', compiled.OutputSegments), range.Scope, root, compiled.TagCount));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                error ??= ex.Message;
            }
        }
        if (error is null)
        {
            try
            {
                foreach (var range in ranges)
                    MesRangePayloadCompiler.Compile(new MesPayload { Mode = MesPayloadModes.DataRanges, DataRanges = [WithoutDynamicLength(range)] },
                        catalogFor(range.SourcePath));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { error = ex.Message; }
        }
        return new MesPayloadPreviewResponse(result, error);
    }

    private static MesDataRange WithoutDynamicLength(MesDataRange range) =>
        MesArrayLengthPolicy.IsAutomatic(range)
            ? new MesDataRange
            {
                SourcePath = range.SourcePath, Scope = range.Scope, OutputPath = range.OutputPath, OutputPathMode = range.OutputPathMode,
                Excludes = range.Excludes, ArrayStart = range.ArrayStart, ArrayMemberPath = range.ArrayMemberPath, Names = range.Names, Omit = range.Omit,
            }
            : range;

    private static MesPayloadNodeDto Describe(
        CompiledMesSourceValue value, string key, string name, MesDataRangeCatalog catalog, bool excludableLeaves, bool insideArray = false)
    {
        switch (value)
        {
            case CompiledMesSourceObject obj:
                return new MesPayloadNodeDto(key, name, "object", null, null, null, false,
                    obj.Properties.Select(property => Describe(property.Value, MesPayloadNames.Child(key, property.Name),
                        property.Name, catalog, excludableLeaves, insideArray)).ToArray(), insideArray);
            case CompiledMesSourceArray array:
            {
                // 数组元素结构相同，按第一个元素展示成员；改名对所有元素生效
                var first = array.Elements.FirstOrDefault();
                string elementKey = MesPayloadNames.Element(key);
                var children = first switch
                {
                    CompiledMesSourceObject element => element.Properties.Select(property => Describe(property.Value,
                        MesPayloadNames.Child(elementKey, property.Name), property.Name, catalog, false, true)).ToArray(),
                    CompiledMesSourceArray nested => Describe(nested, key, name, catalog, false, true).Children,
                    _ => (IReadOnlyList<MesPayloadNodeDto>)[],
                };
                return new MesPayloadNodeDto(key, name, "array", null, ElementType(first, catalog), array.Elements.Count, false, children, insideArray);
            }
            case CompiledMesSourceLogicalArray native:
                return new MesPayloadNodeDto(key, name, "array", native.TagId, native.Definition.ElementType.ToString(),
                    checked((int)native.Slice.ValidateAndCreateShape(native.Definition.Shape).ElementCount), false,
                    native.Definition.Struct is null ? [] : StructNodes(native.Definition.Struct, MesPayloadNames.Element(key)), insideArray);
            case CompiledMesSourceField field:
                return new MesPayloadNodeDto(key, name, "value", field.TagId,
                    catalog.TryGetTag(field.TagId, out var tag) ? tag.DataType.ToString() : null, null,
                    !insideArray && excludableLeaves && key.Length > 0, [], insideArray);
            default:
                return new MesPayloadNodeDto(key, name, "value", null, null, null, false, []);
        }
    }

    private static string? ElementType(CompiledMesSourceValue? first, MesDataRangeCatalog catalog) => first switch
    {
        CompiledMesSourceField field when catalog.TryGetTag(field.TagId, out var tag) => tag.DataType.ToString(),
        CompiledMesSourceObject => "Struct",
        _ => null,
    };

    private static IReadOnlyList<MesPayloadNodeDto> StructNodes(StructDef definition, string path) =>
        definition.Fields.Select(field =>
        {
            string key = MesPayloadNames.Child(path, field.Name);
            if (field.Struct is not null)
                return new MesPayloadNodeDto(key, field.Name, "object", null, "Struct", null, false, StructNodes(field.Struct, key), true);
            if (field.Array is not null)
                return new MesPayloadNodeDto(key, field.Name, "array", null, field.Array.ElementType.ToString(),
                    checked((int)field.Array.Shape.ElementCount), false,
                    field.Array.Struct is null ? [] : StructNodes(field.Array.Struct, MesPayloadNames.Element(key)), true);
            return new MesPayloadNodeDto(key, field.Name, "value", null, field.DataType.ToString(), null, false, [], true);
        }).ToArray();

    private static IEnumerable<string> GroupTagIds(MesCatalogGroup group)
    {
        foreach (var tag in group.Tags) yield return tag.TagId;
        foreach (var child in group.Children)
        foreach (string tagId in GroupTagIds(child))
            yield return tagId;
    }

    private static bool InScope(string path, string scope) =>
        string.Equals(path, scope, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase);

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (!int.TryParse(cursor, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) || value < 0)
            throw new ArgumentException("cursor 无效。", nameof(cursor));
        return value;
    }
}
