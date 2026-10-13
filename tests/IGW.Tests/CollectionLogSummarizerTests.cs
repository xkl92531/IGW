using System.Collections.Concurrent;
using IGW.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 采集批次日志汇总：成功批次每 30 秒一条，异常逐条立即记。
/// 现场 100ms 周期的设备每块每分钟 600 条「采集批次完成」，9 小时 47.8 万条，淹没了真正有用的记录。
/// </summary>
public sealed class CollectionLogSummarizerTests
{
    [Fact]
    public void First_success_is_logged_at_once_and_the_rest_are_summarized_per_window()
    {
        var logger = new RecordingLogger();
        var summarizer = new CollectionLogSummarizer(logger, TimeSpan.FromSeconds(30));

        summarizer.Completed(1, good: true, Batch(duration: 10), nowMs: 0);
        for (int i = 1; i <= 299; i++)
            summarizer.Completed(1, good: true, Batch(duration: i == 150 ? 90 : 20, interval: 100), nowMs: i * 100);
        Assert.Single(logger.Records);                                   // 30 秒窗口还没到

        summarizer.Completed(1, good: true, Batch(duration: 20, interval: 100), nowMs: 30_000);

        Assert.Equal(2, logger.Records.Count);
        var summary = logger.Records[1];
        Assert.Equal("采集批次汇总", summary.Message);
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Equal(300, summary.Fields["igw_scan_count"]);
        Assert.Equal(90d, Convert.ToDouble(summary.Fields["igw_max_duration_ms"]));
        Assert.Equal(20.2, Convert.ToDouble(summary.Fields["igw_duration_ms"]), 1);   // (299×20 + 90 - 20) / 300 ≈ 20.2
        Assert.Equal(300 * 15L, Convert.ToInt64(summary.Fields["igw_sample_count"]));
        Assert.Equal("PLC8.OP140", summary.Fields["igw_device"]);                       // 归属字段沿用
    }

    [Fact]
    public void A_failure_flushes_pending_successes_first_and_the_next_success_is_logged_as_recovery()
    {
        var logger = new RecordingLogger();
        var summarizer = new CollectionLogSummarizer(logger, TimeSpan.FromSeconds(30));
        summarizer.Completed(1, good: true, Batch(10), nowMs: 0);
        summarizer.Completed(1, good: true, Batch(10), nowMs: 100);
        summarizer.Completed(1, good: true, Batch(10), nowMs: 200);

        summarizer.Completed(1, good: false, Batch(500), nowMs: 300);
        summarizer.Completed(1, good: true, Batch(10), nowMs: 400);

        Assert.Equal(["采集批次完成", "采集批次汇总", "采集批次完成", "采集批次完成"], logger.Records.Select(record => record.Message));
        Assert.Equal(2, logger.Records[1].Fields["igw_scan_count"]);
        Assert.Equal(LogLevel.Warning, logger.Records[2].Level);
        Assert.Equal(1, logger.Records[2].Fields["igw_scan_count"]);
    }

    [Fact]
    public void Stopping_flushes_what_is_pending_and_blocks_are_independent()
    {
        var logger = new RecordingLogger();
        var summarizer = new CollectionLogSummarizer(logger, TimeSpan.FromSeconds(30));
        summarizer.Completed(1, good: true, Batch(10), nowMs: 0);
        summarizer.Completed(2, good: true, Batch(10), nowMs: 0);
        summarizer.Completed(1, good: true, Batch(10), nowMs: 100);
        summarizer.Completed(2, good: true, Batch(10), nowMs: 100);
        summarizer.Completed(2, good: true, Batch(10), nowMs: 200);

        summarizer.FlushAll();

        Assert.Equal(4, logger.Records.Count);
        Assert.Equal(5, logger.Records.Sum(record => Convert.ToInt32(record.Fields["igw_scan_count"])));
        summarizer.FlushAll();
        Assert.Equal(4, logger.Records.Count);                           // 没有攒着的就不再记
    }

    private static Dictionary<string, object?> Batch(int duration, int? interval = null) => new()
    {
        ["module"] = "acquisition", ["feature"] = "plc_scan", ["eventKind"] = "collection_batch", ["operation"] = "read",
        ["channel"] = "PLC8", ["device"] = "PLC8.OP140", ["blockId"] = 1, ["status"] = "good",
        ["durationMs"] = duration, ["driverMs"] = duration, ["localMs"] = 0,
        ["configuredPeriodMs"] = 100, ["actualIntervalMs"] = interval,
        ["tagCount"] = 15, ["sampleCount"] = 15, ["badCount"] = 0, ["missedPeriods"] = 0,
    };

    private sealed record Record(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<Record> _records = new();
        public IReadOnlyList<Record> Records => _records.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _records.Enqueue(new Record(logLevel, fields["{OriginalFormat}"]?.ToString() ?? "", fields));
        }
    }
}
