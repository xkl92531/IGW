namespace IGW.Core;

/// <summary>一段时间内同一类成功记录的汇总：次数、平均/最长耗时、首末时间，以及最后一次的字段(用来写汇总日志)。</summary>
public sealed record RepeatedSuccessSummary(
    string Key,
    int Count,
    double AverageMs,
    double MaxMs,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyDictionary<string, object?> Context);

/// <summary>
/// 高频重复的成功记录按窗口汇总(目前用于 MES 心跳这类每半秒一次、只回写固定值的流程)。
/// 逐条记日志时一天上百万条：查询一个任务名要扫遍全部日志而超时，真正有用的采集、状态记录也被淹没。
///
/// 规则与采集批次汇总相同(见 CollectionLogSummarizer)：
/// <list type="bullet">
/// <item>同一类(key)第一次成功、或刚从失败恢复时，照常立即记一条(<see cref="Success"/> 返回 true)；</item>
/// <item>之后的成功攒起来，每 <see cref="Window"/> 写一条汇总；</item>
/// <item>失败、慢请求等需要逐条看的情况由调用方照常记，记之前调 <see cref="Interrupt"/> 把攒着的先写出，时间线不乱；</item>
/// <item>停止时(<see cref="Dispose"/>)补记剩余汇总，不丢。</item>
/// </list>
/// 写汇总的回调出错不外抛：日志不能影响采集、接口和 MES。
/// </summary>
public sealed class RepeatedSuccessLog : IDisposable
{
    /// <summary>同时攒着的类别上限；超出时不再汇总新类别，照常逐条记(防止 key 失控占内存)。</summary>
    public const int MaxKeys = 4096;

    private readonly Action<RepeatedSuccessSummary> _write;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private int _disposed;

    public RepeatedSuccessLog(TimeSpan window, Action<RepeatedSuccessSummary> write, TimeProvider? time = null, bool autoFlush = true)
    {
        ArgumentNullException.ThrowIfNull(write);
        Window = window;
        _write = write;
        _time = time ?? TimeProvider.System;
        // 安静时段也要按时写出汇总；检查间隔取窗口的一部分，写出时间最多比窗口晚这么久
        TimeSpan check = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, window.Ticks / 6));
        if (autoFlush) _timer = _time.CreateTimer(_ => FlushDue(), null, check, check);
    }

    public TimeSpan Window { get; }

    /// <summary>记一次成功。返回 true：调用方照常立即记这一条；false：已攒进汇总，不用单独记。</summary>
    public bool Success(string key, double durationMs, IReadOnlyDictionary<string, object?> context)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out Pending? pending))
            {
                if (_pending.Count >= MaxKeys) return true;
                _pending[key] = new Pending(now);   // 这一条立即记，从下一条开始汇总
                return true;
            }
            pending.Add(now, durationMs, context);
            return false;
        }
    }

    /// <summary>这一类出现了要逐条记的情况(失败、慢)：先写出攒着的汇总，下一次成功重新立即记。</summary>
    public void Interrupt(string key)
    {
        RepeatedSuccessSummary? summary;
        lock (_gate)
        {
            if (!_pending.Remove(key, out Pending? pending)) return;
            summary = pending.Take(key);
        }
        Write(summary);
    }

    /// <summary>写出窗口已到的汇总(定时器调用；测试里手动调)。</summary>
    public void FlushDue()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var due = new List<RepeatedSuccessSummary>();
        lock (_gate)
        {
            List<string>? idle = null;
            foreach (var (key, pending) in _pending)
            {
                if (now - pending.WindowStart < Window) continue;
                if (pending.Take(key) is { } summary) { due.Add(summary); pending.Restart(now); }
                else (idle ??= []).Add(key);   // 整个窗口都没再成功过：忘掉它，下次成功重新立即记(能看出何时恢复)
            }
            if (idle is not null) foreach (string key in idle) _pending.Remove(key);
        }
        foreach (RepeatedSuccessSummary summary in due) Write(summary);
    }

    /// <summary>写出全部攒着的汇总(停止时)。</summary>
    public void FlushAll()
    {
        var all = new List<RepeatedSuccessSummary>();
        lock (_gate)
        {
            foreach (var (key, pending) in _pending)
                if (pending.Take(key) is { } summary) all.Add(summary);
            _pending.Clear();
        }
        foreach (RepeatedSuccessSummary summary in all) Write(summary);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        FlushAll();
    }

    private void Write(RepeatedSuccessSummary? summary)
    {
        if (summary is null) return;
        try { _write(summary); }
        catch { /* 写日志失败不能影响业务 */ }
    }

    private sealed class Pending(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; private set; } = windowStart;
        private int _count;
        private double _totalMs, _maxMs;
        private DateTimeOffset _from, _to;
        private IReadOnlyDictionary<string, object?>? _context;

        public void Add(DateTimeOffset at, double durationMs, IReadOnlyDictionary<string, object?> context)
        {
            if (_count++ == 0) _from = at;
            _to = at;
            _totalMs += durationMs;
            _maxMs = Math.Max(_maxMs, durationMs);
            _context = context;
        }

        /// <summary>取出汇总并清零；没有攒着的返回 null。</summary>
        public RepeatedSuccessSummary? Take(string key)
        {
            if (_count == 0) return null;
            var summary = new RepeatedSuccessSummary(key, _count, _totalMs / _count, _maxMs, _from, _to, _context!);
            _count = 0; _totalMs = _maxMs = 0; _context = null;
            return summary;
        }

        public void Restart(DateTimeOffset at) => WindowStart = at;
    }
}
