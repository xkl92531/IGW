using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// EtherNet/IP 上「读回来了但按配置的类型解不开」要和「根本没读到」区分开。
///
/// 原先这个驱动是一个笼统的 catch，两种失败都标成 StatusCode.Bad。后果有两个：
/// 引擎的隔离策略只认永久性失败码，于是类型配错的点位每个周期都被重读、每次都以同样
/// 方式失败；而且这个 catch 还会把 libplctag 的 tag 对象销毁重建——对解码失败毫无意义，
/// 纯属白花开销。
///
/// 按失败来源区分比按异常类型去猜可靠：ReadAsync 失败是链路问题，Decode 失败是配置问题。
/// </summary>
public sealed class EtherNetIpDecodeFailureTests
{
    private const uint BadDecodeError = 0x80070000;

    [Fact]
    public async Task A_value_that_cannot_be_decoded_is_marked_as_a_decode_error()
    {
        var tag = new DriverProtocolTimingTests.FakeNativeTag
        {
            DecodeValue = _ => throw new InvalidCastException("Int16 装不下 8 字节"),
        };
        await using var driver = DriverProtocolTimingTests.Eip(() => tag);

        var samples = await driver.ReadAsync([DriverProtocolTimingTests.Binding("Status")], default);

        var value = Assert.Single(samples).Value;
        Assert.False(value.IsGood);
        Assert.Equal(BadDecodeError, value.Quality.Code);
    }

    /// <summary>解码失败重建 tag 对象没有任何意义：下一次照样解不开。</summary>
    [Fact]
    public async Task A_decode_failure_does_not_throw_away_the_native_tag()
    {
        var tag = new DriverProtocolTimingTests.FakeNativeTag
        {
            DecodeValue = _ => throw new OverflowException("超出 Int16 范围"),
        };
        await using var driver = DriverProtocolTimingTests.Eip(() => tag);

        await driver.ReadAsync([DriverProtocolTimingTests.Binding("Status")], default);

        Assert.False(tag.Disposed);
    }

    /// <summary>反向保证：读都没读到是链路问题，不能被当成配置错误隔离掉。</summary>
    [Fact]
    public async Task A_failed_read_is_not_reported_as_a_decode_error()
    {
        await using var driver = DriverProtocolTimingTests.Eip(() =>
            new DriverProtocolTimingTests.FakeNativeTag { Read = _ => Task.FromException(new IOException("链路断了")) });

        var samples = await driver.ReadAsync([DriverProtocolTimingTests.Binding("Status")], default);

        var value = Assert.Single(samples).Value;
        Assert.False(value.IsGood);
        Assert.NotEqual(BadDecodeError, value.Quality.Code);
    }

    /// <summary>取消要原样抛出，不能被解码的 catch 吞成坏点。</summary>
    [Fact]
    public async Task Cancellation_during_decode_path_is_not_swallowed()
    {
        await using var driver = DriverProtocolTimingTests.Eip(() =>
            new DriverProtocolTimingTests.FakeNativeTag
            {
                DecodeValue = _ => throw new OperationCanceledException(new CancellationToken(true)),
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            driver.ReadAsync([DriverProtocolTimingTests.Binding("Status")], default));
    }
}
