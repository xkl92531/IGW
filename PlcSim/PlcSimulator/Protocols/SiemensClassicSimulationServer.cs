#nullable enable
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

public sealed class SiemensClassicSimulationServer : IAsyncDisposable
{
    private sealed record TagMemory(SimulationTag Tag, int Db, int Offset, int Bit, int Size)
    {
        public string HslAddress => $"DB{Db}.{Offset}";
    }

    private static readonly Regex AddressPattern = new(
        @"^DB(?<db>\d+)\.DB(?<kind>[XBWD])(?<offset>\d+)(?:\.(?<bit>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StringLengthPattern = new(
        @"String\[(?<length>\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SimulationValueStore _store;
    private readonly StoppableSiemensS7Server _server = new();
    private readonly Dictionary<Guid, TagMemory> _tags;
    private readonly int _port;
    private Timer? _poller;
    private int _syncing;
    private int _pollThreadId;

    public SiemensClassicSimulationServer(SimulationDevice device, SimulationValueStore store, int port)
    {
        _store = store;
        _port = port;
        _tags = device.EnumerateTags()
            .Select(TryMap)
            .Where(x => x is not null)
            .Cast<TagMemory>()
            .ToDictionary(x => x.Tag.Id);
    }

    public bool IsRunning => _server.IsStarted;
    public int ListenPort => _port;
    public int OnlineCount => _server.OnlineCount;
    public bool EnableWrite { get => _server.EnableWrite; set => _server.EnableWrite = value; }

    public void Start()
    {
        if (_server.IsStarted) return;
        try
        {
            _server.ServerStart(_port);
            foreach (var block in _tags.Values.GroupBy(x => x.Db))
            {
                int size = Math.Max(1, block.Max(x => x.Offset + x.Size));
                _server.AddDbBlock(block.Key);
                var initialized = _server.Write($"DB{block.Key}.0", new byte[size]);
                if (!initialized.IsSuccess) throw new InvalidOperationException($"初始化 DB{block.Key} 失败：{initialized.Message}");
            }
            foreach (var tag in _tags.Values)
            {
                try { WriteServer(tag, _store.Read(tag.Tag.Id).Value); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"初始化点位 {tag.Tag.SymbolPath}（{tag.Tag.ClassicAddress}，{tag.Tag.DataType}）失败：{ex.Message}", ex);
                }
            }
            _store.ValueChanged += OnStoreChanged;
            _poller = new Timer(_ => Poll(), null, 50, 50);
        }
        catch
        {
            try { _server.ServerClose(); } catch { }
            throw;
        }
    }

    private void OnStoreChanged(SimulationValueChanged change)
    {
        // Skip only this poll callback; concurrent UI/other protocol writes still propagate.
        if (change.Source == SimulationWriteSource.Protocol && Volatile.Read(ref _pollThreadId) == Environment.CurrentManagedThreadId) return;
        if (!_tags.TryGetValue(change.TagId, out var tag)) return;
        WriteServer(tag, _store.Read(change.TagId).Value);
    }

    private void Poll()
    {
        if (!_server.IsStarted || Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        Volatile.Write(ref _pollThreadId, Environment.CurrentManagedThreadId);
        try
        {
            foreach (var tag in _tags.Values)
            {
                var before = _store.Read(tag.Tag.Id);
                object? value = ReadServer(tag);
                if (value is not null && !ValueEquals(value, before.Value))
                    _store.TryWrite(tag.Tag.Id, value, SimulationWriteSource.Protocol, before.Version);
            }
        }
        catch
        {
            // 单次同步失败不终止协议服务；下一次轮询继续。
        }
        finally { Volatile.Write(ref _pollThreadId, 0); Volatile.Write(ref _syncing, 0); }
    }

    private object? ReadServer(TagMemory memory)
    {
        var result = _server.Read(memory.HslAddress, (ushort)memory.Size);
        if (!result.IsSuccess || result.Content is null || result.Content.Length < memory.Size) return null;
        if (memory.Tag.IsArray)
        {
            var initial = (Array)_store.Read(memory.Tag.Id).Value!;
            var values = Array.CreateInstance(initial.GetType().GetElementType()!, initial.Length);
            for (int i = 0; i < values.Length; i++)
            {
                var element = ElementMemory(memory, i);
                values.SetValue(Decode(element, result.Content.AsSpan(element.Offset - memory.Offset, element.Size)), i);
            }
            return values;
        }
        return Decode(memory, result.Content);
    }

    private static object? Decode(TagMemory memory, ReadOnlySpan<byte> bytes)
    {
        return memory.Tag.DataType switch
        {
            SimulationDataType.Boolean => (bytes[0] & (1 << memory.Bit)) != 0,
            SimulationDataType.Byte => bytes[0],
            SimulationDataType.SByte => unchecked((sbyte)bytes[0]),
            SimulationDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
            SimulationDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
            SimulationDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            SimulationDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            SimulationDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(bytes),
            SimulationDataType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(bytes),
            SimulationDataType.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
            SimulationDataType.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
            SimulationDataType.String => memory.Tag.IsWideString ? DecodeWideString(bytes) : DecodeString(memory.Tag, bytes),
            _ => null,
        };
    }

    private void WriteServer(TagMemory memory, object? value)
    {
        if (memory.Tag.IsArray)
        {
            var values = (Array)value!;
            var current = _server.Read(memory.HslAddress, checked((ushort)memory.Size));
            if (!current.IsSuccess) throw new SimulationValueException(current.Message);
            var bytes = current.Content;
            for (int i = 0; i < values.Length; i++)
            {
                var element = ElementMemory(memory, i);
                int offset = element.Offset - memory.Offset;
                if (memory.Tag.DataType == SimulationDataType.Boolean)
                {
                    if ((bool)values.GetValue(i)!) bytes[offset] |= (byte)(1 << element.Bit);
                    else bytes[offset] &= (byte)~(1 << element.Bit);
                }
                else Encode(element, values.GetValue(i)).CopyTo(bytes, offset);
            }
            var written = _server.Write(memory.HslAddress, bytes);
            if (!written.IsSuccess) throw new SimulationValueException(written.Message);
            return;
        }
        WriteScalar(memory, value);
    }

    private void WriteScalar(TagMemory memory, object? value)
    {
        byte[] bytes = Encode(memory, value);
        if (memory.Tag.DataType == SimulationDataType.Boolean)
        {
            var current = _server.Read(memory.HslAddress, 1);
            byte packed = current.IsSuccess && current.Content is { Length: > 0 } ? current.Content[0] : (byte)0;
            if (Convert.ToBoolean(value)) packed |= (byte)(1 << memory.Bit);
            else packed &= (byte)~(1 << memory.Bit);
            bytes[0] = packed;
        }
        var result = _server.Write(memory.HslAddress, bytes);
        if (!result.IsSuccess) throw new InvalidOperationException($"写入 {memory.Tag.ClassicAddress} 失败：{result.Message}");
    }

    private static byte[] Encode(TagMemory memory, object? value)
    {
        var bytes = new byte[memory.Size];
        switch (memory.Tag.DataType)
        {
            case SimulationDataType.Boolean:
                break;
            case SimulationDataType.Byte:
                bytes[0] = Convert.ToByte(value); break;
            case SimulationDataType.SByte:
                bytes[0] = unchecked((byte)Convert.ToSByte(value)); break;
            case SimulationDataType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(bytes, Convert.ToInt16(value)); break;
            case SimulationDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(value)); break;
            case SimulationDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(bytes, Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64:
                BinaryPrimitives.WriteUInt64BigEndian(bytes, Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32:
                BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(Convert.ToSingle(value))); break;
            case SimulationDataType.Float64:
                BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value))); break;
            case SimulationDataType.String:
                if (memory.Tag.IsWideString) EncodeWideString(bytes, Convert.ToString(value) ?? string.Empty);
                else EncodeString(memory.Tag, bytes, Convert.ToString(value) ?? string.Empty);
                break;
        }
        return bytes;
    }

    private static void EncodeString(SimulationTag tag, Span<byte> destination, string value)
    {
        int maximum = Math.Max(0, destination.Length - 2);
        byte[] encoded = SimulationStringCodec.Encode(tag, value);
        if (encoded.Length > maximum) throw new SimulationValueException($"STRING 最多允许 {maximum} 字节");
        int actual = encoded.Length;
        destination[0] = (byte)Math.Min(byte.MaxValue, maximum);
        destination[1] = (byte)Math.Min(byte.MaxValue, actual);
        encoded.AsSpan(0, actual).CopyTo(destination[2..]);
    }

    private static string DecodeString(SimulationTag tag, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2) return string.Empty;
        int actual = bytes[1];
        if (actual > bytes[0] || actual > bytes.Length - 2) throw new SimulationValueException("STRING 长度头无效");
        return SimulationStringCodec.Decode(tag, bytes.Slice(2, actual));
    }

    private static void EncodeWideString(Span<byte> destination, string value)
    {
        int maximum = (destination.Length - 4) / 2;
        if (value.Length > maximum) throw new SimulationValueException($"WSTRING 最多允许 {maximum} 个 UTF-16 字符");
        BinaryPrimitives.WriteUInt16BigEndian(destination, checked((ushort)maximum));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], checked((ushort)value.Length));
        Encoding.BigEndianUnicode.GetBytes(value.AsSpan(), destination[4..]);
    }

    private static string DecodeWideString(ReadOnlySpan<byte> bytes)
    {
        int maximum = BinaryPrimitives.ReadUInt16BigEndian(bytes), length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (length > maximum || length * 2 > bytes.Length - 4) throw new SimulationValueException("WSTRING 长度头无效");
        return Encoding.BigEndianUnicode.GetString(bytes.Slice(4, length * 2));
    }

    private static TagMemory? TryMap(SimulationTag tag)
    {
        if (tag.DataType == SimulationDataType.Unknown || string.IsNullOrWhiteSpace(tag.ClassicAddress)) return null;
        Match match = AddressPattern.Match(tag.ClassicAddress);
        if (!match.Success) return null;
        int db = int.Parse(match.Groups["db"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int offset = int.Parse(match.Groups["offset"].Value, System.Globalization.CultureInfo.InvariantCulture);
        int bit = match.Groups["bit"].Success
            ? int.Parse(match.Groups["bit"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        int count = tag.IsArray ? tag.Dimensions.Aggregate(1, (a, b) => checked(a * b)) : 1;
        int elementSize = SizeOf(tag), stride = tag.ElementStrideBytes ?? elementSize;
        if (stride < elementSize) throw new SimulationValueException($"{tag.SymbolPath} 数组步长小于元素占用长度");
        int size = tag.DataType == SimulationDataType.Boolean ? (bit + count + 7) / 8 : checked(stride * (count - 1) + elementSize);
        if (size <= 0 || size > ushort.MaxValue || (long)offset + size > 65536)
            throw new SimulationValueException($"{tag.SymbolPath} 超出模拟 DB 的 65536 字节范围");
        return new TagMemory(tag, db, offset, bit, size);
    }

    private static TagMemory ElementMemory(TagMemory memory, int index) => memory.Tag.DataType == SimulationDataType.Boolean
        ? memory with { Offset = memory.Offset + (memory.Bit + index) / 8, Bit = (memory.Bit + index) % 8, Size = 1 }
        : memory with { Offset = memory.Offset + index * (memory.Tag.ElementStrideBytes ?? SizeOf(memory.Tag)), Size = SizeOf(memory.Tag) };

    private static int SizeOf(SimulationTag tag) => tag.DataType switch
    {
        SimulationDataType.Boolean or SimulationDataType.Byte or SimulationDataType.SByte => 1,
        SimulationDataType.Int16 or SimulationDataType.UInt16 => 2,
        SimulationDataType.Int32 or SimulationDataType.UInt32 or SimulationDataType.Float32 => 4,
        SimulationDataType.Int64 or SimulationDataType.UInt64 or SimulationDataType.Float64 => 8,
        SimulationDataType.String => tag.IsWideString ? checked(4 + 2 * StringCapacity(tag)) : checked(2 + StringCapacity(tag)),
        _ => 1,
    };

    private static int StringCapacity(SimulationTag tag) => tag.StringLength ??
        (StringLengthPattern.Match(tag.ClassicAddress) is { Success: true } match
            ? int.Parse(match.Groups["length"].Value, System.Globalization.CultureInfo.InvariantCulture) : 254);

    private static bool ValueEquals(object? left, object? right) =>
        left is Array a && right is Array b
            ? a.Cast<object?>().SequenceEqual(b.Cast<object?>())
            : Equals(left, right);

    public ValueTask DisposeAsync()
    {
        _poller?.Dispose();
        _poller = null;
        _store.ValueChanged -= OnStoreChanged;
        try { _server.ServerClose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
