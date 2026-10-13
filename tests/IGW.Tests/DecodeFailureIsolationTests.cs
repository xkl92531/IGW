using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 类型转换失败的点位要停止读取，而不是每个扫描周期重试一次。
///
/// 现场的坑：点位的数据类型配错(PLC 里是 Int64，点表写成 Int16)，驱动能把这个点单独标成
/// BadDecodeError、不牵连同批其它点，但隔离策略原先只认 BadNodeIdUnknown，于是这个点
/// 每个周期都被重新读一次、每次都以同样的方式失败，白占请求预算，日志也一直刷。
/// 配置错误重读一万次还是同样的结果，所以和「符号找不到」一样按永久性失败隔离。
///
/// 反过来，通信类故障绝不能隔离：那是链路问题不是配置问题，链路一恢复点位自己就好了，
/// 隔离它们只会让恢复白白慢 30 秒，严重时整台设备刚恢复就有一半点位还在隔离期。
/// </summary>
public sealed class DecodeFailureIsolationTests
{
    private const uint BadDecodeError = 0x80070000;
    private const uint BadTypeMismatch = 0x80740000;

    [Theory]
    [InlineData(BadDecodeError)]
    [InlineData(BadTypeMismatch)]
    public void A_tag_that_keeps_failing_to_decode_stops_being_read(uint code)
    {
        var tag = Binding("Line1.OP040.Status.Code");
        var policy = Policy(tag);

        for (int i = 0; i < 3; i++) policy.Record([tag], [Bad(tag, code)], 100);

        Assert.True(policy.IsIsolated(tag.TagId));
        Assert.Equal(1, policy.IsolatedCount);
        Assert.Empty(policy.SelectDue([tag], 30_099, out _));      // 隔离期内不读
        Assert.Single(policy.SelectDue([tag], 30_100, out _));     // 30 秒后复探一次
    }

    [Theory]
    [InlineData(BadDecodeError)]
    [InlineData(BadTypeMismatch)]
    public void Two_failures_are_not_enough_to_isolate(uint code)
    {
        var tag = Binding("a");
        var policy = Policy(tag);

        policy.Record([tag], [Bad(tag, code)], 0);
        policy.Record([tag], [Bad(tag, code)], 1);

        Assert.False(policy.IsIsolated(tag.TagId));
        Assert.Single(policy.SelectDue([tag], 2, out _));
    }

    /// <summary>链路类故障不是配置错误：隔离它们会让链路恢复后的点位白等一个复探周期。</summary>
    [Theory]
    [InlineData(0x80050000u)]   // BadCommFailure
    [InlineData(0x800A0000u)]   // BadTimeout
    [InlineData(0x808A0000u)]   // BadNotConnected
    [InlineData(0x808B0000u)]   // BadDeviceFailure
    [InlineData(0x80000000u)]   // Bad(笼统)
    public void Communication_failures_are_never_isolated(uint code)
    {
        var tag = Binding("a");
        var policy = Policy(tag);

        for (int i = 0; i < 10; i++) policy.Record([tag], [Bad(tag, code)], i);

        Assert.False(policy.IsIsolated(tag.TagId));
        Assert.Equal(0, policy.IsolatedCount);
        Assert.Single(policy.SelectDue([tag], 11, out _));
    }

    /// <summary>把类型改对以后，下一轮复探读到好值就该立刻恢复常规扫描。</summary>
    [Fact]
    public void Fixing_the_configured_type_lifts_the_isolation()
    {
        var tag = Binding("a");
        var policy = Policy(tag);
        for (int i = 0; i < 3; i++) policy.Record([tag], [Bad(tag, BadDecodeError)], 100);
        Assert.True(policy.IsIsolated(tag.TagId));

        policy.Record([tag], [new TagSample(tag.TagId, DataValue.GoodAt(42, DateTime.UtcNow))], 30_100);

        Assert.False(policy.IsIsolated(tag.TagId));
        Assert.Equal(0, policy.IsolatedCount);
        Assert.Single(policy.SelectDue([tag], 30_101, out _));
    }

    /// <summary>一个配错类型的点位不能把同批的好点一起拖进隔离。</summary>
    [Fact]
    public void Only_the_misconfigured_tag_is_isolated()
    {
        var bad = Binding("bad");
        var good = Binding("good");
        var policy = new InvalidNodeReadPolicy(new Dictionary<string, TagBinding>
        {
            [bad.TagId] = bad,
            [good.TagId] = good,
        });

        for (int i = 0; i < 3; i++)
            policy.Record([bad, good],
                [Bad(bad, BadDecodeError), new TagSample(good.TagId, DataValue.GoodAt(1, DateTime.UtcNow))], 100);

        Assert.True(policy.IsIsolated(bad.TagId));
        Assert.False(policy.IsIsolated(good.TagId));
        Assert.Equal([good.TagId], policy.SelectDue([bad, good], 200, out _).Select(t => t.TagId));
    }

    /// <summary>解码失败和符号找不到交替出现，也还是连续的永久性失败，照样要收敛到隔离。</summary>
    [Fact]
    public void Mixed_permanent_failures_still_count_as_consecutive()
    {
        var tag = Binding("a");
        var policy = Policy(tag);

        policy.Record([tag], [Bad(tag, 0x80340000)], 0);
        policy.Record([tag], [Bad(tag, BadDecodeError)], 1);
        policy.Record([tag], [Bad(tag, BadTypeMismatch)], 2);

        Assert.True(policy.IsIsolated(tag.TagId));
    }

    private static InvalidNodeReadPolicy Policy(TagBinding tag) =>
        new(new Dictionary<string, TagBinding> { [tag.TagId] = tag });

    private static TagBinding Binding(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = "DB436.Status.Code", DataType = IgwDataType.Int16 },
        Address = new TagAddress(1, 0, 2),
    };

    private static TagSample Bad(TagBinding tag, uint code) =>
        new(tag.TagId, DataValue.BadAt(new StatusCode(code), DateTime.UtcNow));
}
