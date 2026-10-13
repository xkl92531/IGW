using IGW.Abstractions;
using System.Collections.Concurrent;

namespace IGW.Core;

/// <summary>
/// Owned by a single DeviceLoop (and replaced with its configuration). State is bounded by
/// configured bindings; only explicit consecutive NodeIdUnknown samples are quarantined.
/// Uses the same monotonic timeline as the scan deadline queue.
/// </summary>
internal sealed class InvalidNodeReadPolicy(IReadOnlyDictionary<string, TagBinding> bindings)
{
    private const int FailureThreshold = 3;
    private const int ProbeIntervalMs = 30_000;
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    // Write verification runs on the concurrent write pump. Only notifications cross threads;
    // the scan loop remains the sole owner of failure state and deadlines.
    private readonly ConcurrentDictionary<string, byte> _recoveries = new(StringComparer.Ordinal);
    private readonly record struct Failure(int Count, long ProbeAtMs);

    public int IsolatedCount { get; private set; }

    /// <summary>
    /// 该点位是否已被隔离。已隔离 = 问题已经识别、已经在点位异常里逐点记录、并且每 30 秒复探一次，
    /// 属于「已处理」状态：设备级诊断不该再为它反复报异常，否则一个配错的点位就能让整台设备
    /// 在「在线」和「部分在线」之间来回跳（隔离期不读 → 好，复探那轮读到 → 坏）。
    /// </summary>
    public bool IsIsolated(string tagId) =>
        _failures.TryGetValue(tagId, out var failure) && failure.Count >= FailureThreshold;

    public IReadOnlyList<TagBinding> SelectDue(IReadOnlyList<TagBinding> tags, long nowMs, out int nextProbeMs)
    {
        List<TagBinding>? selected = null;
        nextProbeMs = ProbeIntervalMs;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (_failures.TryGetValue(tag.TagId, out var failure) && failure.Count >= FailureThreshold && failure.ProbeAtMs > nowMs)
            {
                nextProbeMs = Math.Min(nextProbeMs, (int)Math.Min(ProbeIntervalMs, failure.ProbeAtMs - nowMs));
                if (selected is null)
                {
                    selected = new List<TagBinding>(tags.Count);
                    for (int j = 0; j < i; j++) selected.Add(tags[j]);
                }
            }
            else selected?.Add(tag);
        }
        return selected is null ? tags : selected;
    }

    public void Record(IReadOnlyList<TagBinding> requested, IReadOnlyList<TagSample> samples, long completedMs,
        IReadOnlySet<string>? superseded = null)
    {
        // A missing response also breaks consecutiveness. Only process requested configured tags,
        // so unsolicited or duplicate responses cannot expand state or accelerate quarantine.
        HashSet<string>? permanentIds = null;
        foreach (var sample in samples)
        {
            if (IsPermanentReadFailure(sample.Value.Quality.Code) && bindings.ContainsKey(sample.TagId))
                (permanentIds ??= new(StringComparer.Ordinal)).Add(sample.TagId);
        }
        foreach (var tag in requested)
        {
            if (!bindings.ContainsKey(tag.TagId)) continue;
            if (superseded?.Contains(tag.TagId) == true) { Remove(tag.TagId); continue; }
            if (permanentIds is not null && permanentIds.Contains(tag.TagId))
            {
                _failures.TryGetValue(tag.TagId, out var prior);
                int count = Math.Min(FailureThreshold, prior.Count + 1);
                if (count == FailureThreshold && prior.Count < FailureThreshold) IsolatedCount++;
                _failures[tag.TagId] = new Failure(count, count == FailureThreshold ? completedMs + ProbeIntervalMs : 0);
            }
            else Remove(tag.TagId);
        }
    }

    public bool QueueVerifiedRecovery(IReadOnlyList<TagSample> samples)
    {
        bool added = false;
        foreach (var sample in samples)
            if (sample.Value.IsGood && bindings.ContainsKey(sample.TagId))
                added |= _recoveries.TryAdd(sample.TagId, 0);
        return added;
    }

    public IReadOnlySet<string>? ApplyVerifiedRecoveries()
    {
        if (_recoveries.IsEmpty) return null;
        var recovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _recoveries.Keys)
            if (_recoveries.TryRemove(id, out _)) { Remove(id); recovered.Add(id); }
        return recovered;
    }

    public void BreakSequence(IReadOnlyList<TagBinding> requested)
    {
        foreach (var tag in requested) Remove(tag.TagId);
    }

    /// <summary>
    /// 这一次读失败是不是「重读一万次也还是这个结果」的配置错误。只有这类失败才隔离：
    /// 符号找不到、按配置的类型解不开、类型对不上——要么改点表要么改 PLC 程序，网关再试也没用。
    ///
    /// 通信类故障(超时、连接断、设备故障、笼统的 Bad)绝不能进来：那是链路问题不是配置问题，
    /// 链路一恢复点位自己就好了；把它们隔离只会让恢复白白慢 30 秒，整台设备掉线重连时更是一片点位同时进隔离期。
    /// </summary>
    private static bool IsPermanentReadFailure(uint code) => (code & 0xffff0000u) switch
    {
        0x80340000u => true,   // BadNodeIdUnknown：PLC 程序里没这个符号
        0x80070000u => true,   // BadDecodeError：返回的字节按配置的类型解不开
        0x80740000u => true,   // BadTypeMismatch：PLC 侧实际类型与点位声明不一致
        _ => false,
    };
    private void Remove(string id)
    {
        if (_failures.Remove(id, out var prior) && prior.Count >= FailureThreshold) IsolatedCount--;
    }
}
