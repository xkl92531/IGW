using IGW.Abstractions;

namespace IGW.Core;

/// <summary>数组数量来源的配置校验与单次上报解析，不修改工程定义。</summary>
public static class MesArrayLengthPolicy
{
    public static bool IsAutomatic(MesDataRange range) =>
        string.Equals(range.ArrayLengthMode, "Tag", StringComparison.OrdinalIgnoreCase);

    public static bool IsInteger(IgwDataType type) => type is IgwDataType.Byte or IgwDataType.Char
        or IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Int32 or IgwDataType.UInt32
        or IgwDataType.Int64 or IgwDataType.UInt64;

    public static MesDataRange Resolve(MesDataRange range, MesDataRangeCatalog catalog, Func<string, int>? read = null)
    {
        if (!IsAutomatic(range))
        {
            if (!string.Equals(range.ArrayLengthMode, "Fixed", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"数组长度来源无效：{range.ArrayLengthMode}");
            if (range.ArrayLengthTagIds is { Length: > 0 })
                throw new ArgumentException("手动填写和 PLC 自动获取长度只能使用一种。");
            return range;
        }
        if (range.ArrayLengths is { Length: > 0 })
            throw new ArgumentException("手动填写和 PLC 自动获取长度只能使用一种。");

        int[] dimensions, lower;
        if (catalog.TryGetArray(range.SourcePath, out var native))
        { dimensions = native.Definition.Dimensions; lower = native.Definition.LowerBounds; }
        else if (catalog.TryGetLogicalArray(range.SourcePath, out var logical))
        { dimensions = logical.Descriptor.Dimensions; lower = logical.Descriptor.LowerBounds; }
        else if (catalog.TryGetIndexedArray(range.SourcePath, out var indexed))
        { dimensions = [indexed.Length]; lower = [indexed.LowerBound]; }
        else throw new ArgumentException($"PLC 自动长度只能用于数组：{range.SourcePath}");

        var start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart : lower;
        if (start.Length != dimensions.Length || range.ArrayLengthTagIds?.Length != dimensions.Length)
            throw new ArgumentException($"每个数组维度必须选择一个长度点位：{range.SourcePath}");
        var lengths = new int[dimensions.Length];
        for (int i = 0; i < dimensions.Length; i++)
        {
            string id = (range.ArrayLengthTagIds[i] ?? "").Trim();
            if (!catalog.TryGetTag(id, out var tag) || tag.Array is not null || !IsInteger(tag.DataType))
                throw new ArgumentException($"数组长度点位必须是有效的整数标量：{id}");
            if (tag.Access == TagAccess.Write)
                throw new ArgumentException($"数组长度点位不可读取：{id}");
            long available = (long)lower[i] + dimensions[i] - start[i];
            if (start[i] < lower[i] || available <= 0 || available > int.MaxValue)
                throw new ArgumentException($"数组起始位置超出范围：{range.SourcePath}，第 {i + 1} 维");
            int length = read is null ? (int)available : read(id);
            if (length < 0 || length > available)
                throw new ArgumentException($"数组长度点位 {id} 的值 {length} 超出允许范围 0..{available}");
            lengths[i] = length;
        }
        return new MesDataRange
        {
            SourcePath = range.SourcePath, Scope = range.Scope, OutputPath = range.OutputPath,
            OutputPathMode = range.OutputPathMode, IncludeMode = range.IncludeMode, Excludes = range.Excludes,
            OfflineMode = range.OfflineMode, ArrayStart = [.. start], ArrayLengths = lengths,
            ArrayMemberPath = range.ArrayMemberPath,
        };
    }
}
