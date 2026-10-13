using IGW.Abstractions;
using IGW.Persistence.Sqlite;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 批量注册方对「决定采集规模」的几个设置说了算。
///
/// 现场踩过的坑：某台被批量托管的设备，扫描模式在配置器里被改成「请求所有数据以指定的扫描速率」，
/// 于是这台设备几十万个点按各自扫描率全量轮询，把整台 PLC 压满；而批量注册合并设置时用的是
/// 「只补缺失键」，下一次注册纠正不回来，只能手工去改。其余设置仍以现场为准，不覆盖。
/// </summary>
public sealed class BulkDeviceSettingsAuthorityTests
{
    private static DeviceDef BulkManaged(params (string Key, string Value)[] settings)
    {
        var device = new DeviceDef { Id = "dev-1", Name = "S7Plus" };
        device.Settings[ProjectBulkTagMetadata.ManagedByKey] = ProjectBulkTagMetadata.ManagedByBulk;
        foreach (var (key, value) in settings) device.Settings[key] = value;
        return device;
    }

    [Theory]
    [InlineData("scanMode")]
    [InlineData("activeWindowSec")]
    [InlineData("demote")]
    public void Bulk_managed_devices_take_the_request_value_for_scale_settings(string key)
    {
        var device = BulkManaged((key, "被现场改过的值"));

        bool changed = ProjectBulkTagWriter.MergeDeviceSettings(device, new Dictionary<string, string>
        {
            [key] = "批量请求的值",
        });

        Assert.True(changed);
        Assert.Equal("批量请求的值", device.Settings[key]);
    }

    [Fact]
    public void Other_settings_keep_what_the_site_configured()
    {
        var device = BulkManaged(("timeoutMs", "8000"), ("model", "S7-1500"));

        bool changed = ProjectBulkTagWriter.MergeDeviceSettings(device, new Dictionary<string, string>
        {
            ["timeoutMs"] = "2000",
            ["model"] = "S7-1200",
            ["rack"] = "0",           // 缺失键仍然补上
        });

        Assert.True(changed);
        Assert.Equal("8000", device.Settings["timeoutMs"]);
        Assert.Equal("S7-1500", device.Settings["model"]);
        Assert.Equal("0", device.Settings["rack"]);
    }

    [Fact]
    public void A_device_not_managed_by_bulk_is_never_overwritten()
    {
        var device = new DeviceDef { Id = "dev-2", Name = "手工配置的设备" };
        device.Settings["scanMode"] = "请求所有数据以指定的扫描速率";

        bool changed = ProjectBulkTagWriter.MergeDeviceSettings(device, new Dictionary<string, string>
        {
            ["scanMode"] = "遵循客户端指定的扫描速率",
        });

        Assert.False(changed);
        Assert.Equal("请求所有数据以指定的扫描速率", device.Settings["scanMode"]);
    }

    [Fact]
    public void Merging_the_same_values_reports_no_change()
    {
        var device = BulkManaged(("scanMode", "遵循客户端指定的扫描速率"));

        bool changed = ProjectBulkTagWriter.MergeDeviceSettings(device, new Dictionary<string, string>
        {
            ["scanMode"] = "遵循客户端指定的扫描速率",
        });

        Assert.False(changed);
    }
}
