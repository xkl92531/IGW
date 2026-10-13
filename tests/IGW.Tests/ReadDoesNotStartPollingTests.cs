using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 取一次当前值不该把点位拉进轮询。
///
/// 原先 /Tag 各读接口无条件 Touch 请求里的每个点，把它们按 activeWindowSec(默认 30 秒)
/// 判为活动、送进扫描计划。对 QC 盯屏那种"读了还要接着读"的场景是优化，对诊断就是纯浪费：
/// 一次约束诊断读上千个 IO 点、点完就走，却让这批点白白被轮询 30 秒。
///
/// 需要持续采集的调用方本来就该显式声明——MES 走 ScanDemandRegistry 租约，
/// 外部调用方传 ?activate=true。
///
/// 注意 Touch 在读路径上有三处(ReadOne / ReadWithQuality / ReadMany)，
/// 少改一处开关就会被另一处抵消，所以每条读接口都要覆盖。
/// </summary>
public sealed class ReadDoesNotStartPollingTests
{
    private static readonly string[] Tags = ["通用.Sim.DB1.A", "通用.Sim.DB1.B"];

    [Fact]
    public async Task Preparing_a_read_does_not_activate_polling_by_default()
    {
        var runtime = Runtime();

        await new DcsTagOperations(runtime).PrepareReadAsync(Tags, null, default);

        Assert.False(runtime.LastRefreshActivate);
        Assert.Empty(runtime.Touched);
    }

    /// <summary>值还是要拿到的：不在采集的点照常补读 PLC。</summary>
    [Fact]
    public async Task Idle_points_are_still_refreshed_from_the_plc()
    {
        var runtime = Runtime();

        await new DcsTagOperations(runtime).PrepareReadAsync(Tags, null, default);

        Assert.Equal(Tags, runtime.Refreshed);
    }

    [Fact]
    public async Task An_explicit_request_still_activates_polling()
    {
        var runtime = Runtime();

        await new DcsTagOperations(runtime).PrepareReadAsync(Tags, null, default, activate: true);

        Assert.True(runtime.LastRefreshActivate);
    }

    /// <summary>三条读接口各有一处 Touch，逐一确认默认都不激活。</summary>
    [Fact]
    public void No_read_path_activates_polling_by_default()
    {
        var single = Runtime();
        new DcsTagOperations(single).ReadOne(Tags[0], null);
        Assert.Empty(single.Touched);

        var quality = Runtime();
        new DcsTagOperations(quality).ReadWithQuality(Tags, null);
        Assert.Empty(quality.Touched);

        var list = Runtime();
        new DcsTagOperations(list).ReadAsList(Tags, null);
        Assert.Empty(list.Touched);
    }

    [Fact]
    public void Each_read_path_honours_an_explicit_activate()
    {
        var single = Runtime();
        new DcsTagOperations(single).ReadOne(Tags[0], null, activate: true);
        Assert.Contains(Tags[0], single.Touched);

        var quality = Runtime();
        new DcsTagOperations(quality).ReadWithQuality(Tags, null, activate: true);
        Assert.Contains(Tags[0], quality.Touched);

        var list = Runtime();
        new DcsTagOperations(list).ReadAsList(Tags, null, activate: true);
        Assert.Contains(Tags[0], list.Touched);
    }

    private static DcsTagTestRuntime Runtime() =>
        DcsTagTestRuntime.WithGoodTags((Tags[0], (short)1), (Tags[1], (short)2));
}
