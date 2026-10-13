using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public sealed class SiemensPlusException : Exception
{
    public SiemensPlusResultCode Code { get; }

    public SiemensPlusException(SiemensPlusResultCode code)
        : base(MessageFor(code)) => Code = code;

    private static string MessageFor(SiemensPlusResultCode code) => code switch
    {
        SiemensPlusResultCode.AuthenticationRequired => "PLC 要求身份验证，请配置凭据后重试。",
        SiemensPlusResultCode.AuthenticationFailed => "PLC 身份验证失败，请检查凭据。",
        SiemensPlusResultCode.PermissionDenied => "当前 PLC 账户没有执行此操作的权限。",
        SiemensPlusResultCode.UnsupportedFirmware => "当前 PLC 固件不受 S7 Plus 通信支持。",
        SiemensPlusResultCode.Timeout => "S7 Plus 操作超时。",
        SiemensPlusResultCode.TransportFailure => "S7 Plus 连接已断开或无法建立。",
        SiemensPlusResultCode.CapacityFailure => "S7 Plus 请求或响应超过已确认的协议容量。",
        SiemensPlusResultCode.SymbolNotFound => "PLC 程序中找不到该符号，请检查点名或 PLC 程序是否已变更。",
        SiemensPlusResultCode.ItemRejected => "PLC 拒绝写入该点（类型不符、只读或受保护）。",
        SiemensPlusResultCode.BufferTooSmall => "S7 Plus 本地输出缓冲区不足。",
        SiemensPlusResultCode.InvalidArgument => "S7 Plus 请求参数无效。",
        _ => "S7 Plus 协议操作失败。",
    };
}

public interface ISiemensPlusTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    string LastReadError => "";
    /// <summary>实际加载到的原生组件 ABI 版本；0 = 未知(测试替身)。现场要能看出装的原生库是不是跟网关同版本。</summary>
    uint NativeAbiVersion => 0;

    /// <summary>
    /// 变化订阅：PLC 按周期主动推送变化的值，取代周期轮询。原生库低于 ABI v4 时返回 null，
    /// 调用方回退到轮询（不能因为原生库旧就让设备采不上数据）。
    /// </summary>
    Task<ISiemensPlusSubscription?> SubscribeAsync(
        IReadOnlyList<TagBinding> tags, int cycleTimeMs,
        Action<IReadOnlyList<TagSample>> onSamples, CancellationToken ct) =>
        Task.FromResult<ISiemensPlusSubscription?>(null);
    Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct);
    Task AuthenticateAsync(string username, string password, CancellationToken ct);
    Task ProbeAsync(CancellationToken ct);
    Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct);
    /// <summary>列出 PLC 上的数据块(块名 → DB 号)。原生库低于 ABI v5 时返回 null。</summary>
    Task<IReadOnlyList<SiemensPlusNativeDataBlock>?> ListDataBlocksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SiemensPlusNativeDataBlock>?>(null);
    Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct);
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct);
    ArrayDriverCapabilities ArrayCapabilities => new(
        NativeArrays: false, StructuredArrays: true, MultiDimensional: true,
        SliceRead: false, SliceWrite: true, AtomicWholeWrite: false,
        MaxElementsPerRequest: 256, MaxBytesPerRequest: 1_048_576);
    Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
            "当前 S7 Plus 传输未实现数组写入。"));
    Task DisconnectAsync();
}

/// <summary>一条活动的 S7 Plus 订阅。释放时删除 PLC 侧的订阅并停止拉取通知。</summary>
public interface ISiemensPlusSubscription : IAsyncDisposable
{
    /// <summary>实际被 PLC 接受的点位（解析不到的符号不在其中，由调用方回退轮询或报坏点）。</summary>
    IReadOnlyList<string> AcceptedTagIds { get; }
}

public sealed class SiemensPlusTransport : ISiemensPlusTransport, IDriverPriorityWorkAware
{
    private readonly ISiemensPlusNativeApi _native;
    private readonly SemaphoreSlim _operationLane = new(1, 1);
    private bool _disposed;
    private readonly AdaptiveRequestWindow _readWindow;
    private readonly AdaptiveRequestWindow _arrayWindow;
    private Func<bool> _priorityWorkPending = static () => false;
    public bool IsConnected { get; private set; }
    public string LastReadError { get; private set; } = "";
    public uint NativeAbiVersion => _native is SiemensPlusNative native ? native.LoadedAbiVersion : 0;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: false, StructuredArrays: true, MultiDimensional: true,
        SliceRead: false, SliceWrite: true, AtomicWholeWrite: false,
        MaxElementsPerRequest: 256, MaxBytesPerRequest: 1_048_576);

    public SiemensPlusTransport(ISiemensPlusNativeApi? native = null, AdaptiveRequestWindow? readWindow = null,
        AdaptiveRequestWindow? arrayWindow = null)
    {
        _native = native ?? new SiemensPlusNative();
        _readWindow = readWindow ?? new AdaptiveRequestWindow(64, 4096);
        // 数组元素按符号展开后量级大,单独一个窗口控制每条 telegram 的元素数(见 codec 注释)。
        _arrayWindow = arrayWindow ?? new AdaptiveRequestWindow(SiemensSymbolicArrayCodec.AdaptiveInitialBatchSize, 256);
    }

    public void SetPriorityWorkProbe(Func<bool> priorityWorkPending) =>
        _priorityWorkPending = priorityWorkPending ?? (() => false);

    private void YieldToPriorityWriteIfNeeded()
    {
        if (_priorityWorkPending()) throw new DriverReadYieldException();
    }

    public async Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
    {
        var timeoutMs = checked((uint)Math.Clamp(timeout.TotalMilliseconds, 100, uint.MaxValue));
        var code = await InvokeAsync(() => _native.Connect(host, port, timeoutMs, legacy), ct);
        if (!legacy && code is SiemensPlusResultCode.UnsupportedFirmware or SiemensPlusResultCode.ProtocolFailure)
        {
            ThrowIfFailed(await InvokeAsync(_native.Disconnect, ct));
            code = await InvokeAsync(() => _native.Connect(host, port, timeoutMs, legacy: true), ct);
        }
        if (code != SiemensPlusResultCode.Ok)
        {
            // native connect 失败也可能已经创建了 TCP/协议会话；必须主动清理，
            // 否则随后回退 S7 Classic 时 PLC 端会留下占用连接。
            try { _ = await InvokeAsync(_native.Disconnect, CancellationToken.None); } catch { }
            IsConnected = false;
        }
        ThrowIfFailed(code);
        IsConnected = true;
    }

    public async Task AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var code = await InvokeAsync(() => _native.Authenticate(username, password), ct);
        ThrowIfFailed(code);
    }

    public async Task ProbeAsync(CancellationToken ct)
    {
        var code = await InvokeAsync(_native.Probe, ct);
        ThrowIfFailed(code);
    }

    public async Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct)
    {
        var result = await InvokeAsync(_native.Browse, ct);
        ThrowIfFailed(result.Code);
        return result.Tags;
    }

    public async Task<IReadOnlyList<SiemensPlusNativeDataBlock>?> ListDataBlocksAsync(CancellationToken ct)
    {
        var result = await InvokeAsync(_native.ListDataBlocks, ct);
        if (result is null) return null;
        ThrowIfFailed(result.Code);
        return result.Blocks;
    }

    public async Task<ISiemensPlusSubscription?> SubscribeAsync(
        IReadOnlyList<TagBinding> tags, int cycleTimeMs,
        Action<IReadOnlyList<TagSample>> onSamples, CancellationToken ct)
    {
        // 数组点位走不了订阅(订阅按标量符号登记)，交给调用方继续轮询
        var scalars = tags.Where(static tag => tag.Def.Array is null).ToArray();
        if (scalars.Length == 0) return null;

        ushort cycle = (ushort)Math.Clamp(cycleTimeMs, 50, ushort.MaxValue);
        var symbols = scalars.Select(static tag => tag.Def.Address).ToArray();
        var created = await InvokeAsync(() => _native.Subscribe(symbols, cycle), ct);
        if (created is null) return null;                       // 原生库低于 v4：回退轮询
        if (created.Code != SiemensPlusResultCode.Ok) ThrowIfFailed(created.Code);

        var accepted = new List<string>(scalars.Length);
        var slotTags = new TagBinding?[scalars.Length];
        for (int index = 0; index < scalars.Length; index++)
        {
            if (index >= created.ItemCodes.Count || created.ItemCodes[index] != SiemensPlusResultCode.Ok) continue;
            slotTags[index] = scalars[index];
            accepted.Add(scalars[index].TagId);
        }
        if (accepted.Count == 0)
        {
            await InvokeAsync(() => _native.DeleteSubscription(created.SubscriptionId), CancellationToken.None);
            return null;
        }
        var lease = new PlusSubscription(this, created.SubscriptionId, slotTags, accepted, onSamples);
        lease.Start();
        return lease;
    }

    /// <summary>
    /// 订阅租约：后台循环拉取 PLC 推来的通知并回调。
    /// 通知与读写共用同一条连接，所以每次拉取都走同一个操作闸门排队；PLC 每个周期都会给一个
    /// 通知(没变化时是空的)，因此单次拉取不会长时间占住连接。
    /// </summary>
    private sealed class PlusSubscription(
        SiemensPlusTransport transport, uint subscriptionId, TagBinding?[] slotTags,
        IReadOnlyList<string> acceptedTagIds, Action<IReadOnlyList<TagSample>> onSamples)
        : ISiemensPlusSubscription
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _pump;

        public IReadOnlyList<string> AcceptedTagIds => acceptedTagIds;

        public void Start() => _pump = Task.Run(() => PumpAsync(_stop.Token));

        private async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                SiemensPlusNativeNotification notification;
                try
                {
                    notification = await transport.InvokeAsync(
                        () => transport._native.PollNotification(subscriptionId, slotTags.Length), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    // 连接出问题时退出泵：设备循环会重连，重连后订阅由上层重建
                    return;
                }
                if (notification.Code == SiemensPlusResultCode.SubscriptionNotFound) return;
                if (notification.Code != SiemensPlusResultCode.Ok) return;
                if (notification.Values.Count == 0) continue;   // 心跳：这一轮没有变化

                var now = DateTime.UtcNow;
                var samples = new List<TagSample>(notification.Values.Count);
                foreach (var (slot, value) in notification.Values)
                {
                    if (slot < 0 || slot >= slotTags.Length) continue;
                    var tag = slotTags[slot];
                    if (tag is null) continue;
                    samples.Add(new TagSample(tag.TagId, new DataValue(value, StatusCode.Good, now, now)));
                }
                if (samples.Count > 0) onSamples(samples);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_pump is not null) { try { await _pump; } catch { } }
            try { await transport.InvokeAsync(() => transport._native.DeleteSubscription(subscriptionId), CancellationToken.None); }
            catch { }
            _stop.Dispose();
        }
    }

    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        LastReadError = "";
        var errors = new List<string>();
        var samples = new Dictionary<string, TagSample>(StringComparer.Ordinal);
        var scalars = tags.Where(static tag => tag.Def.Array is null).ToArray();
        int recoveryEnd = 0;
        for (int cursor = 0; cursor < scalars.Length;)
        {
            ct.ThrowIfCancellationRequested();
            YieldToPriorityWriteIfNeeded();
            var planWindow = _readWindow.Capture();
            int size = Math.Min(planWindow.Current, scalars.Length - cursor);
            if (cursor < recoveryEnd) size = Math.Min(size, recoveryEnd - cursor);
            var symbols = scalars.Skip(cursor).Take(size).Select(x => x.Def.Address).ToArray();
            double requestMs = 0;
            var result = await InvokeAsync(() =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return _native.ReadMeasured(symbols, () =>
                    { ct.ThrowIfCancellationRequested(); YieldToPriorityWriteIfNeeded(); },
                    new("Read", "S7 Plus symbols", size, -1, 0)); }
                finally { requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (result.Code == SiemensPlusResultCode.CapacityFailure)
            {
                recoveryEnd = Math.Max(recoveryEnd, cursor + size);
                if (size == 1)
                {
                    var tag = scalars[cursor++];
                    var quality = QualityFor(result.Code);
                    samples[tag.TagId] = new(tag.TagId, DataValue.BadAt(quality, now));
                    if (errors.Count < 16) errors.Add($"{tag.TagId} ({tag.Def.Address}): {result.Code}; quality={quality}; {new SiemensPlusException(result.Code).Message}");
                    LastReadError = string.Join("; ", errors);
                    continue;
                }
                _readWindow.OnCapacityFailure(size);
                continue;
            }
            if (result.Code != SiemensPlusResultCode.Ok && !IsSessionFailure(result.Code))
            {
                // 本片整体失败但会话仍可用：只把本片标 Bad，已读成功的其它片照常返回
                var quality = QualityFor(result.Code);
                for (int index = 0; index < size; index++)
                {
                    var tag = scalars[cursor + index];
                    samples[tag.TagId] = new(tag.TagId, DataValue.BadAt(quality, now));
                }
                if (errors.Count < 16) errors.Add($"第 {cursor + 1}～{cursor + size} 项：{result.Code}; quality={quality}; {new SiemensPlusException(result.Code).Message}");
                LastReadError = string.Join("; ", errors);
                cursor += size;
                continue;
            }
            // 连接在读取中被对端掐断，且这一片点数不止一个：PLC 对超出它承受能力的请求
            // 往往不回「容量不足」，而是直接关掉连接(现场实测 93/118 点必断、46~56 点能成)。
            // 这里把断连也当作容量压力，先把窗口砍半再抛出——否则重连后又发同样大的请求，
            // 一样被掐断，永远收敛不到 PLC 能接受的大小。
            if (result.Code is SiemensPlusResultCode.TransportFailure or SiemensPlusResultCode.Timeout && size > 1)
                _readWindow.OnCapacityFailure(size);
            ThrowIfFailed(result.Code);
            if (result.Values.Count != size)
                throw new SiemensPlusException(SiemensPlusResultCode.ProtocolFailure);
            for (int index = 0; index < size; index++)
            {
                var value = result.Values[index];
                var quality = QualityFor(value.Code);
                var tag = scalars[cursor + index];
                samples[tag.TagId] = new TagSample(tag.TagId,
                    value.Code == SiemensPlusResultCode.Ok
                        ? new DataValue(value.Value, quality, now, now)
                        : DataValue.BadAt(quality, now));
                // 单个符号找不到是点位级问题：已按点位返回 BadNodeIdUnknown，由坏点隔离接管，
                // 并在监控中心的「点位异常」里逐点记录原因和处置。不写进 LastReadError——
                // 那会把整台设备标成「部分在线」，一个配错的点位就能让设备状态反复跳。
            }
            if (cursor >= recoveryEnd && !result.LocalBufferRetried && IsConnected && result.Values.All(value => value.Code == SiemensPlusResultCode.Ok))
                _readWindow.OnSuccess(size, requestMs, planWindow);
            cursor += size;
        }
        foreach (var tag in tags.Where(static tag => tag.Def.Array is not null))
        {
            YieldToPriorityWriteIfNeeded();
            var value = await InvokeAsync(() => SiemensSymbolicArrayCodec.Read(_native, tag, _arrayWindow, _priorityWorkPending), ct);
            samples[tag.TagId] = new TagSample(tag.TagId,
                new DataValue(value, value.OverallQuality, now, now));
        }
        await ResyncIfRequestedAsync();
        return tags.Select(tag => samples[tag.TagId]).ToArray();
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var nativeWrites = writes.Select(x => new SiemensPlusNativeWrite(x.Def.Address, x.Value)).ToArray();
        var codes = new SiemensPlusResultCode[writes.Count];
        await InvokeAsync(() => { WriteRange(nativeWrites, 0, nativeWrites.Length, codes); return true; }, ct);
        await ResyncIfRequestedAsync();
        return writes.Select((x, index) => new WriteResult { TagId = x.TagId, Status = QualityFor(codes[index]) }).ToArray();
    }

    /// <summary>
    /// 逐项写入：找不到或被 PLC 拒绝的点只让该点失败。多项报文被整体拒绝(CapacityFailure)时对半拆分重写，
    /// 单项仍被拒则只标该点；断线/超时等会话级失败整段同一结果。
    /// </summary>
    private void WriteRange(SiemensPlusNativeWrite[] writes, int start, int count, SiemensPlusResultCode[] codes)
    {
        var slice = writes.AsSpan(start, count).ToArray();
        var result = ProtocolOperationTiming.MeasureResult(() => _native.WriteItems(slice),
            static result => result.Code != SiemensPlusResultCode.Ok || result.Items.Any(code => code != SiemensPlusResultCode.Ok),
            new("Write", "S7 Plus symbols", count, -1, 0));
        if (result.Code == SiemensPlusResultCode.CapacityFailure && count > 1)
        {
            int half = count / 2;
            WriteRange(writes, start, half, codes);
            WriteRange(writes, start + half, count - half, codes);
            return;
        }
        for (int index = 0; index < count; index++)
            codes[start + index] = result.Code != SiemensPlusResultCode.Ok || result.Items.Count != count
                ? (result.Code == SiemensPlusResultCode.Ok ? SiemensPlusResultCode.ProtocolFailure : result.Code)
                : result.Items[index];
    }

    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        InvokeAsync(() => SiemensSymbolicArrayCodec.Write(_native, request, _arrayWindow), ct);

    /// <summary>
    /// 原生层判断 PLC 程序可能已重新下载(同一 DB 短时间内多个已解析地址被拒)时，断开会话：
    /// 驱动下一轮按正常流程重连并重新认证，符号与类型缓存随新会话重建。本轮已读到的值照常返回。
    /// </summary>
    private async Task ResyncIfRequestedAsync()
    {
        if (!IsConnected || !_native.TakeResyncRequest()) return;
        const string note = "PLC 程序可能已重新下载：同一数据块多个点的地址失效，正在重建 S7 Plus 会话以刷新符号缓存";
        LastReadError = string.IsNullOrEmpty(LastReadError) ? note : LastReadError + "; " + note;
        try { await DisconnectAsync(); } catch { IsConnected = false; }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;
        await _operationLane.WaitAsync();
        try
        {
            ThrowIfFailed(_native.Disconnect());
            IsConnected = false;
        }
        finally { _operationLane.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync(); }
        finally
        {
            _disposed = true;
            _native.Dispose();
            IsConnected = false;
            _operationLane.Dispose();
        }
    }

    private async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await _operationLane.WaitAsync(ct); }
        finally { ProtocolOperationTiming.AddQueue(System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds); }
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(action, CancellationToken.None);
        }
        finally { _operationLane.Release(); }
    }

    /// <summary>会话级失败(断线/超时/认证/原生组件异常)由驱动整体处理；其余失败只影响出错的那一片。</summary>
    private static bool IsSessionFailure(SiemensPlusResultCode code) => code is not
        (SiemensPlusResultCode.ProtocolFailure or SiemensPlusResultCode.SymbolNotFound or SiemensPlusResultCode.BufferTooSmall);

    private static void ThrowIfFailed(SiemensPlusResultCode code)
    {
        if (code != SiemensPlusResultCode.Ok) throw new SiemensPlusException(code);
    }

    private static StatusCode QualityFor(SiemensPlusResultCode code) => code switch
    {
        SiemensPlusResultCode.Ok => StatusCode.Good,
        SiemensPlusResultCode.Timeout => StatusCode.BadTimeout,
        SiemensPlusResultCode.TransportFailure => StatusCode.BadCommFailure,
        SiemensPlusResultCode.ProtocolFailure => StatusCode.BadDecodeError,
        // 符号在 PLC 程序中不存在：接入坏点隔离(连续 3 次后隔离、30 秒复探)，不影响其它点
        SiemensPlusResultCode.SymbolNotFound => StatusCode.BadNodeIdUnknown,
        _ => StatusCode.BadDeviceFailure,
    };
}
