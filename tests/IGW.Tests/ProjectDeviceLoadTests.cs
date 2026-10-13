using System.Linq;
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectDeviceLoadTests
{
    [Fact]
    public async Task Loaded_device_equals_the_same_device_from_full_load()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(ProjectBulkTagWriterTests.Request(
            ProjectBulkTagWriterTests.Item("A", "G1", "G2"), ProjectBulkTagWriterTests.Item("B")), null, default);
        var other = ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("X", "G9"));
        other.Device = other.Device with { Name = "S7" };
        await fx.Repository.BulkUpsertTagsAsync(other, null, default);

        IgwProject full = await fx.Repository.LoadRuntimeProjectAsync(default);
        DeviceDef expected = full.Channels[0].Devices.Single(d => d.Name == "S7Plus");
        ProjectDeviceSnapshot loaded = await fx.Repository.LoadDeviceAsync(expected.Id, default);

        Assert.Equal(ProjectStore.ToJson(new IgwProject { Channels = [new ChannelDef { Id = "c", Name = "c", DriverId = "d", Devices = [expected] }] }),
                     ProjectStore.ToJson(new IgwProject { Channels = [new ChannelDef { Id = "c", Name = "c", DriverId = "d", Devices = [loaded.Device] }] }));
        Assert.Equal(full.Channels[0].Id, loaded.Channel.Id);
        Assert.Empty(loaded.Channel.Devices);
    }

    [Fact]
    public async Task Missing_device_throws()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fx.Repository.LoadDeviceAsync("nope", default));
    }

    [Fact]
    public async Task Loaded_device_secret_setting_round_trips_to_plaintext()
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        var request = ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("A"));
        request.Device = request.Device with
        {
            Settings = new() { ["model"] = "S7-1500", ["password"] = "s3cr3t!" },
        };
        await fx.Repository.BulkUpsertTagsAsync(request, null, default);

        IgwProject full = await fx.Repository.LoadRuntimeProjectAsync(default);
        DeviceDef expected = full.Channels[0].Devices.Single();
        // 全量加载还原后 settings 里应是明文（vault.Restore 已在 LoadRuntimeProjectAsync 内部完成）。
        Assert.Equal("s3cr3t!", expected.Settings["password"]);

        ProjectDeviceSnapshot loaded = await fx.Repository.LoadDeviceAsync(expected.Id, default);

        Assert.Equal("s3cr3t!", loaded.Device.Settings["password"]);
        Assert.Equal("S7-1500", loaded.Device.Settings["model"]);
    }

    public static TheoryData<string> PartialCases => new() { "update", "new-group", "replace", "root-tags", "new-subgroup" };

    /// <summary>批量注册后只读回涉及的根组并与内存里的设备合并，结果必须与整台设备读回完全一致(含组顺序)。</summary>
    [Theory]
    [MemberData(nameof(PartialCases))]
    public async Task Partial_reload_merged_into_the_running_device_equals_a_full_reload(string change)
    {
        await using var fx = await ProjectBulkTagWriterTests.Fixture.CreateAsync(new IgwProject());
        await fx.Repository.BulkUpsertTagsAsync(ProjectBulkTagWriterTests.Request(
            ProjectBulkTagWriterTests.Item("A1", "GA", "Sub"), ProjectBulkTagWriterTests.Item("A2", "GA"),
            ProjectBulkTagWriterTests.Item("B1", "GB"), ProjectBulkTagWriterTests.Item("D1", "GD"),
            ProjectBulkTagWriterTests.Item("R1")), null, default);
        IgwProject running = await fx.Repository.LoadRuntimeProjectAsync(default);

        var request = change switch
        {
            "update" => ProjectBulkTagWriterTests.Request(Described(ProjectBulkTagWriterTests.Item("A2", "GA"), "改过")),
            "new-group" => ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("C1", "GC")),
            "replace" => ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("B9", "GB")),
            "root-tags" => ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("R2"), ProjectBulkTagWriterTests.Item("A3", "GA")),
            _ => ProjectBulkTagWriterTests.Request(ProjectBulkTagWriterTests.Item("S1", "GA", "Sub", "Deep")),
        };
        if (change == "replace") request.ReplaceGroups = [["GB"], ["GA", "Sub"]];
        var written = await fx.Repository.BulkUpsertTagsAsync(request, null, default);
        Assert.True(written.Applied, written.Error);

        var roots = request.Items!.Where(item => item.GroupPath is { Count: > 0 }).Select(item => item.GroupPath![0])
            .Concat((request.ReplaceGroups ?? []).Select(path => path[0])).Distinct().ToArray();
        bool includeRoot = request.Items!.Any(item => item.GroupPath is not { Count: > 0 });
        var partial = await fx.Repository.LoadDeviceGroupsAsync(written.DeviceId!, roots, includeRoot, default);
        var merged = ProjectBulkProjection.MergePartial(running, partial, roots, includeRoot);
        Assert.NotNull(merged);

        IgwProject full = await fx.Repository.LoadRuntimeProjectAsync(default);
        DeviceDef expected = full.Channels[0].Devices.Single();
        Assert.Equal(Json(expected), Json(merged!.Device));
        // 未涉及的根组复用内存里的对象，不是重新读出来的
        Assert.Same(running.Channels[0].Devices[0].Groups.Single(group => group.Name == "GD"),
            merged.Device.Groups.Single(group => group.Name == "GD"));
    }

    private static ProjectBulkTagItem Described(ProjectBulkTagItem item, string description)
    {
        item.Description = description;
        return item;
    }

    private static string Json(DeviceDef device) =>
        ProjectStore.ToJson(new IgwProject { Channels = [new ChannelDef { Id = "c", Name = "c", DriverId = "d", Devices = [device] }] });
}
