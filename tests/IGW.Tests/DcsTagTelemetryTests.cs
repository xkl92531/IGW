using System.Reflection;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 接口日志只记 IGW 自己页面调的管理接口；第三方系统调的数据接口(DCS 兼容 /Tag、KEP 兼容 /iotgateway、
/// 读写点位、批量读值、实时订阅、历史/报警、设备在线状态)不记。
///
/// 早先为了排查 HSJM 停机/约束诊断「一键排查慢」放行过 /Tag(ab52978)，但现场外部系统每秒两百多次
/// /Tag/ReadTagsAsList，一天两千万条日志：日志中心查询一个任务名就超时，任务的接口调用、采集和状态记录全被淹没。
/// 现场要的是 IGW 里开启的任务调 MES 接口的记录(另有日志)，第三方的调用不需要记。
/// </summary>
public sealed class DcsTagTelemetryTests
{
    private static bool ShouldRecord(string path)
    {
        var method = typeof(IGW.Northbound.RestApi.IgwInterfaceTelemetry)
            .GetMethod("ShouldRecord", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("找不到 ShouldRecord");
        return (bool)method.Invoke(null, [new Microsoft.AspNetCore.Http.PathString(path)])!;
    }

    [Theory]
    [InlineData("/Tag/ReadTagsWithQuality")]
    [InlineData("/Tag/ReadTagsAsList")]
    [InlineData("/Tag/ReadTags")]
    [InlineData("/Tag/WriteTags")]
    [InlineData("/iotgateway/read")]
    [InlineData("/iotgateway/write")]
    [InlineData("/api/v1/tags")]
    [InlineData("/api/v1/tags/PLC.D.T1/value")]
    [InlineData("/api/v1/values")]
    [InlineData("/api/v1/values/read")]
    [InlineData("/api/v1/streams/tag-values")]
    [InlineData("/api/v1/history")]
    [InlineData("/api/v1/alarms")]
    [InlineData("/api/v1/devices")]
    public void Third_party_data_interfaces_are_not_recorded(string path) => Assert.False(ShouldRecord(path));

    [Theory]
    [InlineData("/api/v1/project/bulk/tags")]
    [InlineData("/api/v1/project")]
    [InlineData("/api/v1/devices/PLC1/browse")]
    [InlineData("/api/v1/channels/C1/reconnect")]
    public void Gateway_management_calls_keep_being_recorded(string path) => Assert.True(ShouldRecord(path));

    /// <summary>日志中心读自己的日志、健康检查每秒都在跑，记下来只会淹没真正的调用。</summary>
    [Theory]
    [InlineData("/api/v1/log-center/logs")]
    [InlineData("/api/v1/runtime-logs")]
    [InlineData("/api/v1/health")]
    public void Noisy_or_self_referential_paths_stay_excluded(string path) => Assert.False(ShouldRecord(path));

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/")]
    public void Unrelated_paths_are_not_recorded(string path) => Assert.False(ShouldRecord(path));
}
