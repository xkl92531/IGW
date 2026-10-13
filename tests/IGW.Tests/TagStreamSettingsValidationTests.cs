using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class TagStreamSettingsValidationTests
{
    [Fact]
    public void Invalid_realtime_limits_are_rejected_before_runtime_construction()
    {
        var project = new IgwProject();
        project.Settings.TagStream = new TagStreamSettings
        {
            MaxTagsPerStream = 0,
            MaxStreams = 0,
            HeartbeatSeconds = 1,
            SnapshotIntervalSeconds = 1,
            MaxConcurrentSnapshots = 0,
            DeltaBatchWindowMs = 0,
            CoordinatorQueueCapacity = 1,
        };

        var report = ProjectStore.Check(project);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Contains("单流点位", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("实时连接", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("心跳", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("快照周期", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("并发快照", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("增量批窗口", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("协调器队列", StringComparison.Ordinal));
    }
}
