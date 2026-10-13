using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using IGW.Abstractions;
using MQTTnet;
using MQTTnet.Client;

namespace IGW.Drivers.Mqtt;

/// <summary>MQTT 地址 = 订阅主题(如 plant/line1/temp)。负载按数据类型解析为标量。</summary>
public sealed class MqttAddressParser : IAddressParser
{
    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = new TagAddress(0, 0, 1);
        error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = "空地址(应为 MQTT 主题,如 plant/line1/temp)"; return false; }
        return true;
    }
}

/// <summary>MQTT Client 驱动工厂(订阅 broker 主题作为数据来源)。对标 KEP「MQTT Client」。</summary>
public sealed class MqttDriverFactory : IDriverFactory
{
    public const string Id = "Mqtt.Client";
    public string DriverId => Id;

    public DriverDescriptor Describe() => new()
    {
        DriverId = Id,
        DisplayName = "MQTT Client",
        AddressHelp = "MQTT 主题:如 plant/line1/temp。负载按标记数据类型解析(数字/布尔/字符串)。可写标记会把值发布到该主题。",
        ChannelSettings = new[]
        {
            new DriverSetting { Key = "username", Label = "用户名", Default = "" },
            new DriverSetting { Key = "password", Label = "密码", Default = "" },
            new DriverSetting { Key = "clientId", Label = "客户端 ID(留空自动)", Default = "" },
        },
        DeviceSettings = Array.Empty<DriverSetting>(),
    };

    /// <summary>设备端点 = broker host[:port](默认 1883)。</summary>
    public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device)
    {
        string host = (device.Endpoint ?? "").Trim();
        int port = 1883;
        int colon = host.IndexOf(':');
        if (colon > 0) { if (int.TryParse(host[(colon + 1)..], out var p) && p > 0) port = p; host = host[..colon]; }
        string user = channel.Settings.GetValueOrDefault("username", "");
        string pass = channel.Settings.GetValueOrDefault("password", "");
        string clientId = channel.Settings.GetValueOrDefault("clientId", "");
        if (string.IsNullOrWhiteSpace(clientId)) clientId = "IGW-" + Guid.NewGuid().ToString("N")[..8];
        return new MqttDeviceDriver(host, port, user, pass, clientId);
    }
}

/// <summary>MQTT 设备驱动:连 broker、订阅各标记主题、缓存最新负载;ReadAsync 返回缓存,WriteAsync 发布。</summary>
public sealed class MqttDeviceDriver : IDeviceDriver, IArrayCapableDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
{
    private readonly string _host, _user, _pass, _clientId;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, string> _cache = new(); // topic -> 最新负载
    private readonly HashSet<string> _remoteSubscribed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NativeRegistration>> _nativeByTopic = new(StringComparer.Ordinal);
    private readonly object _subLock = new();
    private readonly SemaphoreSlim _remoteSubscriptionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _pendingMessages = new(StringComparer.Ordinal);
    private readonly Channel<byte> _dispatchSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _dispatchLoop;
    private long _nextNativeLeaseId;
    private int _disposed;
    private IMqttClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public string DriverId => MqttDriverFactory.Id;
    public IAddressParser AddressParser { get; } = new MqttAddressParser();
    public DriverLimits Limits { get; } = new DriverLimits { MaxReadUnits = 1, MaxGap = 0, MaxTagsPerRead = 1_024, MaxTagsPerWrite = 1_024 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
    {
        SupportsBatchRead = false,
        SupportsBatchWrite = false,
        SupportsSubscriptions = true,
        MaxInFlight = 4,
        PartitionsReadRequests = true,
        SuggestedVerificationDelayMs = 20,
    };
    public ConnectionState State => _state;
    public event Action<ConnectionState>? StateChanged;
    public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
        NativeArrays: true, StructuredArrays: true, MultiDimensional: true,
        SliceRead: true, SliceWrite: true, AtomicWholeWrite: true,
        MaxElementsPerRequest: 1_000_000, MaxBytesPerRequest: 16 * 1024 * 1024);

    public MqttDeviceDriver(string host, int port, string user, string pass, string clientId)
    {
        _host = host; _port = port; _user = user; _pass = pass; _clientId = clientId;
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    private sealed record NativeRegistration(
        long LeaseId,
        TagBinding Binding,
        Action<IReadOnlyList<TagSample>> Callback);

    private void SetState(ConnectionState s) { if (_state == s) return; _state = s; StateChanged?.Invoke(s); }

    public async Task ConnectAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        try
        {
            _client = new MqttFactory().CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnApplicationMessageAsync;
            var ob = new MqttClientOptionsBuilder().WithTcpServer(_host, _port).WithClientId(_clientId).WithCleanSession();
            if (!string.IsNullOrEmpty(_user)) ob = ob.WithCredentials(_user, _pass);
            await _client.ConnectAsync(ob.Build(), ct);
            string[] desiredTopics;
            lock (_subLock)
            {
                _remoteSubscribed.Clear();
                desiredTopics = _scanTopics.Concat(_nativeByTopic.Keys).Distinct(StringComparer.Ordinal).ToArray();
            }
            SetState(ConnectionState.Connected);
            foreach (var topic in desiredTopics) await EnsureTopicSubscribedAsync(topic, ct);
        }
        catch { SetState(ConnectionState.Faulted); _client?.Dispose(); _client = null; throw; }
    }

    private Task OnApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var seg = e.ApplicationMessage.PayloadSegment;
        string payload = seg.Array != null
            ? Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count)
            : "";
        string topic = e.ApplicationMessage.Topic;
        _cache[topic] = payload;
        _pendingMessages[topic] = payload;
        _dispatchSignal.Writer.TryWrite(0);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        await _remoteSubscriptionGate.WaitAsync();
        try
        {
            var client = Interlocked.Exchange(ref _client, null);
            try { client?.Dispose(); } catch { }
            lock (_subLock) _remoteSubscribed.Clear();
            SetState(ConnectionState.Disconnected);
        }
        finally { _remoteSubscriptionGate.Release(); }
    }

    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_client is null || !_client.IsConnected) throw new IOException("MQTT 未连接");

        // 周期读取继续保留自己的主题需求；原生实时租约释放时不能误删周期采集订阅。
        List<string> topics = new();
        lock (_subLock)
            foreach (var t in tags)
            {
                var topic = t.Def.Address.Trim();
                _scanTopics.Add(topic);
                topics.Add(topic);
            }
        foreach (var topic in topics.Distinct(StringComparer.Ordinal))
            try { await EnsureTopicSubscribedAsync(topic, ct); } catch { }

        var samples = new List<TagSample>(tags.Count);
        foreach (var t in tags)
        {
            var topic = t.Def.Address.Trim();
            if (_cache.TryGetValue(topic, out var payload))
                samples.Add(DecodeSample(t, payload, now));
            else samples.Add(new TagSample(t.TagId, new DataValue(null, StatusCode.Uncertain, now, now))); // 尚无消息
        }
        return samples;
    }

    public async Task<IDriverSubscription> SubscribeAsync(
        IReadOnlyList<TagBinding> tags,
        Action<IReadOnlyList<TagSample>> onSamples,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(onSamples);
        ct.ThrowIfCancellationRequested();
        if (_client is null || !_client.IsConnected) throw new IOException("MQTT 未连接");

        var accepted = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Def.Address))
            .DistinctBy(tag => tag.TagId, StringComparer.Ordinal)
            .ToArray();
        long leaseId = Interlocked.Increment(ref _nextNativeLeaseId);
        lock (_subLock)
        {
            foreach (var binding in accepted)
            {
                string topic = binding.Def.Address.Trim();
                if (!_nativeByTopic.TryGetValue(topic, out var registrations))
                    _nativeByTopic[topic] = registrations = new List<NativeRegistration>();
                registrations.Add(new NativeRegistration(leaseId, binding, onSamples));
            }
        }

        try
        {
            foreach (var topic in accepted.Select(tag => tag.Def.Address.Trim()).Distinct(StringComparer.Ordinal))
                await EnsureTopicSubscribedAsync(topic, ct);
            return new NativeSubscription(this, leaseId, accepted.Select(tag => tag.TagId).ToArray());
        }
        catch
        {
            await RemoveNativeSubscriptionAsync(leaseId);
            throw;
        }
    }

    private async Task EnsureTopicSubscribedAsync(string topic, CancellationToken ct)
    {
        await _remoteSubscriptionGate.WaitAsync(ct);
        try
        {
            lock (_subLock)
                if (_remoteSubscribed.Contains(topic)) return;
            var client = _client;
            if (client is null || !client.IsConnected) throw new IOException("MQTT 未连接");
            await client.SubscribeAsync(topic).WaitAsync(ct);
            lock (_subLock) _remoteSubscribed.Add(topic);
        }
        finally { _remoteSubscriptionGate.Release(); }
    }

    private async ValueTask RemoveNativeSubscriptionAsync(long leaseId)
    {
        List<string> releaseRemote = new();
        lock (_subLock)
        {
            foreach (var (topic, registrations) in _nativeByTopic.ToArray())
            {
                registrations.RemoveAll(registration => registration.LeaseId == leaseId);
                if (registrations.Count > 0) continue;
                _nativeByTopic.Remove(topic);
                if (!_scanTopics.Contains(topic) && _remoteSubscribed.Contains(topic))
                    releaseRemote.Add(topic);
            }
        }

        foreach (var topic in releaseRemote) await UnsubscribeTopicAsync(topic);
    }

    private async Task UnsubscribeTopicAsync(string topic)
    {
        await _remoteSubscriptionGate.WaitAsync();
        try
        {
            var client = _client;
            if (client is not null && client.IsConnected)
                try { await client.UnsubscribeAsync(topic).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* 本地注册已删除；重连会从权威集合重新订阅。 */ }
            lock (_subLock) _remoteSubscribed.Remove(topic);
        }
        finally { _remoteSubscriptionGate.Release(); }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            while (await _dispatchSignal.Reader.WaitToReadAsync(_lifetime.Token))
            {
                while (_dispatchSignal.Reader.TryRead(out _)) { }
                while (!_pendingMessages.IsEmpty)
                {
                    foreach (string topic in _pendingMessages.Keys.ToArray())
                        if (_pendingMessages.TryRemove(topic, out string? payload))
                            DispatchTopic(topic, payload);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void DispatchTopic(string topic, string payload)
    {
        NativeRegistration[] registrations;
        lock (_subLock)
            registrations = _nativeByTopic.TryGetValue(topic, out var registered)
                ? registered.ToArray()
                : [];
        if (registrations.Length == 0) return;

        var now = DateTime.UtcNow;
        var decoded = new Dictionary<TagDef, DataValue>(ReferenceEqualityComparer.Instance);
        foreach (var lease in registrations.GroupBy(registration => registration.LeaseId))
        {
            var samples = new List<TagSample>();
            foreach (var registration in lease)
            {
                if (!decoded.TryGetValue(registration.Binding.Def, out var value))
                {
                    value = DecodeValue(payload, registration.Binding.Def, now);
                    decoded.Add(registration.Binding.Def, value);
                }
                samples.Add(new TagSample(registration.Binding.TagId, value));
            }
            try { lease.First().Callback(samples); }
            catch { /* 实时消费者异常不得中断 MQTT 消息分发。 */ }
        }
    }

    private static TagSample DecodeSample(TagBinding binding, string payload, DateTime now) =>
        new(binding.TagId, DecodeValue(payload, binding.Def, now));

    private static DataValue DecodeValue(string payload, TagDef definition, DateTime now)
    {
        try
        {
            if (definition.Array is not null)
            {
                var array = MqttArrayCodec.Decode(payload, definition, StatusCode.Good);
                return new DataValue(array, array.OverallQuality, now, now);
            }
            var value = Parse(payload, definition.DataType);
            return value is null
                ? new DataValue(null, StatusCode.BadDecodeError, now, now)
                : DataValue.GoodAt(value, now);
        }
        catch { return new DataValue(null, StatusCode.BadDecodeError, now, now); }
    }

    private sealed class NativeSubscription : IDriverSubscription
    {
        private MqttDeviceDriver? _owner;
        private readonly long _leaseId;

        public NativeSubscription(MqttDeviceDriver owner, long leaseId, IReadOnlyCollection<string> tagIds)
        {
            _owner = owner;
            _leaseId = leaseId;
            TagIds = tagIds;
        }

        public IReadOnlyCollection<string> TagIds { get; }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            return owner is null || Volatile.Read(ref owner._disposed) != 0
                ? ValueTask.CompletedTask
                : owner.RemoveNativeSubscriptionAsync(_leaseId);
        }
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        var results = new List<WriteResult>(writes.Count);
        if (_client is null || !_client.IsConnected)
        {
            foreach (var w in writes) results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.BadNotConnected });
            return results;
        }
        foreach (var w in writes)
        {
            try
            {
                var payload = Convert.ToString(w.Value, CultureInfo.InvariantCulture) ?? "";
                await ProtocolOperationTiming.MeasureAsync(() => _client.PublishStringAsync(w.Def.Address.Trim(), payload, cancellationToken: ct),
                    new("Publish", w.Def.Address.Trim(), 1, System.Text.Encoding.UTF8.GetByteCount(payload), 0));
                results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Good });
            }
            catch (OperationCanceledException) { throw; }
            catch { results.Add(new WriteResult { TagId = w.TagId, Status = StatusCode.Bad }); }
        }
        return results;
    }

    public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected)
            return ArrayWriteResult.Failed(StatusCode.BadNotConnected, "MQTT 未连接");
        if (request.Binding.Def.Array is null)
            return ArrayWriteResult.Failed(StatusCode.Bad, "点位不是数组");

        var topic = request.Binding.Def.Address.Trim();
        try
        {
            ArrayRuntimeValue value = request.Value;
            if (!IsWhole(request.Binding.Def.Array.Shape, request.Slice))
            {
                if (!_cache.TryGetValue(topic, out var payload))
                    return ArrayWriteResult.Failed(StatusCode.Uncertain,
                        "MQTT 部分写入前尚未收到完整数组快照");
                var whole = MqttArrayCodec.Decode(payload, request.Binding.Def, StatusCode.Good);
                value = MqttArrayCodec.ReplaceSlice(whole, request.Slice, request.Value);
            }

            var json = MqttArrayCodec.Encode(value);
            await ProtocolOperationTiming.MeasureAsync(() => _client.PublishStringAsync(topic, json, cancellationToken: ct),
                new("Publish", topic, 1, System.Text.Encoding.UTF8.GetByteCount(json), 0));
            _cache[topic] = json;
            return new ArrayWriteResult(StatusCode.Good, request.Value.Count, []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ArrayWriteResult.Failed(StatusCode.Bad, ex.Message);
        }
    }

    private static bool IsWhole(ArrayShape shape, ArraySlice slice)
    {
        if (slice.Start.Count != shape.Rank || slice.Lengths.Count != shape.Rank) return false;
        for (var i = 0; i < shape.Rank; i++)
            if (slice.Start[i] != shape.LowerBounds[i] || slice.Lengths[i] != shape.Dimensions[i])
                return false;
        return true;
    }

    private static object? Parse(string s, IgwDataType t)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        try
        {
            switch (t)
            {
                case IgwDataType.String:
                case IgwDataType.WString: return s;   // WString 原来掉进数值分支 → 中文负载永远解析失败判 Bad
                case IgwDataType.Bool:
                    if (bool.TryParse(s, out var b)) return b;
                    return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd) && bd != 0;
                case IgwDataType.Float: return float.Parse(s, CultureInfo.InvariantCulture);
                case IgwDataType.Double: return double.Parse(s, CultureInfo.InvariantCulture);
                default:
                    double d = double.Parse(s, CultureInfo.InvariantCulture);
                    return t switch
                    {
                        IgwDataType.Byte => (byte)d,
                        IgwDataType.Char => (sbyte)d,
                        IgwDataType.Int16 => (short)d,
                        IgwDataType.UInt16 => (ushort)d,
                        IgwDataType.Int32 => (int)d,
                        IgwDataType.UInt32 => (uint)d,
                        IgwDataType.Int64 => (long)d,
                        IgwDataType.UInt64 => (ulong)d,
                        _ => (short)d,
                    };
            }
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync();
        _lifetime.Cancel();
        _dispatchSignal.Writer.TryComplete();
        try { await _dispatchLoop; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _remoteSubscriptionGate.Dispose();
    }
}
