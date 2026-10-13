using IGW.Abstractions;

namespace IGW.Core;

/// <summary>MES 范围目录中的叶子点。RelativePath 相对其直接父组。</summary>
public sealed record MesCatalogTag(
    string Name,
    string RelativePath,
    string TagId,
    IgwDataType DataType,
    int Order,
    ArrayDef? Array,
    TagAccess Access = TagAccess.Read);

public sealed record MesCatalogArray(
    string Name,
    string SourcePath,
    string DevicePath,
    string DefaultOutputPath,
    string TagId,
    ArrayDef Definition,
    int Order);

/// <summary>由分组结构表达的一个数组元素；Group 保留真实 PLC/OPC UA 点位路径。</summary>
public sealed record MesCatalogIndexedElement(int Index, MesCatalogGroup Group);

/// <summary>
/// 由一批同构下标组表达的结构体数组。支持两类实际结构：
/// Parent/0..N/Member，以及 Parent/Name[0]..Name[N]（含 Name_N）。
/// </summary>
public sealed record MesCatalogIndexedArray(
    string Name,
    string SourcePath,
    string DevicePath,
    string DefaultOutputPath,
    IReadOnlyList<MesCatalogIndexedElement> Elements,
    int LowerBound,
    int Length,
    int TagCount);

/// <summary>由统一来源证据识别出的逻辑数组；保留每个展开叶的真实 TagId。</summary>
public sealed record MesCatalogLogicalArray(
    string Name,
    string SourcePath,
    string DevicePath,
    string DefaultOutputPath,
    LogicalArrayDescriptor Descriptor,
    int TagCount);

/// <summary>MES 范围目录中的组；Children/Tags 顺序与工程树一致。</summary>
public sealed record MesCatalogGroup(
    string Name,
    string SourcePath,
    string RelativePath,
    string DevicePath,
    string DefaultOutputPath,
    IReadOnlyList<MesCatalogTag> Tags,
    IReadOnlyList<MesCatalogGroup> Children,
    int TagCount);

/// <summary>
/// 从工程树一次性建立的 MES 数据范围目录。模板实例在建立目录时展开，运行周期不再遍历工程。
/// </summary>
public sealed class MesDataRangeCatalog
{
    private readonly Dictionary<string, MesCatalogGroup> _groups;
    private readonly Dictionary<string, MesCatalogArray> _arrays;
    private readonly Dictionary<string, MesCatalogIndexedArray> _indexedArrays;
    private readonly Dictionary<string, MesCatalogLogicalArray> _logicalArrays;
    private readonly Dictionary<string, MesCatalogTag> _tags;

    private MesDataRangeCatalog(
        IReadOnlyList<MesCatalogGroup> roots,
        Dictionary<string, MesCatalogGroup> groups,
        Dictionary<string, MesCatalogArray> arrays,
        Dictionary<string, MesCatalogIndexedArray> indexedArrays,
        Dictionary<string, MesCatalogLogicalArray> logicalArrays,
        IReadOnlyList<ArrayRecognitionDiagnostic> recognitionDiagnostics)
    {
        Roots = roots;
        _groups = groups;
        _tags = groups.Values.SelectMany(group => group.Tags).ToDictionary(tag => tag.TagId, StringComparer.OrdinalIgnoreCase);
        _arrays = arrays;
        _indexedArrays = indexedArrays;
        _logicalArrays = logicalArrays;
        RecognitionDiagnostics = recognitionDiagnostics;
    }

    public IReadOnlyList<MesCatalogGroup> Roots { get; }
    public bool TryGetTag(string tagId, out MesCatalogTag tag) => _tags.TryGetValue(tagId, out tag!);
    public IReadOnlyList<MesCatalogLogicalArray> LogicalArrays =>
        _logicalArrays.Values.OrderBy(array => array.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<ArrayRecognitionDiagnostic> RecognitionDiagnostics { get; }

    public bool TryGet(string sourcePath, out MesCatalogGroup source) =>
        _groups.TryGetValue(sourcePath ?? "", out source!);

    /// <summary>单个普通点(非数组)作为数据范围来源：同时给出所在组，用来确定设备和默认输出路径。</summary>
    public bool TryGetScalarTag(string sourcePath, out MesCatalogTag tag, out MesCatalogGroup parent)
    {
        parent = null!;
        if (!_tags.TryGetValue(sourcePath ?? "", out tag!) || tag.Array is not null) return false;
        int dot = tag.TagId.LastIndexOf('.');
        return dot > 0 && _groups.TryGetValue(tag.TagId[..dot], out parent!);
    }

    public bool TryGetArray(string sourcePath, out MesCatalogArray source) =>
        _arrays.TryGetValue(sourcePath ?? "", out source!);

    public bool TryGetIndexedArray(string sourcePath, out MesCatalogIndexedArray source) =>
        _indexedArrays.TryGetValue(sourcePath ?? "", out source!);

    public bool TryGetLogicalArray(string sourcePath, out MesCatalogLogicalArray source) =>
        _logicalArrays.TryGetValue(sourcePath ?? "", out source!);

    public IReadOnlyList<MesCatalogLogicalArray> GetLogicalChildren(string sourcePath)
    {
        string prefix = string.IsNullOrWhiteSpace(sourcePath) ? "" : sourcePath.TrimEnd('.') + ".";
        return _logicalArrays.Values
            .Where(array => array.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            !array.SourcePath[prefix.Length..].Contains('.'))
            .OrderBy(array => array.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 只为 MES 任务实际引用到的设备建立目录。运行中的 MES 引擎只按任务里的路径查询，
    /// 百万点工程里给全部设备建目录要十几秒，而任务通常只涉及其中一部分设备。
    /// </summary>
    public static MesDataRangeCatalog BuildForTasks(IgwProject project)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(project.MesUpload?.Tasks ?? [], ProjectStore.Options);
        var reader = new System.Text.Json.Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType != System.Text.Json.JsonTokenType.String) continue;
            string value = reader.GetString() ?? "";
            for (int index = value.IndexOf('.'); index > 0; index = value.IndexOf('.', index + 1))
                referenced.Add(value[..index]);
            referenced.Add(value);
        }
        var scoped = new IgwProject
        {
            Templates = project.Templates,
            MesUpload = project.MesUpload ?? new(),
            Channels = project.Channels
                .Select(channel => new ChannelDef
                {
                    Id = channel.Id, Name = channel.Name, DriverId = channel.DriverId, Settings = channel.Settings,
                    Devices = channel.Devices.Where(device => referenced.Contains($"{channel.Name}.{device.Name}")).ToList(),
                })
                .Where(channel => channel.Devices.Count > 0)
                .ToList(),
        };
        return Build(scoped);
    }

    public static MesDataRangeCatalog Build(IgwProject project)
    {
        var roots = new List<MesCatalogGroup>();
        var groups = new Dictionary<string, MesCatalogGroup>(StringComparer.OrdinalIgnoreCase);
        var arrays = new Dictionary<string, MesCatalogArray>(StringComparer.OrdinalIgnoreCase);
        var indexedArrays = new Dictionary<string, MesCatalogIndexedArray>(StringComparer.OrdinalIgnoreCase);
        var logicalArrays = new Dictionary<string, MesCatalogLogicalArray>(StringComparer.OrdinalIgnoreCase);
        var deviceTags = new List<MesCatalogTag>();
        int order = 0;

        foreach (var channel in project.Channels ?? new())
        foreach (var configuredDevice in channel.Devices ?? new())
        {
            var device = TemplateExpander.ExpandInProject(project, configuredDevice);
            string devicePath = $"{channel.Name}.{device.Name}";
            foreach (var tag in device.Tags ?? [])
                deviceTags.Add(new MesCatalogTag(tag.Name, tag.Name, $"{devicePath}.{tag.Name}", tag.DataType, 0, tag.Array, tag.Access));
            foreach (var group in device.Groups ?? new())
                roots.Add(BuildGroup(group, "", devicePath, device.Name, groups, arrays, indexedArrays, ref order));
        }

        var recognized = ProjectArrayRecognitionCatalog.Build(project);
        foreach (var descriptor in recognized.LogicalArrays)
        {
            string defaultOutput = string.Join('.', descriptor.SourcePath.Split('.',
                StringSplitOptions.RemoveEmptyEntries).Skip(1));
            logicalArrays[descriptor.SourcePath] = new MesCatalogLogicalArray(
                descriptor.Name,
                descriptor.SourcePath,
                descriptor.DevicePath,
                defaultOutput,
                descriptor,
                descriptor.BindingKind == ArrayBindingKind.NativeTag
                    ? checked((int)Math.Min(int.MaxValue, descriptor.ElementCount))
                    : descriptor.Leaves.Count);
        }

        var result = new MesDataRangeCatalog(
            roots, groups, arrays, indexedArrays, logicalArrays, recognized.Diagnostics);
        foreach (var tag in deviceTags) result._tags[tag.TagId] = tag;
        return result;
    }

    private static MesCatalogGroup BuildGroup(
        TagGroupDef group,
        string parentRelativePath,
        string devicePath,
        string deviceName,
        Dictionary<string, MesCatalogGroup> groups,
        Dictionary<string, MesCatalogArray> arrays,
        Dictionary<string, MesCatalogIndexedArray> indexedArrays,
        ref int order)
    {
        string relativePath = string.IsNullOrEmpty(parentRelativePath)
            ? group.Name
            : parentRelativePath + "." + group.Name;
        string sourcePath = devicePath + "." + relativePath;
        var tags = new List<MesCatalogTag>(group.Tags?.Count ?? 0);
        foreach (var tag in group.Tags ?? new())
        {
            if (string.IsNullOrWhiteSpace(tag.Name)) continue;
            var tagId = sourcePath + "." + tag.Name;
            var tagOrder = order++;
            tags.Add(new MesCatalogTag(
                tag.Name,
                tag.Name,
                tagId,
                tag.DataType,
                tagOrder,
                tag.Array, tag.Access));
            if (tag.Array is not null)
                arrays[tagId] = new MesCatalogArray(tag.Name, tagId, devicePath,
                    deviceName + "." + relativePath + "." + tag.Name, tagId, tag.Array, tagOrder);
        }

        var children = new List<MesCatalogGroup>(group.SubGroups?.Count ?? 0);
        foreach (var child in group.SubGroups ?? new())
            children.Add(BuildGroup(child, relativePath, devicePath, deviceName, groups, arrays, indexedArrays, ref order));

        var result = new MesCatalogGroup(
            group.Name,
            sourcePath,
            relativePath,
            devicePath,
            deviceName + "." + relativePath,
            tags,
            children,
            tags.Sum(tag => tag.Array is null ? 1 : checked((int)tag.Array.Shape.ElementCount)) +
                children.Sum(child => child.TagCount));
        groups[sourcePath] = result;
        RegisterIndexedArrays(result, indexedArrays);
        return result;
    }

    private static void RegisterIndexedArrays(
        MesCatalogGroup group,
        Dictionary<string, MesCatalogIndexedArray> indexedArrays)
    {
        // 格式一：数组父组下直接是纯数字元素组，例如 Bolts/0..199/Member。
        if (group.Tags.Count == 0 && TryBuildElements(group.Children, ParseNumericName, out var numeric))
        {
            indexedArrays[group.SourcePath] = CreateIndexedArray(
                group.Name, group.SourcePath, group.DevicePath, group.DefaultOutputPath, numeric);
        }

        // 格式二/三：同一父组下的 Name[index] 或 Name_index 元素组。
        var named = new Dictionary<string, List<MesCatalogIndexedElement>>(StringComparer.Ordinal);
        foreach (var child in group.Children)
        {
            if (!MesIndexedGroupName.TryParse(child.Name, out string baseName, out int index)) continue;
            if (!named.TryGetValue(baseName, out var list)) named[baseName] = list = new();
            list.Add(new MesCatalogIndexedElement(index, child));
        }
        foreach (var (baseName, candidates) in named)
        {
            if (!TryNormalizeElements(candidates, out var elements)) continue;
            string sourcePath = group.SourcePath + "." + baseName;
            if (indexedArrays.ContainsKey(sourcePath)) continue;
            indexedArrays[sourcePath] = CreateIndexedArray(
                baseName,
                sourcePath,
                group.DevicePath,
                group.DefaultOutputPath + "." + baseName,
                elements);
        }
    }

    private static MesCatalogIndexedArray CreateIndexedArray(
        string name,
        string sourcePath,
        string devicePath,
        string outputPath,
        IReadOnlyList<MesCatalogIndexedElement> elements) => new(
            name,
            sourcePath,
            devicePath,
            outputPath,
            elements,
            elements[0].Index,
            elements.Count,
            elements.Sum(element => element.Group.TagCount));

    private static bool TryBuildElements(
        IReadOnlyList<MesCatalogGroup> groups,
        Func<string, int?> indexOf,
        out IReadOnlyList<MesCatalogIndexedElement> elements)
    {
        elements = [];
        if (groups.Count < 2) return false;
        var candidates = new List<MesCatalogIndexedElement>(groups.Count);
        foreach (var group in groups)
        {
            int? index = indexOf(group.Name);
            if (!index.HasValue) return false;
            candidates.Add(new MesCatalogIndexedElement(index.Value, group));
        }
        return TryNormalizeElements(candidates, out elements);
    }

    private static bool TryNormalizeElements(
        IReadOnlyList<MesCatalogIndexedElement> candidates,
        out IReadOnlyList<MesCatalogIndexedElement> elements)
    {
        elements = [];
        if (candidates.Count < 2) return false;
        var ordered = candidates.OrderBy(item => item.Index).ToArray();
        string signature = SchemaSignature(ordered[0].Group);
        if (signature.Length == 0) return false;
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i > 0 && ordered[i].Index != ordered[i - 1].Index + 1) return false;
            if (!string.Equals(SchemaSignature(ordered[i].Group), signature, StringComparison.Ordinal)) return false;
        }
        elements = ordered;
        return true;
    }

    private static int? ParseNumericName(string name) =>
        int.TryParse(name, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index
            : null;

    private static string SchemaSignature(MesCatalogGroup group)
    {
        var parts = new List<string>();
        foreach (var tag in group.Tags)
        {
            string array = tag.Array is null
                ? ""
                : $"[{string.Join(',', tag.Array.Dimensions)}]:{tag.Array.ElementType}";
            parts.Add($"T:{tag.Name}:{tag.DataType}{array}");
        }
        foreach (var child in group.Children)
            parts.Add($"G:{child.Name}{{{SchemaSignature(child)}}}");
        return string.Join('|', parts);
    }
}
