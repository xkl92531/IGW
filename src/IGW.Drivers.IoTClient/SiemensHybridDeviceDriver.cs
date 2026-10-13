using System.Runtime.CompilerServices;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public enum SiemensDeviceDetailState
{
    None,
    Connected,
    PartialOnline,
    AuthenticationRequired,
    AuthenticationFailed,
    PermissionDenied,
    UnsupportedFirmware,
    TransportFailure,
    SymbolMismatch,
    DbAddressMismatch,
}

public sealed record SiemensConnectionDetail(
    SiemensDeviceDetailState DetailState,
    string Message,
    bool ClassicConnected,
    bool PlusConnected);

public interface ISiemensDeviceDiagnostics
{
    SiemensConnectionDetail Snapshot();
}

public sealed record SiemensCredential(string Username, string Password);

public interface ISiemensCredentialResolver
{
    Task<SiemensCredential?> ResolveAsync(CancellationToken ct);
}

public sealed class SiemensHybridDeviceDriver : IDeviceDriver, IConnectionResettableDriver,
    ISiemensDeviceDiagnostics, IDeviceConnectionDiagnostics, IBrowsableDriver, IDataBlockDirectory, IArrayCapableDriver,
    IDriverCapabilityProvider, IDriverPriorityWorkAware, IDriverSubscriptionProvider
{
    private readonly string _host;
    private readonly ushort _port;
    private readonly TimeSpan _timeout;
    private readonly bool _supportsPlus;
    private readonly bool _legacyPlus;
    private ISiemensClassicTransport _classic;
    private ISiemensPlusTransport _plus;
    private readonly Func<ISiemensClassicTransport>? _classicFactory;
    private readonly Func<ISiemensPlusTransport>? _plusFactory;
    private readonly ISiemensCredentialResolver? _credentialResolver;
    private readonly ConditionalWeakTable<object, SiemensRoutePlan> _readPlans = new();
    private ConnectionState _state;
    private SiemensDeviceDetailState _detailState;
    private string _detailMessage = "未连接";
    private bool _authenticated;
    private bool _disposed;
    private readonly object _routeSync = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private (string Path, TagDef Def)? _classicPoint, _plusPoint;
    private Func<bool> _priorityWorkPending = static () => false;
    /// <summary>地址形式与本设备协议不匹配的点位及原因，交给网关按点位报到监控中心。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _unroutableReasons =
        new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> TagFaultReasons => _unroutableReasons;

    /// <summary>
    /// S7 Plus 套接字超时的下限。符号地址必须先向 PLC 取 DB 列表和类型信息才能解析成 LID 路径，
    /// 大工程上这一步天然是秒级的(现场 32 万点的设备实测 1.7~3 秒)；沿用 Classic 那套 2~3 秒的
    /// 超时会把正常的解析判成「连接失效」，断开后解析缓存要重建、下一次更慢，直接自锁。
    /// Classic 通道不受影响，仍按设备配置的超时判定。设备配得比这更大时以设备配置为准。
    /// </summary>
    internal static readonly TimeSpan PlusMinimumTimeout = TimeSpan.FromSeconds(15);

    private TimeSpan PlusTimeout => _timeout > PlusMinimumTimeout ? _timeout : PlusMinimumTimeout;

    public SiemensHybridDeviceDriver(
        string host,
        ushort port,
        TimeSpan timeout,
        bool supportsPlus,
        ISiemensClassicTransport classic,
        ISiemensPlusTransport plus,
        ISiemensCredentialResolver? credentialResolver = null,
        bool legacyPlus = false,
        Func<ISiemensClassicTransport>? classicFactory = null,
        Func<ISiemensPlusTransport>? plusFactory = null,
        IReadOnlyList<(string Path, TagDef Def)>? configuredPoints = null)
    {
        RegisterRoutes(configuredPoints ?? []);
        _host = host;
        _port = port;
        _timeout = timeout;
        _supportsPlus = supportsPlus;
        _classic = classic;
        _plus = plus;
        _classicFactory = classicFactory;
        _plusFactory = plusFactory;
        _credentialResolver = credentialResolver;
        _legacyPlus = legacyPlus;
        Limits = new DriverLimits
        {
            MaxReadUnits = 4096,
            // The driver partitions a logical batch into physical requests and checks priority between them.
            MaxTagsPerRead = Math.Clamp(classic.MaxTagsPerRead, 1, 256),
            MaxTagsPerWrite = 256,
        };
        ApplyPriorityWorkProbe();
    }

    public string DriverId => IoTSiemensDriverFactory.Id;
    public ConnectionState State => _state;
    public event Action<ConnectionState>? StateChanged;
    public IAddressParser AddressParser { get; } = new SiemensScanAddressParser();
    public DriverLimits Limits { get; }
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        MaxReadBytes = _classic.MaxPayloadBytes,
        PartitionsReadRequests = true,
        // 引擎按这个标志决定要不要建变化订阅；漏了它，SubscribeAsync 一次都不会被调用。
        // 只在 S7-1200/1500 上声明：Classic 的绝对地址没有订阅机制，纯 Classic 设备
        // 声明了只会让引擎每轮白跑一次协调。原生库低于 ABI v4 时 SubscribeAsync 会给出
        // 空租约，调用方照常回退轮询。
        SupportsSubscriptions = _supportsPlus,
    };
    public ArrayDriverCapabilities ArrayCapabilities => _supportsPlus
        ? _plus.ArrayCapabilities : _classic.ArrayCapabilities;

    public void SetPriorityWorkProbe(Func<bool> priorityWorkPending)
    {
        _priorityWorkPending = priorityWorkPending ?? (() => false);
        ApplyPriorityWorkProbe();
    }

    private void ApplyPriorityWorkProbe()
    {
        if (_classic is IDriverPriorityWorkAware classic) classic.SetPriorityWorkProbe(_priorityWorkPending);
        if (_plus is IDriverPriorityWorkAware plus) plus.SetPriorityWorkProbe(_priorityWorkPending);
    }

    public Task ConnectAsync(CancellationToken ct) =>
        InSessionAsync(() => ConnectCoreAsync(ct), ct);

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        bool classicFallback = false;
        try
        {
            if (_supportsPlus && _classicPoint is null)
            {
                try
                {
                    await EnsurePlusConnectedAsync(ct);
                }
                catch (SiemensPlusException plusError) when (CanFallbackToClassic(plusError.Code))
                {
                    // S7-1200/1500 既可能是支持符号访问的真实 PLC，也可能是仅实现
                    // PUT/GET 的 Classic 模拟器。Plus 不可用时尝试 Classic；身份验证
                    // 与权限错误不能回退，否则会掩盖真实 PLC 的安全配置问题。
                    await _plus.DisconnectAsync();
                    if (_plus.IsConnected) throw new IOException("S7 Plus 尚未断开，禁止建立 S7 Classic 会话。");
                    await EnsureClassicAsync(ct);
                    classicFallback = true;
                    SetDetail(SiemensDeviceDetailState.PartialOnline, "S7 Plus 不可用，已使用 S7 Classic");
                }
            }
            else
                await EnsureClassicAsync(ct);
            if (!classicFallback)
                SetDetail(SiemensDeviceDetailState.Connected, "连接正常");
            SetState(ConnectionState.Connected);
        }
        catch (SiemensPlusException error)
        {
            SetPlusError(error.Code);
            SetState(ConnectionState.Faulted);
            throw;
        }
        catch
        {
            try { await Task.WhenAll(_classic.DisconnectAsync(), _plus.DisconnectAsync()); } catch { }
            SetDetail(SiemensDeviceDetailState.TransportFailure, "PLC 连接失败");
            SetState(ConnectionState.Faulted);
            throw;
        }
    }

    /// <summary>
    /// S7 Plus 变化订阅：PLC 按周期主动推送变化的值，符号点位不再周期轮询。
    ///
    /// 只有符号地址能订阅：Classic 的绝对地址没有订阅机制，那部分点位继续走轮询，
    /// 由运行引擎按返回的 TagIds 自行区分（没被接受的点位它会继续轮询）。
    /// 原生库低于 ABI v4、或一个符号都订阅不上时返回空租约，等于完全回退轮询。
    /// </summary>
    public async Task<IDriverSubscription> SubscribeAsync(
        IReadOnlyList<TagBinding> tags,
        Action<IReadOnlyList<TagSample>> onSamples,
        CancellationToken ct)
    {
        if (!_supportsPlus || tags.Count == 0) return new SiemensSubscriptionLease(null, []);
        var plan = SiemensRoutePlanner.Plan(tags, _supportsPlus);
        if (plan.Plus.Count == 0) return new SiemensSubscriptionLease(null, []);

        await InSessionAsync(async () =>
        {
            await EnsurePlusAndAuthenticationAsync(ct);
            return true;
        }, ct);

        // 订阅周期取这批点位里最小的采集周期：订阅只是把轮询换成推送，节奏应当一致
        int cycleMs = plan.Plus.Min(tag => Math.Max(50, tag.RequestedSamplingIntervalMs ?? tag.Def.ScanRateMs));
        var subscription = await _plus.SubscribeAsync(plan.Plus, cycleMs, onSamples, ct);
        return new SiemensSubscriptionLease(subscription, subscription?.AcceptedTagIds ?? []);
    }

    private sealed class SiemensSubscriptionLease(
        ISiemensPlusSubscription? subscription, IReadOnlyCollection<string> tagIds) : IDriverSubscription
    {
        public IReadOnlyCollection<string> TagIds => tagIds;

        public async ValueTask DisposeAsync()
        {
            if (subscription is not null) await subscription.DisposeAsync();
        }
    }

    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
        InSessionAsync(() => ReadCoreAsync(tags, ct), ct);

    private async Task<IReadOnlyList<TagSample>> ReadCoreAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (tags.Count == 0) return [];
        RegisterRoutes(tags.Select(t => (t.TagId, t.Def)));
        var plan = _readPlans.GetValue(tags, key => SiemensRoutePlanner.Plan((IReadOnlyList<TagBinding>)key, _supportsPlus));
        var classicTask = ReadClassicAsync(plan.Classic, ct);
        var plusTask = ReadPlusAsync(plan.Plus, ct);
        await Task.WhenAll(classicTask, plusTask);
        var values = (await classicTask).Concat(await plusTask).ToDictionary(x => x.TagId, StringComparer.Ordinal);
        // 这台设备读不了的点位(如 300/400 上配了符号地址)只标自己为坏值：用 BadNodeIdUnknown 交给
        // 坏点隔离(连续 3 次后隔离、30 秒复探一次)，既不拖累同批的好点，也不会让读取失败被
        // 当成连接问题而反复重连。
        if (plan.Unroutable.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var (binding, reason) in plan.Unroutable)
            {
                values[binding.TagId] = new TagSample(binding.TagId, DataValue.BadAt(StatusCode.BadNodeIdUnknown, now));
                _unroutableReasons[binding.TagId] = reason;
            }
        }
        UpdateAggregateState(plan.Classic.Count > 0, plan.Plus.Count > 0);
        if (values.Count > 0 && values.Values.All(sample => !sample.Value.IsGood)
            && _detailState == SiemensDeviceDetailState.TransportFailure)
            throw new IOException(_detailMessage);
        return tags.Select(tag => values[tag.TagId]).ToArray();
    }

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
        InSessionAsync(() => WriteCoreAsync(writes, ct), ct);

    private async Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        if (writes.Count == 0) return [];
        RegisterRoutes(writes.Select(w => (w.TagId, w.Def)));
        var classicWrites = new List<WriteRequest>();
        var plusWrites = new List<WriteRequest>();
        foreach (var write in writes)
        {
            var route = SiemensAddressDescriptor.Parse(write.Def.Address).Kind;
            if (route == SiemensAddressKind.Symbolic)
            {
                if (!_supportsPlus) throw new InvalidDataException($"点位 {write.TagId} 使用当前 PLC 不支持的 S7 Plus 符号地址。");
                plusWrites.Add(write);
            }
            else classicWrites.Add(write);
        }
        var classicTask = WriteClassicAsync(classicWrites, ct);
        var plusTask = WritePlusAsync(plusWrites, ct);
        await Task.WhenAll(classicTask, plusTask);
        var values = (await classicTask).Concat(await plusTask).ToDictionary(x => x.TagId, StringComparer.Ordinal);
        UpdateAggregateState(classicWrites.Count > 0, plusWrites.Count > 0);
        if (values.Count > 0 && values.Values.All(result => !result.Status.IsGood)
            && _detailState == SiemensDeviceDetailState.TransportFailure)
            throw new IOException(_detailMessage);
        return writes.Select(write => values[write.TagId]).ToArray();
    }

    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        InSessionAsync(() => WriteArrayCoreAsync(request, ct), ct);

    private async Task<ArrayWriteResult> WriteArrayCoreAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        RegisterRoutes([(request.Binding.TagId, request.Binding.Def)]);
        var route = SiemensAddressDescriptor.Parse(request.Binding.Def.Address).Kind;
        if (route == SiemensAddressKind.Symbolic)
        {
            if (!_supportsPlus)
            {
                SetDetail(SiemensDeviceDetailState.DbAddressMismatch,
                    "优化 DB/符号数组要求 S7 Plus；当前 PLC 型号仅启用了 S7 Classic。");
                return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, _detailMessage);
            }
            try
            {
                await EnsurePlusAndAuthenticationAsync(ct);
                return await _plus.WriteArrayAsync(request, ct);
            }
            catch (Exception ex)
            {
                SetDetail(SiemensDeviceDetailState.SymbolMismatch, $"S7 Plus 数组写入失败：{ex.Message}");
                return ArrayWriteResult.Failed(StatusCode.BadDeviceFailure, ex.Message);
            }
        }
        try
        {
            await EnsureClassicAsync(ct);
            return await _classic.WriteArrayAsync(request, ct);
        }
        catch (Exception ex)
        {
            SetDetail(SiemensDeviceDetailState.TransportFailure, $"S7 Classic 数组写入失败：{ex.Message}");
            return ArrayWriteResult.Failed(StatusCode.BadCommFailure, ex.Message);
        }
    }

    public Task<BrowseResult> BrowseAsync(CancellationToken ct) =>
        InSessionAsync(() => BrowseCoreAsync(ct), ct);

    private async Task<BrowseResult> BrowseCoreAsync(CancellationToken ct)
    {
        if (!_supportsPlus || _classicPoint is not null) throw new NotSupportedException("当前设备使用 S7 Classic，不能在同一设备会话浏览 S7 Plus 符号地址。");
        await EnsurePlusAndAuthenticationAsync(ct);
        var variables = await _plus.BrowseAsync(ct);
        const int limit = 100_000;
        var tags = variables.Take(limit).Select(variable =>
        {
            var dot = variable.Symbol.LastIndexOf('.');
            return new BrowsedTag
            {
                Name = dot >= 0 ? variable.Symbol[(dot + 1)..] : variable.Symbol,
                Address = variable.Symbol,
                Path = dot >= 0 ? variable.Symbol[..dot] : string.Empty,
                DataType = MapSoftDataType(variable.SoftDataType),
                IsArray = false,
            };
        }).ToArray();
        return new BrowseResult(tags, variables.Count > limit);
    }

    /// <summary>
    /// 在线列出数据块(块名 → DB 号)，导入不带 DB 号的 DB 源文件时用。只有 S7-1200/1500(S7 Plus)能列。
    /// 设备本身走 S7 Plus 时借用现有会话；走 S7 Classic(非优化 DB 的绝对地址)时同一会话不能切到 Plus，
    /// 临时另开一条 Plus 连接，列完立即断开，不影响设备正在进行的采集。
    /// </summary>
    public async Task<IReadOnlyList<DataBlockInfo>?> ListDataBlocksAsync(CancellationToken ct)
    {
        if (!_supportsPlus) return null;
        IReadOnlyList<SiemensPlusNativeDataBlock>? blocks;
        if (_classicPoint is null)
            blocks = await InSessionAsync(async () =>
            {
                await EnsurePlusAndAuthenticationAsync(ct);
                return await _plus.ListDataBlocksAsync(ct);
            }, ct);
        else
        {
            if (_plusFactory is null) return null;
            await using var temporary = _plusFactory();
            await temporary.ConnectAsync(_host, _port, PlusTimeout, _legacyPlus, ct);
            try
            {
                if (_credentialResolver is not null && await _credentialResolver.ResolveAsync(ct) is { } credential)
                    await temporary.AuthenticateAsync(credential.Username, credential.Password, ct);
                blocks = await temporary.ListDataBlocksAsync(ct);
            }
            finally { await temporary.DisconnectAsync(); }
        }
        return blocks?.Select(block => new DataBlockInfo(block.Name, block.Number)).ToArray();
    }

    public Task DisconnectAsync() => InSessionAsync(DisconnectCoreAsync, CancellationToken.None);

    private async Task DisconnectCoreAsync()
    {
        await Task.WhenAll(_classic.DisconnectAsync(), _plus.DisconnectAsync());
        _authenticated = false;
        SetDetail(SiemensDeviceDetailState.None, "未连接");
        SetState(ConnectionState.Disconnected);
    }

    public Task ResetConnectionAsync(CancellationToken ct) =>
        InSessionAsync(() => ResetConnectionCoreAsync(ct), ct);

    private async Task ResetConnectionCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_classicFactory is null || _plusFactory is null)
        {
            await DisconnectCoreAsync();
            return;
        }

        var oldClassic = _classic;
        var oldPlus = _plus;
        await oldClassic.DisposeAsync();
        await oldPlus.DisposeAsync();
        ct.ThrowIfCancellationRequested();
        _classic = _classicFactory();
        _plus = _plusFactory();
        ApplyPriorityWorkProbe();
        _authenticated = false;
        SetDetail(SiemensDeviceDetailState.None, "未连接");
        SetState(ConnectionState.Disconnected);
    }

    public SiemensConnectionDetail Snapshot() => new(
        _detailState,
        _detailMessage,
        _classic.IsConnected,
        _plus.IsConnected);

    public DeviceConnectionDetail ConnectionDetail() => new(_detailState.ToString(), _detailMessage);

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await _classic.DisposeAsync(); }
            finally { await _plus.DisposeAsync(); }
            SetState(ConnectionState.Disconnected);
        }
        finally { _sessionGate.Release(); }
    }

    // Hold one device lane through both ensure and use. No operation can capture/reopen an
    // old transport while reset disposes and replaces the published generation.
    private async Task InSessionAsync(Func<Task> action, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await action();
        }
        finally { _sessionGate.Release(); }
    }

    private async Task<T> InSessionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await action();
        }
        finally { _sessionGate.Release(); }
    }

    private async Task<IReadOnlyList<TagSample>> ReadClassicAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (tags.Count == 0) return [];
        try
        {
            await EnsureClassicAsync(ct);
            var samples = await _classic.ReadAsync(tags, ct);
            var badIds = samples.Where(sample => !sample.Value.IsGood).Select(sample => sample.TagId)
                .ToHashSet(StringComparer.Ordinal);
            if (badIds.Count > 0)
            {
                string[] dbs = tags.Where(tag => badIds.Contains(tag.TagId)).Select(tag => DataAreaName(tag.Def.Address)).Where(db => db.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
                string affected = dbs.Length == 0 ? "配置的地址" : string.Join("、", dbs);
                bool allBad = badIds.Count == samples.Count;
                string transportError = string.IsNullOrWhiteSpace(_classic.LastError) ? "" : $" 驱动返回：{_classic.LastError}。";
                SetDetail(allBad ? SiemensDeviceDetailState.DbAddressMismatch : SiemensDeviceDetailState.PartialOnline,
                    allBad
                        ? $"S7 Classic 已连接，但 {affected} 的 {badIds.Count} 个点位全部读取失败。{transportError}{AllBadAdvice(dbs)}"
                        : $"S7 Classic 已连接，{affected} 有 {badIds.Count}/{samples.Count} 个点位读取异常。{transportError}其余 Good 点位仍可供 QC 和 MES 使用。");
            }
            else if (_detailState is SiemensDeviceDetailState.DbAddressMismatch or SiemensDeviceDetailState.PartialOnline)
                SetDetail(SiemensDeviceDetailState.Connected, "S7 Classic 通道正常");
            return samples;
        }
        catch (DriverReadYieldException) { throw; }
        catch (TimeoutException ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Classic 单次读取超时，会话保持在线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadTimeout);
        }
        catch (IOException ex)
        {
            try { await _classic.DisconnectAsync(); } catch { }
            SetDetail(SiemensDeviceDetailState.TransportFailure, $"S7 Classic 通道离线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadCommFailure);
        }
        catch (Exception ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Classic 数据块读取失败，会话保持在线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadDeviceFailure);
        }
    }

    private async Task<IReadOnlyList<TagSample>> ReadPlusAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        if (tags.Count == 0) return [];
        try
        {
            bool sessionWasOpen = _plus.IsConnected;
            await EnsurePlusAndAuthenticationAsync(ct);
            IReadOnlyList<TagSample> samples;
            try { samples = await _plus.ReadAsync(tags, ct); }
            catch (SiemensPlusException error) when (
                error.Code == SiemensPlusResultCode.TransportFailure && sessionWasOpen && !ct.IsCancellationRequested)
            {
                // 已经在用的会话被 PLC 掐断(现场 S7-1500 上纯轮询会话约 60 秒一断)：换新会话把这批再读一次。
                // 读取幂等，重读不会有副作用；否则整批被标 BadCommFailure、设备记一次连接失效，
                // 外部 /Tag 读取碰上也整批失败。只重试一次，新会话上再失败按下面原有流程处理。
                try { await _plus.DisconnectAsync(); } catch { }
                await EnsurePlusAndAuthenticationAsync(ct);
                samples = await _plus.ReadAsync(tags, ct);
            }
            if (!string.IsNullOrWhiteSpace(_plus.LastReadError))
                SetDetail(SiemensDeviceDetailState.PartialOnline, _plus.LastReadError);
            else if (samples.All(sample => sample.Value.IsGood) && _detailState == SiemensDeviceDetailState.PartialOnline)
                SetDetail(SiemensDeviceDetailState.Connected, PlusChannelHealthy());
            return samples;
        }
        catch (DriverReadYieldException) { throw; }
        catch (SiemensPlusException error)
        {
            if (error.Code == SiemensPlusResultCode.Timeout)
            {
                SetDetail(SiemensDeviceDetailState.PartialOnline,
                    "S7 Plus 单次读取超时，会话保持在线");
                return BadSamples(tags, StatusCode.BadTimeout);
            }
            SetPlusError(error.Code);
            if (error.Code == SiemensPlusResultCode.TransportFailure)
                try { await _plus.DisconnectAsync(); } catch { }
            return BadSamples(tags,
                error.Code == SiemensPlusResultCode.TransportFailure
                    ? StatusCode.BadCommFailure
                    : StatusCode.BadDeviceFailure);
        }
        catch (TimeoutException ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Plus 单次读取超时，会话保持在线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadTimeout);
        }
        catch (IOException ex)
        {
            try { await _plus.DisconnectAsync(); } catch { }
            SetDetail(SiemensDeviceDetailState.TransportFailure, $"S7 Plus 通道离线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadCommFailure);
        }
        catch (Exception ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Plus 点位读取失败，会话保持在线：{ex.Message}");
            return BadSamples(tags, StatusCode.BadDeviceFailure);
        }
    }

    private async Task<IReadOnlyList<WriteResult>> WriteClassicAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        if (writes.Count == 0) return [];
        try { await EnsureClassicAsync(ct); return await _classic.WriteAsync(writes, ct); }
        catch (TimeoutException ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Classic 单次写入超时，会话保持在线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId, StatusCode.BadTimeout)).ToArray();
        }
        catch (IOException ex)
        {
            try { await _classic.DisconnectAsync(); } catch { }
            SetDetail(SiemensDeviceDetailState.TransportFailure, $"S7 Classic 写入通道离线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId, StatusCode.BadCommFailure)).ToArray();
        }
        catch (Exception ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Classic 点位写入失败，会话保持在线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId)).ToArray();
        }
    }

    private async Task<IReadOnlyList<WriteResult>> WritePlusAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        if (writes.Count == 0) return [];
        try { await EnsurePlusAndAuthenticationAsync(ct); return await _plus.WriteAsync(writes, ct); }
        catch (SiemensPlusException error)
        {
            if (error.Code == SiemensPlusResultCode.Timeout)
            {
                SetDetail(SiemensDeviceDetailState.PartialOnline,
                    "S7 Plus 单次写入超时，会话保持在线");
                return writes.Select(x => BadWrite(x.TagId, StatusCode.BadTimeout)).ToArray();
            }
            SetPlusError(error.Code);
            if (error.Code == SiemensPlusResultCode.TransportFailure)
                try { await _plus.DisconnectAsync(); } catch { }
            return writes.Select(x => BadWrite(x.TagId,
                error.Code == SiemensPlusResultCode.TransportFailure
                    ? StatusCode.BadCommFailure
                    : StatusCode.BadDeviceFailure)).ToArray();
        }
        catch (TimeoutException ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Plus 单次写入超时，会话保持在线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId, StatusCode.BadTimeout)).ToArray();
        }
        catch (IOException ex)
        {
            try { await _plus.DisconnectAsync(); } catch { }
            SetDetail(SiemensDeviceDetailState.TransportFailure, $"S7 Plus 写入通道离线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId, StatusCode.BadCommFailure)).ToArray();
        }
        catch (Exception ex)
        {
            SetDetail(SiemensDeviceDetailState.PartialOnline,
                $"S7 Plus 点位写入失败，会话保持在线：{ex.Message}");
            return writes.Select(x => BadWrite(x.TagId)).ToArray();
        }
    }

    internal static void ValidateConfiguration(IEnumerable<(string Path, TagDef Def)> points)
    {
        (string Path, TagDef Def)? classic = null, plus = null;
        foreach (var point in points)
        {
            if (SiemensAddressDescriptor.Parse(point.Def.Address).Kind == SiemensAddressKind.Classic) classic ??= point;
            else plus ??= point;
        }
        ThrowIfMixed(classic, plus);
    }

    private void RegisterRoutes(IEnumerable<(string Path, TagDef Def)> points)
    {
        lock (_routeSync)
        {
            var classic = _classicPoint;
            var plus = _plusPoint;
            foreach (var point in points)
            {
                if (SiemensAddressDescriptor.Parse(point.Def.Address).Kind == SiemensAddressKind.Classic) classic ??= point;
                else plus ??= point;
            }
            ThrowIfMixed(classic, plus);
            _classicPoint = classic;
            _plusPoint = plus;
        }
    }

    private static void ThrowIfMixed((string Path, TagDef Def)? classic, (string Path, TagDef Def)? plus)
    {
        if (classic is {} c && plus is {} p)
            throw new InvalidDataException($"设备配置冲突：S7 Classic 点位 {c.Path} ({c.Def.Address}) 与 S7 Plus 符号点位 {p.Path} ({p.Def.Address}) 不能属于同一设备会话；请配置为独立设备。");
    }

    // These helpers run only while the caller holds _sessionGate.
    private async Task EnsureClassicAsync(CancellationToken ct)
    {
        if (_plus.IsConnected)
        {
            await _plus.DisconnectAsync();
            _authenticated = false;
            if (_plus.IsConnected) throw new IOException("S7 Plus 尚未断开，禁止建立 S7 Classic 会话。");
        }
        if (!_classic.IsConnected) await _classic.ConnectAsync(ct);
    }

    private async Task EnsurePlusConnectedAsync(CancellationToken ct)
    {
        if (_classic.IsConnected)
        {
            await _classic.DisconnectAsync();
            if (_classic.IsConnected) throw new IOException("S7 Classic 尚未断开，禁止建立 S7 Plus 会话。");
        }
        if (!_plus.IsConnected)
        {
            _authenticated = false;   // 新会话(含传输层为程序变更自愈而主动断开后的重连)都要重新认证
            await _plus.ConnectAsync(_host, _port, PlusTimeout, _legacyPlus, ct);
        }
    }

    private async Task EnsurePlusAndAuthenticationAsync(CancellationToken ct)
    {
        await EnsurePlusConnectedAsync(ct);
        if (_authenticated || _credentialResolver is null) return;
        var credential = await _credentialResolver.ResolveAsync(ct);
        if (credential is null) return;
        try
        {
            await _plus.AuthenticateAsync(credential.Username, credential.Password, ct);
            _authenticated = true;
        }
        finally
        {
            credential = null;
        }
    }

    private static bool CanFallbackToClassic(SiemensPlusResultCode code) => code is
        SiemensPlusResultCode.Timeout or
        SiemensPlusResultCode.TransportFailure or
        SiemensPlusResultCode.ProtocolFailure or
        SiemensPlusResultCode.UnsupportedFirmware;

    private void UpdateAggregateState(bool requiresClassic, bool requiresPlus)
    {
        if (_detailState is SiemensDeviceDetailState.AuthenticationRequired or SiemensDeviceDetailState.AuthenticationFailed or
            SiemensDeviceDetailState.PermissionDenied or SiemensDeviceDetailState.DbAddressMismatch or SiemensDeviceDetailState.PartialOnline or
            SiemensDeviceDetailState.SymbolMismatch or SiemensDeviceDetailState.UnsupportedFirmware)
        {
            SetState(_classic.IsConnected || _plus.IsConnected ? ConnectionState.Connected : ConnectionState.Faulted);
            return;
        }
        if (requiresClassic && !requiresPlus)
        {
            if (_classic.IsConnected)
            {
                SetDetail(SiemensDeviceDetailState.Connected, "S7 Classic 通道正常");
                SetState(ConnectionState.Connected);
            }
            else SetState(ConnectionState.Faulted);
            return;
        }
        if (requiresPlus && !requiresClassic)
        {
            if (_plus.IsConnected)
            {
                SetDetail(SiemensDeviceDetailState.Connected, PlusChannelHealthy());
                SetState(ConnectionState.Connected);
            }
            else SetState(ConnectionState.Faulted);
            return;
        }
        if (_classic.IsConnected && _plus.IsConnected)
            SetDetail(SiemensDeviceDetailState.Connected, "Classic 与 Plus 通道正常");
        else if (_classic.IsConnected || _plus.IsConnected)
            SetDetail(SiemensDeviceDetailState.PartialOnline, "PLC 部分数据通道可用");
        SetState(_classic.IsConnected || _plus.IsConnected ? ConnectionState.Connected : ConnectionState.Faulted);
    }

    private void SetPlusError(SiemensPlusResultCode code)
    {
        var state = code switch
        {
            SiemensPlusResultCode.AuthenticationRequired => SiemensDeviceDetailState.AuthenticationRequired,
            SiemensPlusResultCode.AuthenticationFailed => SiemensDeviceDetailState.AuthenticationFailed,
            SiemensPlusResultCode.PermissionDenied => SiemensDeviceDetailState.PermissionDenied,
            SiemensPlusResultCode.UnsupportedFirmware => SiemensDeviceDetailState.UnsupportedFirmware,
            // ProtocolFailure 是「PLC 没回这一项」这类批次级问题，不是协议层不兼容：
            // 标成 SymbolMismatch 会在界面上显示「DB/协议不匹配」，把人往改型号/改地址上带偏。
            // 真正的协议不兼容是 UnsupportedFirmware，那条仍然单独映射。
            SiemensPlusResultCode.ProtocolFailure => SiemensDeviceDetailState.PartialOnline,
            _ => SiemensDeviceDetailState.TransportFailure,
        };
        SetDetail(state, new SiemensPlusException(code).Message);
    }

    private static IgwDataType MapSoftDataType(byte value) => value switch
    {
        1 => IgwDataType.Bool,
        2 or 52 => IgwDataType.Byte,
        3 or 55 => IgwDataType.Char,
        4 or 53 => IgwDataType.UInt16,
        5 => IgwDataType.Int16,
        6 or 54 => IgwDataType.UInt32,
        7 => IgwDataType.Int32,
        8 => IgwDataType.Float,
        19 => IgwDataType.String,
        48 => IgwDataType.Double,
        49 or 51 or 65 or 66 => IgwDataType.UInt64,
        50 or 64 => IgwDataType.Int64,
        61 or 62 => IgwDataType.WString,
        _ => IgwDataType.ByteArray,
    };

    /// <summary>全部读失败时的处置建议：过程映像区没有 DB、也不涉及优化 DB，该查的是硬件组态里的 I/Q 大小。</summary>
    private static string AllBadAdvice(IReadOnlyList<string> areas) =>
        areas.Count > 0 && areas.All(a => !a.StartsWith("DB", StringComparison.Ordinal))
            ? "请检查这些地址是否在 PLC 硬件组态的过程映像区范围内，以及是否允许 PUT/GET。"
            : "请检查 DB 是否存在、是否允许 PUT/GET、是否为优化 DB，以及地址/长度是否与 PLC 一致。";

    /// <summary>
    /// 诊断里用来指认「哪块数据区出的问题」。DB 区给 DB 号；过程映像区(I/Q/M/V)没有 DB 的概念，
    /// 给区域名——否则诊断只能笼统说「配置的地址」，还会顺带建议去查「优化 DB」，对这些点位是误导。
    /// </summary>
    internal static string DataAreaName(string address)
    {
        var match = System.Text.RegularExpressions.Regex.Match(address ?? "", @"\bDB(?<n>\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return "DB" + match.Groups["n"].Value;
        var area = System.Text.RegularExpressions.Regex.Match(address ?? "", @"^(?<a>[IQEAMV])(?:[XBWD])?\d",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!area.Success) return "";
        return char.ToUpperInvariant(area.Groups["a"].Value[0]) switch
        {
            'I' or 'E' => "输入区(I)",
            'Q' or 'A' => "输出区(Q)",
            'M' => "位存储区(M)",
            'V' => "变量区(V)",
            _ => "",
        };
    }

    private void SetDetail(SiemensDeviceDetailState state, string message) { _detailState = state; _detailMessage = message; }

    /// <summary>
    /// S7 Plus 通道正常时的诊断文案，带上实际加载的原生组件 ABI 版本。
    /// 原生库(igw_s7plus.dll / libigw_s7plus.so)是单独编译分发的，容器/现场很容易只换了网关没换它；
    /// 低于随附版本时逐项坏点隔离等能力不生效，一个解析不了的符号会把同一批点一起拖成坏值，
    /// 因此必须在设备诊断里直接看得到，而不是靠翻代码猜。
    /// </summary>
    private string PlusChannelHealthy()
    {
        uint abi = _plus.NativeAbiVersion;
        if (abi == 0) return "S7 Plus 通道正常";
        if (abi < SiemensPlusNative.AbiVersion)
            return $"S7 Plus 通道正常（原生组件 ABI v{abi}，低于随附的 v{SiemensPlusNative.AbiVersion}：" +
                   "坏符号不会被单点隔离，会连带同批点位变坏值，请用完整安装包/镜像一起升级）";
        return $"S7 Plus 通道正常（原生组件 ABI v{abi}）";
    }
    private void SetState(ConnectionState state) { if (_state == state) return; _state = state; StateChanged?.Invoke(state); }
    private static IReadOnlyList<TagSample> BadSamples(IReadOnlyList<TagBinding> tags, StatusCode code)
    {
        var now = DateTime.UtcNow;
        // 数组点位用「全 Bad 数组壳」占位,保持数组形状与元素级质量,
        // 让监控/切片接口能展示 Bad 网格而不是 409(尚无运行数据)。
        return tags.Select(x => new TagSample(x.TagId, BadValue(x.Def, code, now))).ToArray();
    }

    private static DataValue BadValue(TagDef def, StatusCode code, DateTime now)
    {
        var shell = def.Array is null ? null : ArrayBadShell.CreateBadShell(def.Array, code);
        return shell is null ? DataValue.BadAt(code, now) : new DataValue(shell, code, now, now);
    }
    private static WriteResult BadWrite(string tagId, StatusCode? status = null) =>
        new() { TagId = tagId, Status = status ?? StatusCode.BadDeviceFailure };
}
