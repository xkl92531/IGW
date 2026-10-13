using IGW.Northbound.Mes;
using Xunit;

namespace IGW.Tests;

public sealed class MesInterfaceInboxTests
{
    [Fact]
    public void Inbox_preserves_order_rejects_conflicting_replay_and_commits_ack_with_baseline()
    {
        using var inbox = new MesInterfaceInbox(":memory:");
        var first = inbox.Append("task", "1", "{\"code\":\"A\"}", "sse-1", 2);
        Assert.Equal(first.Sequence, inbox.Append("task", "1", "{\"code\":\"A\"}", "sse-1", 2).Sequence);
        inbox.Append("task", "2", "{\"code\":\"B\"}", "sse-2", 2);
        Assert.Throws<InvalidDataException>(() => inbox.Append("task", "1", "{\"code\":\"X\"}", null, 2));
        Assert.Throws<MesInboxFullException>(() => inbox.Append("task", "3", "{}", null, 2));
        Assert.Equal("sse-2", inbox.LastEventId("task"));
        Assert.Equal("1", inbox.Pending("task")!.Id);
        Assert.Null(inbox.Baseline("task"));
        Assert.Equal("{\"n\":1}", inbox.FreezeBody(first.Sequence, "upload", "{\"n\":1}"));
        Assert.Equal("{\"n\":1}", inbox.FreezeBody(first.Sequence, "upload", "{\"n\":2}"));
        inbox.Commit(first.Sequence, "{\"code\":\"A\"}", "{\"accepted\":true}");
        Assert.Equal("{\"code\":\"A\"}", inbox.Baseline("task"));
        Assert.Equal("2", inbox.Pending("task")!.Id);
        var replay = inbox.Append("task", "1", "{\"code\":\"A\"}", "sse-1", 2);
        Assert.True(replay.Completed);
        Assert.Equal("{\"accepted\":true}", replay.Ack);
        // A replay must not move the SSE resumption position backwards.
        Assert.Equal("sse-2", inbox.LastEventId("task"));
    }
}
