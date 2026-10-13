using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using IGW.Abstractions;

namespace IGW.Drivers.EtherNetIP;

public readonly record struct EtherNetIpArrayChunk(int Start, int Count);

/// <summary>CIP 小端数组/UDT 布局转换；协议分块不改变上层一个逻辑数组点的语义。</summary>
public static class EtherNetIpArrayCodec
{
    public static IReadOnlyList<EtherNetIpArrayChunk> Chunks(int count, int maxElements)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        maxElements = Math.Max(1, maxElements);
        var result = new List<EtherNetIpArrayChunk>((count + maxElements - 1) / maxElements);
        for (var start = 0; start < count; start += maxElements)
            result.Add(new EtherNetIpArrayChunk(start, Math.Min(maxElements, count - start)));
        return result;
    }

    public static ArrayRuntimeValue Decode(ArrayDef definition, byte[] image,
        IReadOnlyList<StatusCode> quality)
    {
        var shape = definition.Shape;
        if (quality.Count != shape.ElementCount)
            throw new ArgumentException("CIP 数组质量数量与元素数量不一致", nameof(quality));
        return definition.ElementType == IgwDataType.Struct
            ? DecodeStruct(shape, definition.Struct ?? throw new InvalidDataException("结构体数组缺少布局"),
                i => ElementBit(definition, i), image, quality)
            : DecodePrimitive(shape, definition.ElementType, i => ElementBit(definition, i),
                definition.StringLength, image, quality);
    }

    public static byte[] Encode(ArrayRuntimeValue value, ArrayDef definition)
    {
        if (value.ElementType != definition.ElementType)
            throw new ArgumentException("CIP 数组元素类型不匹配", nameof(value));
        var bytes = definition.ElementStrideBits > 0
            ? checked((value.Count * definition.ElementStrideBits + 7) / 8)
            : checked(value.Count * definition.ElementStrideBytes);
        var result = new byte[Math.Max(1, bytes)];
        for (var i = 0; i < value.Count; i++)
        {
            var bit = ElementBit(definition, i);
            if (definition.ElementType == IgwDataType.Struct)
                EncodeStruct(result, bit / 8, definition.Struct!, (StructArrayValue)value, i);
            else EncodePrimitive(result, bit, definition.ElementType, value.GetBoxedValue(i), definition.StringLength);
        }
        return result;
    }

    private static StructArrayValue DecodeStruct(ArrayShape shape, StructDef schema,
        Func<int, int> baseBit, byte[] image, IReadOnlyList<StatusCode> outerQuality)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            if (field.Array is { } array)
            {
                var innerCount = checked((int)array.Shape.ElementCount);
                var memberShape = ArrayShape.Create(shape.Dimensions.Concat(array.Dimensions).ToArray(),
                    shape.LowerBounds.Concat(array.LowerBounds).ToArray());
                var memberQuality = Repeat(outerQuality, innerCount);
                Func<int, int> memberBit = linear => checked(baseBit(linear / innerCount) +
                    field.ByteOffset * 8 + Math.Max(0, field.BitOffset) + ElementBit(array, linear % innerCount));
                members[field.Name] = array.ElementType == IgwDataType.Struct
                    ? DecodeStruct(memberShape, array.Struct!, memberBit, image, memberQuality)
                    : DecodePrimitive(memberShape, array.ElementType, memberBit, array.StringLength, image, memberQuality);
            }
            else if (field.Struct is { } nested)
                members[field.Name] = DecodeStruct(shape, nested,
                    i => checked(baseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)), image, outerQuality);
            else
                members[field.Name] = DecodePrimitive(shape, field.DataType,
                    i => checked(baseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)),
                    field.StringLength, image, outerQuality);
        }
        return StructArrayValue.Create(shape, schema, members, outerQuality);
    }

    private static ArrayRuntimeValue DecodePrimitive(ArrayShape shape, IgwDataType type,
        Func<int, int> bit, int? stringLength, byte[] image, IReadOnlyList<StatusCode> quality)
    {
        var count = checked((int)shape.ElementCount);
        return type switch
        {
            IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, Values(count, i =>
                (image[bit(i) / 8] & (1 << (bit(i) & 7))) != 0), quality, type),
            IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, Values(count, i => image[bit(i) / 8]), quality, type),
            IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(shape, Values(count, i => unchecked((sbyte)image[bit(i) / 8])), quality, type),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, Values(count, i => BinaryPrimitives.ReadInt16LittleEndian(Span(image, bit(i), 2))), quality, type),
            IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(shape, Values(count, i => BinaryPrimitives.ReadUInt16LittleEndian(Span(image, bit(i), 2))), quality, type),
            IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, Values(count, i => BinaryPrimitives.ReadInt32LittleEndian(Span(image, bit(i), 4))), quality, type),
            IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(shape, Values(count, i => BinaryPrimitives.ReadUInt32LittleEndian(Span(image, bit(i), 4))), quality, type),
            IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, Values(count, i => BinaryPrimitives.ReadInt64LittleEndian(Span(image, bit(i), 8))), quality, type),
            IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape, Values(count, i => BinaryPrimitives.ReadUInt64LittleEndian(Span(image, bit(i), 8))), quality, type),
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, Values(count, i => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Span(image, bit(i), 4)))), quality, type),
            IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, Values(count, i => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Span(image, bit(i), 8)))), quality, type),
            IgwDataType.String => PrimitiveArrayValue<string>.Create(shape, Values(count, i => ReadString(image, bit(i) / 8, stringLength)), quality, type),
            IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape, Values(count, i => ReadWString(image, bit(i) / 8, stringLength)), quality, type),
            IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(shape, Values(count, i =>
                Span(image, bit(i), Math.Max(1, stringLength ?? 1)).ToArray()), quality, type),
            _ => throw new NotSupportedException($"CIP 数组元素类型尚不支持:{type}"),
        };
    }

    private static void EncodeStruct(byte[] target, int baseByte, StructDef schema,
        StructArrayValue value, int outerOffset)
    {
        foreach (var field in schema.Fields)
        {
            var member = value.Members[field.Name];
            if (field.Array is { } array)
            {
                var innerCount = checked((int)array.Shape.ElementCount);
                for (var inner = 0; inner < innerCount; inner++)
                {
                    var memberOffset = checked(outerOffset * innerCount + inner);
                    var memberBit = checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset) + ElementBit(array, inner));
                    if (array.ElementType == IgwDataType.Struct)
                        EncodeStruct(target, memberBit / 8, array.Struct!, (StructArrayValue)member, memberOffset);
                    else EncodePrimitive(target, memberBit, array.ElementType, member.GetBoxedValue(memberOffset), array.StringLength);
                }
            }
            else if (field.Struct is { } nested)
                EncodeStruct(target, baseByte + field.ByteOffset, nested, (StructArrayValue)member, outerOffset);
            else EncodePrimitive(target, checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset)),
                field.DataType, member.GetBoxedValue(outerOffset), field.StringLength);
        }
    }

    private static void EncodePrimitive(byte[] target, int bit, IgwDataType type, object? value, int? stringLength)
    {
        var offset = bit / 8;
        switch (type)
        {
            case IgwDataType.Bool:
                var mask = 1 << (bit & 7);
                target[offset] = Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                    ? (byte)(target[offset] | mask) : (byte)(target[offset] & ~mask);
                break;
            case IgwDataType.Byte: target[offset] = Convert.ToByte(value, CultureInfo.InvariantCulture); break;
            case IgwDataType.Char: target[offset] = unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int16: BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt16 or IgwDataType.Bcd: BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), Convert.ToUInt16(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int32: BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt32 or IgwDataType.LBcd: BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int64: BinaryPrimitives.WriteInt64LittleEndian(target.AsSpan(offset, 8), Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(offset, 8), Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Float: BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture))); break;
            case IgwDataType.Double: BinaryPrimitives.WriteInt64LittleEndian(target.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture))); break;
            case IgwDataType.String: WriteString(target, offset, stringLength, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            case IgwDataType.WString: WriteWString(target, offset, stringLength, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            case IgwDataType.ByteArray:
                if (value is byte[] bytes) bytes.AsSpan(0, Math.Min(bytes.Length, target.Length - offset)).CopyTo(target.AsSpan(offset));
                break;
            default: throw new NotSupportedException($"CIP 数组元素类型尚不支持:{type}");
        }
    }

    private static string ReadString(byte[] image, int offset, int? configured)
    {
        var available = Math.Max(0, image.Length - offset - 4);
        var length = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset, 4)), 0,
            Math.Min(available, configured.GetValueOrDefault(available)));
        return Encoding.UTF8.GetString(image, offset + 4, length);
    }

    private static void WriteString(byte[] target, int offset, int? configured, string value)
    {
        var capacity = Math.Max(0, Math.Min(configured.GetValueOrDefault(target.Length - offset - 4), target.Length - offset - 4));
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = Math.Min(bytes.Length, capacity);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), length);
        bytes.AsSpan(0, length).CopyTo(target.AsSpan(offset + 4));
    }

    private static string ReadWString(byte[] image, int offset, int? configured)
    {
        var available = Math.Max(0, (image.Length - offset - 4) / 2);
        var length = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset, 4)), 0,
            Math.Min(available, configured.GetValueOrDefault(available)));
        return Encoding.Unicode.GetString(image, offset + 4, length * 2);
    }

    private static void WriteWString(byte[] target, int offset, int? configured, string value)
    {
        var capacity = Math.Max(0, Math.Min(configured.GetValueOrDefault((target.Length - offset - 4) / 2), (target.Length - offset - 4) / 2));
        var length = Math.Min(value.Length, capacity);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), length);
        Encoding.Unicode.GetBytes(value.AsSpan(0, length), target.AsSpan(offset + 4, length * 2));
    }

    private static int ElementBit(ArrayDef definition, int linear) => definition.ElementStrideBits > 0
        ? checked(linear * definition.ElementStrideBits)
        : checked(linear * definition.ElementStrideBytes * 8);

    private static ReadOnlySpan<byte> Span(byte[] image, int bit, int bytes) => image.AsSpan(bit / 8, bytes);
    private static T[] Values<T>(int count, Func<int, T> read)
    {
        var result = new T[count];
        for (var i = 0; i < count; i++) result[i] = read(i);
        return result;
    }
    private static StatusCode[] Repeat(IReadOnlyList<StatusCode> source, int count)
    {
        var result = new StatusCode[checked(source.Count * count)];
        for (var i = 0; i < source.Count; i++) Array.Fill(result, source[i], i * count, count);
        return result;
    }
}
