using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using IGW.Abstractions;
using IoTClient.Clients.PLC;

namespace IGW.Drivers.IoTClient;

public readonly record struct SiemensByteReadResult(
    bool IsGood, byte[] Data, StatusCode Status, string Error)
{
    public static SiemensByteReadResult Good(byte[] data) => new(true, data, StatusCode.Good, "");
    public static SiemensByteReadResult Failed(StatusCode status, string error) => new(false, [], status, error);
}

/// <summary>Classic S7 连续 DB 字节传输。协议适配器只处理分块，布局与类型由 SiemensArrayCodec 统一处理。</summary>
public interface ISiemensByteTransport
{
    int MaxPayloadBytes { get; }
    ValueTask<SiemensByteReadResult> ReadAsync(int dbNumber, int byteOffset, int length, CancellationToken ct);
    ValueTask<StatusCode> WriteAsync(int dbNumber, int byteOffset, ReadOnlyMemory<byte> data, CancellationToken ct);
}

internal sealed class IoTSiemensByteTransport : ISiemensByteTransport
{
    private readonly SiemensClient _client;
    private readonly int _configuredMaxPayloadBytes;

    internal IoTSiemensByteTransport(SiemensClient client, int maxPayloadBytes = 200)
    {
        _client = client;
        _configuredMaxPayloadBytes = Math.Clamp(maxPayloadBytes, 1, ushort.MaxValue);
    }

    public int MaxPayloadBytes => _client is TimedSiemensClient { NegotiatedPdu: >= 19 } timed
        ? Math.Min(_configuredMaxPayloadBytes, timed.NegotiatedPdu - 18)
        : _configuredMaxPayloadBytes;

    public ValueTask<SiemensByteReadResult> ReadAsync(int dbNumber, int byteOffset, int length,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timing = new IoTProtocolTiming(new("Read", $"S7 {SiemensAreaCode.ToAddress(dbNumber, byteOffset)}", -1, length, -1));
        ushort requestLength = checked((ushort)length);
        var result = IoTReadBoundary.Invoke(() => IoTProtocolTiming.ClientIo(_client,
            () => _client.Read(SiemensAreaCode.ToAddress(dbNumber, byteOffset), requestLength, false)));
        if (!result.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(result, _client.Connected))
            throw new IOException("连接断开:" + result.Err);
        if (result.IsSucceed && result.Value is { } value)
        {
            // IoTClient 1.0.48 reverses the entire payload even for raw byte reads.
            // Normalize once here; codecs consume PLC address order, including across chunks.
            Array.Reverse(value);
            return ValueTask.FromResult(SiemensByteReadResult.Good(value));
        }
        return ValueTask.FromResult(SiemensByteReadResult.Failed(
            StatusCode.BadDeviceFailure, result.Err ?? "S7 分块读取失败"));
    }

    public ValueTask<StatusCode> WriteAsync(int dbNumber, int byteOffset, ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = WriteBytes(_client, SiemensAreaCode.ToAddress(dbNumber, byteOffset), data.Span);
        return ValueTask.FromResult(result.IsSucceed ? StatusCode.Good : StatusCode.BadDeviceFailure);
    }

    internal static global::IoTClient.Result WriteBytes(SiemensClient client, string address, ReadOnlySpan<byte> data)
    {
        // The library reverses its input in place. Offset that conversion on a private copy.
        var libraryBytes = data.ToArray();
        Array.Reverse(libraryBytes);
        using var timing = new IoTProtocolTiming(new("Write", address, 1, libraryBytes.Length, 0));
        return IoTProtocolTiming.ClientIo(client, () => client.Write(address, libraryBytes, false));
    }
}

/// <summary>S7 非优化 DB 数组编解码；数组始终作为一个逻辑值发布，传输按 PDU 上限自动分块。</summary>
public static class SiemensArrayCodec
{
    private static readonly PlcTextCodec TextCodec = new();

    /// <summary>Decode a configured array already covered by a bounded DB response; no PLC reads.</summary>
    public static ArrayRuntimeValue Decode(TagBinding binding, ReadOnlyMemory<byte> image)
    {
        var array = RequireArray(binding);
        var address = RequireClassicDb(binding.Def.Address);
        var quality = Enumerable.Repeat(StatusCode.Good, image.Length).ToArray();
        return DecodeArray(array, array.Shape, image.ToArray(), quality,
            address.BitOffset.GetValueOrDefault(), binding.TagId, binding.Def.TextEncoding);
    }

    public static async Task<ArrayRuntimeValue> ReadAsync(ISiemensByteTransport transport,
        TagBinding binding, CancellationToken ct, Func<bool>? priorityWorkPending = null)
    {
        var array = RequireArray(binding);
        var address = RequireClassicDb(binding.Def.Address);
        var shape = array.Shape;
        int startBit = checked((address.ByteOffset!.Value * 8) + address.BitOffset.GetValueOrDefault());
        int totalBits = TotalBits(array);
        int firstByte = startBit / 8;
        int lastExclusive = checked((startBit + totalBits + 7) / 8);
        var image = new byte[lastExclusive - firstByte];
        var quality = new StatusCode[image.Length];
        Array.Fill(quality, StatusCode.Bad);

        int chunkSize = Math.Max(1, transport.MaxPayloadBytes);
        for (int relative = 0; relative < image.Length; relative += chunkSize)
        {
            int length = Math.Min(chunkSize, image.Length - relative);
            if (priorityWorkPending?.Invoke() == true) throw new DriverReadYieldException();
            var result = await transport.ReadAsync(address.DbNumber!.Value, firstByte + relative, length, ct)
                .ConfigureAwait(false);
            if (!result.IsGood) continue;
            int copied = Math.Min(length, result.Data.Length);
            result.Data.AsSpan(0, copied).CopyTo(image.AsSpan(relative));
            Array.Fill(quality, StatusCode.Good, relative, copied);
        }

        return DecodeArray(array, shape, image, quality, startBit - firstByte * 8,
            binding.TagId, binding.Def.TextEncoding);
    }

    public static async Task<ArrayWriteResult> WriteAsync(ISiemensByteTransport transport,
        ArrayWriteRequest request, CancellationToken ct)
    {
        var array = RequireArray(request.Binding);
        var address = RequireClassicDb(request.Binding.Def.Address);
        var sourceShape = array.Shape;
        var sliceShape = request.Slice.ValidateAndCreateShape(sourceShape);
        EnsureSameShape(sliceShape, request.Value.Shape);

        int baseBit = checked(address.ByteOffset!.Value * 8 + address.BitOffset.GetValueOrDefault());
        var pending = new SortedDictionary<int, PendingByte>();
        for (int sliceOffset = 0; sliceOffset < request.Value.Count; sliceOffset++)
        {
            int sourceOffset = checked((int)sourceShape.GetLinearOffset(sliceShape.GetIndices(sliceOffset)));
            int elementBit = checked(baseBit + ElementBitOffset(array, sourceOffset));
            if (array.ElementStrideBits > 0)
            {
                int absoluteByte = elementBit / 8;
                if (!pending.TryGetValue(absoluteByte, out var bitByte))
                {
                    var current = await transport.ReadAsync(address.DbNumber!.Value, absoluteByte, 1, ct)
                        .ConfigureAwait(false);
                    if (!current.IsGood || current.Data.Length == 0)
                        return ArrayWriteResult.Failed(current.Status, current.Error);
                    pending[absoluteByte] = bitByte = new PendingByte(current.Data[0]);
                }
                bool bit = Convert.ToBoolean(request.Value.GetBoxedValue(sliceOffset), CultureInfo.InvariantCulture);
                int mask = 1 << (elementBit & 7);
                bitByte.Value = bit ? (byte)(bitByte.Value | mask) : (byte)(bitByte.Value & ~mask);
                bitByte.Owners.Add(sliceOffset);
                continue;
            }

            var encoded = EncodeElement(array, request.Value, sliceOffset,
                request.Binding.TagId, request.Binding.Def.TextEncoding);
            int absoluteByteOffset = elementBit / 8;
            for (int i = 0; i < encoded.Length; i++)
            {
                if (!pending.TryGetValue(absoluteByteOffset + i, out var item))
                    pending[absoluteByteOffset + i] = item = new PendingByte(encoded[i]);
                item.Value = encoded[i];
                item.Owners.Add(sliceOffset);
            }
        }

        var failed = new Dictionary<int, StatusCode>();
        foreach (var run in Runs(pending, Math.Max(1, transport.MaxPayloadBytes)))
        {
            var status = await transport.WriteAsync(address.DbNumber!.Value, run.Offset, run.Data, ct)
                .ConfigureAwait(false);
            if (status.IsGood) continue;
            foreach (int owner in run.Owners) failed[owner] = status;
        }

        var failures = failed.OrderBy(pair => pair.Key).Select(pair => new ArrayElementFailure(
            sliceShape.GetIndices(pair.Key), pair.Value, "S7 数组分块写入失败")).ToArray();
        int succeeded = request.Value.Count - failed.Count;
        return new ArrayWriteResult(failures.Length == 0 ? StatusCode.Good :
            succeeded > 0 ? StatusCode.Uncertain : failures[0].Status, succeeded, failures);
    }

    private static ArrayRuntimeValue DecodeArray(ArrayDef array, ArrayShape shape, byte[] image,
        StatusCode[] byteQuality, int baseBit, string key, PlcTextEncoding? encoding)
    {
        if (array.ElementType == IgwDataType.Struct)
            return DecodeStruct(shape, array.Struct ?? throw new InvalidDataException("结构体数组缺少布局"),
                i => checked(baseBit + ElementBitOffset(array, i)), image, byteQuality, key, encoding);
        return DecodePrimitive(shape, array.ElementType,
            i => checked(baseBit + ElementBitOffset(array, i)), array.StringLength,
            image, byteQuality, key, encoding, array.ElementStrideBits > 0 ? 1 : StrideBytes(array) * 8);
    }

    private static StructArrayValue DecodeStruct(ArrayShape shape, StructDef schema,
        Func<int, int> elementBaseBit, byte[] image, StatusCode[] byteQuality,
        string key, PlcTextEncoding? encoding)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            string memberKey = $"{key}.{field.Name}";
            if (field.Array is { } memberArray)
            {
                var dimensions = shape.Dimensions.Concat(memberArray.Dimensions).ToArray();
                var lower = shape.LowerBounds.Concat(memberArray.LowerBounds).ToArray();
                var memberShape = ArrayShape.Create(dimensions, lower);
                int innerCount = checked((int)memberArray.Shape.ElementCount);
                Func<int, int> memberBit = linear => checked(elementBaseBit(linear / innerCount) +
                    field.ByteOffset * 8 + Math.Max(0, field.BitOffset) +
                    ElementBitOffset(memberArray, linear % innerCount));
                members[field.Name] = memberArray.ElementType == IgwDataType.Struct
                    ? DecodeStruct(memberShape, memberArray.Struct ?? throw new InvalidDataException($"{field.Name} 缺少结构体布局"),
                        memberBit, image, byteQuality, memberKey, encoding)
                    : DecodePrimitive(memberShape, memberArray.ElementType, memberBit, memberArray.StringLength,
                        image, byteQuality, memberKey, encoding,
                        memberArray.ElementStrideBits > 0 ? 1 : memberArray.ElementStrideBytes * 8);
            }
            else if (field.Struct is { } nested)
            {
                members[field.Name] = DecodeStruct(shape, nested,
                    i => checked(elementBaseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)),
                    image, byteQuality, memberKey, encoding);
            }
            else
            {
                members[field.Name] = DecodePrimitive(shape, field.DataType,
                    i => checked(elementBaseBit(i) + field.ByteOffset * 8 + Math.Max(0, field.BitOffset)),
                    field.StringLength, image, byteQuality, memberKey, encoding, FieldBits(field));
            }
        }

        var outerQuality = new StatusCode[checked((int)shape.ElementCount)];
        for (int i = 0; i < outerQuality.Length; i++)
            outerQuality[i] = RangeQuality(byteQuality, elementBaseBit(i), checked(schema.SizeBytes * 8));
        return StructArrayValue.Create(shape, schema, members, outerQuality);
    }

    private static ArrayRuntimeValue DecodePrimitive(ArrayShape shape, IgwDataType type,
        Func<int, int> bitPosition, int? stringLength, byte[] image, StatusCode[] byteQuality,
        string key, PlcTextEncoding? encoding, int elementBits)
    {
        int count = checked((int)shape.ElementCount);
        var quality = new StatusCode[count];
        for (int i = 0; i < count; i++) quality[i] = RangeQuality(byteQuality, bitPosition(i), elementBits);

        return type switch
        {
            IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape,
                Values(count, i => ReadBit(image, bitPosition(i))), quality, type),
            IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape,
                Values(count, i => image[bitPosition(i) / 8]), quality, type),
            IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(shape,
                Values(count, i => unchecked((sbyte)image[bitPosition(i) / 8])), quality, type),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadInt16BigEndian(Span(image, bitPosition(i), 2))), quality, type),
            IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadUInt16BigEndian(Span(image, bitPosition(i), 2))), quality, type),
            IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadInt32BigEndian(Span(image, bitPosition(i), 4))), quality, type),
            IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadUInt32BigEndian(Span(image, bitPosition(i), 4))), quality, type),
            IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadInt64BigEndian(Span(image, bitPosition(i), 8))), quality, type),
            IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape,
                Values(count, i => BinaryPrimitives.ReadUInt64BigEndian(Span(image, bitPosition(i), 8))), quality, type),
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape,
                Values(count, i => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Span(image, bitPosition(i), 4)))), quality, type),
            IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape,
                Values(count, i => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Span(image, bitPosition(i), 8)))), quality, type),
            IgwDataType.String => PrimitiveArrayValue<string>.Create(shape,
                Values(count, i => ReadString(image, bitPosition(i), stringLength, key, encoding, i)), quality, type),
            IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape,
                Values(count, i => ReadWString(image, bitPosition(i), stringLength)), quality, type),
            IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(shape,
                Values(count, i => Span(image, bitPosition(i), Math.Max(1, elementBits / 8)).ToArray()), quality, type),
            _ => throw new NotSupportedException($"S7 数组元素类型尚不支持:{type}"),
        };
    }

    private static byte[] EncodeElement(ArrayDef array, ArrayRuntimeValue value, int offset,
        string key, PlcTextEncoding? encoding)
    {
        int bytes = array.ElementStrideBytes > 0 ? array.ElementStrideBytes : (TotalBits(array) + 7) / 8;
        var result = new byte[Math.Max(1, bytes)];
        if (array.ElementType == IgwDataType.Struct)
        {
            var structured = value as StructArrayValue ?? throw new ArgumentException("结构体数组写入值类型不匹配");
            EncodeStruct(result, 0, structured.Schema, structured, offset, key, encoding);
        }
        else EncodePrimitive(result, 0, array.ElementType, value.GetBoxedValue(offset), array.StringLength, key, encoding);
        return result;
    }

    private static void EncodeStruct(byte[] target, int baseByte, StructDef schema, StructArrayValue value,
        int outerOffset, string key, PlcTextEncoding? encoding)
    {
        foreach (var field in schema.Fields)
        {
            var member = value.Members[field.Name];
            string memberKey = $"{key}.{field.Name}";
            if (field.Array is { } memberArray)
            {
                int innerCount = checked((int)memberArray.Shape.ElementCount);
                for (int inner = 0; inner < innerCount; inner++)
                {
                    int memberOffset = checked(outerOffset * innerCount + inner);
                    int bit = checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset) + ElementBitOffset(memberArray, inner));
                    if (memberArray.ElementType == IgwDataType.Struct)
                        EncodeStruct(target, bit / 8, memberArray.Struct!, (StructArrayValue)member, memberOffset, memberKey, encoding);
                    else
                        EncodePrimitive(target, bit, memberArray.ElementType, member.GetBoxedValue(memberOffset),
                            memberArray.StringLength, memberKey, encoding);
                }
            }
            else if (field.Struct is { } nested)
            {
                EncodeStruct(target, baseByte + field.ByteOffset, nested, (StructArrayValue)member,
                    outerOffset, memberKey, encoding);
            }
            else
            {
                int bit = checked((baseByte + field.ByteOffset) * 8 + Math.Max(0, field.BitOffset));
                EncodePrimitive(target, bit, field.DataType, member.GetBoxedValue(outerOffset),
                    field.StringLength, memberKey, encoding);
            }
        }
    }

    private static void EncodePrimitive(byte[] target, int bit, IgwDataType type, object? value,
        int? stringLength, string key, PlcTextEncoding? encoding)
    {
        int offset = bit / 8;
        switch (type)
        {
            case IgwDataType.Bool:
                int mask = 1 << (bit & 7);
                target[offset] = Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                    ? (byte)(target[offset] | mask) : (byte)(target[offset] & ~mask);
                break;
            case IgwDataType.Byte: target[offset] = Convert.ToByte(value, CultureInfo.InvariantCulture); break;
            case IgwDataType.Char: target[offset] = unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(target.AsSpan(offset, 2), Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt16 or IgwDataType.Bcd: BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), Convert.ToUInt16(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(offset, 4), Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt32 or IgwDataType.LBcd: BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Int64: BinaryPrimitives.WriteInt64BigEndian(target.AsSpan(offset, 8), Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.UInt64: BinaryPrimitives.WriteUInt64BigEndian(target.AsSpan(offset, 8), Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            case IgwDataType.Float: BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture))); break;
            case IgwDataType.Double: BinaryPrimitives.WriteInt64BigEndian(target.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture))); break;
            case IgwDataType.String: WriteString(target, offset, stringLength, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", key, encoding); break;
            case IgwDataType.WString: WriteWString(target, offset, stringLength, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            case IgwDataType.ByteArray:
                if (value is byte[] raw) raw.AsSpan(0, Math.Min(raw.Length, target.Length - offset)).CopyTo(target.AsSpan(offset));
                break;
            default: throw new NotSupportedException($"S7 数组元素类型尚不支持:{type}");
        }
    }

    private static string ReadString(byte[] image, int bit, int? configuredLength,
        string key, PlcTextEncoding? encoding, int index)
    {
        int offset = bit / 8;
        int capacity = configuredLength.GetValueOrDefault(image[offset]);
        int current = Math.Min(image[offset + 1], Math.Max(0, capacity));
        current = Math.Min(current, Math.Max(0, image.Length - offset - 2));
        return TextCodec.Decode($"{key}[{index}]", image.AsSpan(offset + 2, current), encoding);
    }

    private static string ReadWString(byte[] image, int bit, int? configuredLength)
    {
        int offset = bit / 8;
        int capacity = configuredLength.GetValueOrDefault(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset, 2)));
        int current = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset + 2, 2)), Math.Max(0, capacity));
        current = Math.Min(current, Math.Max(0, (image.Length - offset - 4) / 2));
        return Encoding.BigEndianUnicode.GetString(image, offset + 4, current * 2);
    }

    private static void WriteString(byte[] target, int offset, int? configuredLength, string text,
        string key, PlcTextEncoding? encoding)
    {
        int capacity = Math.Clamp(configuredLength.GetValueOrDefault(target.Length - offset - 2), 1, 254);
        var bytes = TextCodec.Encode(key, text, encoding, Math.Min(capacity, target.Length - offset - 2));
        target[offset] = (byte)capacity;
        target[offset + 1] = (byte)bytes.Length;
        bytes.CopyTo(target, offset + 2);
    }

    private static void WriteWString(byte[] target, int offset, int? configuredLength, string text)
    {
        int capacity = Math.Max(1, configuredLength.GetValueOrDefault((target.Length - offset - 4) / 2));
        int length = Math.Min(capacity, text.Length);
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), checked((ushort)capacity));
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset + 2, 2), checked((ushort)length));
        Encoding.BigEndianUnicode.GetBytes(text.AsSpan(0, length), target.AsSpan(offset + 4, length * 2));
    }

    private static StatusCode RangeQuality(StatusCode[] byteQuality, int bit, int lengthBits)
    {
        int first = Math.Max(0, bit / 8);
        int last = Math.Min(byteQuality.Length - 1, checked((bit + Math.Max(1, lengthBits) - 1) / 8));
        if (first >= byteQuality.Length || last < first) return StatusCode.BadDecodeError;
        StatusCode result = StatusCode.Good;
        for (int i = first; i <= last; i++)
        {
            if (byteQuality[i].Class == QualityClass.Bad) return byteQuality[i];
            if (byteQuality[i].Class == QualityClass.Uncertain) result = StatusCode.Uncertain;
        }
        return result;
    }

    private static ReadOnlySpan<byte> Span(byte[] image, int bit, int length) => image.AsSpan(bit / 8, length);
    private static bool ReadBit(byte[] image, int bit) => (image[bit / 8] & (1 << (bit & 7))) != 0;
    private static T[] Values<T>(int count, Func<int, T> factory)
    {
        var values = new T[count];
        for (int i = 0; i < count; i++) values[i] = factory(i);
        return values;
    }

    private static int TotalBits(ArrayDef array) => array.ElementStrideBits > 0
        ? checked((int)(array.Shape.ElementCount * array.ElementStrideBits))
        : checked((int)(array.Shape.ElementCount * StrideBytes(array) * 8L));

    private static int ElementBitOffset(ArrayDef array, int linearOffset) => array.ElementStrideBits > 0
        ? checked(linearOffset * array.ElementStrideBits)
        : checked(linearOffset * StrideBytes(array) * 8);

    private static int StrideBytes(ArrayDef array) => array.ElementStrideBytes > 0
        ? array.ElementStrideBytes : array.ElementType == IgwDataType.Struct ? array.Struct?.SizeBytes ?? 0 : 0;

    private static int FieldBits(StructFieldDef field) => field.DataType switch
    {
        IgwDataType.Bool => 1,
        IgwDataType.Byte or IgwDataType.Char => 8,
        IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 16,
        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 32,
        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 64,
        IgwDataType.String => checked((field.StringLength.GetValueOrDefault(254) + 2) * 8),
        IgwDataType.WString => checked((field.StringLength.GetValueOrDefault(254) * 2 + 4) * 8),
        IgwDataType.ByteArray => checked(field.StringLength.GetValueOrDefault(1) * 8),
        _ => 8,
    };

    private static ArrayDef RequireArray(TagBinding binding) => binding.Def.Array ??
        throw new ArgumentException($"点位 {binding.TagId} 不是数组。", nameof(binding));

    private static SiemensAddressDescriptor RequireClassicDb(string address)
    {
        var descriptor = SiemensAddressDescriptor.Parse(address);
        if (descriptor.Kind != SiemensAddressKind.Classic || descriptor.DbNumber is null || descriptor.ByteOffset is null)
            throw new InvalidDataException($"S7 Classic 数组必须使用 DB 绝对地址，当前地址:{address}");
        return descriptor;
    }

    private static void EnsureSameShape(ArrayShape expected, ArrayShape actual)
    {
        if (!expected.Dimensions.SequenceEqual(actual.Dimensions) ||
            !expected.LowerBounds.SequenceEqual(actual.LowerBounds))
            throw new ArgumentException("写入数组值形状与请求切片不一致。");
    }

    private sealed class PendingByte(byte value)
    {
        public byte Value { get; set; } = value;
        public HashSet<int> Owners { get; } = [];
    }

    private sealed record WriteRun(int Offset, byte[] Data, HashSet<int> Owners);

    private static IEnumerable<WriteRun> Runs(SortedDictionary<int, PendingByte> pending, int maxBytes)
    {
        if (pending.Count == 0) yield break;
        var entries = pending.ToArray();
        int start = 0;
        while (start < entries.Length)
        {
            int count = 1;
            while (start + count < entries.Length && count < maxBytes &&
                   entries[start + count].Key == entries[start].Key + count) count++;
            var data = new byte[count];
            var owners = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                data[i] = entries[start + i].Value.Value;
                owners.UnionWith(entries[start + i].Value.Owners);
            }
            yield return new WriteRun(entries[start].Key, data, owners);
            start += count;
        }
    }
}
