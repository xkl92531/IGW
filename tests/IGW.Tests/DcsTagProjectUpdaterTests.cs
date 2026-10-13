using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagProjectUpdaterTests
{
    [Theory]
    [InlineData(0, "OpcUa.Client", "opc.tcp://10.0.0.8:4840", null, null)]
    [InlineData(1, "Siemens.S7", "10.0.0.8:102", "model", "S7-1200")]
    [InlineData(2, "Siemens.S7", "10.0.0.8:102", "model", "S7-300")]
    [InlineData(3, "Siemens.S7", "10.0.0.8:102", "model", "S7-400")]
    [InlineData(4, "Siemens.S7", "10.0.0.8:102", "model", "S7-1500")]
    [InlineData(5, "Siemens.S7", "10.0.0.8:102", "model", "S7-200Smart")]
    [InlineData(6, "Siemens.S7", "10.0.0.8:102", "model", "S7-200")]
    [InlineData(7, "Mitsubishi.Mc", "10.0.0.8:6000", "mcVersion", "Qna_3E")]
    [InlineData(9, "Omron.Cip", "10.0.0.8:44818", "path", "")]
    [InlineData(10, "Omron.Fins", "10.0.0.8:9600", null, null)]
    [InlineData(11, "Simulator", "", null, null)]
    public void Maps_communication_type_to_igw_driver_and_defaults(
        int communicationValue, string driverId, string endpoint, string? settingKey, string? settingValue)
    {
        var item = Update((DcsCommunicationType)communicationValue, "10.0.0.8", 0, "DB1.DBW0", "DB1.Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [item], out var updated, out var error), error);

        ChannelDef channel = Assert.Single(updated.Channels);
        DeviceDef device = Assert.Single(channel.Devices);
        Assert.Equal(driverId, channel.DriverId);
        Assert.Equal(endpoint, device.Endpoint);
        if (driverId == "OpcUa.Client") Assert.Equal(endpoint, channel.Settings["url"]);
        if (settingKey is not null) Assert.Equal(settingValue, device.Settings[settingKey]);
    }

    [Fact]
    public void Opc_ua_devices_on_different_servers_get_separate_upstream_channels()
    {
        var first = Update(DcsCommunicationType.OpcUa, "10.0.0.8", 4840, "ns=2;s=A", "A");
        var second = Update(DcsCommunicationType.OpcUa, "10.0.0.9", 4845, "ns=2;s=B", "B");

        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [first, second], out var updated, out var error), error);

        Assert.Equal(2, updated.Channels.Count);
        Assert.Equal(
            ["opc.tcp://10.0.0.8:4840", "opc.tcp://10.0.0.9:4845"],
            updated.Channels.Select(channel => channel.Settings["url"]).Order().ToArray());
    }

    [Fact]
    public void Non_zero_port_is_preserved()
    {
        var item = Update(DcsCommunicationType.S1500, "10.0.0.8", 1102, "DB1.DBW0", "Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [item], out var updated, out var error), error);

        Assert.Equal("10.0.0.8:1102", SingleDevice(updated).Endpoint);
    }

    [Fact]
    public void Repeating_same_add_is_idempotent_and_updates_definition()
    {
        var first = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "DB1.Value");
        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [first], out var once, out var firstError), firstError);
        var changed = first with { DbAddress = "DB1.DBW2", DataType = DcsTagDataType.Word };

        Assert.True(DcsTagProjectUpdater.TryBuild(once, [changed], out var twice, out var secondError), secondError);

        TagDef tag = Assert.Single(AllTags(twice));
        Assert.Equal("DB1.DBW2", tag.Address);
        Assert.Equal(IgwDataType.UInt16, tag.DataType);
        Assert.Equal("Word", tag.Metadata![DcsTagMetadata.DataType]);
    }

    [Fact]
    public void Existing_address_can_be_renamed_without_duplicate()
    {
        var first = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "OldName");
        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [first], out var once, out _));

        Assert.True(DcsTagProjectUpdater.TryBuild(
            once, [first with { TagName = "NewName" }], out var twice, out var error), error);

        Assert.Equal("NewName", Assert.Single(AllTags(twice)).Name);
    }

    [Fact]
    public void Conflicting_address_and_name_are_rejected_without_mutation()
    {
        var project = new IgwProject();
        var a = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "A");
        var b = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW2", "B");
        Assert.True(DcsTagProjectUpdater.TryBuild(project, [a, b], out var seeded, out _));
        string before = ProjectStore.ToJson(seeded);

        var conflict = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "B");
        Assert.False(DcsTagProjectUpdater.TryBuild(seeded, [conflict], out _, out var error));

        Assert.Contains("冲突", error);
        Assert.Equal(before, ProjectStore.ToJson(seeded));
    }

    [Fact]
    public void Unsupported_h5u_does_not_mutate_input_project()
    {
        var original = ExistingProject();
        string before = ProjectStore.ToJson(original);
        var item = Update(DcsCommunicationType.Inovance_H5U, "10.0.0.9", 0, "D0", "Value");

        Assert.False(DcsTagProjectUpdater.TryBuild(original, [item], out _, out var error));

        Assert.Contains("暂不支持", error);
        Assert.Equal(before, ProjectStore.ToJson(original));
    }

    [Fact]
    public void Invalid_item_rejects_entire_batch_before_clone_mutation()
    {
        var original = ExistingProject();
        string before = ProjectStore.ToJson(original);
        var valid = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "Value");
        var invalid = valid with { TagName = "" };

        Assert.False(DcsTagProjectUpdater.TryBuild(original, [valid, invalid], out _, out var error));

        Assert.Contains("名称", error);
        Assert.Equal(before, ProjectStore.ToJson(original));
    }

    [Fact]
    public void Mixed_drivers_use_internal_channels_but_resolve_under_common_alias()
    {
        var s7 = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "DB1.Value");
        var opc = Update(DcsCommunicationType.OpcUa, "10.0.0.9", 4841, "ns=2;s=Value", "Ua.Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [s7, opc], out var updated, out var error), error);
        var ids = PhysicalTagIds(updated);
        var resolver = new DcsTagAliasResolver(updated, ids);

        Assert.Equal(2, updated.Channels.Count);
        Assert.True(resolver.TryResolve("通用.S1500_10.0.0.8.DB1.Value", out _, out var s7Error), s7Error);
        Assert.True(resolver.TryResolve("通用.OpcUa_10.0.0.9.Ua.Value", out _, out var opcError), opcError);
    }

    [Fact]
    public void Existing_unrelated_project_content_is_preserved()
    {
        var original = ExistingProject();
        string unrelatedBefore = System.Text.Json.JsonSerializer.Serialize(original.Channels.Single(), ProjectStore.Options);
        var item = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(original, [item], out var updated, out var error), error);

        Assert.Equal(unrelatedBefore,
            System.Text.Json.JsonSerializer.Serialize(
                updated.Channels.Single(channel => channel.Name == "Existing"), ProjectStore.Options));
    }

    [Fact]
    public void Array_definition_requires_dimensions_and_preserves_lower_bounds()
    {
        var item = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "Values",
            DcsTagDataType.ShortArray) with
        {
            Dimensions = [4],
            LowerBounds = [1],
            ElementStrideBytes = 2,
        };

        Assert.True(DcsTagProjectUpdater.TryBuild(new IgwProject(), [item], out var updated, out var error), error);
        TagDef tag = Assert.Single(AllTags(updated));
        Assert.True(tag.IsArray);
        Assert.NotNull(tag.Array);
        Assert.Equal([4], tag.Array.Dimensions);
        Assert.Equal([1], tag.Array.LowerBounds);
        Assert.Equal(2, tag.Array.ElementStrideBytes);
    }

    [Fact]
    public void Array_definition_without_dimensions_is_rejected_explicitly()
    {
        var item = Update(DcsCommunicationType.S1500, "10.0.0.8", 0, "DB1.DBW0", "Values",
            DcsTagDataType.ShortArray);

        Assert.False(DcsTagProjectUpdater.TryBuild(new IgwProject(), [item], out _, out var error));
        Assert.Contains("数组维度", error);
    }

    [Fact]
    public void Add_tags_for_new_ip_ignores_bulk_managed_project_channel_and_lands_in_common()
    {
        IgwProject original = BulkProject();
        string bulkBefore = System.Text.Json.JsonSerializer.Serialize(original.Channels.Single(), ProjectStore.Options);
        var item = Update(DcsCommunicationType.S1500, "10.0.0.2", 0, "DB1.DBW0", "Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(original, [item], out var updated, out var error), error);

        Assert.Equal(2, updated.Channels.Count);
        ChannelDef common = updated.Channels.Single(channel => channel.Name == "通用");
        DeviceDef device = Assert.Single(common.Devices);
        Assert.Equal("S1500_10.0.0.2", device.Name);
        Assert.False(device.Settings.ContainsKey(ProjectBulkTagMetadata.ManagedByKey));
        Assert.Equal(bulkBefore, System.Text.Json.JsonSerializer.Serialize(
            updated.Channels.Single(channel => channel.Name == "泰兴线_PLC1"), ProjectStore.Options));
    }

    [Fact]
    public void Add_tags_prefers_existing_add_tags_channel_over_earlier_bulk_channel()
    {
        IgwProject original = BulkProject();
        Assert.True(DcsTagProjectUpdater.TryBuild(original,
            [Update(DcsCommunicationType.S1500, "10.0.0.2", 0, "DB1.DBW0", "A")], out var first, out var error), error);
        first.Channels.Single(channel => channel.Name == "通用").Name = "DCS";

        Assert.True(DcsTagProjectUpdater.TryBuild(first,
            [Update(DcsCommunicationType.S1500, "10.0.0.3", 0, "DB1.DBW0", "B")], out var second, out error), error);

        Assert.Equal(2, second.Channels.Count);
        Assert.Equal(["S1500_10.0.0.2", "S1500_10.0.0.3"],
            second.Channels.Single(channel => channel.Name == "DCS").Devices.Select(device => device.Name).ToArray());
        Assert.Single(second.Channels.Single(channel => channel.Name == "泰兴线_PLC1").Devices);
    }

    [Fact]
    public void Add_tags_for_bulk_managed_logical_device_is_rejected_and_bulk_device_untouched()
    {
        IgwProject original = BulkProject();
        string before = System.Text.Json.JsonSerializer.Serialize(original, ProjectStore.Options);
        var ok = Update(DcsCommunicationType.S1500, "10.0.0.2", 0, "DB1.DBW0", "Other");
        var clash = Update(DcsCommunicationType.S1500, "10.0.0.1", 0, "DB1.DBW2", "Value");

        Assert.False(DcsTagProjectUpdater.TryBuild(original, [ok, clash], out var updated, out var error));

        Assert.Same(original, updated);
        Assert.Contains("索引 1", error);
        Assert.Contains("S1500_10.0.0.1", error);
        Assert.Contains("批量注册", error);
        Assert.Contains("/api/v1/project/bulk/tags", error);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(original, ProjectStore.Options));
    }

    [Fact]
    public void Other_communication_type_on_bulk_ip_is_a_different_logical_device()
    {
        // 逻辑设备名 = {CommunicationType}_{ip}：同 IP 但 S1200 与批量的 S1500_ 不同名，按原逻辑进 通用
        var item = Update(DcsCommunicationType.S1200, "10.0.0.1", 0, "DB1.DBW0", "Value");

        Assert.True(DcsTagProjectUpdater.TryBuild(BulkProject(), [item], out var updated, out var error), error);

        Assert.Equal("S1200_10.0.0.1", Assert.Single(updated.Channels.Single(channel => channel.Name == "通用").Devices).Name);
    }

    [Fact]
    public void Aliases_resolve_for_both_bulk_and_add_tags_devices()
    {
        var item = Update(DcsCommunicationType.S1500, "10.0.0.2", 0, "DB1.DBW0", "Value");
        Assert.True(DcsTagProjectUpdater.TryBuild(BulkProject(), [item], out var updated, out var error), error);
        var ids = updated.Channels.SelectMany(channel => channel.Devices.SelectMany(device =>
            device.Tags.Select(tag => $"{channel.Name}.{device.Name}.{tag.Name}")
                .Concat(device.Groups.SelectMany(group => group.Tags.Select(tag =>
                    $"{channel.Name}.{device.Name}.{group.Name}.{tag.Name}"))))).ToArray();
        var resolver = new DcsTagAliasResolver(updated, ids);

        Assert.True(resolver.TryResolve("通用.S1500_10.0.0.1.DB1.Speed", out var bulk, out var bulkError), bulkError);
        Assert.Equal("泰兴线_PLC1.S7Plus.DB1.Speed", bulk);
        Assert.True(resolver.TryResolve("通用.S1500_10.0.0.2.Value", out var addTags, out var addError), addError);
        Assert.Equal("通用.S1500_10.0.0.2.Value", addTags);
    }

    /// <summary>批量注册建的工程通道：设备带 dcsLogicalDevice（供 通用.* 别名）与 dcsManagedBy=bulk。</summary>
    private static IgwProject BulkProject() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Id = "bulk-channel",
                Name = "泰兴线_PLC1",
                DriverId = "Siemens.S7",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "bulk-device",
                        Name = "S7Plus",
                        Endpoint = "10.0.0.1:102",
                        Settings = new()
                        {
                            ["model"] = "S7-1500",
                            [DcsTagMetadata.LogicalDevice] = "S1500_10.0.0.1",
                            [ProjectBulkTagMetadata.ManagedByKey] = ProjectBulkTagMetadata.ManagedByBulk,
                        },
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Id = "bulk-group",
                                Name = "DB1",
                                Tags = [new TagDef { Id = "bulk-tag", Name = "Speed", Address = "DB1.DBW0", DataType = IgwDataType.Int16 }],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static DcsTagUpdateModel Update(
        DcsCommunicationType communication,
        string ip,
        int port,
        string address,
        string name,
        DcsTagDataType dataType = DcsTagDataType.Short) => new()
        {
            CommunicationType = communication,
            Ip = ip,
            Port = port,
            DbAddress = address,
            TagName = name,
            DataType = dataType,
        };

    private static DeviceDef SingleDevice(IgwProject project) => Assert.Single(Assert.Single(project.Channels).Devices);

    private static List<TagDef> AllTags(IgwProject project) =>
        project.Channels.SelectMany(channel => channel.Devices).SelectMany(device => device.Tags).ToList();

    private static IReadOnlyList<string> PhysicalTagIds(IgwProject project) =>
        project.Channels.SelectMany(channel => channel.Devices.SelectMany(device =>
            device.Tags.Select(tag => $"{channel.Name}.{device.Name}.{tag.Name}"))).ToArray();

    private static IgwProject ExistingProject() => new()
    {
        Name = "正式项目",
        Channels =
        [
            new ChannelDef
            {
                Id = "existing-channel",
                Name = "Existing",
                DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "existing-device",
                        Name = "KeepMe",
                        Endpoint = "",
                        Tags =
                        [
                            new TagDef
                            {
                                Id = "existing-tag",
                                Name = "KeepTag",
                                Address = "R0",
                                DataType = IgwDataType.Int16,
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
