using System.Text.Json;
using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayValueApiTests
{
    [Fact]
    public void Json_converter_preserves_multidimensional_shape()
    {
        using var doc = JsonDocument.Parse("[[10,20],[30,40]]");
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.Int16,
            Dimensions = [2, 2],
            LowerBounds = [1, 5],
            ElementStrideBytes = 2,
        };

        var value = Assert.IsType<PrimitiveArrayValue<short>>(
            ArrayJsonValueConverter.FromJson(definition, definition.Shape, doc.RootElement));

        Assert.Equal([2, 2], value.Shape.Dimensions);
        Assert.Equal([1, 5], value.Shape.LowerBounds);
        Assert.Equal(new short[] { 10, 20, 30, 40 }, value.Values.ToArray());
    }

    [Fact]
    public void Json_converter_uses_structure_json_aliases()
    {
        using var doc = JsonDocument.Parse("[{\"mesCode\":\"A01\",\"ok\":true}]");
        var schema = new StructDef
        {
            Id = "Cell",
            Name = "Cell",
            SizeBytes = 12,
            Fields =
            [
                new StructFieldDef { Name = "DMCCode", JsonName = "mesCode", DataType = IgwDataType.String, StringLength = 8 },
                new StructFieldDef { Name = "IsOK", JsonName = "ok", DataType = IgwDataType.Bool, ByteOffset = 10, BitOffset = 0 },
            ],
        };
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.Struct,
            Dimensions = [1],
            LowerBounds = [0],
            ElementStrideBytes = 12,
            Struct = schema,
        };

        var value = Assert.IsType<StructArrayValue>(
            ArrayJsonValueConverter.FromJson(definition, definition.Shape, doc.RootElement));

        Assert.Equal("A01", Assert.IsType<PrimitiveArrayValue<string>>(value.Members["DMCCode"]).Values.Span[0]);
        Assert.True(Assert.IsType<PrimitiveArrayValue<bool>>(value.Members["IsOK"]).Values.Span[0]);
    }
}
