using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class InvalidNodeReadPolicyTests
{
    [Fact]
    public void All_isolated_batch_has_future_deadline_and_failed_probe_renews_it()
    {
        var tag = Binding("a");
        var policy = Policy(tag);
        for (int i = 0; i < 3; i++) policy.Record([tag], [Unknown(tag)], 100);
        Assert.Empty(policy.SelectDue([tag], 30_099, out int remaining));
        Assert.Equal(1, remaining);
        Assert.Single(policy.SelectDue([tag], 30_100, out _));
        policy.Record([tag], [Unknown(tag)], 30_100);
        Assert.Empty(policy.SelectDue([tag], 30_101, out remaining));
        Assert.Equal(29_999, remaining);
        policy.Record([tag], [new TagSample(tag.TagId, DataValue.GoodAt(42, DateTime.UtcNow))], 60_100);
        Assert.Single(policy.SelectDue([tag], 60_101, out _));
        Assert.Equal(0, policy.IsolatedCount);
    }

    [Fact]
    public void Missing_response_and_request_failure_break_the_unknown_sequence()
    {
        var tag = Binding("a");
        var policy = Policy(tag);
        policy.Record([tag], [Unknown(tag)], 0);
        policy.Record([tag], [Unknown(tag)], 1);
        policy.Record([tag], [], 2);
        policy.Record([tag], [Unknown(tag)], 3);
        policy.Record([tag], [Unknown(tag)], 4);
        Assert.Single(policy.SelectDue([tag], 5, out _));
        policy.BreakSequence([tag]);
        policy.Record([tag], [Unknown(tag)], 6);
        policy.Record([tag], [Unknown(tag)], 7);
        Assert.Single(policy.SelectDue([tag], 8, out _));
    }

    [Fact]
    public void Unconfigured_and_duplicate_samples_cannot_expand_state_or_accelerate_isolation()
    {
        var tag = Binding("a");
        var policy = Policy(tag);
        var foreign = Enumerable.Range(0, 1000).Select(i => Binding("foreign" + i)).ToArray();
        for (int i = 0; i < 3; i++) policy.Record(foreign, foreign.Select(Unknown).ToArray(), i);
        Assert.Equal(0, policy.IsolatedCount);
        policy.Record([tag], [Unknown(tag), Unknown(tag), Unknown(tag)], 4);
        Assert.Single(policy.SelectDue([tag], 5, out _));
    }

    [Fact]
    public void Reconfigured_loop_policy_does_not_inherit_old_address_quarantine()
    {
        var oldTag = Binding("a");
        var oldPolicy = Policy(oldTag);
        for (int i = 0; i < 3; i++) oldPolicy.Record([oldTag], [Unknown(oldTag)], i);
        Assert.Empty(oldPolicy.SelectDue([oldTag], 3, out _));
        var newTag = Binding("a", "ns=2;s=Corrected");
        Assert.Single(Policy(newTag).SelectDue([newTag], 3, out _));
    }

    private static InvalidNodeReadPolicy Policy(TagBinding tag) => new(new Dictionary<string, TagBinding> { [tag.TagId] = tag });
    private static TagBinding Binding(string id, string address = "ns=2;s=Missing") => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = address, DataType = IgwDataType.Int16 },
        Address = new TagAddress(1, 0, 2),
    };
    private static TagSample Unknown(TagBinding tag) => new(tag.TagId, DataValue.BadAt(new StatusCode(0x80340000), DateTime.UtcNow));
}
