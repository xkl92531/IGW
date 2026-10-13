using System.Linq;
using System.Text.RegularExpressions;
using IGW.Abstractions;
using IoTClient.Clients.PLC;
using IoTClient.Interfaces;

namespace IGW.Drivers.IoTClient;

/// <summary>PLC 地址解析(西门子/欧姆龙/三菱/AB):地址原样传给 IoTClient,这里只校验非空。</summary>
public sealed class IoTPlcAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = default; error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = "空地址"; return false; }
        result = new TagAddress(0, 0, 1, 0);   // 占位;真正寻址用原始地址串(IoTClient 解析)
        return true;
    }
}

/// <summary>
/// 通用 PLC 设备驱动(IoTClient 实现)。包装 IIoTClient(西门子/欧姆龙FINS/三菱/AB 共同接口),
/// 地址原样传给 IoTClient,按数据类型选 typed Read/Write。每设备一条连接,断线由 DeviceLoop 重连。
/// </summary>
public sealed class IoTPlcDeviceDriver : IDeviceDriver, IConnectionResettableDriver, IArrayCapableDriver,
    IDriverCapabilityProvider, IDriverPriorityWorkAware
{
    private readonly string _driverId;
    private readonly Func<IIoTClient> _create;
    private readonly int _batchSize;
    private readonly int _readRetries;
    private readonly WordOrder _registerOrder;
    private readonly int _maxPayloadBytes;
    private readonly AdaptiveRequestWindow _payloadWindow;
    private readonly DriverBatchingSettings _batchingSettings;
    private IIoTClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string _lastReadError = "";
    private readonly PlcTextCodec _textCodec = new();
    private Func<bool> _priorityWorkPending = static () => false;

    public string DriverId => _driverId;
    public IAddressParser AddressParser { get; } = new IoTPlcAddressParser();
    public DriverLimits Limits { get; }
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = !_batchUnsupported && _driverId != IoTAbDriverFactory.Id,
        SupportsBatchWrite = false,
        MaxReadBytes = _driverId == IoTSiemensDriverFactory.Id ? _payloadWindow.Current : int.MaxValue,
        PartitionsReadRequests = true,
    };
    public ConnectionState State => _state;
    public string LastReadError => Volatile.Read(ref _lastReadError);
    public event Action<ConnectionState>? StateChanged;
    public ArrayDriverCapabilities ArrayCapabilities { get; }

    public IoTPlcDeviceDriver(string driverId, Func<IIoTClient> create, int batchSize = 19,
        int readRetries = 2, WordOrder registerOrder = WordOrder.ABCD, int maxPayloadBytes = 200,
        DriverBatchingSettings? batchingSettings = null)
    {
        _driverId = driverId;
        _create = create;
        _batchSize = Math.Clamp(batchSize, 1, 200);
        _readRetries = Math.Clamp(readRetries, 0, 5);
        _registerOrder = registerOrder;
        _maxPayloadBytes = Math.Clamp(maxPayloadBytes, 1, ushort.MaxValue);
        _payloadWindow = new AdaptiveRequestWindow(_maxPayloadBytes, _maxPayloadBytes);
        _batchingSettings = batchingSettings ?? DriverBatchingSettings.Parse(new Dictionary<string, string>());
        bool siemens = driverId == IoTSiemensDriverFactory.Id;
        if (siemens) AddressParser = new SiemensScanAddressParser();
        // S7 owns physical range partitioning and yields to priority writes at each request boundary.
        Limits = new DriverLimits
        {
            MaxReadUnits = siemens ? _maxPayloadBytes : 256,
            MaxGap = siemens ? 8 : 0,
            MaxTagsPerRead = _batchSize,
            MaxTagsPerWrite = 1,
        };
        var registerProtocol = driverId is IoTOmronFinsDriverFactory.Id or IoTMitsubishiDriverFactory.Id;
        ArrayCapabilities = new ArrayDriverCapabilities(
            NativeArrays: registerProtocol, StructuredArrays: true, MultiDimensional: true,
            SliceRead: registerProtocol, SliceWrite: true, AtomicWholeWrite: false,
            MaxElementsPerRequest: int.MaxValue, MaxBytesPerRequest: siemens ? _maxPayloadBytes : 200);
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
            await Task.Run(() =>
            {
                CloseClient();
                var client = _create();
                _client = client;
                var r = client.Open();
                if (!r.IsSucceed)
                {
                    CloseClient();
                    throw new IOException(_driverId + " 连接失败:" + r.Err);
                }
            }, ct);
            SetState(ConnectionState.Connected);
        }
        catch { CloseClient(); SetState(ConnectionState.Faulted); throw; }
    }

    public Task DisconnectAsync()
    {
        CloseClient();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task ResetConnectionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CloseClient();
        ClearAddressFailures();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    // 每个点表列表(ScanGroup.Tags 实例)缓存一份读取计划:批量清单/地址→类型字典/字符串清单。
    // 点表在组生命周期内不变,原来每周期重建 Where+ToList+Dictionary(每点一次字符串哈希插入)纯浪费。
    private sealed class ReadPlan
    {
        public required List<TagBinding> Batch;
        public required Dictionary<string, global::IoTClient.Enums.DataTypeEnum> Map;
        public required List<TagBinding> Strings;
        public required List<TagBinding> Arrays;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ReadPlan> _plans = new();
    private bool _batchUnsupported;   // 该客户端 BatchRead 为未实现桩(如 AB)→ 记住,直接走逐点
    private readonly object _addressFailuresGate = new();
    private readonly Dictionary<string, AddressFailureState> _addressFailures = new(StringComparer.OrdinalIgnoreCase);

    private sealed class AddressFailureState
    {
        public int Count { get; set; }
        public long RetryAfterMs { get; set; }
    }

    private static ReadPlan BuildPlan(IReadOnlyList<TagBinding> tags)
    {
        var arrays = tags.Where(t => t.Def.Array is not null).ToList();
        var batch = tags.Where(t => t.Def.Array is null &&
            t.Def.DataType is not (IgwDataType.String or IgwDataType.WString)).ToList();
        var map = new Dictionary<string, global::IoTClient.Enums.DataTypeEnum>(batch.Count);
        foreach (var t in batch) map[t.Def.Address] = ToEnum(t.Def.DataType);
        return new ReadPlan
        {
            Batch = batch,
            Map = map,
            Strings = tags.Where(t => t.Def.Array is null &&
                t.Def.DataType is IgwDataType.String or IgwDataType.WString).ToList(),
            Arrays = arrays,
        };
    }

    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (_client is null || State != ConnectionState.Connected)
                    await ConnectAsync(ct).ConfigureAwait(false);
                return await ReadOnceAsync(tags, ct, trainWindow: attempt == 0).ConfigureAwait(false);
            }
            catch (IOException ex) when (LooksLikeTransportFailure(ex.Message) && attempt < _readRetries)
            {
                // WLAN/交换机瞬时丢包不应立刻把整台 PLC 判离线。丢弃失效 Socket 后在驱动内
                // 先完成配置的短重试；只有全部尝试失败才交给 DeviceLoop 标记离线和硬重连。
                InvalidateConnection();
                await Task.Delay(Math.Min(500, 50 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<TagSample>> ReadOnceAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct, bool trainWindow)
    {
        Volatile.Write(ref _lastReadError, "");
        var now = DateTime.UtcNow;
        var samples = new List<TagSample>(tags.Count);
        var cli = _client ?? throw new IOException("未连接");
        using var readBoundary = new IoTReadBoundary(ct, _priorityWorkPending);
        // Injected/third-party IIoTClient implementations may not expose the raw exchange hook.
        if (tags.Count > 0 && cli is not IPhysicalRequestClient)
            ProtocolOperationTiming.MarkOpaqueOperation();
        if (cli is SiemensClient classicClient)
        {
            var classic = tags.Where(t => SiemensAddressDescriptor.Parse(t.Def.Address).DbNumber is not null).ToList();
            if (classic.Count > 0)
            {
                var active = ActiveTags(classic, samples, now);
                var decoded = await SiemensClassicBlockCodec.ReadAsync(
                    new IoTSiemensByteTransport(classicClient, _maxPayloadBytes), active, _textCodec, ct, _priorityWorkPending,
                    error => Volatile.Write(ref _lastReadError, error), _payloadWindow, trainWindow, _batchingSettings).ConfigureAwait(false);
                var bindingsById = active.ToDictionary(t => t.TagId, StringComparer.Ordinal);
                foreach (var sample in decoded)
                {
                    var binding = bindingsById[sample.TagId];
                    if (sample.Value.IsGood) ClearAddressFailure(binding.Def.Address);
                    else RecordAddressFailure(binding.Def.Address);
                    samples.Add(sample);
                }
                if (classic.Count == tags.Count) return samples;
                var handled = classic.ToHashSet();
                tags = tags.Where(t => !handled.Contains(t)).ToArray();
            }
        }
        var plan = _plans.GetValue(tags, static k => BuildPlan((IReadOnlyList<TagBinding>)k));
        var pendingBatch = ActiveTags(plan.Batch, samples, now);
        var strings = ActiveTags(plan.Strings, samples, now);
        var arrays = ActiveTags(plan.Arrays, samples, now);

        // Siemens BatchRead otherwise hides several physical requests inside one opaque call.
        // Keep each call within one native batch and yield before issuing the next request.
        int batchLimit = _batchSize;
        for (int batchStart = 0; batchStart < pendingBatch.Count; batchStart += batchLimit)
        {
            var batch = pendingBatch.Count <= batchLimit ? pendingBatch :
                pendingBatch.GetRange(batchStart, Math.Min(batchLimit, pendingBatch.Count - batchStart));
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            var map = batch.Count == plan.Batch.Count
                ? plan.Map
                : batch.ToDictionary(t => t.Def.Address, t => ToEnum(t.Def.DataType), StringComparer.OrdinalIgnoreCase);
            bool ok = false; Dictionary<string, object>? vals = null;
            if (!_batchUnsupported)   // AB 的 BatchRead 是库内未实现桩(必抛)→ 记住一次,不再每周期抛异常
            {
                try
                {
                    var r = IoTReadBoundary.Invoke(() => cli.BatchRead(map, _batchSize));
                    ok = r.IsSucceed; vals = r.Value;
                    if (!ok)
                    {
                        Volatile.Write(ref _lastReadError, r.Err ?? "批量读取失败");
                        if (IsTransportFailure(r, cli.Connected))
                        {
                            InvalidateConnection();
                            throw new IOException("连接断开:" + r.Err);   // 触发 DeviceLoop 重连
                        }
                        // 某些 PLC/客户端在返回“DB/地址不存在”后会顺带关闭当前 Socket。
                        // 这是数据块级故障，不应升级为整台设备掉线；当前扫描块已经按 DB 隔离，
                        // 标记本块 Bad 后重建会话，让下一个 DB 继续读取。
                        if (!cli.Connected)
                        {
                            foreach (var tag in batch)
                            {
                                RecordAddressFailure(tag.Def.Address);
                                samples.Add(new TagSample(tag.TagId, DataValue.BadAt(StatusCode.Bad, now)));
                            }
                            AddUnreadAsBad(pendingBatch, batchStart + batch.Count, samples, now);
                            AddUnreadAsBad(strings, 0, samples, now);
                            AddUnreadAsBad(arrays, 0, samples, now);
                            RestoreSessionAfterDataFailure();
                            return samples;
                        }
                    }
                }
                catch (DriverReadYieldException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { throw; }
                catch (NotImplementedException) { _batchUnsupported = true; ok = false; }
                catch { ok = false; }
            }

            if (ok && vals != null)
                foreach (var t in batch)
                {
                    if (vals.TryGetValue(t.Def.Address, out var v) && v != null)
                    {
                        ClearAddressFailure(t.Def.Address);
                        samples.Add(new TagSample(t.TagId, DataValue.GoodAt(v, now)));
                    }
                    else
                    {
                        RecordAddressFailure(t.Def.Address);
                        samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now)));
                    }
                }
            else
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    YieldToPriorityWriteIfNeeded();
                    if (!ReadOnePoint(cli, batch[i], now, samples, ct)) continue;
                    AddUnreadAsBad(pendingBatch, batchStart + i + 1, samples, now);
                    AddUnreadAsBad(strings, 0, samples, now);
                    AddUnreadAsBad(arrays, 0, samples, now);
                    RestoreSessionAfterDataFailure();
                    return samples;
                }
            }
        }

        // 字符串/宽字符串:逐点(S7 String 结构化,不进打包)
        for (int i = 0; i < strings.Count; i++)
        {
            YieldToPriorityWriteIfNeeded();
            if (!ReadOnePoint(cli, strings[i], now, samples, ct)) continue;
            AddUnreadAsBad(strings, i + 1, samples, now);
            AddUnreadAsBad(arrays, 0, samples, now);
            RestoreSessionAfterDataFailure();
            return samples;
        }

        if (arrays.Count > 0)
        {
            if (cli is SiemensClient siemens)
            {
                var transport = new IoTSiemensByteTransport(siemens, ArrayCapabilities.MaxBytesPerRequest);
                foreach (var binding in arrays)
                {
                    ct.ThrowIfCancellationRequested();
                    YieldToPriorityWriteIfNeeded();
                    try
                    {
                        var value = await SiemensArrayCodec.ReadAsync(
                            transport, binding, ct, _priorityWorkPending).ConfigureAwait(false);
                        samples.Add(new TagSample(binding.TagId,
                            new DataValue(value, value.OverallQuality, now, now)));
                        if (value.OverallQuality.IsGood) ClearAddressFailure(binding.Def.Address);
                        else RecordAddressFailure(binding.Def.Address);
                    }
                    catch (DriverReadYieldException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (!LooksLikeTransportFailure(ex.Message))
                    {
                        Volatile.Write(ref _lastReadError, ex.Message);
                        RecordAddressFailure(binding.Def.Address);
                        samples.Add(new TagSample(binding.TagId, DataValue.BadAt(StatusCode.BadDecodeError, now)));
                    }
                }
            }
            else if (cli is OmronFinsClient or MitsubishiClient)
            {
                foreach (var binding in arrays)
                {
                    ct.ThrowIfCancellationRequested();
                    YieldToPriorityWriteIfNeeded();
                    try
                    {
                        var value = ReadRegisterArray(cli, binding, ct);
                        samples.Add(new TagSample(binding.TagId,
                            new DataValue(value, value.OverallQuality, now, now)));
                        if (value.OverallQuality.IsGood) ClearAddressFailure(binding.Def.Address);
                        else RecordAddressFailure(binding.Def.Address);
                    }
                    catch (DriverReadYieldException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (!LooksLikeTransportFailure(ex.Message))
                    {
                        Volatile.Write(ref _lastReadError, ex.Message);
                        RecordAddressFailure(binding.Def.Address);
                        samples.Add(new TagSample(binding.TagId,
                            DataValue.BadAt(StatusCode.BadDecodeError, now)));
                    }
                }
            }
            else
            {
                AddUnreadAsBad(arrays, 0, samples, now);
                Volatile.Write(ref _lastReadError, $"{_driverId} 尚未接入数组字节传输");
            }
        }

        return samples;
    }

    /// <returns>true 表示数据地址失败同时使客户端会话掉线；调用方应结束当前 DB 块并重建会话。</returns>
    private bool ReadOnePoint(IIoTClient cli, TagBinding t, DateTime now, List<TagSample> samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var (ok, val, err) = IoTReadBoundary.Invoke(() => ReadByType(cli, t));
            if (ok)
            {
                ClearAddressFailure(t.Def.Address);
                samples.Add(new TagSample(t.TagId, DataValue.GoodAt(val!, now)));
                return false;
            }
            Volatile.Write(ref _lastReadError, string.IsNullOrWhiteSpace(err) ? $"{t.Def.Address} 读取失败" : err);
            if (LooksLikeTransportFailure(err))
            {
                InvalidateConnection();
                throw new IOException("连接断开:" + err);   // 触发 DeviceLoop 重连
            }
            RecordAddressFailure(t.Def.Address);
            samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now)));
            return !cli.Connected;
        }
        catch (IOException ex) when (LooksLikeDataAddressFailure(ex.Message))
        {
            Volatile.Write(ref _lastReadError, ex.Message);
            RecordAddressFailure(t.Def.Address);
            samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now)));
            return !cli.Connected;
        }
        catch (DriverReadYieldException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { InvalidateConnection(); throw; }
        catch (Exception ex) when (LooksLikeTransportFailure(ex.Message))
        {
            Volatile.Write(ref _lastReadError, ex.Message);
            InvalidateConnection();
            throw new IOException("连接断开:" + ex.Message, ex);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastReadError, ex.Message);
            RecordAddressFailure(t.Def.Address);
            samples.Add(new TagSample(t.TagId, DataValue.BadAt(StatusCode.Bad, now)));
            return !cli.Connected;
        }
    }

    private List<TagBinding> ActiveTags(IReadOnlyList<TagBinding> tags, List<TagSample> samples, DateTime now)
    {
        List<TagBinding>? active = null;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (!IsAddressQuarantined(tag.Def.Address))
            {
                active?.Add(tag);
                continue;
            }

            active ??= tags.Take(i).ToList();
            samples.Add(new TagSample(tag.TagId, DataValue.BadAt(StatusCode.Bad, now)));
        }
        return active ?? (tags as List<TagBinding> ?? tags.ToList());
    }

    private bool IsAddressQuarantined(string address)
    {
        lock (_addressFailuresGate)
            return _addressFailures.TryGetValue(address, out var failure)
                && failure.RetryAfterMs > Environment.TickCount64;
    }

    private void RecordAddressFailure(string address)
    {
        lock (_addressFailuresGate)
        {
            if (!_addressFailures.TryGetValue(address, out var failure))
                _addressFailures[address] = failure = new AddressFailureState();
            failure.Count = Math.Min(5, failure.Count + 1);
            int delayMs = Math.Min(5_000, 250 * (1 << (failure.Count - 1)));
            failure.RetryAfterMs = Environment.TickCount64 + delayMs;
        }
    }

    private void ClearAddressFailure(string address)
    {
        lock (_addressFailuresGate) _addressFailures.Remove(address);
    }

    private void ClearAddressFailures()
    {
        lock (_addressFailuresGate) _addressFailures.Clear();
    }

    private static void AddUnreadAsBad(IReadOnlyList<TagBinding> tags, int start,
        List<TagSample> samples, DateTime now)
    {
        for (int i = start; i < tags.Count; i++)
            samples.Add(new TagSample(tags[i].TagId, DataValue.BadAt(StatusCode.Bad, now)));
    }

    private void RestoreSessionAfterDataFailure()
    {
        CloseClient();
        var replacement = _create();
        _client = replacement;
        var opened = replacement.Open();
        if (!opened.IsSucceed)
        {
            CloseClient();
            SetState(ConnectionState.Faulted);
            throw new IOException("数据块读取失败后重建 PLC 会话失败:" + opened.Err);
        }
        SetState(ConnectionState.Connected);
    }

    private static global::IoTClient.Enums.DataTypeEnum ToEnum(IgwDataType t) => t switch
    {
        IgwDataType.Bool => global::IoTClient.Enums.DataTypeEnum.Bool,
        IgwDataType.Byte or IgwDataType.Char => global::IoTClient.Enums.DataTypeEnum.Byte,
        IgwDataType.Int16 => global::IoTClient.Enums.DataTypeEnum.Int16,
        IgwDataType.UInt16 => global::IoTClient.Enums.DataTypeEnum.UInt16,
        IgwDataType.Int32 => global::IoTClient.Enums.DataTypeEnum.Int32,
        IgwDataType.UInt32 => global::IoTClient.Enums.DataTypeEnum.UInt32,
        IgwDataType.Int64 => global::IoTClient.Enums.DataTypeEnum.Int64,
        IgwDataType.UInt64 => global::IoTClient.Enums.DataTypeEnum.UInt64,
        IgwDataType.Float => global::IoTClient.Enums.DataTypeEnum.Float,
        IgwDataType.Double => global::IoTClient.Enums.DataTypeEnum.Double,
        _ => global::IoTClient.Enums.DataTypeEnum.Int16,
    };

    private (bool ok, object? val, string err) ReadByType(IIoTClient c, TagBinding binding)
    {
        var addr = binding.Def.Address;
        var dt = binding.Def.DataType;
        return dt switch
        {
        IgwDataType.Bool => Wrap(c.ReadBoolean(addr)),
        IgwDataType.Int16 => Wrap(c.ReadInt16(addr)),
        IgwDataType.UInt16 => Wrap(c.ReadUInt16(addr)),
        IgwDataType.Int32 => Wrap(c.ReadInt32(addr)),
        IgwDataType.UInt32 => Wrap(c.ReadUInt32(addr)),
        IgwDataType.Int64 => Wrap(c.ReadInt64(addr)),
        IgwDataType.UInt64 => Wrap(c.ReadUInt64(addr)),
        IgwDataType.Float => Wrap(c.ReadFloat(addr)),
        IgwDataType.Double => Wrap(c.ReadDouble(addr)),
        IgwDataType.Byte => Wrap(c.ReadByte(addr)),
        IgwDataType.String => ReadString(c, binding),
        IgwDataType.WString => ReadWString(c, addr),
        _ => Wrap(c.ReadInt16(addr)),
        };
    }

    private (bool ok, object? val, string err) ReadString(IIoTClient client, TagBinding binding)
    {
        if (client is not global::IoTClient.Clients.PLC.SiemensClient siemens)
            return Wrap(client.ReadString(binding.Def.Address));

        var header = siemens.ReadString(binding.Def.Address, 2);
        if (!header.IsSucceed || header.Value is null || header.Value.Length < 2)
            return (false, null, header.Err ?? "读取 S7 STRING 长度失败");

        int currentLength = Math.Min(header.Value[1], (byte)254);
        if (currentLength == 0) return (true, string.Empty, string.Empty);
        YieldToPriorityWriteIfNeeded();
        var raw = siemens.ReadString(binding.Def.Address, checked((ushort)(currentLength + 2)));
        if (!raw.IsSucceed || raw.Value is null || raw.Value.Length < 2)
            return (false, null, raw.Err ?? "读取 S7 STRING 数据失败");

        int available = Math.Min(currentLength, raw.Value.Length - 2);
        string value = _textCodec.Decode(binding.TagId, raw.Value.AsSpan(2, available), binding.Def.TextEncoding);
        return (true, value, string.Empty);
    }

    private static (bool, object?, string) Wrap<T>(global::IoTClient.Result<T> r) => (r.IsSucceed, r.Value, r.Err);

    // 西门子 WString:[最大长度 word][当前长度 word][UTF-16BE 字符...]
    // WString:用安全的 ReadUInt16 逐字读(Read/ReadByte 会断持久会话/读不到)。布局:[maxlen word][curlen word][UTF-16BE 字符]
    private (bool, object?, string) ReadWString(IIoTClient c, string addr)
    {
        try
        {
            return ReadSiemensWString(addr, address =>
            {
                var read = c.ReadUInt16(address);
                return (read.IsSucceed, read.Value, read.Err);
            }, YieldToPriorityWriteIfNeeded);
        }
        catch (DriverReadYieldException) { throw; }
        catch (Exception ex) { return (false, null, $"WSTRING 读取异常:{addr} {ex.Message}"); }
    }

    internal static (bool Success, object? Value, string Error) ReadSiemensWString(
        string addr,
        Func<string, (bool Success, ushort Value, string Error)> readWord,
        Action? beforeCharacterRead = null)
    {
        var match = Regex.Match(addr ?? string.Empty, @"^DB(\d+)\.DB[XBWD](\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return (false, null, $"WSTRING 地址格式无效:{addr}");
        int db = int.Parse(match.Groups[1].Value);
        int offset = int.Parse(match.Groups[2].Value);

        var maximum = readWord($"DB{db}.DBW{offset}");
        if (!maximum.Success)
            return (false, null, $"WSTRING 最大长度读取失败:{addr} {maximum.Error}");
        if (maximum.Value is 0 or > 1024)
            return (false, null, $"WSTRING 声明长度无效:{addr} max={maximum.Value}");

        var current = readWord($"DB{db}.DBW{offset + 2}");
        if (!current.Success)
            return (false, null, $"WSTRING 当前长度读取失败:{addr} {current.Error}");
        if (current.Value > maximum.Value)
            return (false, null,
                $"WSTRING 当前长度超过声明长度:{addr} current={current.Value} max={maximum.Value}");
        if (current.Value == 0) return (true, string.Empty, string.Empty);

        var text = new System.Text.StringBuilder(current.Value);
        for (int index = 0; index < current.Value; index++)
        {
            beforeCharacterRead?.Invoke();
            string wordAddress = $"DB{db}.DBW{offset + 4 + index * 2}";
            var character = readWord(wordAddress);
            if (!character.Success)
                return (false, null,
                    $"WSTRING 字符读取失败:{addr} index={index} address={wordAddress} {character.Error}");
            text.Append((char)character.Value);
        }
        return (true, text.ToString(), string.Empty);
    }

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var results = new List<WriteResult>(writes.Count);
        var cli = _client;
        foreach (var w in writes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (cli is null) { results.Add(Bad(w.TagId)); continue; }
                var r = WriteByType(cli, w);
                if (!r.IsSucceed && IsTransportFailure(r, cli.Connected))
                {
                    InvalidateConnection();
                    throw new IOException("连接断开:" + r.Err);
                }
                results.Add(new WriteResult { TagId = w.TagId, Status = r.IsSucceed ? StatusCode.Good : StatusCode.Bad });
            }
            catch (IOException) { throw; }
            catch (Exception ex) when (LooksLikeTransportFailure(ex.Message))
            {
                InvalidateConnection();
                throw new IOException("连接断开:" + ex.Message, ex);
            }
            catch { results.Add(Bad(w.TagId)); }
        }
        return Task.FromResult<IReadOnlyList<WriteResult>>(results);
    }

    public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        if (_client is null || State != ConnectionState.Connected)
            await ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is SiemensClient siemens)
                return await SiemensArrayCodec.WriteAsync(
                    new IoTSiemensByteTransport(siemens, ArrayCapabilities.MaxBytesPerRequest), request, ct)
                    .ConfigureAwait(false);
            if (_client is OmronFinsClient or MitsubishiClient)
                return WriteRegisterArray(_client, request, ct);
            return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                $"{_driverId} 尚未接入数组字节写入");
        }
        catch (Exception ex) when (LooksLikeTransportFailure(ex.Message))
        {
            InvalidateConnection();
            throw new IOException("连接断开:" + ex.Message, ex);
        }
        catch (Exception ex)
        {
            return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, ex.Message);
        }
    }

    private ArrayRuntimeValue ReadRegisterArray(IIoTClient client, TagBinding binding, CancellationToken ct)
    {
        var definition = binding.Def.Array ?? throw new ArgumentException("点位不是数组", nameof(binding));
        var count = checked((int)definition.Shape.ElementCount);
        var quality = Enumerable.Repeat(StatusCode.Bad, count).ToArray();
        if (definition.ElementStrideBits > 0)
        {
            if (definition.ElementType != IgwDataType.Bool)
                throw new NotSupportedException("位数组仅支持 Bool 元素");
            var totalBits = checked(count * definition.ElementStrideBits);
            var image = new byte[(totalBits + 7) / 8];
            for (var bit = 0; bit < totalBits; bit += 512)
            {
                ct.ThrowIfCancellationRequested();
                YieldToPriorityWriteIfNeeded();
                var length = Math.Min(512, totalBits - bit);
                var address = AdvanceAddress(binding.Def.Address, bit, bitAddress: true);
                global::IoTClient.Result<byte[]> result = client switch
                {
                    OmronFinsClient fins => fins.Read(address, checked((ushort)length), true, false),
                    MitsubishiClient mc => mc.Read(address, checked((ushort)length), true),
                    _ => throw new NotSupportedException(),
                };
                IoTReadBoundary.ThrowIfInterrupted();
                if (result.IsSucceed && result.Value is { } raw)
                {
                    if (client is OmronFinsClient) UnpackFinsBits(raw, image, bit, length);
                    else UnpackMcBits(raw, image, bit, length);
                    MarkBitElements(quality, definition, bit, length, StatusCode.Good);
                }
                else if (IsTransportFailure(result, client.Connected))
                    throw new IOException("连接断开:" + result.Err);
            }
            return RegisterArrayCodec.Decode(image, definition, _registerOrder, quality);
        }

        var totalBytes = checked(count * definition.ElementStrideBytes);
        var registerImage = new byte[totalBytes];
        for (var offset = 0; offset < totalBytes; offset += ArrayCapabilities.MaxBytesPerRequest)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            var length = Math.Min(ArrayCapabilities.MaxBytesPerRequest, totalBytes - offset);
            if ((length & 1) != 0 && offset + length < totalBytes) length--;
            var address = AdvanceAddress(binding.Def.Address, offset / 2, bitAddress: false);
            global::IoTClient.Result<byte[]> result = client switch
            {
                OmronFinsClient fins => fins.Read(address, checked((ushort)length), false, false),
                MitsubishiClient mc => mc.Read(address, checked((ushort)length), false),
                _ => throw new NotSupportedException(),
            };
            IoTReadBoundary.ThrowIfInterrupted();
            if (result.IsSucceed && result.Value is { } raw && raw.Length >= length)
            {
                raw.AsSpan(0, length).CopyTo(registerImage.AsSpan(offset));
                MarkByteElements(quality, definition, offset, length, StatusCode.Good);
            }
            else if (IsTransportFailure(result, client.Connected))
                throw new IOException("连接断开:" + result.Err);
        }
        return RegisterArrayCodec.Decode(registerImage, definition, _registerOrder, quality);
    }

    private ArrayWriteResult WriteRegisterArray(IIoTClient client, ArrayWriteRequest request, CancellationToken ct)
    {
        var definition = request.Binding.Def.Array ?? throw new ArgumentException("点位不是数组", nameof(request));
        var targetShape = request.Slice.ValidateAndCreateShape(definition.Shape);
        var targetOffsets = new int[request.Value.Count];
        for (var i = 0; i < targetOffsets.Length; i++)
            targetOffsets[i] = checked((int)definition.Shape.GetLinearOffset(targetShape.GetIndices(i)));
        var encoded = RegisterArrayCodec.Encode(request.Value, definition, _registerOrder);
        var failures = new List<ArrayElementFailure>();
        var succeeded = 0;

        for (var source = 0; source < targetOffsets.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var run = 1;
            while (source + run < targetOffsets.Length &&
                   targetOffsets[source + run] == targetOffsets[source] + run) run++;
            run = definition.ElementStrideBits > 0
                ? Math.Min(run, 512)
                : Math.Min(run, Math.Max(1, ArrayCapabilities.MaxBytesPerRequest /
                    Math.Max(1, definition.ElementStrideBytes)));

            var addressOffset = definition.ElementStrideBits > 0
                ? targetOffsets[source] * definition.ElementStrideBits
                : targetOffsets[source] * definition.ElementStrideBytes / 2;
            var address = AdvanceAddress(request.Binding.Def.Address, addressOffset,
                definition.ElementStrideBits > 0);
            global::IoTClient.Result result;
            if (definition.ElementStrideBits > 0)
            {
                if (definition.ElementType != IgwDataType.Bool || definition.ElementStrideBits != 1)
                    return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                        "位数组仅支持连续 Bool 元素");
                var raw = client is OmronFinsClient
                    ? PackFinsBits(encoded, source, run)
                    : PackMcBits(encoded, source, run);
                result = client switch
                {
                    OmronFinsClient fins => IoTProtocolTiming.ClientIo(fins, () => fins.Write(address, raw, true)),
                    MitsubishiClient mc => IoTProtocolTiming.ClientIo(mc, () => mc.Write(address, raw, true)),
                    _ => throw new NotSupportedException(),
                };
            }
            else
            {
                if ((definition.ElementStrideBytes & 1) != 0)
                    return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
                        "寄存器数组元素必须按完整字对齐");
                var byteStart = checked(source * definition.ElementStrideBytes);
                var raw = encoded.AsSpan(byteStart, checked(run * definition.ElementStrideBytes)).ToArray();
                result = client switch
                {
                    OmronFinsClient fins => IoTProtocolTiming.ClientIo(fins, () => fins.Write(address, raw, false)),
                    MitsubishiClient mc => IoTProtocolTiming.ClientIo(mc, () => mc.Write(address, raw, false)),
                    _ => throw new NotSupportedException(),
                };
            }

            if (result.IsSucceed) succeeded += run;
            else
            {
                if (IsTransportFailure(result, client.Connected))
                    throw new IOException("连接断开:" + result.Err);
                for (var i = 0; i < run; i++)
                    failures.Add(new ArrayElementFailure(
                        definition.Shape.GetIndices(targetOffsets[source + i]), StatusCode.Bad,
                        result.Err ?? "写入失败"));
            }
            source += run;
        }

        var status = failures.Count == 0 ? StatusCode.Good : succeeded > 0 ? StatusCode.Uncertain : StatusCode.Bad;
        return new ArrayWriteResult(status, succeeded, failures);
    }

    private string AdvanceAddress(string address, int delta, bool bitAddress)
    {
        var match = Regex.Match(address.Trim(), @"^(?<prefix>[A-Za-z]+)(?<number>[0-9A-Fa-f]+)(?:\.(?<bit>\d+))?$");
        if (!match.Success) throw new FormatException($"无法递增数组地址:{address}");
        var prefix = match.Groups["prefix"].Value;
        var numberText = match.Groups["number"].Value;
        var isMcHex = _driverId == IoTMitsubishiDriverFactory.Id &&
            prefix is "X" or "x" or "Y" or "y" or "B" or "b" or "W" or "w";
        var number = Convert.ToInt32(numberText, isMcHex ? 16 : 10);
        if (bitAddress && match.Groups["bit"].Success)
        {
            var bit = int.Parse(match.Groups["bit"].Value) + delta;
            number += bit / 16;
            bit %= 16;
            return prefix + Convert.ToString(number, isMcHex ? 16 : 10).ToUpperInvariant() + "." + bit;
        }
        return prefix + Convert.ToString(number + delta, isMcHex ? 16 : 10).ToUpperInvariant();
    }

    private static void MarkByteElements(StatusCode[] quality, ArrayDef definition,
        int byteStart, int byteLength, StatusCode status)
    {
        var end = byteStart + byteLength;
        for (var i = 0; i < quality.Length; i++)
        {
            var start = i * definition.ElementStrideBytes;
            if (start >= byteStart && start + definition.ElementStrideBytes <= end) quality[i] = status;
        }
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

    private static void UnpackFinsBits(byte[] source, byte[] target, int targetBit, int count)
    {
        for (var i = 0; i < count && i < source.Length; i++)
            if ((source[i] & 1) != 0) target[(targetBit + i) >> 3] |= (byte)(1 << ((targetBit + i) & 7));
    }

    private static void UnpackMcBits(byte[] source, byte[] target, int targetBit, int count)
    {
        for (var i = 0; i < count && (i >> 1) < source.Length; i++)
        {
            var on = (i & 1) == 0 ? (source[i >> 1] & 0x10) != 0 : (source[i >> 1] & 0x01) != 0;
            if (on) target[(targetBit + i) >> 3] |= (byte)(1 << ((targetBit + i) & 7));
        }
    }

    private static byte[] PackFinsBits(byte[] source, int sourceBit, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] =
            (byte)(((source[(sourceBit + i) >> 3] >> ((sourceBit + i) & 7)) & 1) != 0 ? 1 : 0);
        return result;
    }

    private static byte[] PackMcBits(byte[] source, int sourceBit, int count)
    {
        var result = new byte[(count + 1) / 2];
        for (var i = 0; i < count; i++)
            if (((source[(sourceBit + i) >> 3] >> ((sourceBit + i) & 7)) & 1) != 0)
                result[i >> 1] |= (byte)((i & 1) == 0 ? 0x10 : 0x01);
        return result;
    }

    private global::IoTClient.Result WriteByType(IIoTClient c, WriteRequest request)
    {
        var addr = request.Def.Address;
        var v = request.Value;
        return request.Def.DataType switch
        {
        IgwDataType.Bool => WriteValue(c, addr, Convert.ToBoolean(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Int16 => WriteValue(c, addr, Convert.ToInt16(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.UInt16 => WriteValue(c, addr, Convert.ToUInt16(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Int32 => WriteValue(c, addr, Convert.ToInt32(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.UInt32 => WriteValue(c, addr, Convert.ToUInt32(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Int64 => WriteValue(c, addr, Convert.ToInt64(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.UInt64 => WriteValue(c, addr, Convert.ToUInt64(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Float => WriteValue(c, addr, Convert.ToSingle(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Double => WriteValue(c, addr, Convert.ToDouble(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.Byte => WriteValue(c, addr, Convert.ToByte(v), static (c, a, v) => c.Write(a, v)),
        IgwDataType.String => WriteString(c, request, Convert.ToString(v) ?? ""),
        IgwDataType.WString => WriteWString(c, addr, Convert.ToString(v) ?? ""),
        _ => WriteValue(c, addr, Convert.ToInt16(v), static (c, a, v) => c.Write(a, v)),
        };
    }

    private static global::IoTClient.Result WriteValue<T>(IIoTClient client, string address, T value,
        Func<IIoTClient, string, T, global::IoTClient.Result> write) =>
        IoTProtocolTiming.ClientIo(client, () => write(client, address, value));

    private global::IoTClient.Result WriteString(IIoTClient client, WriteRequest request, string value)
    {
        if (client is not global::IoTClient.Clients.PLC.SiemensClient siemens)
            return IoTProtocolTiming.ClientIo(client, () => client.Write(request.Def.Address, value));

        int capacity = request.Def.StringLength.GetValueOrDefault();
        if (capacity <= 0)
        {
            var header = IoTProtocolTiming.ClientIo(siemens, () => siemens.ReadString(request.Def.Address, 2));
            if (header.IsSucceed && header.Value is { Length: >= 1 }) capacity = header.Value[0];
        }
        capacity = Math.Clamp(capacity <= 0 ? 254 : capacity, 1, 254);
        var encoded = _textCodec.Encode(request.TagId, value, request.Def.TextEncoding, capacity);
        var packet = new byte[encoded.Length + 2];
        packet[0] = checked((byte)capacity);
        packet[1] = checked((byte)encoded.Length);
        encoded.CopyTo(packet, 2);
        return IoTSiemensByteTransport.WriteBytes(siemens, request.Def.Address, packet);
    }

    private static global::IoTClient.Result WriteWString(IIoTClient c, string addr, string s)
    {
        var mm = System.Text.RegularExpressions.Regex.Match(addr, @"DB(\d+)\.DB[XBWD](\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!mm.Success) return IoTProtocolTiming.ClientIo(c, () => c.Write(addr, s));
        int db = int.Parse(mm.Groups[1].Value), off = int.Parse(mm.Groups[2].Value);
        int n = s.Length;
        IoTProtocolTiming.ClientIo(c, () => c.Write($"DB{db}.DBW{off}", (ushort)n));
        var last = IoTProtocolTiming.ClientIo(c, () => c.Write($"DB{db}.DBW{off + 2}", (ushort)n));
        for (int i = 0; i < n; i++) last = IoTProtocolTiming.ClientIo(c, () => c.Write($"DB{db}.DBW{off + 4 + i * 2}", (ushort)s[i]));
        return last;
    }

    private static WriteResult Bad(string id) => new() { TagId = id, Status = StatusCode.Bad };

    private void InvalidateConnection()
    {
        CloseClient();
        SetState(ConnectionState.Faulted);
    }

    private void CloseClient()
    {
        var client = Interlocked.Exchange(ref _client, null);
        try { client?.Close(); } catch { }
        try { (client as IDisposable)?.Dispose(); } catch { }
    }

    internal static bool LooksLikeTransportFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        if (LooksLikeDataAddressFailure(error)) return false;
        string value = error.ToLowerInvariant();
        return value.Contains("socket", StringComparison.Ordinal)
            || value.Contains("connection", StringComparison.Ordinal)
            || value.Contains("connect failed", StringComparison.Ordinal)
            || value.Contains("connection reset", StringComparison.Ordinal)
            || value.Contains("timed out", StringComparison.Ordinal)
            || value.Contains("timeout", StringComparison.Ordinal)
            || value.Contains("unreachable", StringComparison.Ordinal)
            || value.Contains("broken pipe", StringComparison.Ordinal)
            || value.Contains("连接断开", StringComparison.Ordinal)
            || value.Contains("连接失败", StringComparison.Ordinal)
            || value.Contains("连接异常", StringComparison.Ordinal)
            || value.Contains("网络异常", StringComparison.Ordinal)
            || value.Contains("超时", StringComparison.Ordinal);
    }

    /// <summary>
    /// IoTClient may close its socket after a local parse/copy exception. The socket flag alone
    /// must not turn that software/data failure into a PLC/network outage.
    /// </summary>
    internal static bool IsTransportFailure(global::IoTClient.Result result, bool connected)
    {
        if (IsLocalProcessingFailure(result.Exception)) return false;
        if (result.Exception is System.Net.Sockets.SocketException or IOException or TimeoutException)
            return true;
        return !connected || LooksLikeTransportFailure(result.Err);
    }

    private static bool IsLocalProcessingFailure(Exception? error) => error is
        ArgumentException or InvalidDataException or FormatException or InvalidCastException or
        OverflowException or IndexOutOfRangeException or NotSupportedException;

    private static bool LooksLikeDataAddressFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        string value = error.ToLowerInvariant();
        return value.Contains("item not available", StringComparison.Ordinal)
            || value.Contains("data item", StringComparison.Ordinal)
            || value.Contains("address out of range", StringComparison.Ordinal)
            || value.Contains("address does not exist", StringComparison.Ordinal)
            || value.Contains("object does not exist", StringComparison.Ordinal)
            || value.Contains("db does not exist", StringComparison.Ordinal)
            || value.Contains("db not exist", StringComparison.Ordinal)
            || value.Contains("invalid address", StringComparison.Ordinal)
            || value.Contains("wrong address", StringComparison.Ordinal)
            || value.Contains("地址不存在", StringComparison.Ordinal)
            || value.Contains("地址越界", StringComparison.Ordinal)
            || value.Contains("地址错误", StringComparison.Ordinal)
            || value.Contains("数据块不存在", StringComparison.Ordinal)
            || value.Contains("db块不存在", StringComparison.Ordinal)
            || value.Contains("db 不存在", StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync() { CloseClient(); SetState(ConnectionState.Disconnected); return ValueTask.CompletedTask; }
}
