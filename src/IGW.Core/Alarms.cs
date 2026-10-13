using System.Collections.Concurrent;
using IGW.Abstractions;

namespace IGW.Core;

public enum AlarmLevel { LoLo, Lo, Hi, HiHi }

/// <summary>当前处于激活状态的一条报警。</summary>
public sealed record ActiveAlarm(string TagId, AlarmLevel Level, double Limit, object? Value, DateTime Since);

/// <summary>一次报警状态变化事件(产生 / 清除)。</summary>
public sealed record AlarmEvent(string TagId, AlarmLevel Level, bool Active, double Limit, object? Value, DateTime Timestamp);

/// <summary>
/// 报警引擎(对标 KEP「报警」)。订阅 DataHub 变化,对配置了限值的点位做四级越限判定,
/// 带回差防抖(清除需越过 限值±回差);维护「当前活动报警」表 + 最近事件环形缓冲。
/// </summary>
public sealed class AlarmEngine : IDisposable
{
    private readonly DataHub _hub;
    private readonly IDisposable _sub;
    private readonly ConcurrentDictionary<string, ActiveAlarm> _active = new();   // key = tagId|level
    private readonly LinkedList<AlarmEvent> _events = new();
    private readonly object _evLock = new();
    private const int MaxEvents = 500;

    public AlarmEngine(DataHub hub)
    {
        _hub = hub;
        // 订阅倒转:只按"带告警定义的点"注册(成本 ∝ 告警点数,不再全量收+回调过滤)。
        // 本引擎随北向在南向应用完成后重建(见 GatewayHost),此刻点表稳定,注册集完整。
        var sub = hub.SubscribePoints(OnChange, "alarms");
        foreach (var (h, _, def) in hub.Snapshot())
            if (def.Alarm is { Enabled: true }) sub.Add(h);
        _sub = sub;
    }

    private ValueTask OnChange(TagChange c)
    {
        if (!_hub.TryGetDef(c.TagId, out var def) || def.Alarm is not { Enabled: true } a)
            return ValueTask.CompletedTask;
        if (!c.Value.IsGood || !TryDouble(c.Value.Value, out double v))
            return ValueTask.CompletedTask;   // 坏质量/非数值:不评估(保留上次状态)

        var ts = DateTime.UtcNow;
        var raw = c.Value.Value;
        EvalLevel(c.TagId, AlarmLevel.HiHi, a.HiHi, v, a.Deadband, high: true, raw, ts);
        EvalLevel(c.TagId, AlarmLevel.Hi, a.Hi, v, a.Deadband, high: true, raw, ts);
        EvalLevel(c.TagId, AlarmLevel.Lo, a.Lo, v, a.Deadband, high: false, raw, ts);
        EvalLevel(c.TagId, AlarmLevel.LoLo, a.LoLo, v, a.Deadband, high: false, raw, ts);
        return ValueTask.CompletedTask;
    }

    private void EvalLevel(string tagId, AlarmLevel level, double? limit, double v, double deadband, bool high, object? raw, DateTime ts)
    {
        if (limit is not double lim) return;
        string key = tagId + "|" + level;
        bool active = _active.ContainsKey(key);
        bool raise = high ? v >= lim : v <= lim;
        bool clear = high ? v < lim - deadband : v > lim + deadband;

        if (!active && raise)
        {
            _active[key] = new ActiveAlarm(tagId, level, lim, raw, ts);
            AddEvent(new AlarmEvent(tagId, level, true, lim, raw, ts));
        }
        else if (active && clear)
        {
            _active.TryRemove(key, out _);
            AddEvent(new AlarmEvent(tagId, level, false, lim, raw, ts));
        }
        else if (active)
        {
            _active[key] = _active[key] with { Value = raw };   // 仍激活:刷新当前值
        }
    }

    private void AddEvent(AlarmEvent e)
    {
        lock (_evLock)
        {
            _events.AddFirst(e);
            while (_events.Count > MaxEvents) _events.RemoveLast();
        }
    }

    /// <summary>当前活动报警(按产生时间)。</summary>
    public IReadOnlyList<ActiveAlarm> ActiveAlarms() =>
        _active.Values.OrderBy(a => a.Since).ToList();

    /// <summary>最近报警事件(最新在前)。</summary>
    public IReadOnlyList<AlarmEvent> RecentEvents(int limit)
    {
        lock (_evLock) return _events.Take(Math.Clamp(limit, 1, MaxEvents)).ToList();
    }

    private static bool TryDouble(object? v, out double d)
    {
        d = 0;
        if (v is null) return false;
        try { d = Convert.ToDouble(v); return true; } catch { return false; }
    }

    public void Dispose() { try { _sub.Dispose(); } catch { } }
}
