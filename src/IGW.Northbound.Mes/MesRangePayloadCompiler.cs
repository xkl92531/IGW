using System.Globalization;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

public abstract record CompiledMesSourceValue;
public sealed record CompiledMesSourceObject(IReadOnlyList<CompiledMesSourceProperty> Properties) : CompiledMesSourceValue;
public sealed record CompiledMesSourceArray(IReadOnlyList<CompiledMesSourceValue> Elements) : CompiledMesSourceValue;
public sealed record CompiledMesSourceField(string TagId) : CompiledMesSourceValue;
/// <param name="MemberNames">元素成员改名(相对元素的成员路径 → 新键名)；为空时按点位定义输出。</param>
/// <param name="OmittedMembers">报文里不输出的元素成员(相对元素的成员路径)。</param>
public sealed record CompiledMesSourceLogicalArray(
    string TagId,
    ArrayDef Definition,
    ArraySlice Slice,
    IReadOnlyList<string> MemberPath,
    IReadOnlyDictionary<string, string>? MemberNames = null,
    IReadOnlySet<string>? OmittedMembers = null) : CompiledMesSourceValue;
public sealed record CompiledMesSourceProperty(string Name, CompiledMesSourceValue Value);

public sealed record CompiledMesRange(
    string SourcePath,
    string DevicePath,
    IReadOnlyList<string> OutputSegments,
    CompiledMesSourceValue Source,
    int TagCount);

public abstract record CompiledMesOutputValue;
public sealed record CompiledMesOutputObject(IReadOnlyList<CompiledMesOutputProperty> Properties) : CompiledMesOutputValue;
public sealed record CompiledMesOutputRange(CompiledMesRange Range) : CompiledMesOutputValue;
public sealed record CompiledMesOutputProperty(string Name, CompiledMesOutputValue Value);

public sealed record CompiledMesPayload(
    CompiledMesOutputObject Root,
    IReadOnlyList<CompiledMesRange> Ranges,
    int TagCount);

public static class MesRangePayloadCompiler
{
    private sealed class OutputNode
    {
        public List<(string Name, OutputNode Node)> Children { get; } = new();
        public CompiledMesRange? Range { get; set; }
    }

    public static CompiledMesPayload Compile(MesPayload payload, MesDataRangeCatalog catalog, Func<string, int>? readArrayLength = null)
    {
        var root = new OutputNode();
        var ranges = new List<CompiledMesRange>(payload.DataRanges.Count);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int ReadOnce(string id) => counts.TryGetValue(id, out int value) ? value : counts[id] = readArrayLength!(id);
        foreach (var configuredRange in payload.DataRanges)
        {
            var range = MesArrayLengthPolicy.Resolve(configuredRange, catalog, readArrayLength is null ? null : ReadOnce);
            bool hasGroup = catalog.TryGet(range.SourcePath, out var foundGroup);
            MesCatalogArray? arraySource = null;
            bool hasArray = catalog.TryGetArray(range.SourcePath, out arraySource!);
            MesCatalogIndexedArray? indexedSource = null;
            bool hasIndexedArray = catalog.TryGetIndexedArray(range.SourcePath, out indexedSource!);
            MesCatalogLogicalArray? logicalSource = null;
            bool hasLogicalArray = catalog.TryGetLogicalArray(range.SourcePath, out logicalSource!);
            MesCatalogTag? tagSource = null;
            MesCatalogGroup? tagParent = null;
            bool hasTag = !hasGroup && !hasArray && !hasIndexedArray && !hasLogicalArray &&
                catalog.TryGetScalarTag(range.SourcePath, out tagSource!, out tagParent!);
            if (!hasGroup && !hasArray && !hasIndexedArray && !hasLogicalArray && !hasTag)
                throw new InvalidDataException($"MES 数据范围来源不存在:{range.SourcePath}");
            MesCatalogGroup? source = hasGroup ? foundGroup : null;
            string outputPath = string.IsNullOrWhiteSpace(range.OutputPath)
                ? logicalSource?.DefaultOutputPath ?? indexedSource?.DefaultOutputPath ??
                  source?.DefaultOutputPath ?? arraySource?.DefaultOutputPath ??
                  tagParent!.DefaultOutputPath + "." + tagSource!.Name
                : range.OutputPath.Trim();
            string[] segments = outputPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
                throw new InvalidDataException($"MES 数据范围输出路径为空:{range.SourcePath}");

            var excludes = new HashSet<string>(range.Excludes ?? new(), StringComparer.OrdinalIgnoreCase);
            int tagCount = 0;
            CompiledMesSourceValue sourceValue;
            string devicePath;
            if (MesArrayLengthPolicy.IsAutomatic(configuredRange) && range.ArrayLengths.Contains(0))
            {
                sourceValue = new CompiledMesSourceArray([]);
                devicePath = arraySource?.DevicePath ?? logicalSource?.DevicePath ?? indexedSource!.DevicePath;
            }
            else if (arraySource is not null)
            {
                var slice = BuildArraySlice(arraySource.Definition, range);
                sourceValue = new CompiledMesSourceLogicalArray(
                    arraySource.TagId,
                    arraySource.Definition,
                    slice,
                    SplitMemberPath(range.ArrayMemberPath));
                tagCount = checked((int)slice.ValidateAndCreateShape(arraySource.Definition.Shape).ElementCount);
                devicePath = arraySource.DevicePath;
            }
            else if (logicalSource is not null &&
                     logicalSource.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
            {
                sourceValue = CompileRecognizedArray(logicalSource, range, out tagCount);
                devicePath = logicalSource.DevicePath;
            }
            else if (indexedSource is not null)
            {
                sourceValue = CompileIndexedArray(indexedSource, range, catalog, out tagCount);
                devicePath = indexedSource.DevicePath;
            }
            else if (tagSource is not null)
            {
                // 单个普通点：报文里就是这一个值，键取输出路径最后一段
                sourceValue = new CompiledMesSourceField(tagSource.TagId);
                tagCount = 1;
                devicePath = tagParent!.DevicePath;
            }
            else
            {
                sourceValue = CompileGroupObject(source!, "", excludes, catalog, ref tagCount);
                devicePath = source!.DevicePath;
            }
            sourceValue = MesPayloadNames.Apply(sourceValue, range.Names, range.Omit, range.SourcePath);
            var compiled = new CompiledMesRange(
                range.SourcePath,
                devicePath,
                segments,
                sourceValue,
                tagCount);
            Insert(root, segments, compiled);
            ranges.Add(compiled);
        }

        return new CompiledMesPayload(
            Freeze(root),
            ranges,
            ranges.Sum(range => range.TagCount));
    }

    private static CompiledMesSourceValue CompileGroupValue(
        MesCatalogGroup group,
        string relativeGroupPath,
        HashSet<string> excludes,
        MesDataRangeCatalog catalog,
        ref int tagCount)
    {
        if (catalog.TryGetLogicalArray(group.SourcePath, out var logical) &&
            logical.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
        {
            var value = CompileRecognizedArray(logical, FullLogicalRange(logical), out int logicalTags);
            tagCount = checked(tagCount + logicalTags);
            return value;
        }
        if (catalog.TryGetIndexedArray(group.SourcePath, out var indexed))
        {
            var all = new MesDataRange
            {
                SourcePath = indexed.SourcePath,
                Scope = "Array",
                ArrayStart = [indexed.LowerBound],
                ArrayLengths = [indexed.Length],
            };
            var value = CompileIndexedArray(indexed, all, catalog, out int arrayTags, relativeGroupPath, excludes);
            tagCount = checked(tagCount + arrayTags);
            return value;
        }
        return CompileGroupObject(group, relativeGroupPath, excludes, catalog, ref tagCount);
    }

    private static CompiledMesSourceObject CompileGroupObject(
        MesCatalogGroup group,
        string relativeGroupPath,
        HashSet<string> excludes,
        MesDataRangeCatalog catalog,
        ref int tagCount)
    {
        var properties = new List<CompiledMesSourceProperty>();
        foreach (var tag in group.Tags)
        {
            string relativeTagPath = Join(relativeGroupPath, tag.Name);
            if (excludes.Contains(relativeTagPath)) continue;
            if (tag.Array is not null)
            {
                properties.Add(new CompiledMesSourceProperty(tag.Name,
                    new CompiledMesSourceLogicalArray(tag.TagId, tag.Array,
                        new ArraySlice(tag.Array.LowerBounds, tag.Array.Dimensions), [])));
                tagCount = checked(tagCount + (int)tag.Array.Shape.ElementCount);
            }
            else
            {
                properties.Add(new CompiledMesSourceProperty(tag.Name, new CompiledMesSourceField(tag.TagId)));
                tagCount++;
            }
        }

        var consumed = new HashSet<int>();
        foreach (var logical in catalog.GetLogicalChildren(group.SourcePath)
                     .Where(child => child.Descriptor.BindingKind != ArrayBindingKind.NativeTag))
        {
            var boundTagIds = logical.Descriptor.Leaves.Select(leaf => leaf.TagId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int position = 0; position < group.Children.Count; position++)
            {
                if (GroupTagIds(group.Children[position]).Any(boundTagIds.Contains)) consumed.Add(position);
            }

            var array = CompileRecognizedArray(logical, FullLogicalRange(logical), out int logicalTags);
            tagCount = checked(tagCount + logicalTags);
            properties.Add(new CompiledMesSourceProperty(logical.Name, array));
        }
        for (int i = 0; i < group.Children.Count; i++)
        {
            if (!consumed.Add(i)) continue;
            var child = group.Children[i];
            if (!TryIndexedName(child.Name, out string arrayName, out _))
            {
                properties.Add(new CompiledMesSourceProperty(
                    child.Name,
                    CompileGroupValue(child, Join(relativeGroupPath, child.Name), excludes, catalog, ref tagCount)));
                continue;
            }

            string syntheticPath = group.SourcePath + "." + arrayName;
            if (!catalog.TryGetIndexedArray(syntheticPath, out var indexed))
            {
                properties.Add(new CompiledMesSourceProperty(
                    child.Name,
                    CompileGroupObject(child, Join(relativeGroupPath, child.Name), excludes, catalog, ref tagCount)));
                continue;
            }
            foreach (var element in indexed.Elements)
            {
                for (int position = 0; position < group.Children.Count; position++)
                    if (ReferenceEquals(group.Children[position], element.Group)) consumed.Add(position);
            }
            var all = new MesDataRange
            {
                SourcePath = indexed.SourcePath,
                Scope = "Array",
                ArrayStart = [indexed.LowerBound],
                ArrayLengths = [indexed.Length],
            };
            var array = CompileIndexedArray(indexed, all, catalog, out int arrayTags,
                relativeGroupPath, excludes);
            tagCount = checked(tagCount + arrayTags);
            properties.Add(new CompiledMesSourceProperty(arrayName, array));
        }
        return new CompiledMesSourceObject(properties);
    }

    private static MesDataRange FullLogicalRange(MesCatalogLogicalArray logical) => new()
    {
        SourcePath = logical.SourcePath,
        Scope = "Array",
        ArrayStart = [.. logical.Descriptor.LowerBounds],
        ArrayLengths = [.. logical.Descriptor.Dimensions],
    };

    private static IEnumerable<string> GroupTagIds(MesCatalogGroup group)
    {
        foreach (var tag in group.Tags) yield return tag.TagId;
        foreach (var child in group.Children)
        foreach (string tagId in GroupTagIds(child))
            yield return tagId;
    }

    private static CompiledMesSourceValue CompileIndexedArray(
        MesCatalogIndexedArray source,
        MesDataRange range,
        MesDataRangeCatalog catalog,
        out int tagCount,
        string relativeGroupPath = "",
        HashSet<string>? excludes = null)
    {
        int start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart[0] : source.LowerBound;
        int length = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths[0] : source.Length;
        ValidateIndexedSlice(source, start, length);
        long end = (long)start + length;
        var values = new List<CompiledMesSourceValue>(length);
        tagCount = 0;
        foreach (var element in source.Elements)
        {
            if (element.Index < start || element.Index >= end) continue;
            int elementTags = 0;
            var elementObject = CompileGroupObject(
                element.Group,
                Join(relativeGroupPath, element.Group.Name),
                excludes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                catalog,
                ref elementTags);
            CompiledMesSourceValue selected = string.IsNullOrWhiteSpace(range.ArrayMemberPath)
                ? elementObject
                : SelectMember(elementObject, SplitMemberPath(range.ArrayMemberPath), source.SourcePath);
            values.Add(selected);
            tagCount = checked(tagCount + (string.IsNullOrWhiteSpace(range.ArrayMemberPath) ? elementTags : 1));
        }
        return new CompiledMesSourceArray(values);
    }

    private static CompiledMesSourceValue CompileRecognizedArray(
        MesCatalogLogicalArray source,
        MesDataRange range,
        out int tagCount)
    {
        var descriptor = source.Descriptor;
        int rank = descriptor.Dimensions.Length;
        if (rank == 0) throw new InvalidDataException($"MES 逻辑数组没有维度:{source.SourcePath}");
        var start = range.ArrayStart is { Length: > 0 }
            ? range.ArrayStart
            : descriptor.LowerBounds;
        var lengths = range.ArrayLengths is { Length: > 0 }
            ? range.ArrayLengths
            : descriptor.Dimensions;
        ValidateRecognizedSlice(descriptor, start, lengths);

        var tuples = descriptor.Leaves
            .Where(binding => binding.Indices.Length >= rank)
            .Select(binding => binding.Indices.Take(rank).ToArray())
            .Where(indices => Inside(indices, start, lengths))
            .GroupBy(IndexKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(IndexKey, StringComparer.Ordinal)
            .ToArray();
        tagCount = 0;
        var excludes = new HashSet<string>(range.Excludes ?? [], StringComparer.OrdinalIgnoreCase);
        var compiled = CompileRecognizedDimension(
            descriptor,
            tuples,
            dimension: 0,
            prefixIndices: [],
            indexOffset: 0,
            memberPrefix: "",
            excludes,
            ref tagCount);
        if (string.IsNullOrWhiteSpace(range.ArrayMemberPath)) return compiled;

        var memberPath = SplitMemberPath(range.ArrayMemberPath);
        return SelectMemberFromArray(compiled, memberPath, source.SourcePath);
    }

    private static CompiledMesSourceValue CompileRecognizedDimension(
        LogicalArrayDescriptor descriptor,
        IReadOnlyList<int[]> tuples,
        int dimension,
        IReadOnlyList<int> prefixIndices,
        int indexOffset,
        string memberPrefix,
        HashSet<string> excludes,
        ref int tagCount)
    {
        var elements = new List<CompiledMesSourceValue>();
        foreach (var group in tuples.GroupBy(tuple => tuple[dimension]).OrderBy(group => group.Key))
        {
            if (dimension + 1 < descriptor.Dimensions.Length)
            {
                elements.Add(CompileRecognizedDimension(
                    descriptor,
                    group.ToArray(),
                    dimension + 1,
                    prefixIndices,
                    indexOffset,
                    memberPrefix,
                    excludes,
                    ref tagCount));
                continue;
            }

            var tuple = group.First();
            var fullPrefix = prefixIndices.Concat(tuple).ToArray();
            elements.Add(CompileRecognizedElement(
                descriptor,
                fullPrefix,
                indexOffset + descriptor.Dimensions.Length,
                memberPrefix,
                excludes,
                ref tagCount));
        }
        return new CompiledMesSourceArray(elements);
    }

    private static CompiledMesSourceValue CompileRecognizedElement(
        LogicalArrayDescriptor descriptor,
        IReadOnlyList<int> fullPrefix,
        int nestedIndexOffset,
        string memberPrefix,
        HashSet<string> excludes,
        ref int tagCount)
    {
        var properties = new List<CompiledMesSourceProperty>();
        foreach (var member in descriptor.Members)
        {
            string fullMemberPath = Join(memberPrefix, member.Path);
            if (excludes.Contains(fullMemberPath) || excludes.Contains(member.Path)) continue;
            if (member.NestedArray is not null)
            {
                var nested = CompileNestedRecognizedArray(
                    member.NestedArray,
                    fullPrefix,
                    nestedIndexOffset,
                    fullMemberPath,
                    excludes,
                    ref tagCount);
                properties.Add(new CompiledMesSourceProperty(member.JsonName, nested));
                continue;
            }

            var binding = descriptor.Leaves.FirstOrDefault(candidate =>
                StartsWith(candidate.Indices, fullPrefix) &&
                (string.Equals(candidate.MemberPath, fullMemberPath, StringComparison.OrdinalIgnoreCase) ||
                 descriptor.Members.Count == 1 && string.IsNullOrWhiteSpace(candidate.MemberPath)));
            if (binding is null) continue;
            properties.Add(new CompiledMesSourceProperty(member.JsonName, new CompiledMesSourceField(binding.TagId)));
            tagCount++;
        }

        if (properties.Count == 1 && descriptor.Members.Count == 1 &&
            string.IsNullOrWhiteSpace(descriptor.Leaves.FirstOrDefault()?.MemberPath))
            return properties[0].Value;
        return new CompiledMesSourceObject(properties);
    }

    private static CompiledMesSourceValue CompileNestedRecognizedArray(
        LogicalArrayDescriptor descriptor,
        IReadOnlyList<int> parentPrefix,
        int indexOffset,
        string memberPrefix,
        HashSet<string> excludes,
        ref int tagCount)
    {
        int rank = descriptor.Dimensions.Length;
        var tuples = descriptor.Leaves
            .Where(binding => binding.Indices.Length >= indexOffset + rank &&
                              StartsWith(binding.Indices, parentPrefix))
            .Select(binding => binding.Indices.Skip(indexOffset).Take(rank).ToArray())
            .GroupBy(IndexKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(IndexKey, StringComparer.Ordinal)
            .ToArray();
        return CompileRecognizedDimension(
            descriptor,
            tuples,
            dimension: 0,
            parentPrefix,
            indexOffset,
            memberPrefix,
            excludes,
            ref tagCount);
    }

    private static CompiledMesSourceValue SelectMemberFromArray(
        CompiledMesSourceValue source,
        IReadOnlyList<string> path,
        string sourcePath)
    {
        if (source is not CompiledMesSourceArray array) return source;
        return new CompiledMesSourceArray(array.Elements.Select(element => element switch
        {
            CompiledMesSourceArray nested => SelectMemberFromArray(nested, path, sourcePath),
            CompiledMesSourceObject item => SelectMember(item, path, sourcePath),
            _ => throw new InvalidDataException(
                $"MES 数组成员路径不是结构体:{sourcePath}.{string.Join('.', path)}"),
        }).ToArray());
    }

    private static void ValidateRecognizedSlice(
        LogicalArrayDescriptor descriptor,
        IReadOnlyList<int> start,
        IReadOnlyList<int> lengths)
    {
        if (start.Count != descriptor.Dimensions.Length || lengths.Count != descriptor.Dimensions.Length)
            throw new InvalidDataException($"MES 逻辑数组切片秩不一致:{descriptor.SourcePath}");
        for (int i = 0; i < start.Count; i++)
        {
            if (lengths[i] <= 0) throw new ArgumentOutOfRangeException(nameof(lengths), "数组切片数量必须大于零。");
            long end = (long)start[i] + lengths[i];
            long sourceEnd = (long)descriptor.LowerBounds[i] + descriptor.Dimensions[i];
            if (start[i] < descriptor.LowerBounds[i] || end > sourceEnd)
                throw new IndexOutOfRangeException($"数组第 {i + 1} 维切片超出实际范围。");
        }
    }

    private static bool Inside(IReadOnlyList<int> indices, IReadOnlyList<int> start, IReadOnlyList<int> lengths)
    {
        for (int i = 0; i < start.Count; i++)
            if (indices[i] < start[i] || indices[i] >= (long)start[i] + lengths[i]) return false;
        return true;
    }

    private static bool StartsWith(IReadOnlyList<int> values, IReadOnlyList<int> prefix)
    {
        if (values.Count < prefix.Count) return false;
        for (int i = 0; i < prefix.Count; i++)
            if (values[i] != prefix[i]) return false;
        return true;
    }

    private static string IndexKey(IEnumerable<int> indices) => string.Join(',', indices);

    private static void ValidateIndexedSlice(MesCatalogIndexedArray source, int start, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "数组切片数量必须大于零。");
        long end = (long)start + length;
        long sourceEnd = (long)source.LowerBound + source.Length;
        if (start < source.LowerBound || end > sourceEnd)
            throw new IndexOutOfRangeException(
                $"数组切片 [{start}..{end - 1}] 超出实际范围 [{source.LowerBound}..{sourceEnd - 1}]。");
    }

    private static CompiledMesSourceValue SelectMember(
        CompiledMesSourceObject source,
        IReadOnlyList<string> path,
        string sourcePath)
    {
        CompiledMesSourceValue current = source;
        foreach (string segment in path)
        {
            if (current is not CompiledMesSourceObject obj)
                throw new InvalidDataException($"MES 数组成员路径不是结构体:{sourcePath}.{string.Join('.', path)}");
            var property = obj.Properties.FirstOrDefault(item =>
                string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                throw new InvalidDataException($"MES 数组成员不存在:{sourcePath}.{string.Join('.', path)}");
            current = property.Value;
        }
        return current;
    }

    private static string Join(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : parent + "." + name;

    private static bool TryIndexedName(string name, out string baseName, out int index) =>
        MesIndexedGroupName.TryParse(name, out baseName, out index);

    private static ArraySlice BuildArraySlice(ArrayDef definition, MesDataRange range)
    {
        var start = range.ArrayStart is { Length: > 0 }
            ? range.ArrayStart
            : definition.LowerBounds;
        var lengths = range.ArrayLengths is { Length: > 0 }
            ? range.ArrayLengths
            : definition.Dimensions;
        var slice = new ArraySlice(start, lengths);
        slice.ValidateAndCreateShape(definition.Shape);
        return slice;
    }

    private static IReadOnlyList<string> SplitMemberPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? [] : path.Split('.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void Insert(OutputNode root, IReadOnlyList<string> segments, CompiledMesRange range)
    {
        var current = root;
        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i];
            var child = current.Children.FirstOrDefault(item =>
                string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child.Node is null)
            {
                child = (segment, new OutputNode());
                current.Children.Add(child);
            }
            current = child.Node;
        }
        if (current.Range is not null || current.Children.Count > 0)
            throw new InvalidDataException($"MES 数据范围输出路径冲突:{string.Join('.', segments)}");
        current.Range = range;
    }

    private static CompiledMesOutputObject Freeze(OutputNode node) =>
        new(node.Children.Select(child => new CompiledMesOutputProperty(
            child.Name,
            child.Node.Range is not null
                ? new CompiledMesOutputRange(child.Node.Range)
                : Freeze(child.Node))).ToList());
}
