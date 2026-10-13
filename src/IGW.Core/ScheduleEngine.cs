using System.Globalization;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;

namespace IGW.Core;

/// <summary>计划任务(对标 KEP「Scheduler」)。按周期定时对某点读(激活轮询)或写(下发值)。</summary>
public sealed class ScheduleEngine : IAsyncDisposable
{
    private readonly RuntimeEngine _engine;
    private readonly DataHub _hub;
    private readonly ILogger _log;
    private readonly List<Timer> _timers = new();
    private readonly CancellationTokenSource _cts = new();
    private long _fired;
    private bool _disposed;

    public long Fired => Interlocked.Read(ref _fired);

    public ScheduleEngine(RuntimeEngine engine, DataHub hub, IReadOnlyList<ScheduleJobDef> jobs, ILogger log)
    {
        _engine = engine; _hub = hub; _log = log;
        foreach (var j in jobs)
        {
            if (!j.Enabled || string.IsNullOrWhiteSpace(j.TagId) || j.IntervalMs <= 0) continue;
            var job = j;
            _timers.Add(new Timer(_ => Fire(job), null, job.IntervalMs, job.IntervalMs));
        }
        if (_timers.Count > 0) _log.LogInformation("计划任务已启用:{n} 个", _timers.Count);
    }

    private void Fire(ScheduleJobDef j)
    {
        try
        {
            if (string.Equals(j.Type, "Read", StringComparison.OrdinalIgnoreCase))
            {
                _hub.Touch(j.TagId);   // 计划读:激活该点(active 模式下即触发轮询)
            }
            else // Write
            {
                if (!_hub.TryGetDef(j.TagId, out var def)) return;
                object? val = ConvertValue(j.Value, def.DataType);
                if (val != null) _ = _engine.WriteAsync(j.TagId, val, _cts.Token);
            }
            Interlocked.Increment(ref _fired);
        }
        catch (Exception ex) { _log.LogDebug("计划任务 {n} 失败:{m}", j.Name, ex.Message); }
    }

    private static object? ConvertValue(string s, IgwDataType t)
    {
        s = (s ?? "").Trim();
        try
        {
            return t switch
            {
                IgwDataType.Bool => bool.TryParse(s, out var b) ? b : double.Parse(s, CultureInfo.InvariantCulture) != 0,
                IgwDataType.String => s,
                IgwDataType.Float => float.Parse(s, CultureInfo.InvariantCulture),
                IgwDataType.Double => double.Parse(s, CultureInfo.InvariantCulture),
                _ => double.Parse(s, CultureInfo.InvariantCulture),
            };
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;   // 幂等:重载回退路径可能二次调用,二次 Cancel 已 Dispose 的 CTS 会炸掉整个回退
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        foreach (var t in _timers) { try { await t.DisposeAsync(); } catch { } }
        _cts.Dispose();
    }
}
