using System.Buffers.Binary;
using System.Text;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>Decodes only configured members; response images are scoped to the current logical read.</summary>
public static class SiemensClassicBlockCodec
{
    public static IReadOnlyList<TagSample> Decode(SiemensClassicReadRange range,
        ReadOnlyMemory<byte> image, DateTime timestamp)
        => Decode(range, image, timestamp, new PlcTextCodec());

    internal static IReadOnlyList<TagSample> Decode(SiemensClassicReadRange range,
        ReadOnlyMemory<byte> image, DateTime timestamp, PlcTextCodec textCodec)
    {
        var samples = new List<TagSample>(range.Tags.Count);
        foreach (var tag in range.Tags)
        {
            try
            {
                var descriptor = SiemensAddressDescriptor.Parse(tag.Def.Address);
                int relative = checked(descriptor.ByteOffset!.Value - range.Start);
                int length = ProtocolStorageSize.Estimate(tag.Def, tag.Address);
                if (descriptor.DbNumber != range.DbNumber || relative < 0 ||
                    length > range.Count - relative || length > image.Length - relative)
                    throw new InvalidDataException("S7 响应短于配置点位范围。");
                object value = tag.Def.Array is not null
                    ? SiemensArrayCodec.Decode(tag, image.Slice(relative, length))
                    : DecodeScalar(tag, image.Span.Slice(relative, length), descriptor.BitOffset ?? 0, textCodec);
                var status = value is ArrayRuntimeValue array ? array.OverallQuality : StatusCode.Good;
                samples.Add(new(tag.TagId, new DataValue(value, status, timestamp, timestamp)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                FormatException or OverflowException or NotSupportedException)
            {
                samples.Add(new(tag.TagId, DataValue.BadAt(StatusCode.BadDecodeError, timestamp)));
            }
        }
        return samples;
    }

    public static Task<IReadOnlyList<TagSample>> ReadAsync(ISiemensByteTransport transport,
        IReadOnlyList<TagBinding> tags, CancellationToken ct, Func<bool>? priorityWorkPending = null,
        Action<string>? readError = null, AdaptiveRequestWindow? window = null, bool trainWindow = true,
        DriverBatchingSettings? batchingSettings = null)
        => ReadAsync(transport, tags, new PlcTextCodec(), ct, priorityWorkPending, readError, window, trainWindow, batchingSettings);

    internal static async Task<IReadOnlyList<TagSample>> ReadAsync(ISiemensByteTransport transport,
        IReadOnlyList<TagBinding> tags, PlcTextCodec textCodec, CancellationToken ct,
        Func<bool>? priorityWorkPending = null, Action<string>? readError = null, AdaptiveRequestWindow? window = null, bool trainWindow = true)
        => await ReadAsync(transport, tags, textCodec, ct, priorityWorkPending, readError, window, trainWindow, null).ConfigureAwait(false);

    internal static async Task<IReadOnlyList<TagSample>> ReadAsync(ISiemensByteTransport transport,
        IReadOnlyList<TagBinding> tags, PlcTextCodec textCodec, CancellationToken ct,
        Func<bool>? priorityWorkPending, Action<string>? readError, AdaptiveRequestWindow? window, bool trainWindow,
        DriverBatchingSettings? batchingSettings)
    {
        var planWindow = window?.Capture();
        int payload = Math.Clamp(Math.Min(transport.MaxPayloadBytes, planWindow?.Current ?? int.MaxValue), 1, ushort.MaxValue);
        int activePayload = payload; // Only capacity failure changes this in-flight plan's physical limit.
        var boundedTransport = new BoundedReadTransport(payload, (db, start, length) => ReadRawSlice(db, start, length));
        var ranges = SiemensClassicReadPlanner.Plan(tags, payload, batchingSettings);
        var included = ranges.SelectMany(r => r.Tags).ToHashSet();
        var samples = new List<TagSample>(tags.Count);
        foreach (var range in ranges) await ReadRange(range).ConfigureAwait(false);

        foreach (var tag in tags.Where(tag => !included.Contains(tag)))
        {
            CheckBoundary();
            if (!SiemensClassicReadPlanner.HasKnownSpan(tag))
            {
                readError?.Invoke($"{tag.Def.Address}: S7 数组缺少已确认的字节布局。");
                samples.Add(new(tag.TagId, DataValue.BadAt(StatusCode.BadDecodeError, DateTime.UtcNow)));
                continue;
            }
            if (tag.Def.Array is not null)
            {
                var value = await SiemensArrayCodec.ReadAsync(boundedTransport, tag, ct, priorityWorkPending).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                samples.Add(new(tag.TagId, new DataValue(value, value.OverallQuality, now, now)));
                continue;
            }
            // A single oversized string is bounded by its declared field, never by a whole DB.
            var address = SiemensAddressDescriptor.Parse(tag.Def.Address);
            int length = ProtocolStorageSize.Estimate(tag.Def, tag.Address);
            var image = new byte[length];
            bool complete = true;
            for (int relative = 0; relative < length; relative += payload)
            {
                CheckBoundary();
                int count = Math.Min(payload, length - relative);
                var result = await ReadRawSlice(address.DbNumber!.Value,
                    checked(address.ByteOffset!.Value + relative), count).ConfigureAwait(false);
                ThrowIfTransportFailure(result);
                if (!result.IsGood || result.Data.Length < count)
                {
                    complete = false;
                    readError?.Invoke($"{tag.Def.Address}: {result.Error} S7 响应未覆盖完整点位。");
                    break;
                }
                ct.ThrowIfCancellationRequested();
                result.Data.AsSpan(0, count).CopyTo(image.AsSpan(relative));
            }
            if (complete)
                AddDecoded(new(address.DbNumber!.Value, address.ByteOffset!.Value, length, 0, [tag]), image);
            else samples.Add(new(tag.TagId, DataValue.BadAt(StatusCode.BadDecodeError, DateTime.UtcNow)));
        }
        return samples;

        void CheckBoundary()
        {
            ct.ThrowIfCancellationRequested();
            if (priorityWorkPending?.Invoke() == true) throw new DriverReadYieldException();
        }

        void AddDecoded(SiemensClassicReadRange range, byte[] image)
        {
            var decoded = Decode(range, image, DateTime.UtcNow, textCodec);
            foreach (var sample in decoded.Where(s => !s.Value.IsGood))
                readError?.Invoke($"{range.Tags.First(t => t.TagId == sample.TagId).Def.Address}: S7 响应长度或点位值解码失败。");
            samples.AddRange(decoded);
        }

        async Task<SiemensByteReadResult> ReadRawSlice(int db, int start, int length, bool retried = false)
        {
            byte[]? image = null;
            int cursor = 0;
            int recoveryEnd = retried ? length : 0;
            while (cursor < length)
            {
                CheckBoundary();
                int count = Math.Min(activePayload, length - cursor);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = await transport.ReadAsync(db, checked(start + cursor), count, ct).ConfigureAwait(false);
                double requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                ct.ThrowIfCancellationRequested();
                ThrowIfTransportFailure(result);
                if (!result.IsGood && IsCapacityFailure(result) && count > 1)
                {
                    recoveryEnd = Math.Max(recoveryEnd, cursor + count);
                    activePayload = Math.Min(activePayload, window?.OnCapacityFailure(count) ?? Math.Max(1, count / 2));
                    continue;
                }
                if (!result.IsGood) return result;
                if (result.Data.Length >= count && trainWindow && cursor >= recoveryEnd && planWindow is { } captured)
                    window!.OnSuccess(count, requestMs, captured);
                // Preserve short-response handling instead of filling missing bytes with Good zeros.
                if (cursor == 0 && count == length) return result;
                image ??= new byte[length];
                int copied = Math.Min(count, result.Data.Length);
                result.Data.AsSpan(0, copied).CopyTo(image.AsSpan(cursor));
                cursor += copied;
                if (copied < count) return SiemensByteReadResult.Good(image.AsSpan(0, cursor).ToArray());
            }
            return SiemensByteReadResult.Good(image ?? []);
        }

        async Task ReadRange(SiemensClassicReadRange range, bool retried = false)
        {
            var result = await ReadRawSlice(range.DbNumber, range.Start, range.Count, retried).ConfigureAwait(false);
            if (result.IsGood)
            {
                AddDecoded(range, result.Data);
                return;
            }
            readError?.Invoke($"{SiemensAreaCode.ToAddress(range.DbNumber, range.Start)} ({range.Count} bytes): {result.Error}");
            if (range.Tags.Count == 1)
            {
                samples.Add(new(range.Tags[0].TagId, DataValue.BadAt(result.Status, DateTime.UtcNow)));
                return;
            }
            // Connected range/data failure: retry only this range's two member halves.
            int middle = range.Tags.Count / 2;
            foreach (var half in new[] { range.Tags.Take(middle).ToArray(), range.Tags.Skip(middle).ToArray() })
                foreach (var retry in SiemensClassicReadPlanner.Plan(half, payload, batchingSettings))
                    await ReadRange(retry, retried: true).ConfigureAwait(false);
        }
    }

    private sealed class BoundedReadTransport(int payload, Func<int, int, int, Task<SiemensByteReadResult>> read)
        : ISiemensByteTransport
    {
        public int MaxPayloadBytes => payload;
        public ValueTask<SiemensByteReadResult> ReadAsync(int db, int offset, int length, CancellationToken ct) =>
            new(read(db, offset, length));
        public ValueTask<StatusCode> WriteAsync(int db, int offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
            throw new NotSupportedException("This adapter is scoped to one logical read.");
    }

    private static bool IsCapacityFailure(SiemensByteReadResult result) =>
        result.Error.Contains("PDU", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("size limit", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("buffer too small", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfTransportFailure(SiemensByteReadResult result)
    {
        if (!result.IsGood && (result.Status == StatusCode.BadTimeout || result.Status == StatusCode.BadCommFailure ||
            IoTPlcDeviceDriver.LooksLikeTransportFailure(result.Error)))
            throw new IOException("连接异常:" + result.Error);
    }

    private static object DecodeScalar(TagBinding tag, ReadOnlySpan<byte> bytes, int bit, PlcTextCodec textCodec) => tag.Def.DataType switch
    {
        IgwDataType.Bool => (bytes[0] & (1 << bit)) != 0,
        IgwDataType.Byte => bytes[0],
        IgwDataType.Char or IgwDataType.SInt => unchecked((sbyte)bytes[0]),
        IgwDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
        IgwDataType.UInt16 or IgwDataType.Word or IgwDataType.Bcd or IgwDataType.Timer or IgwDataType.Counter => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        IgwDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
        IgwDataType.UInt32 or IgwDataType.DWord or IgwDataType.LBcd => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        IgwDataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(bytes),
        IgwDataType.UInt64 or IgwDataType.LWord => BinaryPrimitives.ReadUInt64BigEndian(bytes),
        IgwDataType.Float => BinaryPrimitives.ReadSingleBigEndian(bytes),
        IgwDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(bytes),
        IgwDataType.String => ReadString(tag, bytes, textCodec),
        IgwDataType.WString => ReadWString(tag, bytes),
        IgwDataType.ByteArray => bytes.ToArray(),
        _ => throw new NotSupportedException($"S7 原始块尚未实现 {tag.Def.DataType} 解码。"),
    };

    private static string ReadString(TagBinding tag, ReadOnlySpan<byte> bytes, PlcTextCodec textCodec)
    {
        int capacity = bytes[0], length = bytes[1];
        if (capacity > 254 || length > capacity || capacity > (tag.Def.StringLength ?? 254) || length > bytes.Length - 2)
            throw new InvalidDataException("S7 STRING 长度头非法。");
        return textCodec.Decode(tag.TagId, bytes.Slice(2, length), tag.Def.TextEncoding);
    }

    private static string ReadWString(TagBinding tag, ReadOnlySpan<byte> bytes)
    {
        int capacity = BinaryPrimitives.ReadUInt16BigEndian(bytes), length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (length > capacity || capacity > (tag.Def.StringLength ?? 254) || length * 2 > bytes.Length - 4)
            throw new InvalidDataException("S7 WSTRING 长度头非法。");
        return Encoding.BigEndianUnicode.GetString(bytes.Slice(4, length * 2));
    }
}
