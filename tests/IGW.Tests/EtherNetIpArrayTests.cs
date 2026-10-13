using IGW.Abstractions;
using IGW.Drivers.EtherNetIP;
using Xunit;

namespace IGW.Tests;

public sealed class EtherNetIpArrayTests
{
    [Fact]
    public void Primitive_array_round_trips_little_endian_CIP_layout()
    {
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.Int32, Dimensions = [3], LowerBounds = [0], ElementStrideBytes = 4,
        };
        var value = PrimitiveArrayValue<int>.Create(definition.Shape, [1, -2, 300], elementType: IgwDataType.Int32);

        var encoded = EtherNetIpArrayCodec.Encode(value, definition);
        var decoded = Assert.IsType<PrimitiveArrayValue<int>>(EtherNetIpArrayCodec.Decode(
            definition, encoded, [StatusCode.Good, StatusCode.Good, StatusCode.Good]));

        Assert.Equal([1, -2, 300], decoded.Values.ToArray());
    }

    [Fact]
    public void Chunk_plan_is_bounded_and_keeps_one_logical_array()
    {
        Assert.Equal([(0, 4), (4, 4), (8, 2)],
            EtherNetIpArrayCodec.Chunks(10, 4).Select(x => (x.Start, x.Count)).ToArray());
    }

    [Fact]
    public void Udt_array_round_trips_members_and_partial_quality()
    {
        var schema = new StructDef
        {
            Id = "cell", Name = "Cell", SizeBytes = 8,
            Fields =
            [
                new StructFieldDef { Name = "Code", DataType = IgwDataType.Int32, ByteOffset = 0 },
                new StructFieldDef { Name = "Value", DataType = IgwDataType.Float, ByteOffset = 4 },
            ],
        };
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.Struct, Dimensions = [2], LowerBounds = [1],
            ElementStrideBytes = 8, Struct = schema,
        };
        var shape = definition.Shape;
        var value = StructArrayValue.Create(shape, schema, new Dictionary<string, ArrayRuntimeValue>
        {
            ["Code"] = PrimitiveArrayValue<int>.Create(shape, [10, 20], elementType: IgwDataType.Int32),
            ["Value"] = PrimitiveArrayValue<float>.Create(shape, [1.5f, 2.5f], elementType: IgwDataType.Float),
        });

        var decoded = Assert.IsType<StructArrayValue>(EtherNetIpArrayCodec.Decode(definition,
            EtherNetIpArrayCodec.Encode(value, definition), [StatusCode.Good, StatusCode.BadTimeout]));

        Assert.Equal(20, decoded.Members["Code"].GetBoxedValue(1));
        Assert.Equal(QualityClass.Uncertain, decoded.OverallQuality.Class);
    }
}
