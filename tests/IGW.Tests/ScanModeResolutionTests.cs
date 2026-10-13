using IGW.Core;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 扫描模式一旦被解析成「请求所有数据」，几十万点的设备就会按各自扫描率全量轮询，把整台 PLC 压满。
/// 原先的实现是「除了那一个中文串以外一律当 all」，配置里任何偏差都会静默走到那一步，
/// 现场还看不出是配置问题。所以认不出来的值要保守处理并留下告警。
/// </summary>
public sealed class ScanModeResolutionTests
{
    [Theory]
    [InlineData("遵循客户端指定的扫描速率", "active")]
    [InlineData("  遵循客户端指定的扫描速率  ", "active")]   // 前后空格不该改变语义
    [InlineData("active", "active")]                        // 别的调用方可能直接写英文键
    [InlineData("请求所有数据以指定的扫描速率", "all")]
    [InlineData("all", "all")]
    [InlineData("", "all")]                                 // 未配置：保持各调用方自己的默认
    public void Known_values_map_to_their_meaning(string configured, string expected) =>
        Assert.Equal(expected, RuntimeEngine.ScanModeKey(configured));

    [Theory]
    [InlineData("遵循客户端扫描速率")]      // 少了两个字
    [InlineData("按需采集")]
    [InlineData("Active")]
    public void An_unrecognised_value_warns_and_stays_on_demand(string configured)
    {
        string? warned = null;

        string mode = RuntimeEngine.ScanModeKey(configured, value => warned = value);

        Assert.Equal("active", mode);
        Assert.Equal(configured, warned);
    }

    [Fact]
    public void Recognised_values_do_not_warn()
    {
        bool warned = false;

        RuntimeEngine.ScanModeKey("请求所有数据以指定的扫描速率", _ => warned = true);

        Assert.False(warned);
    }
}
