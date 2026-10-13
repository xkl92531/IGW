using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class PlcTextEncodingProjectTests
{
    [Fact]
    public void Default_auto_mode_does_not_inflate_project_json()
    {
        var project = ProjectWith(new TagDef
        {
            Name = "Message",
            Address = "DB1.DBB0",
            DataType = IgwDataType.String,
        });

        string json = ProjectStore.ToJson(project);

        Assert.DoesNotContain("textEncoding", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explicit_encoding_round_trips_as_camel_case_enum()
    {
        var project = ProjectWith(new TagDef
        {
            Name = "Message",
            Address = "DB1.DBB0",
            DataType = IgwDataType.String,
            TextEncoding = PlcTextEncoding.Gb18030,
        });

        string json = ProjectStore.ToJson(project);
        var restored = System.Text.Json.JsonSerializer.Deserialize<IgwProject>(json, ProjectStore.Options)!;

        Assert.Contains("\"textEncoding\": \"gb18030\"", json);
        Assert.Equal(PlcTextEncoding.Gb18030,
            restored.Channels[0].Devices[0].Groups[0].Tags[0].TextEncoding);
    }

    private static IgwProject ProjectWith(TagDef tag) => new()
    {
        Name = "Encoding",
        Channels =
        [
            new ChannelDef
            {
                Name = "S7",
                DriverId = "siemens-s7",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "PLC1",
                        Groups = [new TagGroupDef { Name = "DB1", Tags = [tag] }],
                    },
                ],
            },
        ],
    };
}
