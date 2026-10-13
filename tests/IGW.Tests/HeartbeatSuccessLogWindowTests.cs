using System.Collections.Concurrent;
using IGW.Northbound.Mes;
using Xunit;

namespace IGW.Tests;

public class HeartbeatSuccessLogWindowTests
{
    [Fact]
    public void First_success_is_immediate_and_duplicates_are_counted_once_per_window()
    {
        var window = new HeartbeatSuccessLogWindow();
        Assert.Equal(new HeartbeatSuccessSummary(1, 0, true, false), window.Success(100));
        Assert.Null(window.Success(200));
        Assert.Null(window.Success(30_099));
        Assert.Equal(new HeartbeatSuccessSummary(3, 30_000, false, false), window.Success(30_100));
        Assert.Null(window.Drain(30_101));
        Assert.Null(window.Success(30_102));
        Assert.Equal(1, window.Drain(30_200)!.Count);
        Assert.Null(window.Drain(30_201));
    }

    [Fact]
    public void Failure_flushes_pending_successes_and_next_success_reports_recovery()
    {
        var window = new HeartbeatSuccessLogWindow();
        window.Success(0);
        window.Success(10);
        Assert.Equal(1, window.Failure(20)!.Count);
        Assert.Null(window.Failure(21));
        Assert.Equal(new HeartbeatSuccessSummary(1, 0, false, true), window.Success(30));
        Assert.Null(window.Success(40));
    }

    [Fact]
    public void Initially_failed_heartbeat_reports_recovery_when_first_success_arrives()
    {
        var window = new HeartbeatSuccessLogWindow();
        Assert.Null(window.Failure(0));
        Assert.Equal(new HeartbeatSuccessSummary(1, 0, false, true), window.Success(1));
    }

    [Fact]
    public void Concurrent_successes_are_neither_lost_nor_double_counted()
    {
        var window = new HeartbeatSuccessLogWindow();
        var emitted = new ConcurrentBag<HeartbeatSuccessSummary>();
        Parallel.For(0, 10_000, _ => { if (window.Success(1) is { } item) emitted.Add(item); });
        emitted.Add(window.Drain(2)!);
        Assert.Equal(10_000, emitted.Sum(item => item.Count));
        Assert.Single(emitted, item => item.First);
        Assert.Null(window.Drain(3));
    }
}
