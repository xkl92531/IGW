using System.Buffers.Binary;
using IGW.Abstractions;
using IoTClient.Clients.Modbus;
using IoTClient.Enums;

namespace IGW.Drivers.IoTClient;

/// <summary>Modbus TCP 设备驱动(IoTClient 实现)。每设备一条连接;相邻地址块合并整读+切片解码,读不进块的回退逐点。</summary>
public sealed class IoTModbusDeviceDriver : IDeviceDriver, IArrayCapableDriver, IDriverCapabilityProvider,
    IDriverPriorityWorkAware
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly EndianFormat _endian;
    private readonly WordOrder _order;   // 块读切片解码用(与 _endian 同源自 wordOrder 设置)
    private readonly byte _station;
    private readonly int _registerReadGap;
    private readonly int _bitReadGap;
    private readonly int _registerBlock;
    private readonly int _coilBlock;
    private readonly string _writeMode;
    private ushort _transaction;
    private ModbusTcpClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private Func<bool> _priorityWorkPending = static () => false;

    public string DriverId => IoTModbusDriverFactory.Id;
    public IAddressParser AddressParser { get; }
    public DriverLimits Limits { get; } = new() { MaxReadUnits = 120, MaxGap = 8, MaxTagsPerRead = 512, MaxTagsPerWrite = 120 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = true,
        PartitionsReadRequests = true,
        SupportsBatchWrite = true,
        MaxWriteBytes = 240,
    };
    public ConnectionState State => _state;
    public event Action<ConnectionState>? StateChanged;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
        SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
        MaxElementsPerRequest: int.MaxValue, MaxBytesPerRequest: 240);

    public IoTModbusDeviceDriver(string host, int port, byte station, EndianFormat endian, string wordOrder, int timeoutMs,
        bool zeroBased, DriverBatchingSettings? batchingSettings = null,
        int regBlock = 120, int coilBlock = 512, string writeMode = "auto")
    {
        _host = host; _port = port; _station = station; _endian = endian; _timeoutMs = timeoutMs;
        _order = ModbusBlockCodec.ParseOrder(wordOrder);
        if (regBlock is < 1 or > 125 || coilBlock is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(regBlock));
        _registerBlock = regBlock;
        _coilBlock = coilBlock;
        _writeMode = writeMode;
        var batching = batchingSettings ?? DriverBatchingSettings.Parse(new Dictionary<string, string>());
        _registerReadGap = EffectiveGap(8, batching);
        _bitReadGap = EffectiveGap(32, batching);
        AddressParser = new IoTModbusAddressParser(zeroBased);
    }

    // ---- 块读计划(按点表列表实例缓存;点表在组生命周期内不变)----
    private sealed class Plan
    {
        public required List<ReadBlock> RegBlocks;   // 寄存器区(3/4):相邻合并整块读
        public required List<ReadBlock> BitBlocks;   // 线圈/离散(0/1):相邻合并整块读
        public required List<TagBinding> Arrays;     // 逻辑数组独立按协议上限分包
        public required List<TagBinding> Singles;    // 进不了块的(理论为空,兜底)
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Plan> _plans = new();

    private Plan BuildPlan(IReadOnlyList<TagBinding> tags)
    {
        var regs = new List<TagBinding>(); var bits = new List<TagBinding>();
        var arrays = new List<TagBinding>(); var singles = new List<TagBinding>();
        foreach (var t in tags)
        {
            if (t.Def.Array is not null) { arrays.Add(t); continue; }
            if (t.Address.Area is 3 or 4) regs.Add(t);
            else if (t.Address.Area is 0 or 1) bits.Add(t);
            else singles.Add(t);
        }
        return new Plan
        {
            RegBlocks = AddressBlockOptimizer.Plan(regs, _registerBlock, _registerReadGap),
            BitBlocks = AddressBlockOptimizer.Plan(bits, _coilBlock, _bitReadGap),
            Arrays = arrays,
            Singles = singles,
        };
    }

    private static int EffectiveGap(int protocolGap, DriverBatchingSettings settings) =>
        settings.Mode == DriverBatchingMode.Manual ? Math.Min(protocolGap, settings.MaxGap) : protocolGap;

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
            await Task.Run(() =>
            {
                _client = new TimedModbusClient(_host, _port, _timeoutMs, _endian);
                var r = _client.Open();
                if (!r.IsSucceed) throw new IOException("Modbus 连接失败:" + r.Err);
            }, ct);
            SetState(ConnectionState.Connected);
        }
        catch { SetState(ConnectionState.Faulted); throw; }
    }

    public Task DisconnectAsync()
    {
        try { _client?.Close(); } catch { }
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var samples = new List<TagSample>(tags.Count);
        var cli = _client ?? throw new IOException("未连接");
        var plan = _plans.GetValue(tags, key => BuildPlan((IReadOnlyList<TagBinding>)key));

        // 寄存器块:一次整块读原始字节 → 按各点偏移/类型/字序切片解码。请求数从 N 点 → 块数(连续地址下 50-100 倍吞吐)
        foreach (var blk in plan.RegBlocks)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            byte fc = blk.Area == 3 ? (byte)4 : (byte)3;   // 3xxxx=输入寄存器(FC4) / 4xxxx=保持寄存器(FC3)
            using var timing = DescribeBlock(blk, fc, bit: false);
            global::IoTClient.Result<byte[]>? r = null;
            try { r = ReadBounded(cli, blk.Start, fc, blk.Count, ct); }
            catch (DriverReadYieldException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { /* 块级异常 → 下面回退逐点 */ }
            if (r is { IsSucceed: true, Value: { } buf } && buf.Length >= blk.Count * 2)
            {
                Array.Reverse(buf);   // IoTClient 块读返回"整数组反转"格式(实测:寄存器序反转+字节交换,与字序配置无关);反转后即 Modbus 线上大端
                DecodeRegBlock(blk, buf, now, samples);
            }
            else
            {
                if (r != null && !r.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(r, cli.Connected))
                    throw new IOException("连接断开:" + r.Err);   // 触发 DeviceLoop 重连
                foreach (var t in blk.Tags) ReadSingle(cli, t, now, samples, ct);   // 块失败(跨到无效寄存器等)→ 本块回退逐点,隔离坏点
            }
        }

        // 线圈/离散块:一次整块读打包位
        foreach (var blk in plan.BitBlocks)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            byte fc = blk.Area == 0 ? (byte)1 : (byte)2;
            using var timing = DescribeBlock(blk, fc, bit: true);
            global::IoTClient.Result<byte[]>? r = null;
            try { r = ReadBounded(cli, blk.Start, fc, blk.Count, ct); }
            catch (DriverReadYieldException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (r is { IsSucceed: true, Value: { } buf } && buf.Length >= (blk.Count + 7) / 8)
            {
                Array.Reverse(buf);   // 同寄存器块:返回是整数组反转;反转后即标准打包位(LSB-first)
                DecodeBitBlock(blk, buf, now, samples);
            }
            else
            {
                if (r != null && !r.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(r, cli.Connected))
                    throw new IOException("连接断开:" + r.Err);
                foreach (var t in blk.Tags) ReadSingle(cli, t, now, samples, ct);
            }
        }

        foreach (var t in plan.Singles) ReadSingle(cli, t, now, samples, ct);
        foreach (var t in plan.Arrays) ReadArray(cli, t, now, samples, ct);
        return Task.FromResult<IReadOnlyList<TagSample>>(samples);
    }

    private void ReadArray(ModbusTcpClient cli, TagBinding binding, DateTime now,
        List<TagSample> samples, CancellationToken ct)
    {
        var definition = binding.Def.Array!;
        var count = checked((int)definition.Shape.ElementCount);
        var quality = Enumerable.Repeat(StatusCode.Bad, count).ToArray();
        try
        {
            if (binding.Address.Area is 0 or 1)
            {
                if (definition.ElementType != IgwDataType.Bool || definition.ElementStrideBits <= 0)
                    throw new NotSupportedException("Modbus 线圈/离散输入数组仅支持 Bool 元素");
                var totalBits = checked(count * definition.ElementStrideBits);
                var image = new byte[(totalBits + 7) / 8];
                ValidateRange(binding.Address.Offset, totalBits);
                for (var bit = 0; bit < totalBits; bit += _coilBlock)
                {
                    ct.ThrowIfCancellationRequested();
                    YieldToPriorityWriteIfNeeded();
                    var length = Math.Min(_coilBlock, totalBits - bit);
                    var fc = binding.Address.Area == 0 ? (byte)1 : (byte)2;
                    using var timing = DescribeRead(fc, binding.Address.Offset + bit, 1, (length + 7) / 8);
                    var result = cli.Read((binding.Address.Offset + bit).ToString(), _station, fc,
                        checked((ushort)length), false);
                    if (result.IsSucceed && result.Value is { } bytes)
                    {
                        Array.Reverse(bytes);
                        CopyBits(bytes, image, bit, length);
                        MarkBitElements(quality, definition, bit, length, StatusCode.Good);
                    }
                    else if (IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                        throw new IOException("连接断开:" + result.Err);
                }
                var value = RegisterArrayCodec.Decode(image, definition, _order, quality);
                samples.Add(new TagSample(binding.TagId, new DataValue(value, value.OverallQuality, now, now)));
                return;
            }

            if (binding.Address.Area is not (3 or 4))
                throw new NotSupportedException("Modbus 数组地址必须位于 0/1/3/4 区");
            if (definition.ElementType == IgwDataType.Bool)
            {
                var value = ReadRegisterBits(cli, binding, ct);
                samples.Add(new TagSample(binding.TagId, new DataValue(value, value.OverallQuality, now, now)));
                return;
            }
            var registers = RegisterArrayCodec.RegisterCount(definition, count);
            ValidateRange(binding.Address.Offset, registers);
            var registerImage = new byte[checked(registers * 2)];
            var received = new bool[registers * 2];
            for (var register = 0; register < registers; register += _registerBlock)
            {
                ct.ThrowIfCancellationRequested();
                YieldToPriorityWriteIfNeeded();
                var length = Math.Min(_registerBlock, registers - register);
                var fc = binding.Address.Area == 3 ? (byte)4 : (byte)3;
                using var timing = DescribeRead(fc, binding.Address.Offset + register, 1, length * 2);
                var result = cli.Read((binding.Address.Offset + register).ToString(), _station, fc,
                    checked((ushort)length), false);
                if (result.IsSucceed && result.Value is { } bytes && bytes.Length >= length * 2)
                {
                    Array.Reverse(bytes);
                    bytes.AsSpan(0, length * 2).CopyTo(registerImage.AsSpan(register * 2));
                    received.AsSpan(register * 2, length * 2).Fill(true);
                }
                else if (IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                    throw new IOException("连接断开:" + result.Err);
            }
            for (var i = 0; i < count; i++)
            {
                var start = definition.ElementStrideBits > 0 ? i * definition.ElementStrideBits / 8 : i * definition.ElementStrideBytes;
                var length = definition.ElementStrideBits > 0 ? Math.Max(1, (definition.ElementStrideBits + 7) / 8) : definition.ElementStrideBytes;
                if (received.AsSpan(start, length).IndexOf(false) < 0) quality[i] = StatusCode.Good;
            }
            var decoded = RegisterArrayCodec.Decode(registerImage, definition, _order, quality);
            samples.Add(new TagSample(binding.TagId,
                new DataValue(decoded, decoded.OverallQuality, now, now)));
        }
        catch (DriverReadYieldException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch
        {
            samples.Add(new TagSample(binding.TagId, DataValue.BadAt(StatusCode.BadDecodeError, now)));
        }
    }

    private void DecodeRegBlock(ReadBlock blk, byte[] buf, DateTime now, List<TagSample> samples)
    {
        foreach (var t in blk.Tags)
        {
            int byteOff = (t.Address.Offset - blk.Start) * 2;
            int byteLen = t.Def.DataType == IgwDataType.Bool ? 2 : t.Address.Length * 2;
            if (byteOff < 0 || byteOff + byteLen > buf.Length)   // 短帧防越界:该点 Bad
            { samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now))); continue; }
            if (t.Def.DataType == IgwDataType.Bool)   // 寄存器位:取 16 位再取位
            {
                ushort rv = (ushort)ModbusBlockCodec.Decode(buf.AsSpan(byteOff, 2), IgwDataType.UInt16, _order);
                samples.Add(new TagSample(t.TagId, DataValue.GoodAt(((rv >> Math.Max(0, t.Address.BitOffset)) & 1) != 0, now)));
            }
            else
            {
                samples.Add(new TagSample(t.TagId, DataValue.GoodAt(ModbusBlockCodec.Decode(buf.AsSpan(byteOff, byteLen), t.Def.DataType, _order), now)));
            }
        }
    }

    private static void DecodeBitBlock(ReadBlock blk, byte[] buf, DateTime now, List<TagSample> samples)
    {
        foreach (var t in blk.Tags)
        {
            int bitIdx = t.Address.Offset - blk.Start;
            if (bitIdx < 0 || (bitIdx >> 3) >= buf.Length)
            { samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now))); continue; }
            samples.Add(new TagSample(t.TagId, DataValue.GoodAt(((buf[bitIdx >> 3] >> (bitIdx & 7)) & 1) != 0, now)));
        }
    }

    // 单点读(块失败回退/不进块的点):沿用库的按类型读
    private void ReadSingle(ModbusTcpClient cli, TagBinding t, DateTime now, List<TagSample> samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        YieldToPriorityWriteIfNeeded();
        var a = t.Address;
        byte fc = a.Area switch { 0 => 1, 1 => 2, 3 => 4, _ => 3 };  // 线圈/离散/输入寄存器/保持寄存器
        string addr = a.Offset.ToString();
        using var timing = DescribeRead(fc, a.Offset, 1, a.Area is 0 or 1 ? 1 : a.Length * 2);
        try
        {
            var result = ReadBounded(cli, a.Offset, fc, a.Length, ct);
            bool ok = result.IsSucceed;
            object? val = null;
            if (ok)
            {
                Array.Reverse(result.Value);
                val = a.Area is 0 or 1 ? (result.Value[0] & 1) != 0
                    : t.Def.DataType == IgwDataType.Bool
                        ? (((ushort)ModbusBlockCodec.Decode(result.Value, IgwDataType.UInt16, _order) >> a.BitOffset) & 1) != 0
                        : ModbusBlockCodec.Decode(result.Value, t.Def.DataType, _order);
            }
            if (ok) { samples.Add(new TagSample(t.TagId, DataValue.GoodAt(val!, now))); return; }
            if (IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                throw new IOException("连接断开:" + result.Err);   // 触发 DeviceLoop 重连
            samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now)));  // 单点错误(非法地址等)
        }
        catch (IOException) { throw; }
        catch (DriverReadYieldException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now))); }
    }

    private IoTProtocolTiming DescribeRead(byte fc, int start, int tags, int bytes, int gap = 0) =>
        new(new("Read", $"Modbus unit={_station} FC{fc} offset={start}", tags, bytes, gap));

    private IoTProtocolTiming DescribeBlock(ReadBlock block, byte fc, bool bit)
    {
        int gaps = 0, end = block.Start;
        foreach (var tag in block.Tags.OrderBy(tag => tag.Address.Offset))
        {
            gaps += Math.Max(0, tag.Address.Offset - end);
            end = Math.Max(end, tag.Address.End);
        }
        return DescribeRead(fc, block.Start, block.Tags.Count,
            bit ? (block.Count + 7) / 8 : block.Count * 2,
            bit ? (gaps + 7) / 8 : gaps * 2);
    }

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var cli = _client;
        if (cli is null || !cli.Connected) throw new IOException("Modbus 未连接");
        var statuses = new Dictionary<string, StatusCode>(writes.Count, StringComparer.Ordinal);
        var coilWrites = new List<WriteRequest>();
        var registerWrites = new List<WriteRequest>();
        foreach (var write in writes)
        {
            if (write.Address.Area == 0 && write.Def.DataType == IgwDataType.Bool)
                coilWrites.Add(write);
            else if (write.Address.Area == 4 &&
                     !(write.Def.DataType == IgwDataType.Bool && write.Address.BitOffset >= 0))
                registerWrites.Add(write);
            else
                statuses[write.TagId] = write.Address.Area is 1 or 3
                    ? StatusCode.BadNotWritable
                    : StatusCode.BadDeviceFailure;
        }

        foreach (var group in ContiguousGroups(coilWrites, 1968))
        {
            ct.ThrowIfCancellationRequested();
            var packed = new byte[(group.Count + 7) / 8];
            for (int i = 0; i < group.Count; i++)
                if (Convert.ToBoolean(group[i].Value)) packed[i >> 3] |= (byte)(1 << (i & 7));
            ApplyBatchResult(group,
                WriteWire(cli, group[0].Address.Offset, group.Count, packed, true, ct));
        }

        foreach (var group in ContiguousGroups(registerWrites, 120))
        {
            ct.ThrowIfCancellationRequested();
            var payload = new List<byte>(group.Sum(item => item.Address.Length * 2));
            foreach (var write in group)
                payload.AddRange(ModbusBlockCodec.Encode(write.Value, write.Def.DataType, _order));
            ApplyBatchResult(group,
                WriteWire(cli, group[0].Address.Offset, payload.Count / 2, payload.ToArray(), false, ct));
        }

        // 寄存器位必须读改写，无法与其它值安全合并；仍在同一设备串行线程执行。
        foreach (var write in writes.Where(item =>
                     item.Address.Area == 4 && item.Def.DataType == IgwDataType.Bool && item.Address.BitOffset >= 0))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string address = write.Address.Offset.ToString();
                var current = cli.Read(address, _station, 3, 1, false);
                if (!current.IsSucceed)
                {
                    if (IoTPlcDeviceDriver.IsTransportFailure(current, cli.Connected))
                        throw new IOException("Modbus 连接断开:" + current.Err);
                    statuses[write.TagId] = StatusCode.BadDeviceFailure;
                    continue;
                }
                if (current.Value is null || current.Value.Length != 2)
                    throw new FormatException("Modbus 寄存器位读取长度错误");
                Array.Reverse(current.Value);
                var currentValue = (ushort)ModbusBlockCodec.Decode(current.Value, IgwDataType.UInt16, _order);
                ushort next = Convert.ToBoolean(write.Value)
                    ? (ushort)(currentValue | (1 << write.Address.BitOffset))
                    : (ushort)(currentValue & ~(1 << write.Address.BitOffset));
                var result = WriteWire(cli, write.Address.Offset, 1,
                    ModbusBlockCodec.Encode(next, IgwDataType.UInt16, _order), false, ct);
                if (!result.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                    throw new IOException("Modbus 连接断开:" + result.Err);
                statuses[write.TagId] = result.IsSucceed ? StatusCode.Good : StatusCode.BadDeviceFailure;
            }
            catch (IOException) { throw; }
            catch (Exception) { statuses[write.TagId] = StatusCode.BadDecodeError; }
        }

        return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
        {
            TagId = write.TagId,
            Status = statuses.GetValueOrDefault(write.TagId, StatusCode.BadDeviceFailure),
        }).ToArray());

        void ApplyBatchResult(IReadOnlyList<WriteRequest> group, global::IoTClient.Result result)
        {
            if (!result.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                throw new IOException("Modbus 连接断开:" + result.Err);
            StatusCode status = result.IsSucceed ? StatusCode.Good : StatusCode.BadDeviceFailure;
            foreach (var write in group) statuses[write.TagId] = status;
        }
    }

    private static void ValidateRange(int offset, int count)
    {
        if (offset < 0 || count < 1 || (long)offset + count > 65536)
            throw new FormatException("Modbus 请求跨度超出 0-65535 地址范围");
    }

    // Preserve IoTClient's reversed buffer contract while enforcing the configured physical limit,
    // including when a single multi-register scalar is larger than the configured block.
    private global::IoTClient.Result<byte[]> ReadBounded(ModbusTcpClient cli, int start, byte fc, int count, CancellationToken ct)
    {
        ValidateRange(start, count);
        bool bits = fc is 1 or 2;
        int limit = bits ? _coilBlock : _registerBlock;
        if (count <= limit)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            var direct = cli.Read(start.ToString(), _station, fc, checked((ushort)count), false);
            if (direct.IsSucceed && (direct.Value is null || direct.Value.Length < (bits ? (count + 7) / 8 : count * 2)))
                return new() { IsSucceed = false, Err = "Modbus 读取响应长度错误" };
            return direct;
        }
        var image = new byte[bits ? (count + 7) / 8 : count * 2];
        for (int offset = 0; offset < count; offset += limit)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            int length = Math.Min(limit, count - offset);
            var result = cli.Read((start + offset).ToString(), _station, fc, checked((ushort)length), false);
            if (!result.IsSucceed) return result;
            if (result.Value is null || result.Value.Length < (bits ? (length + 7) / 8 : length * 2))
                return new() { IsSucceed = false, Err = "Modbus 读取响应长度错误" };
            Array.Reverse(result.Value);
            if (bits) CopyBits(result.Value, image, offset, length);
            else result.Value.AsSpan(0, length * 2).CopyTo(image.AsSpan(offset * 2));
        }
        Array.Reverse(image);
        return new() { IsSucceed = true, Value = image };
    }

    // IoTClient's byte[] Write builds a register quantity even for FC15; construct the PDU
    // explicitly so coil quantities are bits, and FC05/06 carry the value without a byte count.
    private global::IoTClient.Result WriteWire(ModbusTcpClient cli, int start, int count, byte[] payload, bool coils, CancellationToken ct)
    {
        ValidateRange(start, count);
        if (payload.Length != (coils ? (count + 7) / 8 : count * 2))
            throw new FormatException("Modbus 写入数据长度错误");
        bool single = _writeMode == "single" || (_writeMode == "auto" && count == 1);
        int limit = single ? 1 : coils ? 1968 : 123;
        global::IoTClient.Result result = new() { IsSucceed = true };
        for (int offset = 0; offset < count; offset += limit)
        {
            ct.ThrowIfCancellationRequested();
            int quantity = Math.Min(limit, count - offset);
            byte fc = coils ? single ? (byte)5 : (byte)15 : single ? (byte)6 : (byte)16;
            byte[] bytes = coils ? PackBits(payload, offset, quantity) : payload.AsSpan(offset * 2, quantity * 2).ToArray();
            var frame = new byte[single ? 12 : 13 + bytes.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame, unchecked(++_transaction));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), checked((ushort)(frame.Length - 6)));
            frame[6] = _station;
            frame[7] = fc;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), checked((ushort)(start + offset)));
            if (single)
            {
                if (coils) frame[10] = (bytes[0] & 1) != 0 ? (byte)255 : (byte)0;
                else bytes.CopyTo(frame, 10);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), checked((ushort)quantity));
                frame[12] = checked((byte)bytes.Length);
                bytes.CopyTo(frame, 13);
            }
            var exchange = cli.SendPackageSingle(frame);
            result = exchange;
            if (!exchange.IsSucceed) return result;
            var reply = exchange.Value;
            if (reply is null || reply.Length < 8 || !reply.AsSpan(0, 4).SequenceEqual(frame.AsSpan(0, 4)) ||
                reply[6] != _station || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4)) != reply.Length - 6)
                return new global::IoTClient.Result { IsSucceed = false, Err = "Modbus 写入响应 MBAP 不匹配" };
            if (reply[7] == (fc | 0x80) && reply.Length == 9)
                return new global::IoTClient.Result { IsSucceed = false, Err = $"Modbus exception {reply[8]}" };
            if (reply.Length != 12 || !reply.AsSpan(7, 5).SequenceEqual(frame.AsSpan(7, 5)))
                return new global::IoTClient.Result { IsSucceed = false, Err = "Modbus 写入响应功能码/地址/数量不匹配" };
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<WriteRequest>> ContiguousGroups(
        IReadOnlyList<WriteRequest> source,
        int maxUnits)
    {
        var result = new List<IReadOnlyList<WriteRequest>>();
        foreach (var area in source.GroupBy(item => item.Address.Area))
        {
            var ordered = area.OrderBy(item => item.Address.Offset).ToArray();
            for (int i = 0; i < ordered.Length;)
            {
                var group = new List<WriteRequest> { ordered[i] };
                int start = ordered[i].Address.Offset;
                int end = ordered[i].Address.End;
                int j = i + 1;
                while (j < ordered.Length && ordered[j].Address.Offset == end &&
                       ordered[j].Address.End - start <= maxUnits)
                {
                    group.Add(ordered[j]);
                    end = ordered[j].Address.End;
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
        var cli = _client;
        if (cli is null || !cli.Connected)
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadNotConnected, "Modbus 未连接"));
        var definition = request.Binding.Def.Array;
        if (definition is null)
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.Bad, "点位不是数组"));
        if (request.Binding.Address.Area is not (0 or 4))
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadNotWritable,
                "Modbus 离散输入和输入寄存器为只读区"));

        try
        {
            if (request.Binding.Address.Area == 4 && definition.ElementType == IgwDataType.Bool)
                return Task.FromResult(WriteRegisterBits(cli, request, ct));
            ValidateRange(request.Binding.Address.Offset, request.Binding.Address.Area == 0
                ? checked((int)definition.Shape.ElementCount * definition.ElementStrideBits)
                : RegisterArrayCodec.RegisterCount(definition, checked((int)definition.Shape.ElementCount)));
            var targetShape = request.Slice.ValidateAndCreateShape(definition.Shape);
            if (request.Value.Count != targetShape.ElementCount || request.Value.ElementType != definition.ElementType)
                throw new FormatException("Modbus 数组写入值数量或类型不匹配");
            var targetOffsets = new int[request.Value.Count];
            for (var i = 0; i < targetOffsets.Length; i++)
                targetOffsets[i] = checked((int)definition.Shape.GetLinearOffset(targetShape.GetIndices(i)));
            var encoded = RegisterArrayCodec.Encode(request.Value, definition, _order);
            var failures = new List<ArrayElementFailure>();
            var succeeded = 0;

            for (var source = 0; source < targetOffsets.Length;)
            {
                ct.ThrowIfCancellationRequested();
                var run = 1;
                while (source + run < targetOffsets.Length &&
                       targetOffsets[source + run] == targetOffsets[source] + run) run++;
                run = request.Binding.Address.Area == 0
                    ? Math.Min(run, 1968)
                    : Math.Min(run, Math.Max(1, 240 / Math.Max(1, definition.ElementStrideBytes)));

                global::IoTClient.Result result;
                if (request.Binding.Address.Area == 0)
                {
                    if (definition.ElementType != IgwDataType.Bool || definition.ElementStrideBits != 1)
                        return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                            "Modbus 线圈数组仅支持连续 Bool 元素"));
                    var packed = PackBits(encoded, source, run);
                    result = WriteWire(cli, request.Binding.Address.Offset + targetOffsets[source], run, packed, true, ct);
                }
                else
                {
                    if (definition.ElementStrideBits > 0 || (definition.ElementStrideBytes & 1) != 0)
                        return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                            "Modbus 保持寄存器数组元素必须按完整寄存器对齐"));
                    var byteStart = checked(source * definition.ElementStrideBytes);
                    var bytes = encoded.AsSpan(byteStart, checked(run * definition.ElementStrideBytes)).ToArray();
                    var register = checked(request.Binding.Address.Offset +
                        targetOffsets[source] * definition.ElementStrideBytes / 2);
                    result = WriteWire(cli, register, bytes.Length / 2, bytes, false, ct);
                }

                if (result.IsSucceed) succeeded += run;
                else
                {
                    if (IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                        throw new IOException("连接断开:" + result.Err);
                    for (var i = 0; i < run; i++)
                        failures.Add(new ArrayElementFailure(
                            definition.Shape.GetIndices(targetOffsets[source + i]), StatusCode.Bad, result.Err ?? "写入失败"));
                }
                source += run;
            }

            var status = failures.Count == 0 ? StatusCode.Good : succeeded > 0 ? StatusCode.Uncertain : StatusCode.Bad;
            return Task.FromResult(new ArrayWriteResult(status, succeeded, failures));
        }
        catch (IOException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, ex.Message));
        }
    }

    private static (int Stride, int Registers) RegisterBitLayout(TagBinding binding)
    {
        var definition = binding.Def.Array!;
        int stride = definition.ElementStrideBits > 0 ? definition.ElementStrideBits : checked(definition.ElementStrideBytes * 8);
        if (stride < 1 || binding.Address.BitOffset is < 0 or > 15)
            throw new FormatException("Modbus 寄存器 Bool 数组需要合法起始位及元素步长");
        long bits = checked(binding.Address.BitOffset + (definition.Shape.ElementCount - 1) * stride + 1);
        int registers = checked((int)((bits + 15) / 16));
        ValidateRange(binding.Address.Offset, registers);
        return (stride, registers);
    }

    private PrimitiveArrayValue<bool> ReadRegisterBits(ModbusTcpClient cli, TagBinding binding, CancellationToken ct)
    {
        var (stride, registers) = RegisterBitLayout(binding);
        var definition = binding.Def.Array!;
        var image = new ushort[registers];
        var received = new bool[registers];
        byte fc = binding.Address.Area == 3 ? (byte)4 : (byte)3;
        for (int start = 0; start < registers; start += _registerBlock)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            int length = Math.Min(_registerBlock, registers - start);
            using var timing = DescribeRead(fc, binding.Address.Offset + start, 1, length * 2);
            var result = ReadBounded(cli, binding.Address.Offset + start, fc, length, ct);
            if (!result.IsSucceed)
            {
                if (IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected)) throw new IOException("连接断开:" + result.Err);
                continue;
            }
            Array.Reverse(result.Value);
            for (int i = 0; i < length; i++)
            {
                image[start + i] = (ushort)ModbusBlockCodec.Decode(result.Value.AsSpan(i * 2, 2), IgwDataType.UInt16, _order);
                received[start + i] = true;
            }
        }
        int count = checked((int)definition.Shape.ElementCount);
        var values = new bool[count];
        var quality = new StatusCode[count];
        for (int i = 0; i < count; i++)
        {
            int bit = checked(binding.Address.BitOffset + i * stride);
            values[i] = (image[bit / 16] & (1 << (bit % 16))) != 0;
            quality[i] = received[bit / 16] ? StatusCode.Good : StatusCode.Bad;
        }
        return PrimitiveArrayValue<bool>.Create(definition.Shape, values, quality);
    }

    private ArrayWriteResult WriteRegisterBits(ModbusTcpClient cli, ArrayWriteRequest request, CancellationToken ct)
    {
        var (stride, _) = RegisterBitLayout(request.Binding);
        var definition = request.Binding.Def.Array!;
        var shape = request.Slice.ValidateAndCreateShape(definition.Shape);
        if (request.Value.Count != shape.ElementCount || request.Value.ElementType != IgwDataType.Bool)
            throw new FormatException("Modbus Bool 数组写入值数量或类型不匹配");
        var changes = Enumerable.Range(0, request.Value.Count).Select(i =>
        {
            var indices = shape.GetIndices(i);
            int bit = checked(request.Binding.Address.BitOffset + (int)definition.Shape.GetLinearOffset(indices) * stride);
            return (Index: i, Indices: indices, Bit: bit);
        }).GroupBy(change => change.Bit / 16);
        int succeeded = 0;
        var failures = new List<ArrayElementFailure>();
        foreach (var group in changes)
        {
            ct.ThrowIfCancellationRequested();
            int register = request.Binding.Address.Offset + group.Key;
            // Writes execute on the serialized device loop; do not yield this RMW to another write.
            var read = cli.Read(register.ToString(), _station, 3, 1, false);
            global::IoTClient.Result result = read;
            if (read.IsSucceed && read.Value is { Length: 2 })
            {
                Array.Reverse(read.Value);
                var current = (ushort)ModbusBlockCodec.Decode(read.Value, IgwDataType.UInt16, _order);
                foreach (var change in group)
                {
                    int mask = 1 << (change.Bit % 16);
                    current = Convert.ToBoolean(request.Value.GetBoxedValue(change.Index))
                        ? (ushort)(current | mask) : (ushort)(current & ~mask);
                }
                result = WriteWire(cli, register, 1, ModbusBlockCodec.Encode(current, IgwDataType.UInt16, _order), false, ct);
            }
            else if (read.IsSucceed) result = new global::IoTClient.Result { IsSucceed = false, Err = "Modbus 位数组读取长度错误" };
            if (!result.IsSucceed && IoTPlcDeviceDriver.IsTransportFailure(result, cli.Connected))
                throw new IOException("连接断开:" + result.Err);
            if (result.IsSucceed) succeeded += group.Count();
            else foreach (var change in group)
                failures.Add(new ArrayElementFailure(change.Indices, StatusCode.BadDeviceFailure, result.Err ?? "写入失败"));
        }
        return new ArrayWriteResult(failures.Count == 0 ? StatusCode.Good : succeeded > 0 ? StatusCode.Uncertain : StatusCode.Bad,
            succeeded, failures);
    }

    private static void MarkBitElements(StatusCode[] quality, ArrayDef definition,
        int bitStart, int bitLength, StatusCode status)
    {
        var end = bitStart + bitLength;
        for (var i = 0; i < quality.Length; i++)
        {
            var start = i * definition.ElementStrideBits;
            if (start >= bitStart && start + definition.ElementStrideBits <= end) quality[i] = status;
        }
    }

    private static void CopyBits(byte[] source, byte[] target, int targetBit, int count)
    {
        for (var i = 0; i < count; i++)
            if ((source[i >> 3] & (1 << (i & 7))) != 0)
                target[(targetBit + i) >> 3] |= (byte)(1 << ((targetBit + i) & 7));
    }

    private static byte[] PackBits(byte[] source, int sourceBit, int count)
    {
        var result = new byte[(count + 7) / 8];
        for (var i = 0; i < count; i++)
            if ((source[(sourceBit + i) >> 3] & (1 << ((sourceBit + i) & 7))) != 0)
                result[i >> 3] |= (byte)(1 << (i & 7));
        return result;
    }

    public ValueTask DisposeAsync() { try { _client?.Close(); } catch { } return ValueTask.CompletedTask; }
}

/// <summary>Modbus TCP 驱动工厂(IoTClient)。驱动 ID 与原生 Modbus 一致,配置完全兼容。</summary>
public sealed class IoTModbusDriverFactory : IDriverFactory
{
    public const string Id = "Modbus.Tcp";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "Modbus TCP",
        AddressHelp = "零基偏移:C:0 线圈 / DI:0 离散输入 / IR:0 输入寄存器 / HR:0 保持寄存器。寄存器位 HR:0.3。兼容 00001/10001/30001/40001 及六位地址。",
        ChannelSettings = new[]
        {
            new DriverSetting { Key = "wordOrder", Label = "字序 (ABCD/CDAB/BADC/DCBA)", Default = "ABCD" },
            new DriverSetting { Key = "timeoutMs", Label = "超时 (ms)", Default = "2000" },
        },
        DeviceSettings = new[]
        {
            new DriverSetting { Key = "unitId", Label = "从站号 UnitId", Default = "1" },
            new DriverSetting { Key = "zeroBased", Label = "零基址寻址 (启用/禁用)", Default = "禁用" },
            new DriverSetting { Key = "wordOrder", Label = "字序", Default = "ABCD" },
            new DriverSetting { Key = "regBlock", Label = "寄存器读取块 (1-125)", Default = "120" },
            new DriverSetting { Key = "coilBlock", Label = "位读取块 (1-2000)", Default = "512" },
            new DriverSetting { Key = "writeMode", Label = "写入模式 (auto/single/multiple)", Default = "auto" },
            new DriverSetting { Key = "timeoutMs", Label = "设备超时 (ms，覆盖通道)", Default = "2000" },
        },
    };

    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
    {
        var settings = ModbusDeviceSettings.Parse(channel, device);
        return new IoTModbusDeviceDriver(settings.Host, settings.Port, settings.UnitId,
            ParseEndian(settings.WordOrder), settings.WordOrder, settings.TimeoutMs, settings.ZeroBased,
            DriverBatchingSettings.Parse(channel.Settings), settings.RegisterBlock, settings.CoilBlock, settings.WriteMode);
    }
    private static EndianFormat ParseEndian(string s) => s.ToUpperInvariant() switch
    {
        "CDAB" => EndianFormat.CDAB,
        "BADC" => EndianFormat.BADC,
        "DCBA" => EndianFormat.DCBA,
        _ => EndianFormat.ABCD,
    };

}
