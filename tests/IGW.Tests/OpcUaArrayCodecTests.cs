using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;
using IgwStatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Tests;

public sealed class OpcUaArrayCodecTests
{
    [Fact]
    public void Decodes_multidimensional_primitive_array_with_configured_plc_lower_bounds()
    {
        var tag = new TagDef
        {
            Name = "Matrix", Address = "ns=2;s=Matrix", DataType = IgwDataType.Int16,
            Array = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [2, 2], LowerBounds = [1, -1], ElementStrideBytes = 2 },
        };
        var source = new short[,] { { 1, 2 }, { 3, 4 } };

        var value = Assert.IsType<PrimitiveArrayValue<short>>(
            OpcUaArrayCodec.Decode(source, tag, IgwStatusCode.Good));

        Assert.Equal([1, 2, 3, 4], value.Values.ToArray());
        Assert.Equal([1, -1], value.Shape.LowerBounds);
        Assert.Equal(ValueRanks.TwoDimensions, OpcUaArrayCodec.ValueRank(value.Shape));
    }

    [Fact]
    public void Converts_extension_object_array_using_persisted_structure_schema()
    {
        var tag = StructTag();
        IEncodeable[] bodies =
        [
            new Argument { Name = "A55", ValueRank = 0 },
            new Argument { Name = "B66", ValueRank = 1 },
        ];
        var source = bodies.Select(body => new ExtensionObject(body)).ToArray();

        var value = Assert.IsType<StructArrayValue>(
            OpcUaArrayCodec.Decode(source, tag, IgwStatusCode.Good));

        Assert.Equal("A55", value.Members["Name"].GetBoxedValue(0));
        Assert.Equal(1, value.Members["ValueRank"].GetBoxedValue(1));
        var encoded = Assert.IsType<ExtensionObject[]>(OpcUaArrayCodec.Encode(value, source));
        Assert.Equal(2, encoded.Length);
        Assert.Equal("A55", Assert.IsType<Argument>(encoded[0].Body).Name);
    }

    private static TagDef StructTag() => new()
    {
        Name = "Cells", Address = "ns=2;s=Cells", DataType = IgwDataType.Struct,
        Array = new ArrayDef
        {
            ElementType = IgwDataType.Struct, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 8,
            Struct = new StructDef
            {
                Id = "cell", Name = "Cell", SizeBytes = 8,
                Fields =
                [
                    new StructFieldDef { Name = "Name", DataType = IgwDataType.String, StringLength = 20 },
                    new StructFieldDef { Name = "ValueRank", DataType = IgwDataType.Int32 },
                ],
            },
        },
    };
}
