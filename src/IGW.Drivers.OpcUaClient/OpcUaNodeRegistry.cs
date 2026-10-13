using System.Collections.Concurrent;
using Opc.Ua;

namespace IGW.Drivers.OpcUaClient;

/// <summary>
/// 地址字符串到 NodeId 的解析缓存。地址中的命名空间索引是字面量，与会话无关，可在整个驱动生命周期复用；
/// 大工程下每个点每个周期重新解析字符串会白白占用网关 CPU。
/// </summary>
internal sealed class OpcUaNodeIdCache
{
    private const int MaxEntries = 1_000_000;
    private readonly ConcurrentDictionary<string, NodeId?> _parsed = new(StringComparer.Ordinal);

    public bool TryGet(string address, out NodeId nodeId)
    {
        if (_parsed.TryGetValue(address, out var cached))
        {
            nodeId = cached ?? NodeId.Null;
            return cached is not null;
        }
        NodeId? parsed;
        try { parsed = NodeId.Parse(address.Trim()); }
        catch { parsed = null; }
        if (_parsed.Count < MaxEntries) _parsed.TryAdd(address, parsed);
        nodeId = parsed ?? NodeId.Null;
        return parsed is not null;
    }
}

/// <summary>
/// 单个会话内的 RegisterNodes 句柄表。S7-1500 等服务器的符号 NodeId 是很长的字符串，
/// 每次读写都要重新解析；注册后请求里改用服务器给出的句柄，报文更小、服务器查找更快。
/// 句柄只在注册它的会话里有效，因此每个会话一个实例，重连后整体丢弃。
/// 注册只是加速手段：任何失败都退回原始 NodeId，绝不让读写因此失败。
/// </summary>
internal sealed class OpcUaNodeRegistry
{
    internal const int DefaultMaxRegisteredNodes = 200_000;
    /// <summary>服务器没有声明单次注册上限时每次调用的节点数。</summary>
    internal const int UndeclaredRegisterBatch = 1_000;
    /// <summary>超时、通信异常等暂时性失败后，暂停注册的时间。</summary>
    internal const int RetryAfterFailureMs = 30_000;

    private readonly Func<NodeIdCollection, CancellationToken, Task<NodeIdCollection>> _register;
    private readonly Action<string> _report;
    private readonly Func<long> _clock;
    private readonly int _batchSize;
    private readonly int _maxRegistered;
    private readonly SemaphoreSlim _registerGate = new(1, 1);
    private readonly ConcurrentDictionary<NodeId, NodeId> _handles = new();
    private volatile bool _disabled;
    private long _retryAfter;

    public OpcUaNodeRegistry(
        Func<NodeIdCollection, CancellationToken, Task<NodeIdCollection>> register,
        int batchSize,
        Action<string> report,
        int maxRegistered = DefaultMaxRegisteredNodes,
        Func<long>? clock = null)
    {
        _register = register;
        _batchSize = Math.Max(1, batchSize);
        _report = report;
        _maxRegistered = Math.Max(0, maxRegistered);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public int RegisteredCount => _handles.Count;
    public bool Disabled => _disabled;

    /// <summary>返回与输入一一对应的请求用 NodeId；已注册的换成句柄，其余保持原样。</summary>
    public async Task<NodeId[]> MapAsync(IReadOnlyList<NodeId> nodes, CancellationToken ct)
    {
        var mapped = new NodeId[nodes.Count];
        List<NodeId>? missing = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (_handles.TryGetValue(nodes[i], out var handle)) mapped[i] = handle;
            else
            {
                mapped[i] = nodes[i];
                (missing ??= new()).Add(nodes[i]);
            }
        }
        if (missing is null || !CanRegister()) return mapped;

        await RegisterMissingAsync(missing, ct).ConfigureAwait(false);
        for (int i = 0; i < nodes.Count; i++)
            if (_handles.TryGetValue(nodes[i], out var handle)) mapped[i] = handle;
        return mapped;
    }

    private bool CanRegister() =>
        !_disabled && _handles.Count < _maxRegistered && _clock() >= Interlocked.Read(ref _retryAfter);

    private async Task RegisterMissingAsync(List<NodeId> missing, CancellationToken ct)
    {
        await _registerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 并发读写可能同时发现同一批未注册节点；进锁后再筛一次，避免重复注册。
            var pending = missing.Where(node => !_handles.ContainsKey(node)).Distinct().ToList();
            for (int cursor = 0; cursor < pending.Count && CanRegister(); cursor += _batchSize)
            {
                int room = _maxRegistered - _handles.Count;
                var slice = pending.Skip(cursor).Take(Math.Min(_batchSize, room)).ToArray();
                if (slice.Length == 0) break;
                NodeIdCollection handles;
                try
                {
                    handles = await _register(new NodeIdCollection(slice), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    HandleFailure(ex);
                    return;
                }
                if (handles is null || handles.Count != slice.Length || handles.Any(NodeId.IsNull))
                {
                    _disabled = true;
                    _report("服务器返回的注册结果数量或内容不正确，本会话改用原始 NodeId 读写");
                    return;
                }
                for (int i = 0; i < slice.Length; i++) _handles[slice[i]] = handles[i];
            }
            if (_handles.Count >= _maxRegistered)
                _report($"已注册节点达到上限 {_maxRegistered}，其余节点使用原始 NodeId 读写");
        }
        finally { _registerGate.Release(); }
    }

    private void HandleFailure(Exception error)
    {
        uint? code = null;
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ServiceResultException service) { code = service.StatusCode; break; }
        if (code is StatusCodes.BadServiceUnsupported or StatusCodes.BadNotSupported or StatusCodes.BadNotImplemented)
        {
            _disabled = true;
            _report("服务器不支持 RegisterNodes，本会话改用原始 NodeId 读写");
            return;
        }
        Interlocked.Exchange(ref _retryAfter, _clock() + RetryAfterFailureMs);
        _report($"RegisterNodes 失败，{RetryAfterFailureMs / 1000} 秒内使用原始 NodeId 读写：{error.Message}");
    }
}
