using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>工程配置右键「查看日志」：按工程路径查出这一项及其下级的全部日志。</summary>
public sealed class LogPathFilterTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private static bool Matches(string path, string message, string channel = "", string device = "", string target = "")
    {
        var request = new LogCenterRequest { From = At.AddMinutes(-1), To = At.AddMinutes(1), Path = path };
        var record = new LogRecord(LogRecordCodec.ToMicros(At), 2, "collection", message,
            new Dictionary<string, object?> { ["igw_channel"] = channel, ["igw_device"] = device, ["igw_target"] = target });
        return LogFilter.Create(request).Matches(record);
    }

    [Fact]
    public void Channel_path_finds_channel_logs_and_logs_of_its_devices_and_tags()
    {
        Assert.True(Matches("PLC6", "扫描完成", channel: "PLC6"));
        Assert.True(Matches("PLC6", "设备离线", device: "PLC6.D1"));                 // 设备日志只记了设备字段
        Assert.True(Matches("PLC6", "写入 PLC6.D1.Result 失败"));
        Assert.False(Matches("PLC6", "设备离线", device: "PLC66.D1"));
        Assert.False(Matches("PLC6", "写入 PLC66.D1.Result 失败"));
        Assert.False(Matches("PLC6", "写入 X.PLC6.Result 失败"));
        Assert.False(Matches("PLC6", "扫描完成", channel: "PLC7"));
    }

    [Fact]
    public void Device_and_group_paths_cover_their_tags_but_not_siblings()
    {
        Assert.True(Matches("PLC6.D1", "连接超时", device: "PLC6.D1"));
        Assert.False(Matches("PLC6.D1", "连接超时", device: "PLC6.D10"));
        Assert.True(Matches("PLC6.D1.OP051", "上报字段 PLC6.D1.OP051.Barcode 为空"));
        Assert.True(Matches("PLC6.D1.OP051", "读取 [PLC6.D1.OP051] 完成"));
        Assert.False(Matches("PLC6.D1.OP051", "上报字段 PLC6.D1.OP0510.Barcode 为空"));
        Assert.False(Matches("PLC6.D1.OP051", "连接超时", device: "PLC6.D1"));        // 设备整体的日志不算某个组的
    }

    [Theory]
    [InlineData("PLC6", true)]
    [InlineData("PLC6.D1", true)]
    [InlineData("PLC6.D1.OP051", true)]
    [InlineData("PLC6.D1.OP051.Sub", true)]
    [InlineData("PLC6.D1.OP05", false)]
    [InlineData("PLC6.D1.OP051.Sub.Tag", false)]
    public void Collection_owner_scope_matches_its_ancestors_without_relying_on_sampled_tag_values(string path, bool expected)
    {
        var filter = LogFilter.Create(new LogCenterRequest { From = At.AddMinutes(-1), To = At.AddMinutes(1), Path = path });
        var record = new LogRecord(LogRecordCodec.ToMicros(At), 2, "collection", "采集批次完成",
            new Dictionary<string, object?> { ["igw_scope"] = "PLC7.D1.Other, PLC6.D1.OP051.Sub", ["igw_task_ids"] = "task-other, task-sub" });
        Assert.Equal(expected, filter.Matches(record));
    }

    [Theory]
    [InlineData("PLC6.D1.Group", "PLC6.D1.Group(2)", false)]
    [InlineData("PLC6.D1.Group", "PLC6.D1.Group,2", false)]
    [InlineData("PLC6.D1.Group", "PLC6.D1.Group, 2", false)]
    [InlineData("PLC6.D1.Group", "PLC6.D1.Group.Sub", true)]
    [InlineData("PLC6.D1.Group(2)", "PLC6.D1.Group(2)", true)]
    [InlineData("PLC6.D1.Group, 2", "PLC6.D1.Group, 2.Sub", true)]
    public void Single_task_scope_preserves_punctuation_in_group_names(string path, string scope, bool expected)
    {
        var filter = LogFilter.Create(new LogCenterRequest { From = At.AddMinutes(-1), To = At.AddMinutes(1), Path = path });
        var record = new LogRecord(LogRecordCodec.ToMicros(At), 2, "interface", "MES HTTP 请求完成",
            new Dictionary<string, object?> { ["igw_device"] = "PLC6.D1", ["igw_scope"] = scope, ["igw_task_id"] = "task" });
        Assert.Equal(expected, filter.Matches(record));
    }

    [Theory]
    [InlineData("PLC6.D1.Other, PLC6.D1.Group(2)", false)]
    [InlineData("PLC6.D1.Other, PLC6.D1.Group,2", false)]
    [InlineData("PLC6.D1.Other, PLC6.D1.Group, 2", false)]
    [InlineData("PLC6.D1.Other, PLC6.D1.Group", true)]
    [InlineData("PLC6.D1.Other, PLC6.D1.Group.Sub", true)]
    public void Shared_collection_scopes_match_whole_paths_not_punctuation_prefixes(string scope, bool expected)
    {
        var filter = LogFilter.Create(new LogCenterRequest { From = At.AddMinutes(-1), To = At.AddMinutes(1), Path = "PLC6.D1.Group" });
        var record = new LogRecord(LogRecordCodec.ToMicros(At), 2, "collection", "采集批次完成",
            new Dictionary<string, object?> { ["igw_device"] = "PLC6.D1", ["igw_scope"] = scope, ["igw_task_ids"] = "other, task" });
        Assert.Equal(expected, filter.Matches(record));
    }

    [Fact]
    public void Tag_path_matches_whole_names_and_key_fields()
    {
        Assert.True(Matches("PLC6.D1.Result", "值越界", target: "PLC6.D1.Result"));
        Assert.True(Matches("PLC6.D1.Arr", "写入 PLC6.D1.Arr[3] 成功"));
        Assert.False(Matches("PLC6.D1.Result", "写入 PLC6.D1.Result2 成功"));
    }
}
