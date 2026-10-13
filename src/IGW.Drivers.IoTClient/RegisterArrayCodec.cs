using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>FINS/Modbus 等 16 位寄存器协议的统一数组/结构体编解码。</summary>
public static class RegisterArrayCodec
{
    public static ArrayRuntimeValue Decode(byte[] image, ArrayDef definition, WordOrder order,
        IReadOnlyList<StatusCode> quality)
    {
        var shape = definition.Shape;
        if (quality.Count != shape.ElementCount) throw new ArgumentException("数组质量数量不匹配", nameof(quality));
        return definition.ElementType == IgwDataType.Struct
            ? DecodeStruct(shape, definition.Struct!, i => ElementBit(definition, i), image, order, quality)
            : DecodePrimitive(shape, definition.ElementType, i => ElementBit(definition, i),
                definition.StringLength, image, order, quality);
    }

    public static byte[] Encode(ArrayRuntimeValue value, ArrayDef definition, WordOrder order)
    {
        var bits = definition.ElementStrideBits > 0
            ? checked(value.Count * definition.ElementStrideBits)
            : checked(value.Count * definition.ElementStrideBytes * 8);
        var image = new byte[Math.Max(1, (bits + 7) / 8)];
        for (var i = 0; i < value.Count; i++)
        {
            var bit = ElementBit(definition, i);
            if (definition.ElementType == IgwDataType.Struct)
                EncodeStruct(image, bit / 8, definition.Struct!, (StructArrayValue)value, i, order);
            else EncodePrimitive(image, bit, definition.ElementType, value.GetBoxedValue(i), definition.StringLength, order);
        }
        return image;
    }

    public static int RegisterCount(ArrayDef definition, int elements) => definition.ElementStrideBits > 0
        ? checked((elements * definition.ElementStrideBits + 15) / 16)
        : checked((elements * definition.ElementStrideBytes + 1) / 2);

    private static StructArrayValue DecodeStruct(ArrayShape shape, StructDef schema, Func<int, int> baseBit,
        byte[] image, WordOrder order, IReadOnlyList<StatusCode> quality)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            if (field.Array is { } array)
            {
                var inner = checked((int)array.Shape.ElementCount);
                var memberShape = ArrayShape.Create(shape.Dimensions.Concat(array.Dimensions).ToArray(),
                    shape.LowerBounds.Concat(array.LowerBounds).ToArray());
                var memberQuality = Repeat(quality, inner);
                Func<int, int> memberBit = linear => checked(baseBit(linear / inner) + field.ByteOffset * 8 +
                    Math.Max(0, field.BitOffset) + ElementBit(array, linear % inner));
                members[field.Name] = array.ElementType == IgwDataType.Struct
                    ? DecodeStruct(memberShape, array.Struct!, memberBit, image, order, memberQuality)
                    : DecodePrimitive(memberShape, array.ElementType, memberBit, array.StringLength, image, order, memberQuality);
            }
            else if (field.Struct is { } nested)
                members[field.Name] = DecodeStruct(shape, nested,
                    i => checked(baseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)), image, order, quality);
            else members[field.Name] = DecodePrimitive(shape, field.DataType,
                i => checked(baseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)),
                field.StringLength, image, order, quality);
        }
        return StructArrayValue.Create(shape, schema, members, quality);
    }

    private static ArrayRuntimeValue DecodePrimitive(ArrayShape shape, IgwDataType type, Func<int, int> bit,
        int? stringLength, byte[] image, WordOrder order, IReadOnlyList<StatusCode> quality)
    {
        var count = checked((int)shape.ElementCount);
        object DecodeNumber(int i, IgwDataType t, int bytes) => ModbusBlockCodec.Decode(
            image.AsSpan(bit(i) / 8, bytes), t, order);
        return type switch
        {
            IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, Values(count, i =>
                (image[bit(i) / 8] & (1 << (bit(i) & 7))) != 0), quality, type),
            IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, Values(count, i => (byte)DecodeNumber(i, type, 2)), quality, type),
            IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(shape, Values(count, i => (sbyte)DecodeNumber(i, type, 2)), quality, type),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, Values(count, i => (short)DecodeNumber(i, type, 2)), quality, type),
            IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(shape, Values(count, i => (ushort)DecodeNumber(i, type, 2)), quality, type),
            IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, Values(count, i => (int)DecodeNumber(i, type, 4)), quality, type),
            IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(shape, Values(count, i => (uint)DecodeNumber(i, type, 4)), quality, type),
            IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, Values(count, i => (long)DecodeNumber(i, type, 8)), quality, type),
            IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape, Values(count, i => (ulong)DecodeNumber(i, type, 8)), quality, type),
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, Values(count, i => (float)DecodeNumber(i, type, 4)), quality, type),
            IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, Values(count, i => (double)DecodeNumber(i, type, 8)), quality, type),
            IgwDataType.String => PrimitiveArrayValue<string>.Create(shape, Values(count, i => ReadText(image, bit(i) / 8, stringLength, false)), quality, type),
            IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape, Values(count, i => ReadText(image, bit(i) / 8, stringLength, true)), quality, type),
            IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(shape, Values(count, i =>
                image.AsSpan(bit(i) / 8, Math.Max(1, stringLength ?? 1)).ToArray()), quality, type),
            _ => throw new NotSupportedException($"寄存器数组元素类型尚不支持:{type}"),
        };
    }

    private static void EncodeStruct(byte[] target, int baseByte, StructDef schema,
        StructArrayValue value, int row, WordOrder order)
    {
        foreach (var field in schema.Fields)
        {
            var member = value.Members[field.Name];
            if (field.Array is { } array)
            {
                var inner = checked((int)array.Shape.ElementCount);
                for (var i = 0; i < inner; i++)
                {
                    var offset = checked(row * inner + i);
                    var bit = checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset) + ElementBit(array, i));
                    if (array.ElementType == IgwDataType.Struct)
                        EncodeStruct(target, bit / 8, array.Struct!, (StructArrayValue)member, offset, order);
                    else EncodePrimitive(target, bit, array.ElementType, member.GetBoxedValue(offset), array.StringLength, order);
                }
            }
            else if (field.Struct is { } nested)
                EncodeStruct(target, baseByte + field.ByteOffset, nested, (StructArrayValue)member, row, order);
            else EncodePrimitive(target, checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset)),
                field.DataType, member.GetBoxedValue(row), field.StringLength, order);
        }
    }

    private static void EncodePrimitive(byte[] target, int bit, IgwDataType type, object? value,
        int? stringLength, WordOrder order)
    {
        var offset = bit / 8;
        if (type == IgwDataType.Bool)
        {
            var mask = 1 << (bit & 7);
            target[offset] = Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                ? (byte)(target[offset] | mask) : (byte)(target[offset] & ~mask);
            return;
        }
        if (type is IgwDataType.String or IgwDataType.WString)
        {
            WriteText(target, offset, stringLength, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                type == IgwDataType.WString);
            return;
        }
        if (type == IgwDataType.ByteArray)
        {
            if (value is byte[] raw) raw.AsSpan(0, Math.Min(raw.Length, target.Length - offset)).CopyTo(target.AsSpan(offset));
            return;
        }
        var canonical = Canonical(type, value);
        ModbusBlockCodec.ApplyOrder(canonical, order).CopyTo(target, offset);
    }

    private static byte[] Canonical(IgwDataType type, object? value)
    {
        var result = new byte[type switch
        {
            IgwDataType.Byte or IgwDataType.Char or IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
            IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
            _ => 8,
        }];
        switch (type)
        {
            case IgwDataType.Byte: BinaryPrimitives.WriteUInt16BigEndian(result, Convert.ToByte(value)); break;
            case IgwDataType.Char: BinaryPrimitives.WriteInt16BigEndian(result, Convert.ToSByte(value)); break;
            case IgwDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(result, Convert.ToInt16(value)); break;
            case IgwDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(result, Convert.ToUInt16(value)); break;
            case IgwDataType.Bcd: BinaryPrimitives.WriteUInt16BigEndian(result, ToBcd16(Convert.ToUInt16(value))); break;
            case IgwDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(result, Convert.ToInt32(value)); break;
            case IgwDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(result, Convert.ToUInt32(value)); break;
            case IgwDataType.LBcd: BinaryPrimitives.WriteUInt32BigEndian(result, ToBcd32(Convert.ToUInt32(value))); break;
            case IgwDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(result, Convert.ToInt64(value)); break;
            case IgwDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(result, Convert.ToUInt64(value)); break;
            case IgwDataType.Float: BinaryPrimitives.WriteSingleBigEndian(result, Convert.ToSingle(value)); break;
            case IgwDataType.Double: BinaryPrimitives.WriteDoubleBigEndian(result, Convert.ToDouble(value)); break;
        }
        return result;
    }

    private static string ReadText(byte[] source, int offset, int? length, bool wide)
    {
        var bytes = Math.Min(source.Length - offset, Math.Max(0, length.GetValueOrDefault(source.Length - offset)) * (wide ? 2 : 1));
        var span = source.AsSpan(offset, bytes);
        while (span.Length >= (wide ? 2 : 1) && span[^1] == 0) span = span[..^1];
        return (wide ? Encoding.BigEndianUnicode : Encoding.UTF8).GetString(span);
    }

    private static void WriteText(byte[] target, int offset, int? length, string value, bool wide)
    {
        var capacity = Math.Min(target.Length - offset, Math.Max(0, length.GetValueOrDefault(target.Length - offset)) * (wide ? 2 : 1));
        var bytes = (wide ? Encoding.BigEndianUnicode : Encoding.UTF8).GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, capacity)).CopyTo(target.AsSpan(offset));
    }

    private static ushort ToBcd16(ushort value) { ushort r = 0; for (var i = 0; i < 4; i++) { r |= (ushort)((value % 10) << (i * 4)); value /= 10; } return r; }
    private static uint ToBcd32(uint value) { uint r = 0; for (var i = 0; i < 8; i++) { r |= (value % 10) << (i * 4); value /= 10; } return r; }
    private static int ElementBit(ArrayDef d, int i) => d.ElementStrideBits > 0 ? checked(i * d.ElementStrideBits) : checked(i * d.ElementStrideBytes * 8);
    private static T[] Values<T>(int count, Func<int, T> factory) { var r = new T[count]; for (var i = 0; i < count; i++) r[i] = factory(i); return r; }
    private static StatusCode[] Repeat(IReadOnlyList<StatusCode> source, int count) { var r = new StatusCode[checked(source.Count * count)]; for (var i = 0; i < source.Count; i++) Array.Fill(r, source[i], i * count, count); return r; }
}
