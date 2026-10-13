using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 工程的唯一逻辑数组识别结果。调用者只按完整 SourcePath 查询，不再自行解析显示名称。
/// </summary>
public sealed class ProjectArrayRecognitionCatalog
{
    private readonly Dictionary<string, LogicalArrayDescriptor> _arrays;

    private ProjectArrayRecognitionCatalog(
        Dictionary<string, LogicalArrayDescriptor> arrays,
        IReadOnlyList<ArrayRecognitionDiagnostic> diagnostics)
    {
        _arrays = arrays;
        Diagnostics = diagnostics;
        LogicalArrays = arrays.Values.OrderBy(array => array.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<LogicalArrayDescriptor> LogicalArrays { get; }
    public IReadOnlyList<ArrayRecognitionDiagnostic> Diagnostics { get; }

    public bool TryGet(string sourcePath, out LogicalArrayDescriptor descriptor) =>
        _arrays.TryGetValue(sourcePath ?? "", out descriptor!);

    public static ProjectArrayRecognitionCatalog Build(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var arrays = new Dictionary<string, LogicalArrayDescriptor>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ArrayRecognitionDiagnostic>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in project.Channels ?? [])
        foreach (var configuredDevice in channel.Devices ?? [])
        {
            var device = TemplateExpander.ExpandInProject(project, configuredDevice);
            string devicePath = channel.Name + "." + device.Name;
            foreach (var group in device.Groups ?? [])
            {
                var root = BuildGroup(group, devicePath, devicePath);
                ArrayShapeRecognizer.Collect(root, arrays, diagnostics, consumed);
            }
        }

        return new ProjectArrayRecognitionCatalog(arrays, diagnostics);
    }

    private static ArrayRecognitionGroup BuildGroup(TagGroupDef group, string parentPath, string devicePath)
    {
        string sourcePath = parentPath + "." + group.Name;
        var direct = (group.Tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag.Name)).Select(tag =>
        {
            string tagId = sourcePath + "." + tag.Name;
            ArrayAddressEvidence? evidence = ArrayAddressEvidenceParser.TryParse(tag.Address, out var parsed)
                ? parsed
                : null;
            return new ArrayRecognitionLeaf(tag, tagId, evidence);
        }).ToArray();
        var children = (group.SubGroups ?? []).Select(child => BuildGroup(child, sourcePath, devicePath)).ToArray();
        var all = new List<ArrayRecognitionLeaf>(direct.Length + children.Sum(child => child.AllLeaves.Count));
        all.AddRange(direct);
        foreach (var child in children) all.AddRange(child.AllLeaves);
        return new ArrayRecognitionGroup
        {
            Definition = group,
            SourcePath = sourcePath,
            DevicePath = devicePath,
            DirectLeaves = direct,
            Children = children,
            AllLeaves = all,
        };
    }
}
