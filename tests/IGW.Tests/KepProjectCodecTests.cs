using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.ProjectExchange.Kep;
using Xunit;

namespace IGW.Tests;

public sealed class KepProjectCodecTests
{
    [Fact]
    public void Native_kep_hierarchy_round_trips_nested_groups_and_unknown_properties()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var codec = new TestCodec();
        KepProjectReadResult imported = new KepProjectReader([codec]).Read(fixture.RootElement);

        ChannelDef channel = Assert.Single(imported.Project.Channels);
        Assert.Equal("PLC8", channel.Name);
        DeviceDef device = Assert.Single(channel.Devices);
        Assert.Equal("127.0.0.1", device.Endpoint);
        Assert.Equal("Direct point", Assert.Single(device.Tags).Description);
        TagGroupDef area = Assert.Single(device.Groups);
        Assert.Equal("Area group", area.Description);
        TagDef nested = Assert.Single(Assert.Single(area.SubGroups).Tags);
        Assert.True(nested.IsArray);
        Assert.Equal(IgwDataType.Bool, nested.DataType);

        using JsonDocument exported = Export(imported.Project, codec);
        JsonElement exportedChannel = exported.RootElement.GetProperty("channels")[0];
        Assert.Equal(17, exportedChannel.GetProperty("servermain.CHANNEL_UNKNOWN").GetInt32());
        JsonElement exportedDevice = exportedChannel.GetProperty("devices")[0];
        Assert.True(exportedDevice.GetProperty("servermain.DEVICE_UNKNOWN").GetBoolean());
        JsonElement exportedTag = exportedDevice.GetProperty("tags")[0];
        Assert.Equal("native", exportedTag.GetProperty("servermain.TAG_UNKNOWN").GetProperty("mode").GetString());
        Assert.Equal("keep-group", exportedDevice.GetProperty("tag_groups")[0]
            .GetProperty("servermain.GROUP_UNKNOWN").GetString());
        Assert.False(exportedChannel.TryGetProperty("igw", out _));
    }

    [Fact]
    public void All_kep_type_codes_are_preserved_after_import_and_export()
    {
        var tags = Enumerable.Range(0, 35).Select(code => $$"""
            {"common.ALLTYPES_NAME":"T{{code}}","servermain.TAG_ADDRESS":"R{{code}}","servermain.TAG_DATA_TYPE":{{code}},"servermain.TAG_READ_WRITE_ACCESS":1,"servermain.TAG_SCAN_RATE_MILLISECONDS":100}
            """);
        string json = "{\"project\":{\"common.ALLTYPES_NAME\":\"P\",\"channels\":[" +
            "{\"common.ALLTYPES_NAME\":\"C\",\"servermain.MULTIPLE_TYPES_DEVICE_DRIVER\":\"Simulator\",\"devices\":[" +
            "{\"common.ALLTYPES_NAME\":\"D\",\"tags\":[" + string.Join(',', tags) +
            "],\"tag_groups\":[]}]}]}}";
        using JsonDocument source = JsonDocument.Parse(json);
        var codec = new TestCodec();

        IgwProject imported = new KepProjectReader([codec]).Read(source.RootElement).Project;
        using JsonDocument exported = Export(imported, codec);
        int[] codes = exported.RootElement.GetProperty("channels")[0].GetProperty("devices")[0]
            .GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("servermain.TAG_DATA_TYPE").GetInt32()).ToArray();

        Assert.Equal(Enumerable.Range(0, 35), codes);
    }

    private static JsonDocument Export(IgwProject project, IKepDriverCodec codec)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
            new KepProjectWriter([codec]).Write(writer, project);
        return JsonDocument.Parse(output.ToArray());
    }

    private static string FixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Kep", "common-nested-project.json");

    private sealed class TestCodec : IKepDriverCodec
    {
        public string IgwDriverId => "Simulator";
        public IReadOnlyCollection<string> KepDriverNames { get; } = ["Simulator"];
        public KepCompatibilityReport Inspect(ChannelDef channel) => KepCompatibilityReport.Compatible;
        public void WriteChannelProperties(Utf8JsonWriter writer, ChannelDef channel) =>
            writer.WriteString("servermain.MULTIPLE_TYPES_DEVICE_DRIVER", "Simulator");
        public void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device) =>
            writer.WriteString("servermain.DEVICE_ID_STRING", device.Endpoint);
        public void ReadChannelProperties(JsonElement source, ChannelDef target, IDictionary<string, JsonElement> unknown) =>
            target.DriverId = IgwDriverId;
        public void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown) =>
            target.Endpoint = source.TryGetProperty("servermain.DEVICE_ID_STRING", out JsonElement value)
                ? value.GetString() ?? ""
                : "";
    }
}
