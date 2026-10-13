using System.Text.Json;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using IGW.Drivers.Mqtt;
using Xunit;

namespace IGW.Tests;

public sealed class RegisterAndMqttArrayTests
{
    [Fact]
    public void Register_codec_round_trips_word_order_and_partial_quality()
    {
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.UInt32, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 4,
        };
        byte[] bytes = [0, 1, 0, 2, 0, 3, 0, 4];
        var decoded = Assert.IsType<PrimitiveArrayValue<uint>>(RegisterArrayCodec.Decode(
            bytes, definition, WordOrder.ABCD, [StatusCode.Good, StatusCode.BadTimeout]));

        Assert.Equal([0x00010002u, 0x00030004u], decoded.Values.ToArray());
        Assert.Equal(QualityClass.Uncertain, decoded.OverallQuality.Class);
        Assert.Equal(bytes, RegisterArrayCodec.Encode(decoded, definition, WordOrder.ABCD));
    }

    [Fact]
    public void Mqtt_struct_array_round_trips_json_objects()
    {
        var schema = new StructDef
        {
            Id = "cell", Name = "Cell", SizeBytes = 4,
            Fields = [new StructFieldDef { Name = "IsNG", JsonName = "isNg", DataType = IgwDataType.Int16 }],
        };
        var tag = new TagDef
        {
            Name = "Cells", Address = "cells", DataType = IgwDataType.Struct, IsArray = true,
            Array = new ArrayDef { ElementType = IgwDataType.Struct, Dimensions = [2], LowerBounds = [1], ElementStrideBytes = 4, Struct = schema },
        };
        const string json = "[{\"isNg\":0},{\"isNg\":1}]";

        var value = MqttArrayCodec.Decode(json, tag, StatusCode.Good);
        using var encoded = JsonDocument.Parse(MqttArrayCodec.Encode(value));

        Assert.Equal(1, encoded.RootElement[1].GetProperty("isNg").GetInt16());
    }
}
