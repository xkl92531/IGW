using System.Collections.Concurrent;
using IGW.Abstractions;
using libplctag;

namespace IGW.Drivers.EtherNetIP;

/// <summary>EtherNet/IP(CIP)地址 = PLC 符号标记名(如 MyTag、Program:Main.Cnt、Arr[3])。</summary>
public sealed class EtherNetIpAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = new TagAddress(0, 0, 1);
        error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = "空地址(应为 PLC 标记名,如 MyTag 或 Program:Main.Cnt)"; return false; }
        return true;
    }
}

/// <summary>EtherNet/IP 设备驱动(基于 libplctag):覆盖 Allen-Bradley(ControlLogix 等)与欧姆龙 NJ/NX,按符号标记名读/写。</summary>
public sealed class EtherNetIpDeviceDriver : IDeviceDriver, IArrayCapableDriver, IDriverCapabilityProvider, IDriverPriorityWorkAware
{
    private readonly string _gateway;
    private readonly string? _path;
    private readonly PlcType _plcType;
    private readonly TimeSpan _timeout;
    private readonly int _arrayChunkElements;
    private readonly Func<CancellationToken, Task<bool>> _transportProbe;
    private readonly Func<Tag, INativeTag> _createTag;
    private Func<bool> _priorityWorkPending = static () => false;
    private readonly ConcurrentDictionary<string, INativeTag> _tags = new();
    private readonly SemaphoreSlim _sem = new(4, 4);   // 并发读上限(避免一次发起太多压垮 libplctag)
    // libplctag 的同一个 Tag 实例不能同时 Read/Write。固定条带锁避免按百万点永久创建 Semaphore，
    // 同一 Tag（以及少量哈希碰撞）严格有序，不同 Tag 可并发。
    private readonly SemaphoreSlim[] _tagOperationLanes =
        Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private ConnectionState _state = ConnectionState.Disconnected;

    public string DriverId { get; }
    public IAddressParser AddressParser { get; } = new EtherNetIpAddressParser();
    public DriverLimits Limits { get; } = new DriverLimits { MaxReadUnits = 1, MaxGap = 0, MaxTagsPerRead = 256, MaxTagsPerWrite = 256 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = false,
        PartitionsReadRequests = true,
        SupportsBatchWrite = false,
        MaxInFlight = 4,
    };
    public ConnectionState State => _state;
    public ArrayDriverCapabilities ArrayCapabilities { get; }
    public event Action<ConnectionState>? StateChanged;

    public EtherNetIpDeviceDriver(string driverId, string gateway, string? path, PlcType plcType, int timeoutMs,
        int arrayChunkElements = 128)
        : this(driverId, gateway, path, plcType, timeoutMs, arrayChunkElements, null)
    {
    }

    internal EtherNetIpDeviceDriver(string driverId, string gateway, string? path, PlcType plcType, int timeoutMs,
        int arrayChunkElements, Func<CancellationToken, Task<bool>>? transportProbe, Func<Tag, INativeTag>? createTag = null)
    {
        DriverId = driverId; _gateway = gateway; _path = path; _plcType = plcType;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs <= 0 ? 5000 : timeoutMs);
        _arrayChunkElements = Math.Clamp(arrayChunkElements, 1, 4096);
        _transportProbe = transportProbe ?? ProbeTransportAsync;
        _createTag = createTag ?? (tag => new NativeTag(tag));
        ArrayCapabilities = new ArrayDriverCapabilities(
            NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
            SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
            MaxElementsPerRequest: _arrayChunkElements, MaxBytesPerRequest: 64 * 1024);
    }

    private void SetState(ConnectionState s) { if (_state == s) return; _state = s; StateChanged?.Invoke(s); }

    private volatile bool _lastAllFailed;   // 上一轮读是否全军覆没(据此在重连时做真实探测,退避才能生效)

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_lastAllFailed)
        {
            // 上轮全部点失败:先做 TCP 探测。不通就抛异常 → DeviceLoop 指数退避;
            // 否则"假 Connected"会让退避永远不生效,变成以扫描速率打 PLC 的重连风暴。
            SetState(ConnectionState.Connecting);
            string host = _gateway; int port = 44818;   // EtherNet/IP 默认端口
            int i = host.LastIndexOf(':');
            if (i > 0 && int.TryParse(host[(i + 1)..], out var p2)) { port = p2; host = host[..i]; }
            using var tcp = new System.Net.Sockets.TcpClient();
            try { await tcp.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (Exception ex) { SetState(ConnectionState.Faulted); throw new IOException($"EtherNet/IP {host}:{port} 不可达:{ex.Message}"); }
            _lastAllFailed = false;
        }
        SetState(ConnectionState.Connected); // libplctag 按标记惰性连接
    }

    public Task DisconnectAsync()
    {
        foreach (var t in _tags.Values) { try { t.Dispose(); } catch { } }
        _tags.Clear();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (tags.Count == 0) return Array.Empty<TagSample>();

        // 固定数量的异步工人，避免一次读取 N 个点就创建 N 个 Task。
        var samples = new TagSample[tags.Count];
        int next = -1;
        int workerCount = Math.Min(Capabilities.MaxInFlight, tags.Count);
        var workers = new Task[workerCount];
        for (int worker = 0; worker < workers.Length; worker++)
            workers[worker] = ReadWorkerAsync();
        await Task.WhenAll(workers);

        async Task ReadWorkerAsync()
        {
            while (true)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= tags.Count) return;
                samples[index] = await ReadOneAsync(tags[index], ct);
            }
        }

        if (tags.Count > 0 && samples.All(s => !s.Value.IsGood))
        {
            // 点位不存在、权限不足和类型不匹配也会造成整批 Bad，不能据此销毁整条会话。
            // 只有独立的 TCP 探测也失败，才把故障提升为传输层断线。
            if (await ConfirmTransportLostAfterAllBadAsync(tags.Count, ct))
                throw new DriverOperationException(
                    $"EtherNet/IP 全部 {tags.Count} 点读取失败且传输通道不可达",
                    DriverFailureScope.Transport,
                    DriverRetryDisposition.RebuildConnection,
                    StatusCode.BadCommFailure);
        }
        return samples;
    }

    internal async Task<bool> ConfirmTransportLostAfterAllBadAsync(int tagCount, CancellationToken ct)
    {
        bool reachable;
        try { reachable = await _transportProbe(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { reachable = false; }

        if (reachable)
        {
            _lastAllFailed = false;
            SetState(ConnectionState.Connected);
            return false;
        }

        _lastAllFailed = tagCount > 0;
        SetState(ConnectionState.Faulted);
        return true;
    }

    private async Task<bool> ProbeTransportAsync(CancellationToken ct)
    {
        string host = _gateway;
        int port = 44818;
        if (Uri.TryCreate(_gateway.Contains("://", StringComparison.Ordinal) ? _gateway : $"tcp://{_gateway}",
                UriKind.Absolute, out var endpoint))
        {
            host = endpoint.Host;
            if (!endpoint.IsDefaultPort) port = endpoint.Port;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(_timeout.TotalMilliseconds, 250, 1500)));
        using var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task<TagSample> ReadOneAsync(TagBinding b, CancellationToken ct)
    {
        YieldRead(ct);
        await WaitLaneAsync(_sem, ct);
        try
        {
            if (b.Def.Array is not null) return await ReadArrayAsync(b, ct);
            var lane = OperationLane(b.TagId);
            await WaitLaneAsync(lane, ct);
            try
            {
                YieldRead(ct);
                var t = await EnsureTagAsync(b, ct);
                YieldRead(ct);
                await MeasureNativeAsync(() => t.ReadAsync(ct), "Read", b.Def.Address);
                object decoded;
                try { decoded = t.Decode(b.Def.DataType); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 读回来了、只是按配置的类型解不开：这是配置错误，不是链路问题。
                    // 标成 BadDecodeError 让引擎把它隔离掉(重读一万次还是同样结果)，而不是像下面的
                    // 通信失败那样把 tag 对象销毁重建——对解码失败而言那纯属白花开销。
                    return new TagSample(b.TagId, DataValue.BadAt(StatusCode.BadDecodeError, DateTime.UtcNow));
                }
                return new TagSample(b.TagId, DataValue.GoodAt(decoded, DateTime.UtcNow));
            }
            finally { lane.Release(); }
        }
        catch (DriverReadYieldException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _tags.TryRemove(b.TagId, out var dead);
            try { dead?.Dispose(); } catch { }
            return new TagSample(b.TagId, DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        }
        finally { _sem.Release(); }
    }

    private async Task<INativeTag> EnsureTagAsync(TagBinding b, CancellationToken ct)
    {
        if (_tags.TryGetValue(b.TagId, out var existing)) return existing;
        var tag = _createTag(new Tag
        {
            Name = b.Def.Address.Trim(),
            Gateway = _gateway,
            Path = _path,
            PlcType = _plcType,
            Protocol = Protocol.ab_eip,
            Timeout = _timeout,
        });
        try { await MeasureNativeAsync(() => tag.InitializeAsync(ct), "Initialize", b.Def.Address); }
        catch { tag.Dispose(); throw; }
        _tags[b.TagId] = tag;
        return tag;
    }

    private async Task<TagSample> ReadArrayAsync(TagBinding binding, CancellationToken ct)
    {
        var definition = binding.Def.Array!;
        var count = checked((int)definition.Shape.ElementCount);
        var imageBytes = definition.ElementStrideBits > 0
            ? checked((count * definition.ElementStrideBits + 7) / 8)
            : checked(count * definition.ElementStrideBytes);
        var image = new byte[Math.Max(1, imageBytes)];
        var quality = Enumerable.Repeat(StatusCode.Bad, count).ToArray();
        foreach (var chunk in EtherNetIpArrayCodec.Chunks(count, _arrayChunkElements))
        {
            YieldRead(ct);
            string cacheKey = ArrayCacheKey(binding.TagId, chunk.Start, chunk.Count);
            // 同一父数组即使读写切片边界不同，也必须落在同一条操作锁上，避免两个不同
            // cacheKey 对重叠 PLC 元素并发 Read/Write。
            var lane = OperationLane(binding.TagId);
            await WaitLaneAsync(lane, ct);
            try
            {
                YieldRead(ct);
                var tag = await EnsureArrayTagAsync(binding, chunk.Start, chunk.Count, ct);
                YieldRead(ct);
                await MeasureNativeAsync(() => tag.ReadAsync(ct), "Read", binding.Def.Address);
                CopyChunk(tag.GetBuffer(), image, definition, chunk.Start, chunk.Count);
                Array.Fill(quality, StatusCode.Good, chunk.Start, chunk.Count);
            }
            catch (DriverReadYieldException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                _tags.TryRemove(cacheKey, out var dead);
                try { dead?.Dispose(); } catch { }
            }
            finally { lane.Release(); }
        }
        var value = EtherNetIpArrayCodec.Decode(definition, image, quality);
        return new TagSample(binding.TagId, new DataValue(value, value.OverallQuality, DateTime.UtcNow, DateTime.UtcNow));
    }

    private async Task<INativeTag> EnsureArrayTagAsync(TagBinding binding, int start, int count, CancellationToken ct)
    {
        string key = ArrayCacheKey(binding.TagId, start, count);
        if (_tags.TryGetValue(key, out var existing)) return existing;
        var definition = binding.Def.Array!;
        var tag = _createTag(new Tag
        {
            Name = ElementAddress(binding.Def.Address, definition.Shape.GetIndices(start)),
            Gateway = _gateway, Path = _path, PlcType = _plcType, Protocol = Protocol.ab_eip,
            Timeout = _timeout, ElementCount = count,
            ElementSize = definition.ElementStrideBits > 0 ? 1 : Math.Max(1, definition.ElementStrideBytes),
        });
        try { await MeasureNativeAsync(() => tag.InitializeAsync(ct), "Initialize", binding.Def.Address); }
        catch { tag.Dispose(); throw; }
        _tags[key] = tag;
        return tag;
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var results = new List<WriteResult>(writes.Count);
        foreach (var w in writes)
        {
            await WaitLaneAsync(_sem, ct);
            try
            {
                var lane = OperationLane(w.TagId);
                await WaitLaneAsync(lane, ct);
                try
                {
                    var binding = new TagBinding { TagId = w.TagId, Def = w.Def, Address = w.Address };
                    var t = await EnsureTagAsync(binding, ct);
                    await MeasureNativeAsync(() => t.ReadAsync(ct), "ReadForWrite", w.Def.Address);
                    t.Encode(w.Value, w.Def.DataType);
                    await MeasureNativeAsync(() => t.WriteAsync(ct), "Write", w.Def.Address);
                    results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Good });
                }
                finally { lane.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch { results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Bad }); }
            finally { _sem.Release(); }
        }
        return results;
    }

    public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        if (request.Binding.Def.Array is not { } definition)
            return ArrayWriteResult.Failed(StatusCode.BadDecodeError, "目标点位不是数组");
        ArrayShape sliceShape;
        try { sliceShape = request.Slice.ValidateAndCreateShape(definition.Shape); }
        catch (Exception ex) { return ArrayWriteResult.Failed(StatusCode.BadDecodeError, ex.Message); }
        if (!sliceShape.Dimensions.SequenceEqual(request.Value.Shape.Dimensions) ||
            !sliceShape.LowerBounds.SequenceEqual(request.Value.Shape.LowerBounds))
            return ArrayWriteResult.Failed(StatusCode.BadDecodeError, "写入值形状与切片不一致");

        var encoded = EtherNetIpArrayCodec.Encode(request.Value, definition);
        var targetOffsets = Enumerable.Range(0, request.Value.Count)
            .Select(i => checked((int)definition.Shape.GetLinearOffset(sliceShape.GetIndices(i)))).ToArray();
        var failures = new List<ArrayElementFailure>();
        var succeeded = 0;
        for (var input = 0; input < targetOffsets.Length;)
        {
            var run = 1;
            while (input + run < targetOffsets.Length && run < _arrayChunkElements &&
                   targetOffsets[input + run] == targetOffsets[input] + run) run++;
            string key = ArrayCacheKey(request.Binding.TagId, targetOffsets[input], run);
            await WaitLaneAsync(_sem, ct);
            try
            {
                var lane = OperationLane(request.Binding.TagId);
                await WaitLaneAsync(lane, ct);
                try
                {
                    var tag = await EnsureArrayTagAsync(request.Binding, targetOffsets[input], run, ct);
                    tag.SetBuffer(ExtractChunk(encoded, definition, input, run));
                    await MeasureNativeAsync(() => tag.WriteAsync(ct), "Write", request.Binding.Def.Address);
                    succeeded += run;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _tags.TryRemove(key, out var dead);
                    try { dead?.Dispose(); } catch { }
                    for (var i = 0; i < run; i++)
                        failures.Add(new ArrayElementFailure(sliceShape.GetIndices(input + i),
                            StatusCode.BadDeviceFailure, ex.Message));
                }
                finally { lane.Release(); }
            }
            finally { _sem.Release(); }
            input += run;
        }
        return new ArrayWriteResult(failures.Count == 0 ? StatusCode.Good :
            succeeded > 0 ? StatusCode.Uncertain : StatusCode.BadDeviceFailure, succeeded, failures);
    }

    private static void CopyChunk(byte[] source, byte[] target, ArrayDef definition, int start, int count)
    {
        if (definition.ElementStrideBits <= 0)
        {
            var bytes = checked(count * definition.ElementStrideBytes);
            source.AsSpan(0, Math.Min(bytes, source.Length)).CopyTo(
                target.AsSpan(checked(start * definition.ElementStrideBytes), bytes));
            return;
        }
        var bits = checked(count * definition.ElementStrideBits);
        for (var bit = 0; bit < bits; bit++)
        {
            var set = (source[bit / 8] & (1 << (bit & 7))) != 0;
            var targetBit = checked(start * definition.ElementStrideBits + bit);
            if (set) target[targetBit / 8] |= (byte)(1 << (targetBit & 7));
        }
    }

    private static byte[] ExtractChunk(byte[] source, ArrayDef definition, int start, int count)
    {
        if (definition.ElementStrideBits <= 0)
            return source.AsSpan(checked(start * definition.ElementStrideBytes),
                checked(count * definition.ElementStrideBytes)).ToArray();
        var bits = checked(count * definition.ElementStrideBits);
        var result = new byte[(bits + 7) / 8];
        for (var bit = 0; bit < bits; bit++)
        {
            var sourceBit = checked(start * definition.ElementStrideBits + bit);
            if ((source[sourceBit / 8] & (1 << (sourceBit & 7))) != 0)
                result[bit / 8] |= (byte)(1 << (bit & 7));
        }
        return result;
    }

    private static string ElementAddress(string address, IReadOnlyList<int> indices)
    {
        var trimmed = address.Trim();
        if (trimmed.EndsWith(']'))
        {
            var bracket = trimmed.LastIndexOf('[');
            if (bracket >= 0 && trimmed[(bracket + 1)..^1].Split(',')
                    .All(part => int.TryParse(part.Trim(), out _)))
                trimmed = trimmed[..bracket];
        }
        return $"{trimmed}[{string.Join(',', indices)}]";
    }

    private static string ArrayCacheKey(string tagId, int start, int count) => $"{tagId}\u001f{start}:{count}";

    private SemaphoreSlim OperationLane(string key)
    {
        uint hash = unchecked((uint)StringComparer.Ordinal.GetHashCode(key));
        return _tagOperationLanes[hash % (uint)_tagOperationLanes.Length];
    }

    public void SetPriorityWorkProbe(Func<bool> priorityWorkPending) =>
        _priorityWorkPending = priorityWorkPending ?? (() => false);

    private void YieldRead(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_priorityWorkPending()) throw new DriverReadYieldException();
    }

    private static async Task WaitLaneAsync(SemaphoreSlim lane, CancellationToken ct)
    {
        long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await lane.WaitAsync(ct); }
        finally { ProtocolOperationTiming.AddQueue(System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds); }
    }

    private static Task<bool> MeasureNativeAsync(Func<Task> action, string operation, string address) =>
        ProtocolOperationTiming.MeasureAsync(async () => { await action(); return true; },
            new(operation, $"libplctag {address}", 1, -1, 0));

    // One adapter operation corresponds to one libplctag API operation, not necessarily one wire packet.
    internal interface INativeTag : IDisposable
    {
        Task InitializeAsync(CancellationToken ct);
        Task ReadAsync(CancellationToken ct);
        Task WriteAsync(CancellationToken ct);
        byte[] GetBuffer();
        void SetBuffer(byte[] bytes);
        object Decode(IgwDataType type);
        void Encode(object value, IgwDataType type);
    }

    private sealed class NativeTag(Tag tag) : INativeTag
    {
        public Task InitializeAsync(CancellationToken ct) => tag.InitializeAsync(ct);
        public Task ReadAsync(CancellationToken ct) => tag.ReadAsync(ct);
        public Task WriteAsync(CancellationToken ct) => tag.WriteAsync(ct);
        public byte[] GetBuffer() => tag.GetBuffer();
        public void SetBuffer(byte[] bytes) => tag.SetBuffer(bytes);
        public object Decode(IgwDataType type) => EtherNetIpDeviceDriver.Decode(tag, type);
        public void Encode(object value, IgwDataType type) => EtherNetIpDeviceDriver.Encode(tag, value, type);
        public void Dispose() => tag.Dispose();
    }

    private static object Decode(Tag t, IgwDataType type) => type switch
    {
        IgwDataType.Bool => t.GetBit(0),
        IgwDataType.Byte => t.GetUInt8(0),
        IgwDataType.Char => t.GetInt8(0),
        IgwDataType.Int16 => t.GetInt16(0),
        IgwDataType.UInt16 => t.GetUInt16(0),
        IgwDataType.Int32 => t.GetInt32(0),
        IgwDataType.UInt32 => t.GetUInt32(0),
        IgwDataType.Int64 => t.GetInt64(0),
        IgwDataType.UInt64 => t.GetUInt64(0),
        IgwDataType.Float => t.GetFloat32(0),
        IgwDataType.Double => t.GetFloat64(0),
        _ => t.GetInt16(0),
    };

    private static void Encode(Tag t, object v, IgwDataType type)
    {
        switch (type)
        {
            case IgwDataType.Bool: t.SetBit(0, Convert.ToBoolean(v)); break;
            case IgwDataType.Byte: t.SetUInt8(0, Convert.ToByte(v)); break;
            case IgwDataType.Char: t.SetInt8(0, Convert.ToSByte(v)); break;
            case IgwDataType.Int16: t.SetInt16(0, Convert.ToInt16(v)); break;
            case IgwDataType.UInt16: t.SetUInt16(0, Convert.ToUInt16(v)); break;
            case IgwDataType.Int32: t.SetInt32(0, Convert.ToInt32(v)); break;
            case IgwDataType.UInt32: t.SetUInt32(0, Convert.ToUInt32(v)); break;
            case IgwDataType.Int64: t.SetInt64(0, Convert.ToInt64(v)); break;
            case IgwDataType.UInt64: t.SetUInt64(0, Convert.ToUInt64(v)); break;
            case IgwDataType.Float: t.SetFloat32(0, Convert.ToSingle(v)); break;
            case IgwDataType.Double: t.SetFloat64(0, Convert.ToDouble(v)); break;
            default: t.SetInt16(0, Convert.ToInt16(v)); break;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
