using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public class HeartbeatLoggingTests
{
    [Fact]
    public async Task Heartbeat_keeps_failures_and_recovery_without_changing_write_sequence_or_warning_on_shutdown()
    {
        await using var hub = new DataHub();
        var logger = new CaptureLogger();
        var observedValues = new ConcurrentQueue<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var task = new MesTask
        {
            Id = "hb-task", Name = "HB", Enabled = true, CollectIntervalMs = 100,
            Flow = new MesFlow { Nodes = [new MesNode { Id = "trigger", Type = "Trigger" }] },
            Heartbeat = new MesHeartbeat { Enabled = true, TagId = "C.D.Heartbeat", IntervalMs = 5, MaxLimit = 100 },
        };
        var engine = new MesUploadEngine(hub,
            new MesUploadSettings { Enabled = true, LogDbPath = "", Tasks = [task] },
            async (_, value, _, ct) =>
            {
                observedValues.Enqueue(Convert.ToInt32(value));
                int call = Interlocked.Increment(ref calls);
                if (call == 3) return StatusCode.BadNotWritable;
                if (call == 4) throw new IOException("transport failed");
                if (call >= 6)
                {
                    completed.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return StatusCode.Good;
            }, logger);
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, observedValues.ToArray());
            Assert.Contains(logger.Items, item => item.Level == LogLevel.Information && item.Text.Contains("心跳首次成功"));
            Assert.Contains(logger.Items, item => item.Level == LogLevel.Information && item.Text.Contains("心跳恢复"));
            Assert.Equal(2, logger.Items.Count(item => item.Level == LogLevel.Warning && item.Text.Contains("心跳失败")));
        }
        finally { await engine.DisposeAsync(); }
        Assert.Equal(2, logger.Items.Count(item => item.Level == LogLevel.Warning && item.Text.Contains("心跳失败")));
    }

    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Items { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Items.Enqueue((level, formatter(state, exception)));
    }
}
