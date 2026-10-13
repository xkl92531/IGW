using Microsoft.Extensions.Logging;

namespace IGW.Core;

/// <summary>
/// 采集批次日志按块汇总。每个采集块每读一次就记一条「采集批次完成」，100ms 周期的设备每块每分钟
/// 600 条——现场 9 小时 48.9 万条日志里 47.8 万条是它，淹没了真正有用的记录，查询、导出都被拖慢。
///
/// 规则：
/// <list type="bullet">
/// <item>每个块第一次成功(刚启动、刚从异常恢复)立即记一条，能看到采集开始/恢复的时刻；</item>
/// <item>之后的成功批次每 <see cref="Window"/> 汇总成一条「采集批次汇总」(次数、平均/最大耗时、样本、坏点、错过周期)；</item>
/// <item>异常批次照旧逐条立即记，记之前先把攒着的成功批次汇总出来，时间线不乱；</item>
/// <item>采集循环停止时补记剩余汇总，不丢。</item>
/// </list>
/// 字段沿用单条记录(通道、设备、块、任务归属等)，日志中心的筛选和展示不受影响。
///
/// 点位值(<see cref="TagValuesLogField"/>)：立即记的首条/恢复记录和汇总记录都带(汇总用窗口里最后一批的值，
/// 其余批次的值对象直接丢弃、不序列化)。异常批次在持续失败时可能每 100ms~2s 一条，
/// 每块每个窗口只第一条带点位值，后面的同类记录不再重复几 KB 的点位清单。
/// </summary>
internal sealed class CollectionLogSummarizer(ILogger log, TimeSpan window)
{
    public TimeSpan Window { get; } = window;
    private readonly Dictionary<int, Pending> _blocks = [];
    private readonly Dictionary<int, long> _failureValuesAt = [];   // 块 → 上次异常记录带点位值的时刻

    /// <summary>一个批次完成：按规则立即记、攒进汇总或先汇总再记。</summary>
    public void Completed(int blockId, bool good, Dictionary<string, object?> fields, long nowMs)
    {
        if (!good)
        {
            Flush(blockId);
            _blocks.Remove(blockId);            // 下一次成功算「恢复」，立即记
            if (_failureValuesAt.TryGetValue(blockId, out long valuesAt) && nowMs - valuesAt < (long)Window.TotalMilliseconds)
                fields.Remove(TagValuesLogField.FieldName);
            else if (fields.ContainsKey(TagValuesLogField.FieldName))
                _failureValuesAt[blockId] = nowMs;
            Write(LogLevel.Warning, "采集批次完成", fields, 1);
            return;
        }
        if (!_blocks.TryGetValue(blockId, out Pending? pending))
        {
            Write(LogLevel.Information, "采集批次完成", fields, 1);
            _blocks[blockId] = new Pending(nowMs);
            _failureValuesAt.Remove(blockId);   // 恢复后再失败，第一条异常记录照样带点位值
            return;
        }
        pending.Add(fields);
        if (nowMs - pending.StartedMs >= (long)Window.TotalMilliseconds)
        {
            Flush(blockId);
            _blocks[blockId] = new Pending(nowMs);
        }
    }

    /// <summary>采集循环停止：补记所有块攒着的汇总。</summary>
    public void FlushAll()
    {
        foreach (int blockId in _blocks.Keys.ToArray()) Flush(blockId);
        _blocks.Clear();
    }

    private void Flush(int blockId)
    {
        if (!_blocks.TryGetValue(blockId, out Pending? pending) || pending.Scans == 0) return;
        var fields = new Dictionary<string, object?>(pending.Last!)
        {
            ["durationMs"] = Math.Round(pending.TotalMs / pending.Scans, 1),
            ["maxDurationMs"] = pending.MaxMs,
            ["driverMs"] = Math.Round(pending.DriverMs / pending.Scans, 1),
            ["localMs"] = Math.Round(pending.LocalMs / pending.Scans, 1),
            ["sampleCount"] = pending.Samples,
            ["badCount"] = pending.Bad,
            ["missedPeriods"] = pending.Missed,
            ["windowSeconds"] = Math.Round(Window.TotalSeconds),
        };
        if (pending.Intervals > 0) fields["actualIntervalMs"] = Math.Round(pending.IntervalMs / pending.Intervals, 1);
        Write(LogLevel.Information, "采集批次汇总", fields, pending.Scans);
        pending.Reset();
    }

    private void Write(LogLevel level, string message, Dictionary<string, object?> fields, int scans)
    {
        fields["scanCount"] = scans;
        GatewayTelemetry.Log(log, level, "collection", message, fields);
    }

    private sealed class Pending(long startedMs)
    {
        public long StartedMs { get; } = startedMs;
        public int Scans;
        public double TotalMs, MaxMs, DriverMs, LocalMs, IntervalMs;
        public int Intervals;
        public long Samples, Bad, Missed;
        public Dictionary<string, object?>? Last;

        public void Add(Dictionary<string, object?> fields)
        {
            Scans++;
            double total = Number(fields, "durationMs");
            TotalMs += total;
            MaxMs = Math.Max(MaxMs, total);
            DriverMs += Number(fields, "driverMs");
            LocalMs += Number(fields, "localMs");
            if (fields.TryGetValue("actualIntervalMs", out object? interval) && interval is not null)
            {
                IntervalMs += Convert.ToDouble(interval);
                Intervals++;
            }
            Samples += (long)Number(fields, "sampleCount");
            Bad += (long)Number(fields, "badCount");
            Missed += (long)Number(fields, "missedPeriods");
            Last = fields;
        }

        public void Reset()
        {
            Scans = 0; Intervals = 0;
            TotalMs = MaxMs = DriverMs = LocalMs = IntervalMs = 0;
            Samples = Bad = Missed = 0;
        }

        private static double Number(Dictionary<string, object?> fields, string key) =>
            fields.TryGetValue(key, out object? value) && value is not null ? Convert.ToDouble(value) : 0;
    }
}
