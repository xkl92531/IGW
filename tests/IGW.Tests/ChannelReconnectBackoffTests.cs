using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ChannelReconnectBackoffTests
{
    [Fact]
    public void Long_rebuild_is_not_admitted_again_until_cooldown_after_completion()
    {
        var policy = new ChannelReconnectBackoff();
        Assert.True(policy.TryBegin(0));
        Assert.False(policy.IsReady(90_000));
        Assert.False(policy.TryBegin(90_000));
        Assert.Equal(30_000, policy.Complete(90_000));
        Assert.False(policy.TryBegin(119_999));
        Assert.True(policy.TryBegin(120_000));
        Assert.Equal(60_000, policy.Complete(121_000));
    }

    [Fact]
    public void Persistent_failure_backs_off_but_manual_recovery_can_run_immediately()
    {
        var policy = new ChannelReconnectBackoff();
        long now = 0;
        foreach (int delay in new[] { 30_000, 60_000, 120_000, 240_000, 300_000, 300_000 })
        {
            Assert.True(policy.TryBegin(now));
            Assert.Equal(delay, policy.Complete(now));
            now += delay;
        }
        Assert.True(policy.TryBegin(now - 1, manual: true));
        Assert.Equal(30_000, policy.Complete(now));
        Assert.True(policy.TryBegin(now + 600_001));
        Assert.Equal(30_000, policy.Complete(now + 600_001));
    }
}
