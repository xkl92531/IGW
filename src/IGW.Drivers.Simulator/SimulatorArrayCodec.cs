using IGW.Abstractions;

namespace IGW.Drivers.Simulator;

/// <summary>模拟器数组的确定性生成与不可变切片替换。</summary>
public static class SimulatorArrayCodec
{
    public static ArrayRuntimeValue Create(TagDef tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Array is null) throw new ArgumentException("标记不是数组。", nameof(tag));
        return Create(tag.Array.ElementType, tag.Array.Shape, tag.Array.StringLength,
            tag.Array.Struct, StableHash(tag.Name));
    }

    public static ArrayRuntimeValue ReplaceSlice(
        ArrayRuntimeValue whole, ArraySlice slice, ArrayRuntimeValue replacement)
    {
        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        if (whole.ElementType != replacement.ElementType || targetShape.Rank != replacement.Shape.Rank)
            throw new ArgumentException("替换数组的元素类型或维度不一致。", nameof(replacement));
        for (var i = 0; i < targetShape.Rank; i++)
            if (targetShape.Dimensions[i] != replacement.Shape.Dimensions[i])
                throw new ArgumentException("替换数组的形状与切片不一致。", nameof(replacement));

        return whole switch
        {
            PrimitiveArrayValue<bool> value => ReplacePrimitive(value, slice, Require<bool>(replacement)),
            PrimitiveArrayValue<byte> value => ReplacePrimitive(value, slice, Require<byte>(replacement)),
            PrimitiveArrayValue<sbyte> value => ReplacePrimitive(value, slice, Require<sbyte>(replacement)),
            PrimitiveArrayValue<short> value => ReplacePrimitive(value, slice, Require<short>(replacement)),
            PrimitiveArrayValue<ushort> value => ReplacePrimitive(value, slice, Require<ushort>(replacement)),
            PrimitiveArrayValue<int> value => ReplacePrimitive(value, slice, Require<int>(replacement)),
            PrimitiveArrayValue<uint> value => ReplacePrimitive(value, slice, Require<uint>(replacement)),
            PrimitiveArrayValue<long> value => ReplacePrimitive(value, slice, Require<long>(replacement)),
            PrimitiveArrayValue<ulong> value => ReplacePrimitive(value, slice, Require<ulong>(replacement)),
            PrimitiveArrayValue<float> value => ReplacePrimitive(value, slice, Require<float>(replacement)),
            PrimitiveArrayValue<double> value => ReplacePrimitive(value, slice, Require<double>(replacement)),
            PrimitiveArrayValue<string> value => ReplacePrimitive(value, slice, Require<string>(replacement)),
            PrimitiveArrayValue<byte[]> value => ReplacePrimitive(value, slice, Require<byte[]>(replacement)),
            StructArrayValue value => ReplaceStruct(value, slice,
                replacement as StructArrayValue ?? throw new ArgumentException("替换值不是结构体数组。", nameof(replacement))),
            _ => throw new NotSupportedException($"模拟器不支持数组快照类型:{whole.GetType().Name}"),
        };
    }

    private static ArrayRuntimeValue Create(IgwDataType type, ArrayShape shape, int? stringLength,
        StructDef? schema, int seed)
    {
        var count = checked((int)shape.ElementCount);
        return type switch
        {
            IgwDataType.Bool => Values(shape, count, i => ((i + seed) & 1) == 0, type),
            IgwDataType.Byte => Values(shape, count, i => (byte)((i + seed) & 0xFF), type),
            IgwDataType.Char => Values(shape, count, i => (sbyte)((i + seed) % 127), type),
            IgwDataType.Int16 => Values(shape, count, i => (short)(i + seed % 100), type),
            IgwDataType.UInt16 => Values(shape, count, i => (ushort)(i + seed % 100), type),
            IgwDataType.Int32 => Values(shape, count, i => i + seed % 1000, type),
            IgwDataType.UInt32 => Values(shape, count, i => (uint)(i + seed % 1000), type),
            IgwDataType.Int64 => Values(shape, count, i => (long)i + seed, type),
            IgwDataType.UInt64 => Values(shape, count, i => (ulong)(i + seed), type),
            IgwDataType.Float => Values(shape, count, i => (float)(i + (seed % 10) / 10d), type),
            IgwDataType.Double => Values(shape, count, i => i + (seed % 100) / 100d, type),
            IgwDataType.String => Values(shape, count,
                i => Truncate($"SIM-{seed % 1000:D3}-{i:D6}", stringLength), type),
            IgwDataType.WString => Values(shape, count,
                i => Truncate($"模拟值-{seed % 1000:D3}-{i:D6}", stringLength), type),
            IgwDataType.Bcd => Values(shape, count, i => (ushort)((i + seed) % 10_000), type),
            IgwDataType.LBcd => Values(shape, count, i => (uint)((i + seed) % 100_000_000), type),
            IgwDataType.ByteArray => Values(shape, count,
                i => new[] { (byte)(i & 0xFF), (byte)((i + seed) & 0xFF) }, type),
            IgwDataType.Struct => CreateStruct(shape,
                schema ?? throw new ArgumentException("结构体数组缺少结构定义。", nameof(schema)), seed),
            _ => throw new NotSupportedException($"模拟器不支持数组元素类型:{type}"),
        };
    }

    private static StructArrayValue CreateStruct(ArrayShape outerShape, StructDef schema, int seed)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < schema.Fields.Count; i++)
        {
            var field = schema.Fields[i];
            ArrayShape memberShape;
            IgwDataType memberType;
            int? stringLength;
            StructDef? memberStruct;
            if (field.Array is not null)
            {
                memberShape = Combine(outerShape, field.Array.Shape);
                memberType = field.Array.ElementType;
                stringLength = field.Array.StringLength;
                memberStruct = field.Array.Struct;
            }
            else
            {
                memberShape = outerShape;
                memberType = field.DataType;
                stringLength = field.StringLength;
                memberStruct = field.Struct;
            }
            members.Add(field.Name, Create(memberType, memberShape, stringLength, memberStruct,
                unchecked(seed * 31 + i + 1)));
        }
        return StructArrayValue.Create(outerShape, schema, members);
    }

    private static ArrayShape Combine(ArrayShape outer, ArrayShape inner) => ArrayShape.Create(
        outer.Dimensions.Concat(inner.Dimensions).ToArray(),
        outer.LowerBounds.Concat(inner.LowerBounds).ToArray());

    private static PrimitiveArrayValue<T> Values<T>(ArrayShape shape, int count, Func<int, T> create,
        IgwDataType elementType) where T : notnull
    {
        var values = new T[count];
        for (var i = 0; i < count; i++) values[i] = create(i);
        return PrimitiveArrayValue<T>.Create(shape, values, elementType: elementType);
    }

    private static PrimitiveArrayValue<T> ReplacePrimitive<T>(
        PrimitiveArrayValue<T> whole, ArraySlice slice, PrimitiveArrayValue<T> replacement) where T : notnull
    {
        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        var values = whole.Values.ToArray();
        var quality = whole.ElementQuality.ToArray();
        for (var sourceOffset = 0; sourceOffset < replacement.Count; sourceOffset++)
        {
            var targetOffset = checked((int)whole.Shape.GetLinearOffset(targetShape.GetIndices(sourceOffset)));
            values[targetOffset] = replacement.Values.Span[sourceOffset];
            quality[targetOffset] = replacement.ElementQuality.Span[sourceOffset];
        }
        return PrimitiveArrayValue<T>.Create(whole.Shape, values, quality, whole.ElementType);
    }

    private static StructArrayValue ReplaceStruct(
        StructArrayValue whole, ArraySlice slice, StructArrayValue replacement)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in whole.Schema.Fields)
        {
            if (!replacement.Members.TryGetValue(field.Name, out var incoming))
                throw new ArgumentException($"替换结构体缺少成员:{field.Name}", nameof(replacement));
            var memberSlice = field.Array is null ? slice : StructArrayValue.ExpandMemberSlice(slice, field.Array);
            members[field.Name] = ReplaceSlice(whole.Members[field.Name], memberSlice, incoming);
        }

        var targetShape = slice.ValidateAndCreateShape(whole.Shape);
        var quality = whole.ElementQuality.ToArray();
        for (var sourceOffset = 0; sourceOffset < replacement.Count; sourceOffset++)
        {
            var targetOffset = checked((int)whole.Shape.GetLinearOffset(targetShape.GetIndices(sourceOffset)));
            quality[targetOffset] = replacement.ElementQuality.Span[sourceOffset];
        }
        return StructArrayValue.Create(whole.Shape, whole.Schema, members, quality);
    }

    private static PrimitiveArrayValue<T> Require<T>(ArrayRuntimeValue value) where T : notnull =>
        value as PrimitiveArrayValue<T>
        ?? throw new ArgumentException($"替换数组 CLR 元素类型应为 {typeof(T).Name}。", nameof(value));

    private static string Truncate(string value, int? capacity) =>
        capacity is > 0 && value.Length > capacity ? value[..capacity.Value] : value;

    private static int StableHash(string value)
    {
        var hash = 17;
        foreach (var c in value ?? "") hash = unchecked(hash * 31 + c);
        return hash & int.MaxValue;
    }
}
