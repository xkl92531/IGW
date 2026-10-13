using IGW.Abstractions;

namespace IGW.Core;

internal static class MesDataRangeValidator
{
    public static void Check(IgwProject project, List<string> errors)
    {
        var catalog = MesDataRangeCatalog.Build(project);
        foreach (var task in project.MesUpload?.Tasks ?? new())
        {
            CheckPayload(task.Payload, $"MES 任务 {DisplayName(task)}", catalog, errors);
            if (task.Flow is null) continue;
            foreach (var node in task.Flow.Nodes.Where(node => node.Type == "Upload"))
            {
                CheckPayload(node.Payload,
                    $"MES 任务 {DisplayName(task)} 的上报节点 {DisplayName(node)}",
                    catalog, errors);
            }
        }
    }

    private static void CheckPayload(
        MesPayload payload,
        string owner,
        MesDataRangeCatalog catalog,
        List<string> errors)
    {
        if (!MesPayloadModes.IsValid(payload.Mode))
            errors.Add($"{owner} 的上报数据模式无效:{payload.Mode}。");
        if (!MesPayloadModes.IsDataRanges(payload)) return;

        foreach (var configuredRange in payload.DataRanges ?? new())
        {
            MesDataRange range;
            try { range = MesArrayLengthPolicy.Resolve(configuredRange, catalog); }
            catch (ArgumentException ex)
            {
                errors.Add($"{owner} 的数组长度配置无效：{ex.Message}");
                continue;
            }
            if (!IsOneOf(range.Scope, "Group", "Struct", "Array", "Tag"))
                errors.Add($"{owner} 的数据范围 {range.SourcePath} Scope 仅支持 Group/Struct/Array/Tag。");
            if (!IsOneOf(range.IncludeMode, "All"))
                errors.Add($"{owner} 的数据范围 {range.SourcePath} IncludeMode 首版仅支持 All。");
            if (!IsOneOf(range.OfflineMode, "Null"))
                errors.Add($"{owner} 的数据范围 {range.SourcePath} OfflineMode 首版仅支持 Null。");
            if (!IsOneOf(range.OutputPathMode, "Auto", "Custom"))
                errors.Add($"{owner} 的数据范围 {range.SourcePath} OutputPathMode 仅支持 Auto/Custom。");
            MesCatalogGroup? source = null;
            MesCatalogArray? array = null;
            MesCatalogIndexedArray? indexedArray = null;
            MesCatalogLogicalArray? logicalArray = null;
            bool hasGroup = !string.IsNullOrWhiteSpace(range.SourcePath) &&
                catalog.TryGet(range.SourcePath, out source);
            bool hasArray = !string.IsNullOrWhiteSpace(range.SourcePath) &&
                catalog.TryGetArray(range.SourcePath, out array);
            bool hasIndexedArray = !string.IsNullOrWhiteSpace(range.SourcePath) &&
                catalog.TryGetIndexedArray(range.SourcePath, out indexedArray);
            bool hasLogicalArray = !string.IsNullOrWhiteSpace(range.SourcePath) &&
                catalog.TryGetLogicalArray(range.SourcePath, out logicalArray);
            bool hasTag = !hasGroup && !hasArray && !hasIndexedArray && !hasLogicalArray &&
                !string.IsNullOrWhiteSpace(range.SourcePath) && catalog.TryGetScalarTag(range.SourcePath, out _, out _);
            if (!hasGroup && !hasArray && !hasIndexedArray && !hasLogicalArray && !hasTag)
                errors.Add($"{owner} 的数据范围来源不存在:{range.SourcePath}。");
            else if (hasTag)
            {
                if ((range.Excludes?.Count ?? 0) > 0)
                    errors.Add($"{owner} 的单点数据范围 {range.SourcePath} 不能再排除字段。");
            }
            else if (hasArray)
            {
                if ((range.Excludes?.Count ?? 0) > 0)
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 不支持排除字段，请使用成员投影。");
                try
                {
                    var start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart : array!.Definition.LowerBounds;
                    var lengths = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths : array!.Definition.Dimensions;
                    new ArraySlice(start, lengths).ValidateAndCreateShape(array!.Definition.Shape);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 切片无效:{ex.Message}");
                }
                if (!string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                    !HasMember(array!.Definition.Struct, range.ArrayMemberPath))
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 成员不存在:{range.ArrayMemberPath}。");
            }
            else if (hasLogicalArray && logicalArray!.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
            {
                try
                {
                    ValidateLogicalSlice(logicalArray.Descriptor, range);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 切片无效:{ex.Message}");
                }
                if (!string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                    !HasLogicalMember(logicalArray.Descriptor, range.ArrayMemberPath))
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 成员不存在:{range.ArrayMemberPath}。");
                foreach (string exclude in range.Excludes ?? [])
                    if (string.IsNullOrWhiteSpace(exclude) || !HasLogicalMember(logicalArray.Descriptor, exclude.Trim()))
                        errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 排除成员不存在:{exclude}。");
            }
            else if (hasIndexedArray)
            {
                if ((range.Excludes?.Count ?? 0) > 0)
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 不支持排除字段，请使用成员投影。");
                try
                {
                    ValidateIndexedSlice(indexedArray!, range);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 切片无效:{ex.Message}");
                }
                if (!string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                    !HasIndexedMember(indexedArray!, range.ArrayMemberPath))
                    errors.Add($"{owner} 的数组数据范围 {range.SourcePath} 成员不存在:{range.ArrayMemberPath}。");
            }
            else if (hasGroup)
            {
                var leaves = new HashSet<string>(RelativeLeafPaths(source!), StringComparer.OrdinalIgnoreCase);
                foreach (string exclude in range.Excludes ?? new())
                    if (string.IsNullOrWhiteSpace(exclude) || !leaves.Contains(exclude.Trim()))
                        errors.Add($"{owner} 的数据范围 {range.SourcePath} 排除项不是来源下的叶子点:{exclude}。");
            }
        }

        var ranges = payload.DataRanges ?? new();
        foreach (var range in ranges)
        {
            string outputPath = ResolveOutputPath(range, catalog);
            string top = outputPath.Split('.', 2, StringSplitOptions.TrimEntries)[0];
            if (top.Length > 0 && (payload.Append?.Keys.Any(key =>
                    string.Equals(key, top, StringComparison.OrdinalIgnoreCase)) ?? false))
                errors.Add($"{owner} 的固定附加字段 {top} 与数据范围输出路径 {outputPath} 冲突。");
        }

        for (int i = 0; i < ranges.Count; i++)
        for (int j = i + 1; j < ranges.Count; j++)
        {
            var left = ranges[i];
            var right = ranges[j];
            if (IsSameOrDescendant(right.SourcePath, left.SourcePath))
                errors.Add($"{owner} 的数据范围 {right.SourcePath} 已被上级范围 {left.SourcePath} 包含。");
            else if (IsSameOrDescendant(left.SourcePath, right.SourcePath))
                errors.Add($"{owner} 的数据范围 {left.SourcePath} 已被上级范围 {right.SourcePath} 包含。");

            string leftOutput = ResolveOutputPath(left, catalog);
            string rightOutput = ResolveOutputPath(right, catalog);
            if (!string.IsNullOrWhiteSpace(leftOutput) &&
                !string.IsNullOrWhiteSpace(rightOutput) &&
                (IsSameOrDescendant(leftOutput, rightOutput) ||
                 IsSameOrDescendant(rightOutput, leftOutput)))
                errors.Add($"{owner} 的输出路径冲突:{leftOutput} 与 {rightOutput}。");
        }
    }

    private static string ResolveOutputPath(MesDataRange range, MesDataRangeCatalog catalog)
    {
        if (!string.IsNullOrWhiteSpace(range.OutputPath)) return range.OutputPath.Trim();
        if (!string.Equals(range.OutputPathMode, "Auto", StringComparison.OrdinalIgnoreCase)) return "";
        if (catalog.TryGetLogicalArray(range.SourcePath, out var logical)) return logical.DefaultOutputPath;
        if (catalog.TryGetIndexedArray(range.SourcePath, out var indexed)) return indexed.DefaultOutputPath;
        if (catalog.TryGet(range.SourcePath, out var source)) return source.DefaultOutputPath;
        if (catalog.TryGetScalarTag(range.SourcePath, out var tag, out var parent)) return parent.DefaultOutputPath + "." + tag.Name;
        return catalog.TryGetArray(range.SourcePath, out var array) ? array.DefaultOutputPath : "";
    }

    private static bool IsSameOrDescendant(string candidate, string ancestor) =>
        string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsOneOf(string? value, params string[] allowed) =>
        allowed.Any(item => string.Equals(value, item, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> RelativeLeafPaths(MesCatalogGroup source)
    {
        foreach (var tag in source.Tags) yield return tag.Name;
        foreach (var child in source.Children)
        foreach (var relative in RelativeLeafPaths(child))
            yield return child.Name + "." + relative;
    }

    private static bool HasMember(StructDef? schema, string path)
    {
        if (schema is null) return false;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            var field = schema.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.JsonName, segment, StringComparison.OrdinalIgnoreCase));
            if (field is null || field.Array is not null) return false;
            if (index == segments.Length - 1) return field.Struct is null;
            if (field.Struct is null) return false;
            schema = field.Struct!;
        }
        return false;
    }

    private static void ValidateIndexedSlice(MesCatalogIndexedArray source, MesDataRange range)
    {
        if (range.ArrayStart is { Length: > 1 } || range.ArrayLengths is { Length: > 1 })
            throw new ArgumentException("分组结构体数组只支持一维切片。");
        int start = range.ArrayStart is { Length: 1 } ? range.ArrayStart[0] : source.LowerBound;
        int length = range.ArrayLengths is { Length: 1 } ? range.ArrayLengths[0] : source.Length;
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(range.ArrayLengths), "数量必须大于零。");
        long end = (long)start + length;
        long sourceEnd = (long)source.LowerBound + source.Length;
        if (start < source.LowerBound || end > sourceEnd)
            throw new IndexOutOfRangeException(
                $"[{start}..{end - 1}] 超出 [{source.LowerBound}..{sourceEnd - 1}]。");
    }

    private static bool HasIndexedMember(MesCatalogIndexedArray source, string path)
    {
        if (source.Elements.Count == 0) return false;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return false;
        MesCatalogGroup group = source.Elements[0].Group;
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            var tag = group.Tags.FirstOrDefault(item =>
                string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (tag is not null) return index == segments.Length - 1;
            var child = group.Children.FirstOrDefault(item =>
                string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) return false;
            if (index == segments.Length - 1) return true;
            group = child;
        }
        return false;
    }

    private static void ValidateLogicalSlice(LogicalArrayDescriptor descriptor, MesDataRange range)
    {
        var start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart : descriptor.LowerBounds;
        var lengths = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths : descriptor.Dimensions;
        if (start.Length != descriptor.Dimensions.Length || lengths.Length != descriptor.Dimensions.Length)
            throw new ArgumentException("数组切片秩不一致。");
        for (int i = 0; i < start.Length; i++)
        {
            if (lengths[i] <= 0) throw new ArgumentOutOfRangeException(nameof(range.ArrayLengths), "数量必须大于零。");
            long end = (long)start[i] + lengths[i];
            long sourceEnd = (long)descriptor.LowerBounds[i] + descriptor.Dimensions[i];
            if (start[i] < descriptor.LowerBounds[i] || end > sourceEnd)
                throw new IndexOutOfRangeException($"第 {i + 1} 维切片超出实际范围。");
        }
    }

    private static bool HasLogicalMember(LogicalArrayDescriptor descriptor, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        LogicalArrayDescriptor current = descriptor;
        for (int index = 0; index < segments.Length; index++)
        {
            var member = current.Members.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, segments[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.JsonName, segments[index], StringComparison.OrdinalIgnoreCase));
            if (member is null) return false;
            if (index == segments.Length - 1) return true;
            if (member.NestedArray is null) return false;
            current = member.NestedArray;
        }
        return false;
    }

    private static string DisplayName(MesTask task) =>
        string.IsNullOrWhiteSpace(task.Name) ? task.Id : task.Name;

    private static string DisplayName(MesNode node) =>
        string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title;
}
