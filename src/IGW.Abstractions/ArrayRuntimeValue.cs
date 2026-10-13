namespace IGW.Abstractions;

/// <summary>数组切片；Start 使用 PLC 的实际下界索引，Lengths 表示每维数量。</summary>
public sealed class ArraySlice
{
    private readonly int[] _start;
    private readonly int[] _lengths;

    public ArraySlice(IReadOnlyList<int> start, IReadOnlyList<int> lengths)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(lengths);
        _start = start.ToArray();
        _lengths = lengths.ToArray();
    }

    public IReadOnlyList<int> Start => _start;
    public IReadOnlyList<int> Lengths => _lengths;

    public ArrayShape ValidateAndCreateShape(ArrayShape source)
    {
        if (_start.Length != source.Rank || _lengths.Length != source.Rank)
            throw new ArgumentException("切片起始索引、数量必须与数组维度一致。");

        for (var i = 0; i < source.Rank; i++)
        {
            if (_lengths[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(Lengths), "切片数量必须大于零。");
            var sourceEnd = (long)source.LowerBounds[i] + source.Dimensions[i];
            var sliceEnd = (long)_start[i] + _lengths[i];
            if (_start[i] < source.LowerBounds[i] || sliceEnd > sourceEnd)
                throw new IndexOutOfRangeException($"第 {i + 1} 维切片超出数组范围。");
        }

        return ArrayShape.Create(_lengths, _start);
    }
}

/// <summary>一个逻辑数组点位的不可变运行时快照。</summary>
public abstract class ArrayRuntimeValue
{
    private static long _nextVersion;
    private readonly StatusCode[] _elementQuality;
    private readonly ulong[] _segmentHashes;

    protected ArrayRuntimeValue(ArrayShape shape, StatusCode[] ownedQuality, ulong[] ownedSegmentHashes)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.ElementCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shape), "当前连续快照不能超过 Int32.MaxValue 个元素。");
        if (ownedQuality.Length != (int)shape.ElementCount)
            throw new ArgumentException("元素质量数量与数组元素数量不一致。", nameof(ownedQuality));

        Shape = shape;
        _elementQuality = ownedQuality;
        _segmentHashes = ownedSegmentHashes;
        Version = Interlocked.Increment(ref _nextVersion);
        OverallQuality = ArrayQuality.Aggregate(ownedQuality);
        var goodCount = 0;
        var uncertainCount = 0;
        var badCount = 0;
        foreach (var status in ownedQuality)
        {
            if (status.Class == QualityClass.Good) goodCount++;
            else if (status.Class == QualityClass.Uncertain) uncertainCount++;
            else badCount++;
        }
        GoodCount = goodCount;
        UncertainCount = uncertainCount;
        BadCount = badCount;
    }

    public ArrayShape Shape { get; }
    public long Version { get; }
    public ReadOnlyMemory<StatusCode> ElementQuality => _elementQuality;
    public ReadOnlyMemory<ulong> SegmentHashes => _segmentHashes;
    public StatusCode OverallQuality { get; }
    public int GoodCount { get; }
    public int UncertainCount { get; }
    public int BadCount { get; }
    public abstract IgwDataType ElementType { get; }
    public abstract int Count { get; }
    public abstract object? GetBoxedValue(int linearOffset);
    public abstract ArrayRuntimeValue Slice(ArraySlice slice);

    public bool HasSameContent(ArrayRuntimeValue other)
    {
        if (ReferenceEquals(this, other) || Version == other.Version) return true;
        if (ElementType != other.ElementType || Count != other.Count || Shape.Rank != other.Shape.Rank)
            return false;
        for (var i = 0; i < Shape.Rank; i++)
        {
            if (Shape.Dimensions[i] != other.Shape.Dimensions[i] ||
                Shape.LowerBounds[i] != other.Shape.LowerBounds[i])
                return false;
        }
        return _segmentHashes.AsSpan().SequenceEqual(other._segmentHashes);
    }

    protected int[] BuildSourceOffsets(ArraySlice slice, out ArrayShape resultShape)
    {
        resultShape = slice.ValidateAndCreateShape(Shape);
        var result = new int[checked((int)resultShape.ElementCount)];
        for (var i = 0; i < result.Length; i++)
            result[i] = checked((int)Shape.GetLinearOffset(resultShape.GetIndices(i)));
        return result;
    }

    protected StatusCode[] CopyQuality(IReadOnlyList<int> sourceOffsets)
    {
        var result = new StatusCode[sourceOffsets.Count];
        for (var i = 0; i < result.Length; i++) result[i] = _elementQuality[sourceOffsets[i]];
        return result;
    }

    protected static StatusCode[] OwnQuality(int count, IReadOnlyList<StatusCode>? quality)
    {
        if (quality is null)
        {
            var good = new StatusCode[count];
            Array.Fill(good, StatusCode.Good);
            return good;
        }
        if (quality.Count != count)
            throw new ArgumentException("元素质量数量与数组元素数量不一致。", nameof(quality));

        var result = new StatusCode[count];
        for (var i = 0; i < count; i++) result[i] = quality[i];
        return result;
    }

    protected static ulong[] HashSegments<T>(IReadOnlyList<T> values, IReadOnlyList<StatusCode> quality)
    {
        const int segmentSize = 4096;
        var hashes = new ulong[(values.Count + segmentSize - 1) / segmentSize];
        for (var segment = 0; segment < hashes.Length; segment++)
        {
            var hash = 14695981039346656037UL;
            var end = Math.Min(values.Count, (segment + 1) * segmentSize);
            for (var i = segment * segmentSize; i < end; i++)
            {
                hash ^= unchecked((uint)(values[i]?.GetHashCode() ?? 0));
                hash *= 1099511628211UL;
                hash ^= quality[i].Code;
                hash *= 1099511628211UL;
            }
            hashes[segment] = hash;
        }
        return hashes;
    }
}

/// <summary>
/// 通信失败时的数组占位壳：保持数组形状与元素级 Bad 质量，使监控/切片接口
/// 能返回「全 Bad 的数组」而非 409(数组运行值不可用)。仅支持基础元素类型；
/// 结构体数组或超大数组返回 null(调用方退回标量 Bad)。
/// </summary>
public static class ArrayBadShell
{
    public static ArrayRuntimeValue? CreateBadShell(ArrayDef definition, StatusCode code)
    {
    if (definition is null || definition.Struct is not null) return null;
    if (definition.ElementType == IgwDataType.Struct) return null;
    try
    {
        var shape = definition.Shape;
        if (shape.ElementCount > BadShellMaxElements) return null;
        int count = checked((int)shape.ElementCount);
        var quality = new StatusCode[count];
        Array.Fill(quality, code);
        return definition.ElementType switch
        {
            var type when SiemensValueCodec.IsText(type) => PrimitiveArrayValue<string>.Create(shape, new string[count], quality, type),
            IgwDataType.SInt => PrimitiveArrayValue<sbyte>.Create(shape, new sbyte[count], quality, definition.ElementType),
            IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => PrimitiveArrayValue<ushort>.Create(shape, new ushort[count], quality, definition.ElementType),
            IgwDataType.DWord => PrimitiveArrayValue<uint>.Create(shape, new uint[count], quality, definition.ElementType),
            IgwDataType.LWord => PrimitiveArrayValue<ulong>.Create(shape, new ulong[count], quality, definition.ElementType),
            IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, new bool[count], quality),
            IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, new byte[count], quality),
            IgwDataType.Char => PrimitiveArrayValue<char>.Create(shape, new char[count], quality),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, new short[count], quality),
            IgwDataType.UInt16 => PrimitiveArrayValue<ushort>.Create(shape, new ushort[count], quality),
            IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, new int[count], quality),
            IgwDataType.UInt32 => PrimitiveArrayValue<uint>.Create(shape, new uint[count], quality),
            IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, new long[count], quality),
            IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape, new ulong[count], quality),
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, new float[count], quality),
            IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, new double[count], quality),
            IgwDataType.String => PrimitiveArrayValue<string>.Create(shape, new string[count], quality),
            _ => null,
        };
    }
    catch
    {
        return null;
    }
}

private const int BadShellMaxElements = 65_536;
}

/// <summary>基础类型数组；构造时取得输入缓冲所有权副本，发布后不可修改。</summary>
public sealed class PrimitiveArrayValue<T> : ArrayRuntimeValue where T : notnull
{
    private readonly T[] _values;
    private readonly IgwDataType _elementType;

    private PrimitiveArrayValue(ArrayShape shape, T[] ownedValues, StatusCode[] ownedQuality, IgwDataType elementType)
        : base(shape, ownedQuality, HashSegments(ownedValues, ownedQuality))
    {
        _values = ownedValues;
        _elementType = elementType;
    }

    public ReadOnlyMemory<T> Values => _values;
    public override IgwDataType ElementType => _elementType;
    public override int Count => _values.Length;

    public static PrimitiveArrayValue<T> Create(
        ArrayShape shape, IReadOnlyList<T> values, IReadOnlyList<StatusCode>? quality = null,
        IgwDataType? elementType = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(values);
        if (shape.ElementCount != values.Count)
            throw new ArgumentException("数组值数量与形状不一致。", nameof(values));

        var ownedValues = new T[values.Count];
        for (var i = 0; i < ownedValues.Length; i++) ownedValues[i] = values[i];
        var declaredType = elementType ?? TypeMap.Value;
        if (declaredType == IgwDataType.Struct)
            throw new ArgumentException("基础数组不能声明为结构体类型。", nameof(elementType));
        return new PrimitiveArrayValue<T>(shape, ownedValues, OwnQuality(values.Count, quality), declaredType);
    }

    public override object? GetBoxedValue(int linearOffset) => _values[linearOffset];

    public override ArrayRuntimeValue Slice(ArraySlice slice)
    {
        var offsets = BuildSourceOffsets(slice, out var shape);
        var values = new T[offsets.Length];
        for (var i = 0; i < values.Length; i++) values[i] = _values[offsets[i]];
        return new PrimitiveArrayValue<T>(shape, values, CopyQuality(offsets), _elementType);
    }

    private static class TypeMap
    {
        public static readonly IgwDataType Value = typeof(T) switch
        {
            var t when t == typeof(bool) => IgwDataType.Bool,
            var t when t == typeof(byte) => IgwDataType.Byte,
            var t when t == typeof(sbyte) || t == typeof(char) => IgwDataType.Char,
            var t when t == typeof(short) => IgwDataType.Int16,
            var t when t == typeof(ushort) => IgwDataType.UInt16,
            var t when t == typeof(int) => IgwDataType.Int32,
            var t when t == typeof(uint) => IgwDataType.UInt32,
            var t when t == typeof(long) => IgwDataType.Int64,
            var t when t == typeof(ulong) => IgwDataType.UInt64,
            var t when t == typeof(float) => IgwDataType.Float,
            var t when t == typeof(double) => IgwDataType.Double,
            var t when t == typeof(string) => IgwDataType.String,
            _ => throw new NotSupportedException($"不支持的数组 CLR 元素类型:{typeof(T).FullName}"),
        };
    }
}

/// <summary>结构体数组的列式快照；每个成员列仍是一个不可变数组快照。</summary>
public sealed class StructArrayValue : ArrayRuntimeValue
{
    private readonly IReadOnlyDictionary<string, ArrayRuntimeValue> _members;

    private StructArrayValue(ArrayShape shape, StructDef schema,
        IReadOnlyDictionary<string, ArrayRuntimeValue> members, StatusCode[] quality)
        : base(shape, quality, HashMemberSegments(members, quality, checked((int)shape.ElementCount)))
    {
        Schema = schema;
        _members = members;
    }

    public StructDef Schema { get; }
    public IReadOnlyDictionary<string, ArrayRuntimeValue> Members => _members;
    public override IgwDataType ElementType => IgwDataType.Struct;
    public override int Count => checked((int)Shape.ElementCount);

    public static StructArrayValue Create(ArrayShape shape, StructDef schema,
        IReadOnlyDictionary<string, ArrayRuntimeValue> members, IReadOnlyList<StatusCode>? quality = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(members);
        var count = checked((int)shape.ElementCount);
        var copy = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            if (!members.TryGetValue(field.Name, out var member))
                throw new ArgumentException($"缺少结构体成员列:{field.Name}", nameof(members));
            ValidateMemberShape(shape, field, member);
            copy.Add(field.Name, member);
        }
        return new StructArrayValue(shape, schema, copy, OwnQuality(count, quality));
    }

    public override object GetBoxedValue(int linearOffset)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Schema.Fields)
            result[field.JsonName ?? field.Name] = field.Array is null
                ? _members[field.Name].GetBoxedValue(linearOffset)
                : GetArrayMemberSlice(field, linearOffset);
        return result;
    }

    public override ArrayRuntimeValue Slice(ArraySlice slice)
    {
        _ = BuildSourceOffsets(slice, out var shape);
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Schema.Fields)
        {
            var memberSlice = field.Array is null ? slice : ExpandMemberSlice(slice, field.Array);
            members[field.Name] = _members[field.Name].Slice(memberSlice);
        }
        var offsets = BuildSourceOffsets(slice, out _);
        return new StructArrayValue(shape, Schema, members, CopyQuality(offsets));
    }

    public ArrayRuntimeValue GetArrayMemberSlice(StructFieldDef field, int outerLinearOffset)
    {
        if (field.Array is null) throw new ArgumentException($"字段 {field.Name} 不是数组。", nameof(field));
        var outerIndices = Shape.GetIndices(outerLinearOffset);
        var start = outerIndices.Concat(field.Array.LowerBounds).ToArray();
        var lengths = Enumerable.Repeat(1, Shape.Rank).Concat(field.Array.Dimensions).ToArray();
        return _members[field.Name].Slice(new ArraySlice(start, lengths));
    }

    public static ArraySlice ExpandMemberSlice(ArraySlice outerSlice, ArrayDef member)
    {
        var start = outerSlice.Start.Concat(member.LowerBounds).ToArray();
        var lengths = outerSlice.Lengths.Concat(member.Dimensions).ToArray();
        return new ArraySlice(start, lengths);
    }

    private static void ValidateMemberShape(ArrayShape outer, StructFieldDef field, ArrayRuntimeValue member)
    {
        var expectedDimensions = field.Array is null
            ? outer.Dimensions.ToArray()
            : outer.Dimensions.Concat(field.Array.Dimensions).ToArray();
        var expectedLowerBounds = field.Array is null
            ? outer.LowerBounds.ToArray()
            : outer.LowerBounds.Concat(field.Array.LowerBounds).ToArray();
        if (member.Shape.Rank != expectedDimensions.Length)
            throw new ArgumentException($"结构体成员列 {field.Name} 的维度不一致。", nameof(member));
        for (var i = 0; i < expectedDimensions.Length; i++)
        {
            if (member.Shape.Dimensions[i] != expectedDimensions[i] ||
                member.Shape.LowerBounds[i] != expectedLowerBounds[i])
                throw new ArgumentException($"结构体成员列 {field.Name} 的形状不一致。", nameof(member));
        }
    }

    private static ulong[] HashMemberSegments(IReadOnlyDictionary<string, ArrayRuntimeValue> members,
        IReadOnlyList<StatusCode> quality, int count)
    {
        var identity = new long[count];
        foreach (var member in members.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hashes = member.Value.SegmentHashes.Span;
            for (var i = 0; i < identity.Length; i++)
                identity[i] = unchecked(identity[i] * 397 ^ (long)hashes[Math.Min(i / 4096, hashes.Length - 1)]);
        }
        return HashSegments(identity, quality);
    }
}

public static class ArrayQuality
{
    public static StatusCode Aggregate(IReadOnlyList<StatusCode> quality)
    {
        if (quality.Count == 0) return StatusCode.BadDecodeError;
        var good = 0;
        var bad = 0;
        StatusCode mostSpecificBad = StatusCode.Bad;
        foreach (var status in quality)
        {
            if (status.Class == QualityClass.Good) good++;
            else if (status.Class == QualityClass.Bad)
            {
                bad++;
                if (mostSpecificBad == StatusCode.Bad && status != StatusCode.Bad)
                    mostSpecificBad = status;
            }
        }
        if (good == quality.Count) return StatusCode.Good;
        if (bad == quality.Count) return mostSpecificBad;
        return StatusCode.Uncertain;
    }
}
