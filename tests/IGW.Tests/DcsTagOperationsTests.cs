using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagOperationsTests
{
    [Fact]
    public void Read_tag_returns_good_cached_value_without_starting_polling()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("通用.Sim.DB1.Value", 42);
        var operations = new DcsTagOperations(runtime);

        var result = operations.ReadOne("通用.Sim.DB1.Value", user: null);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Body.IsSuccess);
        Assert.Equal(42, result.Body.BackResult);
        // 读取不再顺带开启轮询；要激活得显式传 activate
        Assert.Empty(runtime.Touched);
        new DcsTagOperations(runtime).ReadOne("通用.Sim.DB1.Value", user: null, activate: true);
        Assert.Contains("通用.Sim.DB1.Value", runtime.Touched);
    }

    [Fact]
    public void Batch_read_preserves_order_and_dcs_json_string_shape()
    {
        var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1), ("B", "x"));

        var result = new DcsTagOperations(runtime).ReadAsJsonStrings(["B", "A"], null);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal(["\"x\"", "1"], result.Body.BackResult);
    }

    [Fact]
    public void Read_as_list_preserves_requested_key_and_value()
    {
        var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1), ("B", "x"));

        var result = new DcsTagOperations(runtime).ReadAsList(["B", "A"], null);

        Assert.True(result.Body.IsSuccess);
        Assert.Collection(result.Body.BackResult!,
            first => { Assert.Equal("B", first.Key); Assert.Equal("x", first.Value); },
            second => { Assert.Equal("A", second.Key); Assert.Equal(1, second.Value); });
    }

    [Fact]
    public void Read_with_quality_never_fails_the_request_for_one_bad_point_and_classifies_each_reason()
    {
        var def = new TagDef { DataType = IgwDataType.Int32 };
        var at = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var runtime = DcsTagTestRuntime.WithGoodTags(("Good", 7))
            .AddTag("Symbol", def, DataValue.BadAt(StatusCode.BadNodeIdUnknown, at))
            .AddTag("Comm", def, DataValue.BadAt(StatusCode.BadCommFailure, at))
            .AddTag("Slow", def, DataValue.BadAt(StatusCode.BadTimeout, at))
            .AddTag("FirstValue", def, DataValue.BadAt(StatusCode.BadNotConnected, at))
            .AddTag("Offline", def, DataValue.BadAt(StatusCode.BadNotConnected, at))
            .AddTag("Changed", def, DataValue.BadAt(StatusCode.BadWaitingForInitialData, at))
            .AddTag("Classic", def, DataValue.BadAt(StatusCode.Bad, at));
        runtime.DeviceStates["FirstValue"] = ConnectionState.Connected;   // 设备已连上，点还没有首个采集值
        runtime.DeviceStates["Offline"] = ConnectionState.Faulted;

        var result = new DcsTagOperations(runtime).ReadWithQuality(
            ["Good", "Symbol", "Comm", "Slow", "FirstValue", "Offline", "Changed", "Classic", "Missing", "Good"], null);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Body.IsSuccess);
        var rows = result.Body.BackResult!;
        Assert.Equal(["Good", "Symbol", "Comm", "Slow", "FirstValue", "Offline", "Changed", "Classic", "Missing", "Good"],
            rows.Select(row => row.Key));
        Assert.Equal([null, "SymbolNotFound", "CommFailure", "Timeout", "WaitingForData", "CommFailure", "WaitingForData",
            "DeviceError", "NotRegistered", null], rows.Select(row => row.Reason));
        Assert.True(rows[0].IsGood); Assert.Equal(7, rows[0].Value); Assert.Equal("Good", rows[0].Status);
        Assert.False(rows[1].IsGood); Assert.Null(rows[1].Value); Assert.Equal(0x80340000u, rows[1].StatusCode);
        Assert.Equal(at, rows[1].TimestampUtc);   // 隔离期间时间戳停在最后一次真实采集
        Assert.Equal(0x80340000u, rows[8].StatusCode); Assert.Null(rows[8].TimestampUtc);
    }

    [Fact]
    public void Read_with_quality_only_fails_the_request_for_bad_arguments_or_reload()
    {
        var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1));
        var operations = new DcsTagOperations(runtime);
        Assert.Equal(400, operations.ReadWithQuality([], null).StatusCode);
        Assert.Equal(400, operations.ReadWithQuality(["A", " "], null).StatusCode);
        Assert.Equal(400, operations.ReadWithQuality(Enumerable.Repeat("A", 1001).ToList(), null).StatusCode);
        Assert.Equal(200, operations.ReadWithQuality(Enumerable.Repeat("A", 1000).ToList(), null).StatusCode);
        runtime.Reloading = true;
        Assert.Equal(503, operations.ReadWithQuality(["A"], null).StatusCode);
    }

    [Fact]
    public void Read_as_dictionary_returns_requested_names()
    {
        var runtime = DcsTagTestRuntime.WithGoodTags(("A", 1), ("B", "x"));

        var result = new DcsTagOperations(runtime).ReadAsDictionary(["B", "A"], null);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal("x", result.Body.BackResult!["B"]);
        Assert.Equal(1, result.Body.BackResult["A"]);
    }

    [Fact]
    public void Dictionary_read_rejects_duplicate_names_without_throwing()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("A", 1);

        var result = new DcsTagOperations(runtime).ReadAsDictionary(["A", "a"], null);

        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Body.IsSuccess);
        Assert.Contains("重复", result.Body.ErrorMessage);
    }

    [Fact]
    public void Bad_quality_is_http_200_failure()
    {
        var runtime = DcsTagTestRuntime.WithBadTag("A", StatusCode.BadNotConnected);

        var result = new DcsTagOperations(runtime).ReadOne("A", null);

        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Body.IsSuccess);
        Assert.Contains("质量", result.Body.ErrorMessage);
    }

    [Fact]
    public async Task Prepared_read_fetches_idle_tags_from_the_plc_before_returning()
    {
        var runtime = DcsTagTestRuntime.WithBadTag("A", StatusCode.BadNotConnected);
        runtime.RefreshValue = _ => new DataValue(7, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow);
        var operations = new DcsTagOperations(runtime);

        await operations.PrepareReadAsync(["A", "Missing", " "], null, default);
        var result = operations.ReadOne("A", null);

        Assert.Equal(["A"], runtime.Refreshed);
        Assert.True(result.Body.IsSuccess);
        Assert.Equal(7, result.Body.BackResult);
    }

    [Fact]
    public void Unknown_tag_is_http_200_failure()
    {
        var result = new DcsTagOperations(new DcsTagTestRuntime()).ReadOne("Missing", null);

        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Body.IsSuccess);
        Assert.Contains("不存在", result.Body.ErrorMessage);
    }

    [Fact]
    public void Empty_batch_is_http_400_failure()
    {
        var result = new DcsTagOperations(new DcsTagTestRuntime()).ReadAsList([], null);

        Assert.Equal(400, result.StatusCode);
        Assert.False(result.Body.IsSuccess);
        Assert.Equal(400, result.Body.HttpStatusCode);
    }

    [Fact]
    public void Type_lookup_uses_automatic_dcs_type_name()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("A", (short)1);

        var result = new DcsTagOperations(runtime).GetTypes(["A"], null);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal("Short", result.Body.BackResult!["A"]);
    }

    [Fact]
    public void Branch_permission_is_checked_after_alias_resolution()
    {
        const string physical = "通用_S1500.Plc.DB1.Value";
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "通用_S1500",
                    DriverId = "Siemens.S7",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Settings = new() { [DcsTagMetadata.LogicalDevice] = "S1500_10.0.0.1" },
                        },
                    ],
                },
            ],
        };
        var runtime = DcsTagTestRuntime.WithGoodTag(physical, 1).WithProject(project);
        var user = new UserDef { ReadBranches = ["Other.Plc"] };

        var result = new DcsTagOperations(runtime).ReadOne("通用.S1500_10.0.0.1.DB1.Value", user);

        Assert.False(result.Body.IsSuccess);
        Assert.Contains("权限", result.Body.ErrorMessage);
        Assert.Empty(runtime.Touched);
    }

    [Fact]
    public void Branch_permission_rejects_a_sibling_db_with_the_same_prefix()
    {
        const string physical = "Line.Plc.DB100.Value";
        var runtime = DcsTagTestRuntime.WithGoodTag(physical, 1);
        var user = new UserDef { ReadBranches = ["Line.Plc.DB10"] };

        var result = new DcsTagOperations(runtime).ReadOne(physical, user);

        Assert.False(result.Body.IsSuccess);
        Assert.Contains("权限", result.Body.ErrorMessage);
        Assert.Empty(runtime.Touched);
    }

    [Fact]
    public void Condition_cache_compatibility_returns_successful_null()
    {
        var result = new DcsTagOperations(new DcsTagTestRuntime()).ReadConditionCache("GroupA");

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Body.IsSuccess);
        Assert.Null(result.Body.BackResult);
    }

    [Fact]
    public void Reloading_runtime_returns_service_unavailable_envelope()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("A", 1);
        runtime.Reloading = true;

        var result = new DcsTagOperations(runtime).ReadOne("A", null);

        Assert.Equal(503, result.StatusCode);
        Assert.False(result.Body.IsSuccess);
        Assert.Equal(503, result.Body.HttpStatusCode);
    }

    [Fact]
    public async Task Batch_validation_failure_prevents_every_write()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Byte)
            .AddTag("C.D.B",
                new TagDef { DataType = IgwDataType.Byte, Access = TagAccess.ReadWrite, ScanRateMs = 1 },
                DataValue.GoodAt((byte)0, DateTime.UtcNow));

        var result = await new DcsTagOperations(runtime).WriteManyAsync(
            [TagValue("C.D.A", "1"), TagValue("C.D.B", "999")], WritableUser(), default);

        Assert.False(result.Body.IsSuccess);
        Assert.Empty(runtime.Writes);
    }

    [Fact]
    public void Check_validity_never_writes_and_does_not_require_write_permission()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);

        var result = new DcsTagOperations(runtime).CheckValidity(
            [TagValue("C.D.A", "12")], new UserDef { CanWrite = false });

        Assert.True(result.Body.IsSuccess);
        Assert.True(result.Body.BackResult);
        Assert.Empty(runtime.Writes);
    }

    [Fact]
    public async Task Write_converts_string_number_and_confirms_read_back()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", "12"), WritableUser(), default);

        Assert.True(result.Body.IsSuccess);
        Assert.True(result.Body.BackResult);
        Assert.Single(runtime.Writes);
        Assert.Equal((short)12, runtime.Writes[0].Value);
    }

    [Fact]
    public async Task Write_retries_until_read_back_confirms()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);
        runtime.ConfirmOnWriteAttempt = 3;

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", 12), WritableUser(), default);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal(3, runtime.Writes.Count);
    }

    [Fact]
    public async Task Write_fails_after_initial_attempt_and_two_retries()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);
        runtime.ConfirmOnWriteAttempt = 4;

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", 12), WritableUser(), default);

        Assert.False(result.Body.IsSuccess);
        Assert.Equal(3, runtime.Writes.Count);
        Assert.Contains("回读", result.Body.ErrorMessage);
    }

    [Fact]
    public async Task Read_only_target_is_rejected_before_write()
    {
        var runtime = new DcsTagTestRuntime().AddTag(
            "C.D.A",
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.Read },
            DataValue.GoodAt((short)0, DateTime.UtcNow));

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", 1), WritableUser(), default);

        Assert.False(result.Body.IsSuccess);
        Assert.Contains("只读", result.Body.ErrorMessage);
        Assert.Empty(runtime.Writes);
    }

    [Fact]
    public async Task User_without_write_permission_is_rejected_before_write()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", 1), new UserDef { CanWrite = false }, default);

        Assert.False(result.Body.IsSuccess);
        Assert.Contains("写权限", result.Body.ErrorMessage);
        Assert.Empty(runtime.Writes);
    }

    [Fact]
    public async Task Driver_failure_is_retried_and_reported()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16);
        runtime.WriteHandler = (_, _, _) => StatusCode.BadNotConnected;

        var result = await new DcsTagOperations(runtime).WriteOneAsync(
            TagValue("C.D.A", 1), WritableUser(), default);

        Assert.False(result.Body.IsSuccess);
        Assert.Equal(3, runtime.Writes.Count);
        Assert.Contains("写入", result.Body.ErrorMessage);
    }

    [Fact]
    public async Task Different_devices_write_concurrently()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C1.D1.A", IgwDataType.Int16)
            .AddTag("C2.D2.B",
                new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 1 },
                DataValue.GoodAt((short)0, DateTime.UtcNow));
        int current = 0, maximum = 0;
        runtime.WriteAsyncHandler = async (_, _, _, ct) =>
        {
            int now = Interlocked.Increment(ref current);
            maximum = Math.Max(maximum, now);
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref current);
            return StatusCode.Good;
        };

        var result = await new DcsTagOperations(runtime).WriteManyAsync(
            [TagValue("C1.D1.A", 1), TagValue("C2.D2.B", 2)], WritableUser(), default);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task Same_device_writes_remain_sequential_and_ordered()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("C.D.A", IgwDataType.Int16)
            .AddTag("C.D.B",
                new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 1 },
                DataValue.GoodAt((short)0, DateTime.UtcNow));
        int current = 0, maximum = 0;
        runtime.WriteAsyncHandler = async (_, _, _, ct) =>
        {
            int now = Interlocked.Increment(ref current);
            maximum = Math.Max(maximum, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref current);
            return StatusCode.Good;
        };

        var result = await new DcsTagOperations(runtime).WriteManyAsync(
            [TagValue("C.D.A", 1), TagValue("C.D.B", 2)], WritableUser(), default);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal(1, maximum);
        Assert.Equal(["C.D.A", "C.D.B"], runtime.Writes.Select(item => item.TagId));
    }

    [Fact]
    public async Task Add_tags_reload_failure_returns_failure_and_never_changes_live_project()
    {
        var original = new IgwProject { Name = "Live" };
        var runtime = new DcsTagTestRuntime().WithProject(original);
        runtime.ReloadFailure = new InvalidOperationException("reload failed");
        string before = IGW.Core.ProjectStore.ToJson(runtime.Project);

        var result = await new DcsTagOperations(runtime).AddTagsAsync(
            [ValidUpdate()], WritableUser(), default);

        Assert.False(result.Body.IsSuccess);
        Assert.Equal(before, IGW.Core.ProjectStore.ToJson(runtime.Project));
        Assert.Single(runtime.Reloads);
    }

    [Fact]
    public async Task Add_tags_success_reloads_once_and_returns_null_string_result()
    {
        var runtime = new DcsTagTestRuntime();

        var result = await new DcsTagOperations(runtime).AddTagsAsync(
            [ValidUpdate()], WritableUser(), default);

        Assert.True(result.Body.IsSuccess);
        Assert.Null(result.Body.BackResult);
        Assert.Single(runtime.Reloads);
    }

    [Fact]
    public async Task Add_tags_requires_write_permission()
    {
        var runtime = new DcsTagTestRuntime();

        var result = await new DcsTagOperations(runtime).AddTagsAsync(
            [ValidUpdate()], new UserDef { CanWrite = false }, default);

        Assert.False(result.Body.IsSuccess);
        Assert.Empty(runtime.Reloads);
    }

    private static IgwProject SingleDeviceProject(string channelName, string deviceName, string logicalDevice) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = channelName,
                DriverId = "Siemens.S7",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = deviceName,
                        Settings = new() { [DcsTagMetadata.LogicalDevice] = logicalDevice },
                    },
                ],
            },
        ],
    };

    [Fact]
    public async Task Background_rebuild_keeps_old_reads_fast_and_new_device_becomes_readable_within_two_seconds()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("Old.Dev.Tag", 1);
        var operations = new DcsTagOperations(runtime);
        // 先读一次，建立初始（同步构建的）解析器。
        Assert.True(operations.ReadOne("Old.Dev.Tag", null).Body.IsSuccess);

        const string physicalNew = "ChannelN.DeviceN.Tag1";
        runtime.AddTag(physicalNew,
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.GoodAt((short)7, DateTime.UtcNow));
        // 整体换工程对象，模拟批量注册点位后的 scoped apply。
        runtime.WithProject(SingleDeviceProject("ChannelN", "DeviceN", "DevN"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var immediate = operations.ReadOne("Old.Dev.Tag", null);
        stopwatch.Stop();

        Assert.True(immediate.Body.IsSuccess);
        Assert.True(stopwatch.ElapsedMilliseconds < 50,
            $"新旧工程共有的旧点位应立即返回（用旧解析器，不等待后台重建），实际耗时 {stopwatch.ElapsedMilliseconds}ms");

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        DcsApiOutcome<object?> newResult;
        do
        {
            newResult = operations.ReadOne("通用.DevN.Tag1", null);
            if (newResult.Body.IsSuccess) break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);

        Assert.True(newResult.Body.IsSuccess,
            $"后台重建应在 2 秒内完成，让新工程独有的点位变为可解析；最终错误：{newResult.Body.ErrorMessage}");
        Assert.Equal((short)7, newResult.Body.BackResult);
    }

    [Fact]
    public async Task Two_rapid_project_swaps_eventually_resolve_only_the_newest_project()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("Old.Dev.Tag", 1);
        var operations = new DcsTagOperations(runtime);
        Assert.True(operations.ReadOne("Old.Dev.Tag", null).Body.IsSuccess);

        const string physicalB = "ChannelB.DeviceB.TagB";
        runtime.AddTag(physicalB,
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.GoodAt((short)11, DateTime.UtcNow));
        const string physicalC = "ChannelC.DeviceC.TagC";
        runtime.AddTag(physicalC,
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.GoodAt((short)22, DateTime.UtcNow));

        runtime.WithProject(SingleDeviceProject("ChannelB", "DeviceB", "DevB"));
        operations.ReadOne("Old.Dev.Tag", null); // 触发针对工程 B 的后台重建（可能已经在飞行中）
        runtime.WithProject(SingleDeviceProject("ChannelC", "DeviceC", "DevC")); // 重建完成前又切到工程 C，不能丢更新

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        DcsApiOutcome<object?> resultC;
        do
        {
            resultC = operations.ReadOne("通用.DevC.TagC", null);
            if (resultC.Body.IsSuccess) break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);

        Assert.True(resultC.Body.IsSuccess,
            $"连续两次快速切换工程后，最终必须能解析到最新工程 C 的点位（不丢更新）；最终错误：{resultC.Body.ErrorMessage}");
        Assert.Equal((short)22, resultC.Body.BackResult);

        // 稳定后应停在最新工程 C 上，过渡态工程 B 独有的点位不应再被解析出来。
        var resultBAfterSettling = operations.ReadOne("通用.DevB.TagB", null);
        Assert.False(resultBAfterSettling.Body.IsSuccess);
    }

    [Fact]
    public void First_ever_read_still_builds_resolver_synchronously()
    {
        const string physical = "ChannelS.DeviceS.TagS";
        var runtime = DcsTagTestRuntime.WithGoodTag(physical, (short)5)
            .WithProject(SingleDeviceProject("ChannelS", "DeviceS", "DevS"));
        var operations = new DcsTagOperations(runtime);

        // 全新实例、尚无旧解析器可退：第一次读取必须同步完成构建，一次调用即成功，不需要轮询重试。
        var result = operations.ReadOne("通用.DevS.TagS", null);

        Assert.True(result.Body.IsSuccess);
        Assert.Equal((short)5, result.Body.BackResult);
    }

    [Fact]
    public async Task Background_rebuild_failure_backs_off_instead_of_retrying_on_every_read()
    {
        var originalBackoff = DcsTagOperations.ResolverRebuildFailureBackoff;
        DcsTagOperations.ResolverRebuildFailureBackoff = TimeSpan.FromSeconds(5);
        try
        {
            var runtime = DcsTagTestRuntime.WithGoodTag("Old.Dev.Tag", 1);
            var logger = new CountingErrorLogger();
            var operations = new DcsTagOperations(runtime, logger);
            Assert.True(operations.ReadOne("Old.Dev.Tag", null).Body.IsSuccess);

            // 构造一个会让 DcsTagAliasResolver 构造函数抛异常的工程：分组 Tags 故意置 null，
            // EnumerateGroupedTags 内部 `foreach (TagDef tag in group.Tags)` 枚举 null 会抛 NullReferenceException。
            var broken = new IgwProject
            {
                Channels =
                [
                    new ChannelDef
                    {
                        Name = "ChannelBroken",
                        DriverId = "Siemens.S7",
                        Devices =
                        [
                            new DeviceDef
                            {
                                Name = "DeviceBroken",
                                Settings = new() { [DcsTagMetadata.LogicalDevice] = "DevBroken" },
                                Groups = [new TagGroupDef { Name = "G", Tags = null! }],
                            },
                        ],
                    },
                ],
            };
            runtime.WithProject(broken);

            // 第一次读取触发后台重建，该重建会失败一次；等它真正跑完（失败）。
            operations.ReadOne("Old.Dev.Tag", null);
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (logger.ErrorCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal(1, logger.ErrorCount);

            // 退避窗口内反复读取：不应该反复发起新的重建尝试（错误计数保持为 1），
            // 旧解析器（快照）应继续正常提供服务。
            for (int i = 0; i < 20; i++)
            {
                var result = operations.ReadOne("Old.Dev.Tag", null);
                Assert.True(result.Body.IsSuccess);
                await Task.Delay(10);
            }
            Assert.Equal(1, logger.ErrorCount);
        }
        finally
        {
            DcsTagOperations.ResolverRebuildFailureBackoff = originalBackoff;
        }
    }

    /// <summary>带大量填充设备的工程：让解析器重建耗时明显（数十毫秒级），后台重建不可能在切换后的下一次调用前完成。</summary>
    private static IgwProject LargeProjectWith(string channelName, string deviceName, string logicalDevice)
    {
        IgwProject project = SingleDeviceProject(channelName, deviceName, logicalDevice);
        for (int d = 0; d < 60; d++)
            project.Channels.Add(new ChannelDef
            {
                Name = "Filler" + d,
                DriverId = "Siemens.S7",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "D",
                        Settings = new() { [DcsTagMetadata.LogicalDevice] = "Filler" + d },
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Name = "G",
                                Tags = [.. Enumerable.Range(0, 1000).Select(i => new TagDef { Name = "T" + i })],
                            },
                        ],
                    },
                ],
            });
        return project;
    }

    [Fact]
    public void New_device_alias_is_readable_immediately_after_a_project_swap()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("Old.Dev.Tag", 1);
        var operations = new DcsTagOperations(runtime);
        Assert.True(operations.ReadOne("Old.Dev.Tag", null).Body.IsSuccess);

        runtime.AddTag("ChannelN.DeviceN.Tag1",
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.GoodAt((short)7, DateTime.UtcNow));
        runtime.AddTag("ChannelN.DeviceN.Tag2",
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.GoodAt((short)8, DateTime.UtcNow));
        runtime.WithProject(LargeProjectWith("ChannelN", "DeviceN", "DevN"));

        // 不轮询：切换后第一次读取新设备的别名就必须成功（未命中旧快照时等待/完成重建再查一次）
        var one = operations.ReadOne("通用.DevN.Tag1", null);
        Assert.True(one.Body.IsSuccess, one.Body.ErrorMessage);
        Assert.Equal((short)7, one.Body.BackResult);
        var many = operations.ReadAsList(["Old.Dev.Tag", "通用.DevN.Tag2"], null);
        Assert.True(many.Body.IsSuccess, many.Body.ErrorMessage);
    }

    [Fact]
    public async Task Write_after_logical_device_remap_targets_the_new_physical_device()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("ChannelA.DeviceA.T", IgwDataType.Int16);
        runtime.AddTag("ChannelB.DeviceB.T",
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 1 },
            DataValue.GoodAt((short)0, DateTime.UtcNow));
        runtime.WithProject(SingleDeviceProject("ChannelA", "DeviceA", "Dev"));
        var operations = new DcsTagOperations(runtime);

        var first = await operations.WriteOneAsync(TagValue("通用.Dev.T", 1), WritableUser(), default);
        Assert.True(first.Body.IsSuccess, first.Body.ErrorMessage);
        Assert.Equal("ChannelA.DeviceA.T", runtime.Writes[^1].TagId);

        // 逻辑设备 Dev 改挂到 ChannelB.DeviceB：写入绝不能经过旧快照写到旧 PLC
        runtime.WithProject(LargeProjectWith("ChannelB", "DeviceB", "Dev"));
        var second = await operations.WriteOneAsync(TagValue("通用.Dev.T", 2), WritableUser(), default);

        Assert.True(second.Body.IsSuccess, second.Body.ErrorMessage);
        Assert.Equal("ChannelB.DeviceB.T", runtime.Writes[^1].TagId);
        Assert.DoesNotContain(runtime.Writes.Skip(1), write => write.TagId == "ChannelA.DeviceA.T");
    }

    [Fact]
    public async Task Concurrent_writers_share_one_synchronous_fallback_build()
    {
        var runtime = DcsTagTestRuntime.WithWritableTag("ChannelA.DeviceA.T", IgwDataType.Int16);
        runtime.WithProject(SingleDeviceProject("ChannelA", "DeviceA", "Dev"));
        var operations = new DcsTagOperations(runtime) { CatchUpTimeout = TimeSpan.Zero };
        Assert.True((await operations.WriteOneAsync(TagValue("通用.Dev.T", 1), WritableUser(), default)).Body.IsSuccess);

        runtime.WithProject(LargeProjectWith("ChannelA", "DeviceA", "Dev"));
        using var start = new Barrier(8);
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            start.SignalAndWait();
            return await operations.WriteOneAsync(TagValue("通用.Dev.T", i), WritableUser(), default);
        })).ToArray();
        var results = await Task.WhenAll(writers);

        Assert.All(results, result => Assert.True(result.Body.IsSuccess, result.Body.ErrorMessage));
        Assert.True(operations.SynchronousBuildCount <= 1,
            $"等不到后台重建的写者应共享同一次同步构建，实际构建 {operations.SynchronousBuildCount} 次");
    }

    [Fact]
    public void Hits_on_unchanged_aliases_do_not_wait_for_the_rebuild()
    {
        var runtime = DcsTagTestRuntime.WithGoodTag("ChannelO.DeviceO.Tag", (short)3)
            .WithProject(SingleDeviceProject("ChannelO", "DeviceO", "DevO"));
        var operations = new DcsTagOperations(runtime);
        Assert.True(operations.ReadOne("通用.DevO.Tag", null).Body.IsSuccess);

        var next = LargeProjectWith("ChannelO", "DeviceO", "DevO");
        runtime.WithProject(next);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = operations.ReadOne("通用.DevO.Tag", null);
        watch.Stop();

        Assert.True(result.Body.IsSuccess);
        Assert.True(watch.ElapsedMilliseconds < 50, $"命中未变化别名不应等待重建，实际 {watch.ElapsedMilliseconds}ms");
    }

    private sealed class CountingErrorLogger : ILogger
    {
        private int _errorCount;
        public int ErrorCount => Volatile.Read(ref _errorCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Interlocked.Increment(ref _errorCount);
        }
    }

    private static UserDef WritableUser() => new() { CanWrite = true };

    private static DcsTagValue TagValue(string key, object value) =>
        new(key, JsonSerializer.SerializeToElement(value, DcsJson.Options));

    private static DcsTagUpdateModel ValidUpdate() => new()
    {
        Ip = "10.0.0.8",
        Port = 0,
        CommunicationType = DcsCommunicationType.S1500,
        DbAddress = "DB1.DBW0",
        DataType = DcsTagDataType.Short,
        TagName = "Value",
    };
}
