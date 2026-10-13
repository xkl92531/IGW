using System.Text.Json;
using System.Diagnostics;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Igw;
using IGW.ProjectExchange.Kep;
using Xunit;

namespace IGW.Tests;

public sealed class IgwExtensionRoundTripTests
{
    [Fact]
    public void Ten_thousand_sibling_tags_are_matched_in_near_linear_time()
    {
        IgwProject saved = RichProject();
        TagGroupDef savedGroup = saved.Channels[0].Devices[0].Groups[0];
        savedGroup.Tags = Enumerable.Range(0, 10_000)
            .Select(index => Tag($"saved-{index}", $"Value{index:D5}", $"R{index}"))
            .ToList();
        IgwProject kep = ProjectStore.Clone(saved);
        foreach (TagDef tag in kep.Channels[0].Devices[0].Groups[0].Tags) tag.Id = "";
        var elapsed = Stopwatch.StartNew();

        IgwExtensionReader.Validate(saved, kep);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"匹配耗时 {elapsed.Elapsed}，疑似退化为平方复杂度。");
    }

    [Fact]
    public void Extension_round_trip_preserves_all_fields_ids_and_duplicate_leaf_names()
    {
        IgwProject expected = RichProject();
        using JsonDocument extension = WriteExtension(expected);
        IgwProject kepLayer = ProjectStore.Clone(expected);
        ClearIds(kepLayer);

        IgwProject actual = new IgwExtensionReader().Merge(extension.RootElement, kepLayer);

        Assert.Equal(ProjectStore.ToJson(expected), ProjectStore.ToJson(actual));
        Assert.Equal("channel-1", actual.Channels[0].Id);
        Assert.Equal("device-1", actual.Channels[0].Devices[0].Id);
        Assert.Equal(["tag-a", "tag-b"], actual.Channels[0].Devices[0].Groups.SelectMany(g => g.Tags).Select(t => t.Id));
    }

    [Fact]
    public void Ambiguous_normalized_path_stops_merge_and_lists_candidates()
    {
        IgwProject saved = RichProject();
        saved.Channels[0].Devices[0].Groups =
        [
            new TagGroupDef { Id = "", Name = "AREA", Tags = [Tag("saved", "Value", "R1")] },
        ];
        IgwProject kep = RichProject();
        kep.Channels[0].Devices[0].Groups =
        [
            new TagGroupDef { Id = "kep-1", Name = "Area", Tags = [Tag("kep-a", "Value", "R1")] },
            new TagGroupDef { Id = "kep-2", Name = " area ", Tags = [Tag("kep-b", "Value", "R1")] },
        ];
        using JsonDocument extension = WriteExtension(saved);

        IgwExtensionMergeException error = Assert.Throws<IgwExtensionMergeException>(() =>
            new IgwExtensionReader().Merge(extension.RootElement, kep));

        Assert.Equal("igw_match_ambiguous", error.Code);
        Assert.Equal(2, error.Candidates.Count);
        Assert.All(error.Candidates, candidate => Assert.Contains("PLC8.Device8", candidate, StringComparison.Ordinal));
    }

    [Fact]
    public void Kep_and_igw_address_conflict_blocks_import()
    {
        IgwProject saved = RichProject();
        IgwProject kep = ProjectStore.Clone(saved);
        kep.Channels[0].Devices[0].Groups[0].Tags[0].Address = "R999";
        ClearIds(kep);
        using JsonDocument extension = WriteExtension(saved);

        IgwExtensionMergeException error = Assert.Throws<IgwExtensionMergeException>(() =>
            new IgwExtensionReader().Merge(extension.RootElement, kep));

        Assert.Equal("kep_igw_conflict", error.Code);
        Assert.Contains("PLC8.Device8.Area.Value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(IgwDataType.Dtl, false)]
    [InlineData(IgwDataType.Dtl, true)]
    [InlineData(IgwDataType.Struct, false)]
    [InlineData(IgwDataType.DateTime, false)]
    public void Types_kep_cannot_express_round_trip_through_the_kep_default_code(IgwDataType type, bool isArray)
    {
        IgwProject saved = RichProject();
        TagDef savedTag = saved.Channels[0].Devices[0].Groups[0].Tags[0];
        savedTag.DataType = type;
        savedTag.IsArray = isArray;
        // KEP 层读回的样子：编码 0/20(默认)，运行类型占位为 Int16，并保留原始编码。
        int code = KepTypeMap.ToCode(savedTag);
        Assert.Equal(isArray ? 20 : 0, code);
        IgwProject kep = ProjectStore.Clone(saved);
        TagDef kepTag = kep.Channels[0].Devices[0].Groups[0].Tags[0];
        kepTag.DataType = KepTypeMap.FromCode(code).Type;
        kepTag.KepProperties = new() { ["servermain.TAG_DATA_TYPE"] = JsonSerializer.SerializeToElement(code) };
        ClearIds(kep);
        using JsonDocument extension = WriteExtension(saved);

        IgwProject actual = new IgwExtensionReader().Merge(extension.RootElement, kep);

        TagDef merged = actual.Channels[0].Devices[0].Groups[0].Tags[0];
        Assert.Equal(type, merged.DataType);
        Assert.Equal(isArray, merged.IsArray);
    }

    [Fact]
    public void Kep_and_igw_type_conflict_between_expressible_types_still_blocks_import()
    {
        IgwProject saved = RichProject();
        IgwProject kep = ProjectStore.Clone(saved);
        TagDef kepTag = kep.Channels[0].Devices[0].Groups[0].Tags[0];
        kepTag.DataType = IgwDataType.Float;
        kepTag.KepProperties = new() { ["servermain.TAG_DATA_TYPE"] = JsonSerializer.SerializeToElement(8) };
        ClearIds(kep);
        using JsonDocument extension = WriteExtension(saved);

        IgwExtensionMergeException error = Assert.Throws<IgwExtensionMergeException>(() =>
            new IgwExtensionReader().Merge(extension.RootElement, kep));

        Assert.Equal("kep_igw_conflict", error.Code);
    }

    private static JsonDocument WriteExtension(IgwProject project)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new IgwExtensionWriter().Write(writer, project, KepCompatibilityReport.Compatible);
        return JsonDocument.Parse(stream.ToArray());
    }

    private static IgwProject RichProject() => new()
    {
        SchemaVersion = 3,
        Name = "完整工程",
        Description = "保留全部 IGW 配置",
        Settings = new ProjectSettings { RestPort = 48020, TagStream = new TagStreamSettings { MaxStreams = 100 } },
        MesUpload = new MesUploadSettings
        {
            Enabled = true,
            DefaultMesUrl = "http://mes.local",
            Tasks =
            [
                new MesTask
                {
                    Id = "task-1", Name = "上报", GroupRef = "PLC8.Device8.Area", DeviceRef = "PLC8.Device8",
                    Enabled = true, FlowAvailable = true,
                    Flow = new MesFlow
                    {
                        Nodes = [new MesNode { Id = "n1", Type = "Trigger", Title = "采集周期", X = 1, Y = 2 }],
                    },
                },
            ],
        },
        Channels =
        [
            new ChannelDef
            {
                Id = "channel-1", Name = "PLC8", DriverId = "Simulator", Description = "通道说明",
                Settings = new() { ["mode"] = "cycle" },
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "device-1", Name = "Device8", Endpoint = "127.0.0.1", Description = "设备说明",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Id = "group-a", Name = "Area", Description = "区域 A",
                                Tags = [Tag("tag-a", "Value", "R1")],
                            },
                            new TagGroupDef
                            {
                                Id = "group-b", Name = "Backup", Description = "区域 B",
                                Tags = [Tag("tag-b", "Value", "R2")],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static TagDef Tag(string id, string name, string address) => new()
    {
        Id = id,
        Name = name,
        Address = address,
        DataType = IgwDataType.Int16,
        Access = TagAccess.ReadWrite,
        ScanRateMs = 20,
        StringLength = 16,
        Description = "点说明",
        Metadata = new() { ["source"] = "test" },
        WriteConfirmation = new WriteConfirmationDef { Mode = WriteConfirmationMode.ReadBack },
    };

    private static void ClearIds(IgwProject project)
    {
        foreach (ChannelDef channel in project.Channels)
        {
            channel.Id = "";
            foreach (DeviceDef device in channel.Devices)
            {
                device.Id = "";
                ClearGroups(device.Groups);
                foreach (TagDef tag in device.Tags) tag.Id = "";
            }
        }
    }

    private static void ClearGroups(IEnumerable<TagGroupDef> groups)
    {
        foreach (TagGroupDef group in groups)
        {
            group.Id = "";
            foreach (TagDef tag in group.Tags) tag.Id = "";
            ClearGroups(group.SubGroups);
        }
    }
}
