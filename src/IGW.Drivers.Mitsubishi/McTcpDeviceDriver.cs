using System.Buffers.Binary;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;

namespace IGW.Drivers.Mitsubishi;

/// <summary>三菱 MELSEC MC 3E(二进制/TCP)设备驱动。MELSEC 线上为小端(字内 + 字序均低位在前)。</summary>
public sealed class McTcpDeviceDriver : IDeviceDriver, IArrayCapableDriver, IDriverCapabilityProvider,
    IDriverPriorityWorkAware
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeout;
    private readonly int _readMaxGap;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ConnectionState _state = ConnectionState.Disconnected;
    private Func<bool> _priorityWorkPending = static () => false;

    public string DriverId => MitsubishiMcDriverFactory.Id;
    public IAddressParser AddressParser { get; } = new McAddressParser();
    public DriverLimits Limits { get; } = new DriverLimits { MaxReadUnits = 480, MaxGap = 8, MaxTagsPerRead = 480, MaxTagsPerWrite = 480 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = true,
        PartitionsReadRequests = true,
        SupportsBatchWrite = true,
        MaxReadBytes = 960,
        MaxWriteBytes = 960,
    };
    public ConnectionState State => _state;
    public event Action<ConnectionState>? StateChanged;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
        SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
        MaxElementsPerRequest: int.MaxValue, MaxBytesPerRequest: 960);

    public McTcpDeviceDriver(string host, int port, int timeoutMs, DriverBatchingSettings? batchingSettings = null)
    {
        _host = host; _port = port; _timeout = timeoutMs <= 0 ? 2000 : timeoutMs;
        var batching = batchingSettings ?? DriverBatchingSettings.Parse(new Dictionary<string, string>());
        _readMaxGap = batching.Mode == DriverBatchingMode.Manual ? Math.Min(8, batching.MaxGap) : 8;
    }

    private void SetState(ConnectionState s) { if (_state == s) return; _state = s; StateChanged?.Invoke(s); }

    public void SetPriorityWorkProbe(Func<bool> priorityWorkPending) =>
        _priorityWorkPending = priorityWorkPending ?? (() => false);

    private void YieldToPriorityWriteIfNeeded()
    {
        if (_priorityWorkPending()) throw new DriverReadYieldException();
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        try
        {
            _tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            await _tcp.ConnectAsync(_host, _port, cts.Token);
            _tcp.ReceiveTimeout = _timeout;
            _tcp.SendTimeout = _timeout;
            _stream = _tcp.GetStream();
            SetState(ConnectionState.Connected);
        }
        catch { SetState(ConnectionState.Faulted); Cleanup(); throw; }
    }

    private void Cleanup() { try { _stream?.Dispose(); } catch { } try { _tcp?.Dispose(); } catch { } _stream = null; _tcp = null; }

    public Task DisconnectAsync() { Cleanup(); SetState(ConnectionState.Disconnected); return Task.CompletedTask; }

    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var samples = new List<TagSample>(tags.Count);
        if (_stream is null) throw new IOException("MC 未连接");
        var scalars = new List<McReadItem>(tags.Count);
        foreach (var tag in tags)
        {
            ct.ThrowIfCancellationRequested();
            if (!McAddress.TryParse(tag.Def.Address, out var loc, out _))
            { samples.Add(new TagSample(tag.TagId, DataValue.BadAt(StatusCode.BadDecodeError, now))); continue; }
            if (tag.Def.Array is not null)
            {
                YieldToPriorityWriteIfNeeded();
                try
                {
                    var array = ReadArray(loc, tag.Def.Array, ct);
                    samples.Add(new TagSample(tag.TagId, new DataValue(array, array.OverallQuality, now, now)));
                }
                catch (DriverReadYieldException) { throw; }
                catch (SocketException) { throw; }
                catch (IOException) { throw; }
                catch { samples.Add(new TagSample(tag.TagId, DataValue.BadAt(StatusCode.BadDecodeError, now))); }
                continue;
            }
            scalars.Add(new McReadItem(tag, loc,
                tag.Def.DataType == IgwDataType.Bool && loc.IsBit ? 1 : McAddress.Words(tag.Def.DataType)));
        }

        foreach (var group in GroupReads(scalars))
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            try
            {
                var first = group[0];
                int units = group.Max(item => item.Loc.Number + item.Units) - first.Loc.Number;
                int gapUnits = 0, end = first.Loc.Number;
                foreach (var item in group)
                {
                    gapUnits += Math.Max(0, item.Loc.Number - end);
                    end = Math.Max(end, item.Loc.Number + item.Units);
                }
                byte[] data = ReadDevice(first.Loc, units, first.Loc.IsBit, group.Count,
                    first.Loc.IsBit ? (gapUnits + 1) / 2 : gapUnits * 2);
                foreach (var item in group)
                {
                    int relative = item.Loc.Number - first.Loc.Number;
                    object value;
                    if (first.Loc.IsBit)
                        value = (data[relative >> 1] & ((relative & 1) == 0 ? 0x10 : 0x01)) != 0;
                    else
                    {
                        var slice = data.AsSpan(relative * 2, item.Units * 2).ToArray();
                        value = item.Binding.Def.DataType == IgwDataType.Bool
                            ? (BinaryPrimitives.ReadUInt16LittleEndian(slice) & 1) != 0
                            : Decode(slice, item.Binding.Def.DataType);
                    }
                    samples.Add(new TagSample(item.Binding.TagId, DataValue.GoodAt(value, now)));
                }
            }
            catch (SocketException) { throw; }
            catch (IOException) { throw; }
            catch
            {
                foreach (var item in group)
                    samples.Add(new TagSample(item.Binding.TagId, DataValue.BadAt(StatusCode.BadDeviceFailure, now)));
            }
        }
        return Task.FromResult<IReadOnlyList<TagSample>>(samples);
    }

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        if (_stream is null) throw new IOException("MC 未连接");
        var statuses = new Dictionary<string, StatusCode>(writes.Count, StringComparer.Ordinal);
        var parsed = new List<McWriteItem>(writes.Count);
        foreach (var write in writes)
        {
            if (!McAddress.TryParse(write.Def.Address, out var loc, out _))
            { statuses[write.TagId] = StatusCode.BadDecodeError; continue; }
            try
            {
                byte[] encoded = write.Def.DataType == IgwDataType.Bool && loc.IsBit
                    ? [(byte)(Convert.ToBoolean(write.Value) ? 0x10 : 0x00)]
                    : Encode(write.Value, write.Def.DataType);
                int units = loc.IsBit ? 1 : McAddress.Words(write.Def.DataType);
                parsed.Add(new McWriteItem(write, loc, units, encoded));
            }
            catch { statuses[write.TagId] = StatusCode.BadDecodeError; }
        }

        foreach (var group in GroupWrites(parsed))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var first = group[0];
                int units = group.Sum(item => item.Units);
                byte[] data;
                if (first.Loc.IsBit)
                {
                    data = new byte[(units + 1) / 2];
                    for (int i = 0; i < group.Count; i++)
                        if ((group[i].Encoded[0] & 0x10) != 0)
                            data[i >> 1] |= (byte)((i & 1) == 0 ? 0x10 : 0x01);
                }
                else data = group.SelectMany(item => item.Encoded).ToArray();
                WriteDevice(first.Loc, units, data, first.Loc.IsBit);
                foreach (var item in group) statuses[item.Request.TagId] = StatusCode.Good;
            }
            catch (SocketException) { throw; }
            catch (IOException) { throw; }
            catch (Exception ex)
            {
                foreach (var item in group)
                    statuses[item.Request.TagId] = ex is ArgumentException or FormatException or OverflowException
                        ? StatusCode.BadDecodeError
                        : StatusCode.BadDeviceFailure;
            }
        }

        return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
        {
            TagId = write.TagId,
            Status = statuses.GetValueOrDefault(write.TagId, StatusCode.BadDeviceFailure),
        }).ToArray());
    }

    private sealed record McReadItem(TagBinding Binding, McLoc Loc, int Units);
    private sealed record McWriteItem(WriteRequest Request, McLoc Loc, int Units, byte[] Encoded);

    private IReadOnlyList<IReadOnlyList<McReadItem>> GroupReads(IReadOnlyList<McReadItem> source) =>
        GroupContiguous(source, item => item.Loc, item => item.Units, maxGap: _readMaxGap);

    private static IReadOnlyList<IReadOnlyList<McWriteItem>> GroupWrites(IReadOnlyList<McWriteItem> source) =>
        GroupContiguous(source, item => item.Loc, item => item.Units);

    private static IReadOnlyList<IReadOnlyList<T>> GroupContiguous<T>(
        IReadOnlyList<T> source,
        Func<T, McLoc> location,
        Func<T, int> units, int maxGap = 0)
    {
        var result = new List<IReadOnlyList<T>>();
        foreach (var device in source.GroupBy(item => (location(item).Code, location(item).IsBit)))
        {
            var ordered = device.OrderBy(item => location(item).Number).ToArray();
            int limit = device.Key.IsBit ? 960 : 480;
            for (int i = 0; i < ordered.Length;)
            {
                var group = new List<T> { ordered[i] };
                int start = location(ordered[i]).Number;
                int end = start + units(ordered[i]);
                int j = i + 1;
                while (j < ordered.Length &&
                       (maxGap > 0 ? location(ordered[j]).Number <= end + maxGap : location(ordered[j]).Number == end) &&
                       location(ordered[j]).Number + units(ordered[j]) - start <= limit)
                {
                    group.Add(ordered[j]);
                    end = Math.Max(end, location(ordered[j]).Number + units(ordered[j]));
                    j++;
                }
                result.Add(group);
                i = j;
            }
        }
        return result;
    }

    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        if (_stream is null)
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadNotConnected, "MC 未连接"));
        if (!McAddress.TryParse(request.Binding.Def.Address, out var baseLoc, out var error))
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, error ?? "MC 地址错误"));
        var definition = request.Binding.Def.Array;
        if (definition is null)
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, "点位不是数组"));
        try
        {
            var targetShape = request.Slice.ValidateAndCreateShape(definition.Shape);
            var targetOffsets = new int[request.Value.Count];
            for (var i = 0; i < targetOffsets.Length; i++)
                targetOffsets[i] = checked((int)definition.Shape.GetLinearOffset(targetShape.GetIndices(i)));
            var encoded = RegisterArrayCodec.Encode(request.Value, definition, WordOrder.DCBA);
            var failures = new List<ArrayElementFailure>();
            var succeeded = 0;

            for (var source = 0; source < targetOffsets.Length;)
            {
                ct.ThrowIfCancellationRequested();
                var run = 1;
                while (source + run < targetOffsets.Length &&
                       targetOffsets[source + run] == targetOffsets[source] + run) run++;
                run = definition.ElementStrideBits > 0
                    ? Math.Min(run, 960)
                    : Math.Min(run, Math.Max(1, 960 / Math.Max(1, definition.ElementStrideBytes)));
                try
                {
                    if (definition.ElementStrideBits > 0)
                    {
                        if (!baseLoc.IsBit || definition.ElementType != IgwDataType.Bool ||
                            definition.ElementStrideBits != 1)
                            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                                "MC 位数组仅支持位设备上的连续 Bool 元素"));
                        var raw = PackMcBits(encoded, source, run);
                        WriteDevice(baseLoc with { Number = baseLoc.Number + targetOffsets[source] },
                            run, raw, bit: true);
                    }
                    else
                    {
                        if (baseLoc.IsBit || (definition.ElementStrideBytes & 1) != 0)
                            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                                "MC 字数组必须位于字设备且元素按完整字对齐"));
                        var byteStart = checked(source * definition.ElementStrideBytes);
                        var raw = encoded.AsSpan(byteStart,
                            checked(run * definition.ElementStrideBytes)).ToArray();
                        var wordOffset = checked(targetOffsets[source] * definition.ElementStrideBytes / 2);
                        WriteDevice(baseLoc with { Number = baseLoc.Number + wordOffset }, raw.Length / 2,
                            raw, bit: false);
                    }
                    succeeded += run;
                }
                catch (Exception ex) when (ex is not IOException and not SocketException)
                {
                    for (var i = 0; i < run; i++)
                        failures.Add(new ArrayElementFailure(
                            definition.Shape.GetIndices(targetOffsets[source + i]), StatusCode.Bad, ex.Message));
                }
                source += run;
            }
            var status = failures.Count == 0 ? StatusCode.Good : succeeded > 0 ? StatusCode.Uncertain : StatusCode.Bad;
            return Task.FromResult(new ArrayWriteResult(status, succeeded, failures));
        }
        catch (Exception ex) when (ex is not IOException and not SocketException)
        {
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, ex.Message));
        }
    }

    private ArrayRuntimeValue ReadArray(McLoc baseLoc, ArrayDef definition, CancellationToken ct)
    {
        var count = checked((int)definition.Shape.ElementCount);
        var quality = Enumerable.Repeat(StatusCode.Bad, count).ToArray();
        if (definition.ElementStrideBits > 0)
        {
            if (!baseLoc.IsBit || definition.ElementType != IgwDataType.Bool)
                throw new NotSupportedException("MC 位数组仅支持位设备上的 Bool 元素");
            var totalBits = checked(count * definition.ElementStrideBits);
            var image = new byte[(totalBits + 7) / 8];
            for (var bit = 0; bit < totalBits; bit += 960)
            {
                ct.ThrowIfCancellationRequested();
                YieldToPriorityWriteIfNeeded();
                var length = Math.Min(960, totalBits - bit);
                var raw = ReadDevice(baseLoc with { Number = baseLoc.Number + bit }, length, bit: true);
                UnpackMcBits(raw, image, bit, length);
                MarkBitElements(quality, definition, bit, length);
            }
            return RegisterArrayCodec.Decode(image, definition, WordOrder.DCBA, quality);
        }

        if (baseLoc.IsBit) throw new NotSupportedException("MC 结构体/数值数组必须位于字设备");
        var totalBytes = checked(count * definition.ElementStrideBytes);
        var imageBytes = new byte[totalBytes];
        for (var offset = 0; offset < totalBytes;)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            var bytes = Math.Min(960, totalBytes - offset);
            var words = (bytes + 1) / 2;
            var raw = ReadDevice(baseLoc with { Number = baseLoc.Number + offset / 2 }, words, bit: false);
            var copied = Math.Min(bytes, raw.Length);
            raw.AsSpan(0, copied).CopyTo(imageBytes.AsSpan(offset));
            MarkByteElements(quality, definition, offset, copied);
            offset += bytes;
        }
        return RegisterArrayCodec.Decode(imageBytes, definition, WordOrder.DCBA, quality);
    }

    private static void MarkByteElements(StatusCode[] quality, ArrayDef definition, int start, int length)
    {
        var end = start + length;
        for (var i = 0; i < quality.Length; i++)
        {
            var element = i * definition.ElementStrideBytes;
            if (element >= start && element + definition.ElementStrideBytes <= end)
                quality[i] = StatusCode.Good;
        }
    }

    private static void MarkBitElements(StatusCode[] quality, ArrayDef definition, int start, int length)
    {
        var end = start + length;
        for (var i = 0; i < quality.Length; i++)
        {
            var element = i * definition.ElementStrideBits;
            if (element >= start && element + definition.ElementStrideBits <= end)
                quality[i] = StatusCode.Good;
        }
    }

    private static void UnpackMcBits(byte[] source, byte[] target, int targetBit, int count)
    {
        for (var i = 0; i < count && (i >> 1) < source.Length; i++)
        {
            var on = (i & 1) == 0 ? (source[i >> 1] & 0x10) != 0 : (source[i >> 1] & 0x01) != 0;
            if (on) target[(targetBit + i) >> 3] |= (byte)(1 << ((targetBit + i) & 7));
        }
    }

    private static byte[] PackMcBits(byte[] source, int sourceBit, int count)
    {
        var result = new byte[(count + 1) / 2];
        for (var i = 0; i < count; i++)
            if (((source[(sourceBit + i) >> 3] >> ((sourceBit + i) & 7)) & 1) != 0)
                result[i >> 1] |= (byte)((i & 1) == 0 ? 0x10 : 0x01);
        return result;
    }

    // ---------- 3E 帧 ----------
    private byte[] ReadDevice(McLoc loc, int count, bool bit, int tagCount = 1, int gapBytes = 0)
    {
        var body = BuildBody(0x0401, bit ? (ushort)1 : (ushort)0, loc, count, null);
        var resp = ProtocolOperationTiming.Measure(() => Transceive(body),
            new("Read", $"MC area=0x{loc.Code:X2} offset={loc.Number} count={count}",
                tagCount, bit ? (count + 1) / 2 : count * 2, gapBytes));
        return resp; // 字读=count*2 字节(小端);位读=ceil(count/2)字节
    }

    private void WriteDevice(McLoc loc, int count, byte[] data, bool bit)
    {
        var body = BuildBody(0x1401, bit ? (ushort)1 : (ushort)0, loc, count, data);
        ProtocolOperationTiming.Measure(() => Transceive(body),
            new("Write", $"MC area=0x{loc.Code:X2} offset={loc.Number} count={count}", -1, data.Length, 0));
    }

    /// <summary>组 3E 请求(从监视定时器开始的部分),返回响应数据段(end code 之后)。</summary>
    private byte[] BuildBody(ushort command, ushort subcommand, McLoc loc, int count, byte[]? writeData)
    {
        int payloadLen = 2 /*timer*/ + 2 /*cmd*/ + 2 /*subcmd*/ + 3 /*devnum*/ + 1 /*devcode*/ + 2 /*count*/ + (writeData?.Length ?? 0);
        var f = new byte[11 + payloadLen]; // 11 字节头
        int i = 0;
        f[i++] = 0x50; f[i++] = 0x00;          // 子头
        f[i++] = 0x00;                          // 网络号
        f[i++] = 0xFF;                          // PLC 号
        f[i++] = 0xFF; f[i++] = 0x03;           // 请求目标模块 IO (03FF)
        f[i++] = 0x00;                          // 请求目标模块站号
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(i), (ushort)payloadLen); i += 2; // 请求数据长度
        f[i++] = 0x00; f[i++] = 0x00;           // 监视定时器(无限等待)
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(i), command); i += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(i), subcommand); i += 2;
        f[i++] = (byte)(loc.Number & 0xFF);     // 设备号(3 字节小端)
        f[i++] = (byte)((loc.Number >> 8) & 0xFF);
        f[i++] = (byte)((loc.Number >> 16) & 0xFF);
        f[i++] = loc.Code;                      // 设备代码
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(i), (ushort)count); i += 2;
        if (writeData != null) { Array.Copy(writeData, 0, f, i, writeData.Length); i += writeData.Length; }
        return f[..i];
    }

    private byte[] Transceive(byte[] frame)
    {
        _stream!.Write(frame, 0, frame.Length);
        var head = ReadExact(9);                 // 子头(2)+网络/PLC/IO/站(5)+长度(2)
        int len = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(7));
        var rest = ReadExact(len);               // end code(2) + 数据
        int end = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(0));
        if (end != 0) throw new InvalidOperationException($"MC end code 0x{end:X4}");
        return rest[2..];
    }

    private byte[] ReadExact(int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = _stream!.Read(buf, got, n - got);  // 超时 → IOException/SocketException
            if (r <= 0) throw new IOException("MC 连接已关闭");
            got += r;
        }
        return buf;
    }

    // ---------- 编解码(小端)----------
    private static object Decode(byte[] b, IgwDataType t) => t switch
    {
        IgwDataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b),
        IgwDataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(b),
        IgwDataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b),
        IgwDataType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(b),
        IgwDataType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b),
        IgwDataType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(b),
        IgwDataType.Float => BinaryPrimitives.ReadSingleLittleEndian(b),
        IgwDataType.Double => BinaryPrimitives.ReadDoubleLittleEndian(b),
        IgwDataType.Byte => (byte)(BinaryPrimitives.ReadUInt16LittleEndian(b) & 0xFF),
        IgwDataType.Char => (sbyte)(BinaryPrimitives.ReadUInt16LittleEndian(b) & 0xFF),
        _ => BinaryPrimitives.ReadInt16LittleEndian(b),
    };

    private static byte[] Encode(object v, IgwDataType t)
    {
        var b = new byte[McAddress.Words(t) * 2];
        switch (t)
        {
            case IgwDataType.Int16: BinaryPrimitives.WriteInt16LittleEndian(b, Convert.ToInt16(v)); break;
            case IgwDataType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToUInt16(v)); break;
            case IgwDataType.Int32: BinaryPrimitives.WriteInt32LittleEndian(b, Convert.ToInt32(v)); break;
            case IgwDataType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(b, Convert.ToUInt32(v)); break;
            case IgwDataType.Int64: BinaryPrimitives.WriteInt64LittleEndian(b, Convert.ToInt64(v)); break;
            case IgwDataType.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(b, Convert.ToUInt64(v)); break;
            case IgwDataType.Float: BinaryPrimitives.WriteSingleLittleEndian(b, Convert.ToSingle(v)); break;
            case IgwDataType.Double: BinaryPrimitives.WriteDoubleLittleEndian(b, Convert.ToDouble(v)); break;
            case IgwDataType.Byte: BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToByte(v)); break;
            case IgwDataType.Char: BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(byte)Convert.ToSByte(v)); break;
            default: BinaryPrimitives.WriteInt16LittleEndian(b, Convert.ToInt16(v)); break;
        }
        return b;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
