using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>高频重复的成功记录(外部系统轮询读值、心跳回写)按窗口汇总，失败和首条照常立即记。</summary>
public sealed class RepeatedSuccessLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_success_is_logged_and_later_ones_are_summarized_per_window()
    {
        var time = new ManualTime(Start);
        var written = new List<RepeatedSuccessSummary>();
        using var log = new RepeatedSuccessLog(TimeSpan.FromSeconds(60), written.Add, time, autoFlush: false);

        Assert.True(log.Success("POST /Tag/ReadTagsAsList", 40, Context("A")));    // 第一次：照常立即记
        for (int i = 0; i < 99; i++)
        {
            time.Now = Start.AddMilliseconds(250 * (i + 1));
            Assert.False(log.Success("POST /Tag/ReadTagsAsList", i % 2 == 0 ? 10 : 30, Context("B" + i)));
        }
        log.FlushDue();
        Assert.Empty(written);                                                      // 窗口还没到

        time.Now = Start.AddSeconds(61);
        log.FlushDue();
        RepeatedSuccessSummary summary = Assert.Single(written);
        Assert.Equal("POST /Tag/ReadTagsAsList", summary.Key);
        Assert.Equal(99, summary.Count);
        Assert.Equal((50 * 10 + 49 * 30) / 99.0, summary.AverageMs, 3);
        Assert.Equal(30, summary.MaxMs);
        Assert.Equal(Start.AddMilliseconds(250), summary.From);
        Assert.Equal(Start.AddMilliseconds(250 * 99), summary.To);
        Assert.Equal("B98", summary.Context["who"]);                                // 最后一次的字段

        // 汇总写出后继续攒，不再单独记
        Assert.False(log.Success("POST /Tag/ReadTagsAsList", 5, Context("C")));
    }

    [Fact]
    public void Interruption_flushes_pending_first_and_next_success_is_logged_immediately()
    {
        var time = new ManualTime(Start);
        var written = new List<RepeatedSuccessSummary>();
        using var log = new RepeatedSuccessLog(TimeSpan.FromSeconds(60), written.Add, time, autoFlush: false);
        log.Success("回写", 3, Context("1"));
        log.Success("回写", 3, Context("2"));
        log.Success("回写", 5, Context("3"));

        log.Interrupt("回写");                                       // 失败/慢请求：先把攒着的写出，时间线不乱
        Assert.Equal(2, Assert.Single(written).Count);
        Assert.True(log.Success("回写", 3, Context("4")));            // 恢复后第一次立即记
        log.Interrupt("没记过的");                                    // 没有攒着的也不出错
        Assert.Single(written);
    }

    [Fact]
    public void Keys_are_summarized_independently_and_flush_all_writes_everything_pending()
    {
        var time = new ManualTime(Start);
        var written = new List<RepeatedSuccessSummary>();
        using (var log = new RepeatedSuccessLog(TimeSpan.FromSeconds(60), written.Add, time, autoFlush: false))
        {
            foreach (string key in new[] { "Heart-4/回写(true)", "Heart-9/回写(false)" })
                for (int i = 0; i < 3; i++) log.Success(key, 1, Context(key));
        }   // Dispose = 停止前补记
        Assert.Equal(["Heart-4/回写(true)", "Heart-9/回写(false)"], written.Select(item => item.Key).Order());
        Assert.All(written, item => Assert.Equal(2, item.Count));
    }

    [Fact]
    public void Callback_failures_never_escape()
    {
        var time = new ManualTime(Start);
        using var log = new RepeatedSuccessLog(TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("日志坏了"), time, autoFlush: false);
        log.Success("k", 1, Context("a"));
        log.Success("k", 1, Context("b"));
        time.Now = Start.AddSeconds(5);
        log.FlushDue();
        log.Interrupt("k");
    }

    private static IReadOnlyDictionary<string, object?> Context(string who) => new Dictionary<string, object?> { ["who"] = who };

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
