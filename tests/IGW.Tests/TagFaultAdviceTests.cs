using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 监控中心的点位异常要回答三件事：完整路径、为什么不对、需要怎么做。
/// 类型配错是现场最常见的一类，所以它必须有专属的说法，而不是掉进
/// 「点位质量异常（Bad，0x80740000）」这种看了等于没看的兜底文案。
/// </summary>
public sealed class TagFaultAdviceTests
{
    private static TagDef Def(IgwDataType type = IgwDataType.Int16) =>
        new() { Name = "Code", Address = "DB436.Status.Code", DataType = type };

    [Fact]
    public void A_type_mismatch_says_which_configured_type_is_wrong()
    {
        var (category, cause, suggestion) =
            TagFaultAdvice.For(new StatusCode(0x80740000), Def(IgwDataType.Int16), "siemens", null);

        Assert.Equal("TypeMismatch", category);
        Assert.Contains("Int16", cause);              // 配的是什么
        Assert.Contains("DB436.Status.Code", cause);  // 哪个点
        Assert.Contains("数据类型", suggestion);       // 要动的是数据类型，不是地址
    }

    /// <summary>类型对不上和解码失败现在都会停读，界面上必须说清楚，否则用户会以为点位被漏采了。</summary>
    [Theory]
    [InlineData(0x80070000u)]
    [InlineData(0x80740000u)]
    public void A_permanently_failing_tag_tells_the_user_it_stopped_being_read(uint code)
    {
        var (_, _, suggestion) = TagFaultAdvice.For(new StatusCode(code), Def(), "siemens", null);

        Assert.Contains("停止读取", suggestion);
        Assert.Contains("30 秒", suggestion);
    }

    /// <summary>通信类故障不停读，就不能说自己停读了——否则用户会去查一个不存在的隔离。</summary>
    [Theory]
    [InlineData(0x80050000u)]   // BadCommFailure
    [InlineData(0x800A0000u)]   // BadTimeout
    public void A_communication_failure_does_not_claim_to_be_isolated(uint code)
    {
        var (_, _, suggestion) = TagFaultAdvice.For(new StatusCode(code), Def(), "siemens", null);

        Assert.DoesNotContain("停止读取", suggestion);
    }

    [Fact]
    public void A_decode_failure_still_points_at_the_configured_type()
    {
        var (category, cause, _) =
            TagFaultAdvice.For(new StatusCode(0x80070000), Def(IgwDataType.Int16), "siemens", null);

        Assert.Equal("DecodeMismatch", category);
        Assert.Contains("Int16", cause);
    }

    /// <summary>驱动说得出具体原因时以驱动为准：它知道地址形式和型号这些网关看不到的上下文。</summary>
    [Fact]
    public void A_driver_supplied_reason_wins()
    {
        var (category, cause, _) = TagFaultAdvice.For(
            new StatusCode(0x80340000), Def(), "siemens", "这台设备是 S7-300，不支持符号地址");

        Assert.Equal("AddressNotRoutable", category);
        Assert.Equal("这台设备是 S7-300，不支持符号地址", cause);
    }
}
