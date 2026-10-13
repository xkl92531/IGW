using System.Globalization;
using IGW.Abstractions;

namespace IGW.Core;

internal sealed record ArrayRecognitionLeaf(
    TagDef Tag,
    string TagId,
    ArrayAddressEvidence? AddressEvidence);

internal sealed class ArrayRecognitionGroup
{
    public required TagGroupDef Definition { get; init; }
    public required string SourcePath { get; init; }
    public required string DevicePath { get; init; }
    public required IReadOnlyList<ArrayRecognitionLeaf> DirectLeaves { get; init; }
    public required IReadOnlyList<ArrayRecognitionGroup> Children { get; init; }
    public required IReadOnlyList<ArrayRecognitionLeaf> AllLeaves { get; init; }
}

internal static class ArrayShapeRecognizer
{
    public static void Collect(
        ArrayRecognitionGroup group,
        IDictionary<string, LogicalArrayDescriptor> arrays,
        ICollection<ArrayRecognitionDiagnostic> diagnostics,
        ISet<string> consumedLeafIds)
    {
        foreach (var child in group.Children)
            Collect(child, arrays, diagnostics, consumedLeafIds);

        CollectNative(group, arrays);

        var parsed = group.AllLeaves
            .Where(leaf => leaf.Tag.Array is null && leaf.AddressEvidence is not null &&
                           !consumedLeafIds.Contains(leaf.TagId))
            .ToArray();

        foreach (var candidate in parsed.GroupBy(
                     leaf => leaf.AddressEvidence!.RootIdentity,
                     StringComparer.OrdinalIgnoreCase))
        {
            var leaves = candidate.ToArray();
            if (!HasMultipleRootElements(leaves)) continue;

            if (!HasConsistentElementSchema(leaves, out string conflict))
            {
                diagnostics.Add(new ArrayRecognitionDiagnostic(
                    "ArraySchemaConflict",
                    group.SourcePath,
                    conflict));
                continue;
            }

            string rootName = leaves[0].AddressEvidence!.IndexedSegments[0].Name;
            string sourcePath = ResolveSourcePath(group, leaves, parsed, rootName);
            if (arrays.ContainsKey(sourcePath)) continue;

            var descriptor = BuildDescriptor(
                sourcePath[(sourcePath.LastIndexOf('.') + 1)..],
                sourcePath,
                group.DevicePath,
                leaves,
                segmentIndex: 0);
            arrays[sourcePath] = descriptor;
            foreach (var leaf in leaves) consumedLeafIds.Add(leaf.TagId);
        }

        if (parsed.Length == 0 && LooksLikeUnprovenExpandedArray(group))
        {
            diagnostics.Add(new ArrayRecognitionDiagnostic(
                "ArrayEvidenceMissing",
                group.SourcePath,
                "重复目录只有显示名称相似，真实地址中没有可证明的数组下标，已保留原结构。"));
        }
    }

    private static void CollectNative(
        ArrayRecognitionGroup group,
        IDictionary<string, LogicalArrayDescriptor> arrays)
    {
        foreach (var leaf in group.DirectLeaves)
        {
            var definition = leaf.Tag.Array;
            if (definition is null || definition.Dimensions.Length == 0) continue;
            var path = leaf.TagId;
            if (arrays.ContainsKey(path)) continue;
            long elementCount = definition.Shape.ElementCount;
            arrays[path] = new LogicalArrayDescriptor(
                leaf.Tag.Name,
                path,
                group.DevicePath,
                LogicalArrayShapeKind.Rectangular,
                [.. definition.Dimensions],
                definition.LowerBounds.Length == definition.Dimensions.Length
                    ? [.. definition.LowerBounds]
                    : new int[definition.Dimensions.Length],
                [],
                NativeMembers(definition, path, group.DevicePath, leaf.Tag.Access),
                [],
                ArrayBindingKind.NativeTag,
                SourceFormat(leaf.Tag, "IGW.ArrayDef"),
                ArrayRecognitionConfidence.Explicit,
                [definition.SourceShape ?? "ArrayDef"],
                elementCount);
        }
    }

    private static IReadOnlyList<LogicalArrayMember> NativeMembers(
        ArrayDef definition,
        string sourcePath,
        string devicePath,
        TagAccess access)
    {
        if (definition.Struct is null) return [];
        return definition.Struct.Fields.Select(field =>
        {
            LogicalArrayDescriptor? nested = null;
            if (field.Array is { Dimensions.Length: > 0 } child)
            {
                nested = new LogicalArrayDescriptor(
                    field.Name,
                    sourcePath + "." + field.Name,
                    devicePath,
                    LogicalArrayShapeKind.Rectangular,
                    [.. child.Dimensions],
                    child.LowerBounds.Length == child.Dimensions.Length
                        ? [.. child.LowerBounds]
                        : new int[child.Dimensions.Length],
                    [],
                    NativeMembers(child, sourcePath + "." + field.Name, devicePath, access),
                    [],
                    ArrayBindingKind.NativeTag,
                    "IGW.ArrayDef",
                    ArrayRecognitionConfidence.Explicit,
                    [child.SourceShape ?? "StructField.ArrayDef"],
                    child.Shape.ElementCount);
            }
            return new LogicalArrayMember(
                field.Name,
                string.IsNullOrWhiteSpace(field.JsonName) ? field.Name : field.JsonName!,
                field.Array?.ElementType ?? field.DataType,
                access,
                nested);
        }).ToArray();
    }

    private static string SourceFormat(TagDef tag, string fallback) =>
        tag.Metadata is not null && tag.Metadata.TryGetValue("array.sourceFormat", out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static bool HasMultipleRootElements(IReadOnlyList<ArrayRecognitionLeaf> leaves) =>
        leaves.Select(leaf => Key(leaf.AddressEvidence!.IndexedSegments[0].Indices))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() == 2;

    private static bool HasConsistentElementSchema(
        IReadOnlyList<ArrayRecognitionLeaf> leaves,
        out string conflict)
    {
        conflict = "";
        var signatures = leaves.GroupBy(
                leaf => Key(leaf.AddressEvidence!.IndexedSegments[0].Indices),
                StringComparer.Ordinal)
            .Select(group => string.Join('|', group
                .Select(SchemaLeaf)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (signatures.Length <= 1) return true;
        conflict = "数组候选的不同下标具有不一致的成员名、类型、权限或嵌套结构。";
        return false;
    }

    private static string SchemaLeaf(ArrayRecognitionLeaf leaf)
    {
        var evidence = leaf.AddressEvidence!;
        string nested = string.Join('/', evidence.IndexedSegments.Skip(1).Select(segment => segment.Name));
        return string.Join(':', evidence.MemberPath, nested, leaf.Tag.DataType, leaf.Tag.Access,
            leaf.Tag.StringLength?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private static string ResolveSourcePath(
        ArrayRecognitionGroup group,
        IReadOnlyList<ArrayRecognitionLeaf> candidate,
        IReadOnlyList<ArrayRecognitionLeaf> allParsed,
        string rootName)
    {
        bool coversWholeGroup = candidate.Count == group.AllLeaves.Count && candidate.Count == allParsed.Count;
        if (coversWholeGroup && TryGetProvenSiblingArrayName(group, candidate, out string siblingArrayName))
            return group.SourcePath + "." + siblingArrayName;
        bool childrenExposeRootName = group.Children.Count > 1 && group.Children.All(child =>
            child.Definition.Name.Contains(rootName, StringComparison.OrdinalIgnoreCase));
        return coversWholeGroup && !childrenExposeRootName
            ? group.SourcePath
            : group.SourcePath + "." + rootName;
    }

    private static bool TryGetProvenSiblingArrayName(
        ArrayRecognitionGroup group,
        IReadOnlyList<ArrayRecognitionLeaf> candidate,
        out string arrayName)
    {
        arrayName = "";
        if (group.Children.Count < 2) return false;

        var candidateIds = candidate.Select(leaf => leaf.TagId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elementNames = new List<string>(group.Children.Count);
        var elementIndices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in group.Children)
        {
            string? rootIndex = null;
            foreach (ArrayRecognitionLeaf leaf in child.AllLeaves)
            {
                if (!candidateIds.Contains(leaf.TagId)) continue;
                string current = Key(leaf.AddressEvidence!.IndexedSegments[0].Indices);
                if (rootIndex is null) rootIndex = current;
                else if (!string.Equals(rootIndex, current, StringComparison.Ordinal)) return false;
            }
            if (rootIndex is null || !elementIndices.Add(rootIndex)) return false;
            if (!MesIndexedGroupName.TryParse(child.Definition.Name, out string baseName, out _)) return false;
            elementNames.Add(baseName);
        }

        if (elementNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return false;
        arrayName = elementNames[0];
        return !string.IsNullOrWhiteSpace(arrayName);
    }

    private static LogicalArrayDescriptor BuildDescriptor(
        string name,
        string sourcePath,
        string devicePath,
        IReadOnlyList<ArrayRecognitionLeaf> leaves,
        int segmentIndex)
    {
        var indices = leaves
            .Where(leaf => leaf.AddressEvidence!.IndexedSegments.Count > segmentIndex)
            .Select(leaf => leaf.AddressEvidence!.IndexedSegments[segmentIndex].Indices)
            .ToArray();
        var shape = Shape(indices);

        var members = new List<LogicalArrayMember>();
        foreach (var scalarGroup in leaves
                     .Where(leaf => leaf.AddressEvidence!.IndexedSegments.Count == segmentIndex + 1)
                     .GroupBy(leaf => ScalarMemberPath(leaf, segmentIndex), StringComparer.OrdinalIgnoreCase))
        {
            var first = scalarGroup.First();
            string memberPath = scalarGroup.Key;
            members.Add(new LogicalArrayMember(
                memberPath,
                memberPath,
                first.Tag.DataType,
                first.Tag.Access,
                null));
        }

        foreach (var nestedGroup in leaves
                     .Where(leaf => leaf.AddressEvidence!.IndexedSegments.Count > segmentIndex + 1)
                     .GroupBy(leaf => leaf.AddressEvidence!.IndexedSegments[segmentIndex + 1].Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            var nestedLeaves = nestedGroup.ToArray();
            var nested = BuildDescriptor(
                nestedGroup.Key,
                sourcePath + "." + nestedGroup.Key,
                devicePath,
                nestedLeaves,
                segmentIndex + 1);
            members.Add(new LogicalArrayMember(
                nestedGroup.Key,
                nestedGroup.Key,
                IgwDataType.Struct,
                nestedLeaves[0].Tag.Access,
                nested));
        }

        bool jagged = IsJaggedAtSegment(leaves, segmentIndex) ||
                      members.Any(member => member.NestedArray?.ShapeKind == LogicalArrayShapeKind.Jagged);
        var kind = jagged ? LogicalArrayShapeKind.Jagged : shape.Kind;
        var unique = DistinctIndices(indices);
        var existing = kind == LogicalArrayShapeKind.Rectangular ? [] : unique;
        var bindings = leaves.Select(leaf => new LogicalArrayLeafBinding(
                [.. leaf.AddressEvidence!.FlattenedIndices],
                leaf.AddressEvidence.MemberPath,
                leaf.TagId,
                leaf.Tag.Address))
            .OrderBy(binding => Key(binding.Indices), StringComparer.Ordinal)
            .ThenBy(binding => binding.MemberPath, StringComparer.Ordinal)
            .ToArray();
        var sourceFormats = leaves.Select(leaf => leaf.AddressEvidence!.SourceFormat)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string sourceFormat = sourceFormats.Length == 1 ? sourceFormats[0] : "Mixed";
        long elementCount = kind == LogicalArrayShapeKind.Rectangular
            ? shape.Dimensions.Aggregate(1L, (total, dimension) => checked(total * dimension))
            : unique.Count;

        return new LogicalArrayDescriptor(
            name,
            sourcePath,
            devicePath,
            kind,
            shape.Dimensions,
            shape.LowerBounds,
            existing,
            members.OrderBy(member => member.Path, StringComparer.Ordinal).ToArray(),
            bindings,
            ArrayBindingKind.ExpandedLeaves,
            sourceFormat,
            ArrayRecognitionConfidence.Proven,
            leaves.Select(leaf => leaf.AddressEvidence!.CanonicalPattern)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            elementCount);
    }

    private static string ScalarMemberPath(ArrayRecognitionLeaf leaf, int segmentIndex)
    {
        string path = leaf.AddressEvidence!.MemberPath;
        var prefixes = leaf.AddressEvidence.IndexedSegments.Skip(1).Take(segmentIndex)
            .Select(segment => segment.Name)
            .ToArray();
        string prefix = string.Join('.', prefixes);
        if (prefix.Length > 0 && path.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            path = path[(prefix.Length + 1)..];
        return string.IsNullOrWhiteSpace(path) ? leaf.Tag.Name : path;
    }

    private static bool IsJaggedAtSegment(IReadOnlyList<ArrayRecognitionLeaf> leaves, int segmentIndex)
    {
        if (segmentIndex == 0)
        {
            return leaves.Any(leaf => leaf.AddressEvidence!.IndexedSegments.Count > 1) &&
                   NestedSetsDiffer(leaves, 1);
        }
        return NestedSetsDiffer(leaves, segmentIndex);
    }

    private static bool NestedSetsDiffer(IReadOnlyList<ArrayRecognitionLeaf> leaves, int segmentIndex)
    {
        if (leaves.All(leaf => leaf.AddressEvidence!.IndexedSegments.Count <= segmentIndex)) return false;
        var setSignatures = leaves
            .Where(leaf => leaf.AddressEvidence!.IndexedSegments.Count > segmentIndex)
            .GroupBy(leaf => PrefixKey(leaf.AddressEvidence!, segmentIndex), StringComparer.Ordinal)
            .Select(group => string.Join('|', group
                .Select(leaf => Key(leaf.AddressEvidence!.IndexedSegments[segmentIndex].Indices))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();
        return setSignatures > 1;
    }

    private static string PrefixKey(ArrayAddressEvidence evidence, int segmentIndex) =>
        string.Join('/', evidence.IndexedSegments.Take(segmentIndex).Select(segment => Key(segment.Indices)));

    private static (LogicalArrayShapeKind Kind, int[] Dimensions, int[] LowerBounds) Shape(
        IReadOnlyList<int[]> source)
    {
        var unique = DistinctIndices(source);
        int rank = unique.Count == 0 ? 1 : unique[0].Length;
        if (unique.Any(indices => indices.Length != rank))
            return (LogicalArrayShapeKind.Jagged, [Math.Max(1, unique.Count)], [0]);
        var lower = new int[rank];
        var dimensions = new int[rank];
        long expected = 1;
        for (int dimension = 0; dimension < rank; dimension++)
        {
            int minimum = unique.Min(indices => indices[dimension]);
            int maximum = unique.Max(indices => indices[dimension]);
            lower[dimension] = minimum;
            dimensions[dimension] = checked(maximum - minimum + 1);
            expected = checked(expected * dimensions[dimension]);
        }
        return (expected == unique.Count ? LogicalArrayShapeKind.Rectangular : LogicalArrayShapeKind.Sparse,
            dimensions, lower);
    }

    private static IReadOnlyList<int[]> DistinctIndices(IEnumerable<int[]> source) => source
        .GroupBy(Key, StringComparer.Ordinal)
        .Select(group => group.First().ToArray())
        .OrderBy(Key, StringComparer.Ordinal)
        .ToArray();

    private static string Key(IEnumerable<int> values) => string.Join(',', values);

    private static bool LooksLikeUnprovenExpandedArray(ArrayRecognitionGroup group)
    {
        if (group.Children.Count < 2 || group.AllLeaves.Count < 2) return false;
        bool indexedNames = group.Children.All(child =>
            int.TryParse(child.Definition.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            MesIndexedGroupName.TryParse(child.Definition.Name, out _, out _));
        if (!indexedNames) return false;
        string first = DisplaySchema(group.Children[0]);
        return first.Length > 0 && group.Children.Skip(1)
            .All(child => string.Equals(DisplaySchema(child), first, StringComparison.Ordinal));
    }

    private static string DisplaySchema(ArrayRecognitionGroup group) => string.Join('|',
        group.AllLeaves.Select(leaf => $"{leaf.Tag.Name}:{leaf.Tag.DataType}")
            .OrderBy(value => value, StringComparer.Ordinal));
}
