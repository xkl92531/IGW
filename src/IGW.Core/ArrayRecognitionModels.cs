namespace IGW.Core;

/// <summary>逻辑数组的结构形态；由来源元数据和真实地址证据决定。</summary>
public enum LogicalArrayShapeKind
{
    Rectangular,
    Sparse,
    Jagged,
}

/// <summary>逻辑数组到南向协议地址的绑定方式。</summary>
public enum ArrayBindingKind
{
    NativeTag,
    AddressPattern,
    ExpandedLeaves,
}

/// <summary>数组识别结论的证据强度。</summary>
public enum ArrayRecognitionConfidence
{
    Explicit,
    Proven,
    Ambiguous,
}

/// <summary>一个带下标的物理路径段；相邻的 [i][j] 会合并成同一段。</summary>
public sealed record ArrayIndexedSegment(string Name, int[] Indices);

/// <summary>
/// 从一个真实协议地址提取出的数组证据。IndexVectors 保留每一个 [] 的秩，
/// FlattenedIndices 供普通矩形数组快速使用。
/// </summary>
public sealed record ArrayAddressEvidence(
    string SourceFormat,
    string OriginalAddress,
    string RootIdentity,
    string CanonicalPattern,
    IReadOnlyList<int[]> IndexVectors,
    IReadOnlyList<int> FlattenedIndices,
    IReadOnlyList<ArrayIndexedSegment> IndexedSegments,
    string MemberPath);

/// <summary>展开叶点到逻辑数组元素/成员的真实绑定。</summary>
public sealed record LogicalArrayLeafBinding(
    int[] Indices,
    string MemberPath,
    string TagId,
    string Address);

public sealed record LogicalArrayMember(
    string Path,
    string JsonName,
    IGW.Abstractions.IgwDataType DataType,
    IGW.Abstractions.TagAccess Access,
    LogicalArrayDescriptor? NestedArray);

public sealed record LogicalArrayDescriptor(
    string Name,
    string SourcePath,
    string DevicePath,
    LogicalArrayShapeKind ShapeKind,
    int[] Dimensions,
    int[] LowerBounds,
    IReadOnlyList<int[]> ExistingIndices,
    IReadOnlyList<LogicalArrayMember> Members,
    IReadOnlyList<LogicalArrayLeafBinding> Leaves,
    ArrayBindingKind BindingKind,
    string SourceFormat,
    ArrayRecognitionConfidence Confidence,
    IReadOnlyList<string> Evidence,
    long ElementCount);

public sealed record ArrayRecognitionDiagnostic(
    string Code,
    string SourcePath,
    string Message,
    string Severity = "Warning");
