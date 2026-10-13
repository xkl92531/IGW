using IGW.Abstractions;

namespace IGW.Core;

public sealed record MesTaskTopologyResult(
    int RemovedTasks,
    int ClearedPayloads,
    int ClearedRanges);

/// <summary>
/// 保持 MES 任务与工程真实组结构一致。带 groupRef 的任务必须绑定当前工程中的完整组路径；
/// 未绑定的旧版任务保留，避免升级时误删用户尚未迁移的配置。
/// </summary>
public static class MesTaskTopology
{
    public static MesTaskTopologyResult Reconcile(IgwProject project)
    {
        if (project.MesUpload?.Tasks is not { Count: > 0 } tasks)
            return new MesTaskTopologyResult(0, 0, 0);

        project.Channels ??= new();
        project.Templates ??= new();
        var catalog = MesDataRangeCatalog.Build(project);
        int removed = 0;
        int clearedPayloads = 0;
        int clearedRanges = 0;

        for (int i = tasks.Count - 1; i >= 0; i--)
        {
            var task = tasks[i];
            string groupRef = task.GroupRef?.Trim() ?? "";
            if (groupRef.Length > 0)
            {
                if (!catalog.TryGet(groupRef, out var group))
                {
                    tasks.RemoveAt(i);
                    removed++;
                    continue;
                }
                task.GroupRef = group.SourcePath;
                task.DeviceRef = group.DevicePath;
            }

            ReconcilePayload(task.Payload, catalog, ref clearedPayloads, ref clearedRanges);
            foreach (var node in task.Flow?.Nodes ?? new())
                if (string.Equals(node.Type, "Upload", StringComparison.OrdinalIgnoreCase))
                    ReconcilePayload(node.Payload, catalog, ref clearedPayloads, ref clearedRanges);
        }

        return new MesTaskTopologyResult(removed, clearedPayloads, clearedRanges);
    }

    public static int PruneOrphans(IgwProject project)
    {
        return Reconcile(project).RemovedTasks;
    }

    private static void ReconcilePayload(
        MesPayload payload,
        MesDataRangeCatalog catalog,
        ref int clearedPayloads,
        ref int clearedRanges)
    {
        if (!MesPayloadModes.IsDataRanges(payload) || payload.DataRanges is not { Count: > 0 } ranges)
            return;

        bool stale = ranges.Any(range =>
        {
            try { range = MesArrayLengthPolicy.Resolve(range, catalog); }
            catch (ArgumentException) { return true; }
            if (string.IsNullOrWhiteSpace(range.SourcePath))
                return true;
            if (catalog.TryGetLogicalArray(range.SourcePath.Trim(), out var logicalArray) &&
                logicalArray.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
            {
                try
                {
                    if (!IsLogicalSliceValid(logicalArray.Descriptor, range)) return true;
                    if (!string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                        !HasLogicalMember(logicalArray.Descriptor, range.ArrayMemberPath)) return true;
                    return (range.Excludes ?? []).Any(excluded =>
                        string.IsNullOrWhiteSpace(excluded) ||
                        !HasLogicalMember(logicalArray.Descriptor, excluded.Trim()));
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    return true;
                }
            }
            if (catalog.TryGetIndexedArray(range.SourcePath.Trim(), out var indexedArray))
            {
                if ((range.Excludes?.Count ?? 0) > 0) return true;
                try
                {
                    if (range.ArrayStart is { Length: > 1 } || range.ArrayLengths is { Length: > 1 })
                        return true;
                    int start = range.ArrayStart is { Length: 1 }
                        ? range.ArrayStart[0] : indexedArray.LowerBound;
                    int length = range.ArrayLengths is { Length: 1 }
                        ? range.ArrayLengths[0] : indexedArray.Length;
                    long end = (long)start + length;
                    long sourceEnd = (long)indexedArray.LowerBound + indexedArray.Length;
                    if (length <= 0 || start < indexedArray.LowerBound || end > sourceEnd) return true;
                    return !string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                        !HasIndexedMember(indexedArray, range.ArrayMemberPath);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    return true;
                }
            }
            if (catalog.TryGetArray(range.SourcePath.Trim(), out var array))
            {
                if ((range.Excludes?.Count ?? 0) > 0) return true;
                try
                {
                    var start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart : array.Definition.LowerBounds;
                    var lengths = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths : array.Definition.Dimensions;
                    new ArraySlice(start, lengths).ValidateAndCreateShape(array.Definition.Shape);
                    return !string.IsNullOrWhiteSpace(range.ArrayMemberPath) &&
                        !HasMember(array.Definition.Struct, range.ArrayMemberPath);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                {
                    return true;
                }
            }
            // 单个普通点：点还在就有效(单点范围不能带排除项)
            if (!catalog.TryGet(range.SourcePath.Trim(), out var source))
                return !catalog.TryGetScalarTag(range.SourcePath.Trim(), out _, out _) || (range.Excludes?.Count ?? 0) > 0;

            var leaves = new HashSet<string>(RelativeLeafPaths(source), StringComparer.OrdinalIgnoreCase);
            return (range.Excludes ?? new()).Any(excluded =>
                string.IsNullOrWhiteSpace(excluded) || !leaves.Contains(excluded.Trim()));
        });
        if (!stale) return;

        clearedPayloads++;
        clearedRanges += ranges.Count;
        ranges.Clear();
    }

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
            var field = schema.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, segments[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.JsonName, segments[index], StringComparison.OrdinalIgnoreCase));
            if (field is null || field.Array is not null) return false;
            if (index == segments.Length - 1) return field.Struct is null;
            if (field.Struct is null) return false;
            schema = field.Struct;
        }
        return false;
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

    private static bool IsLogicalSliceValid(LogicalArrayDescriptor descriptor, MesDataRange range)
    {
        var start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart : descriptor.LowerBounds;
        var lengths = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths : descriptor.Dimensions;
        if (start.Length != descriptor.Dimensions.Length || lengths.Length != descriptor.Dimensions.Length)
            return false;
        for (int i = 0; i < start.Length; i++)
        {
            if (lengths[i] <= 0) return false;
            long end = (long)start[i] + lengths[i];
            long sourceEnd = (long)descriptor.LowerBounds[i] + descriptor.Dimensions[i];
            if (start[i] < descriptor.LowerBounds[i] || end > sourceEnd) return false;
        }
        return true;
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
}
