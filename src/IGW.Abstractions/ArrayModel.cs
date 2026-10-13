using System.Text.Json.Serialization;

namespace IGW.Abstractions;

/// <summary>一个逻辑数组标记的持久化定义。</summary>
public sealed class ArrayDef
{
    public IgwDataType ElementType { get; set; }
    public int[] Dimensions { get; set; } = [];
    public int[] LowerBounds { get; set; } = [];
    public int ElementStrideBytes { get; set; }
    /// <summary>位数组元素步长；S7 Bool 数组等按位连续布局时为 1，其余为 0。</summary>
    public int ElementStrideBits { get; set; }
    public int? StringLength { get; set; }
    public StructDef? Struct { get; set; }
    public string? SourceShape { get; set; }

    [JsonIgnore]
    public ArrayShape Shape => ArrayShape.Create(Dimensions, LowerBounds);
}

/// <summary>可递归复用的结构体定义。</summary>
public sealed class StructDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SizeBytes { get; set; }
    public List<StructFieldDef> Fields { get; set; } = [];
}

/// <summary>结构体成员定义；成员本身可以继续是数组或嵌套结构体。</summary>
public sealed class StructFieldDef
{
    public string Name { get; set; } = "";
    public string? JsonName { get; set; }
    public string? Description { get; set; }
    public IgwDataType DataType { get; set; }
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; } = -1;
    public int? StringLength { get; set; }
    public ArrayDef? Array { get; set; }
    public StructDef? Struct { get; set; }
}

/// <summary>数组的维度、下界与行优先索引换算。</summary>
public sealed class ArrayShape
{
    private readonly int[] _dimensions;
    private readonly int[] _lowerBounds;

    private ArrayShape(int[] dimensions, int[] lowerBounds, long elementCount)
    {
        _dimensions = dimensions;
        _lowerBounds = lowerBounds;
        ElementCount = elementCount;
    }

    public int Rank => _dimensions.Length;
    public long ElementCount { get; }
    public IReadOnlyList<int> Dimensions => _dimensions;
    public IReadOnlyList<int> LowerBounds => _lowerBounds;

    public static ArrayShape Create(IReadOnlyList<int> dimensions, IReadOnlyList<int> lowerBounds)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(lowerBounds);

        if (dimensions.Count == 0)
            throw new ArgumentException("数组至少需要一个维度。", nameof(dimensions));
        if (dimensions.Count != lowerBounds.Count)
            throw new ArgumentException("数组维度与下界数量不一致。", nameof(lowerBounds));

        var dimensionCopy = new int[dimensions.Count];
        var lowerBoundCopy = new int[lowerBounds.Count];
        long elementCount = 1;

        for (var i = 0; i < dimensions.Count; i++)
        {
            if (dimensions[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(dimensions), "数组维度必须大于零。");

            dimensionCopy[i] = dimensions[i];
            lowerBoundCopy[i] = lowerBounds[i];
            elementCount = checked(elementCount * dimensions[i]);
        }

        return new ArrayShape(dimensionCopy, lowerBoundCopy, elementCount);
    }

    public long GetLinearOffset(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count != Rank)
            throw new ArgumentException("索引数量必须与数组维度一致。", nameof(indices));

        long offset = 0;
        for (var i = 0; i < Rank; i++)
        {
            var normalized = (long)indices[i] - _lowerBounds[i];
            if (normalized < 0 || normalized >= _dimensions[i])
                throw new IndexOutOfRangeException($"第 {i + 1} 维索引 {indices[i]} 超出范围。");
            offset = checked(offset * _dimensions[i] + normalized);
        }

        return offset;
    }

    public int[] GetIndices(long linearOffset)
    {
        if (linearOffset < 0 || linearOffset >= ElementCount)
            throw new IndexOutOfRangeException($"线性索引 {linearOffset} 超出数组范围。");

        var result = new int[Rank];
        for (var i = Rank - 1; i >= 0; i--)
        {
            result[i] = checked((int)(linearOffset % _dimensions[i])) + _lowerBounds[i];
            linearOffset /= _dimensions[i];
        }

        return result;
    }
}
