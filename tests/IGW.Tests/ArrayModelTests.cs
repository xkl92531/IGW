using System.Text.Json;
using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayModelTests
{
    [Fact]
    public void Array_shape_preserves_rank_lower_bounds_and_struct_layout()
    {
        var tag = new TagDef
        {
            Name = "CellData",
            DataType = IgwDataType.Struct,
            IsArray = true,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Struct,
                Dimensions = [2, 3],
                LowerBounds = [1, -2],
                ElementStrideBytes = 172,
                Struct = new StructDef
                {
                    Id = "Cell",
                    Name = "Cell",
                    SizeBytes = 172,
                    Fields =
                    [
                        new StructFieldDef
                        {
                            Name = "IsNG",
                            JsonName = "isNg",
                            DataType = IgwDataType.Int16,
                            ByteOffset = 168,
                        },
                    ],
                },
            },
        };

        var copy = JsonSerializer.Deserialize<TagDef>(JsonSerializer.Serialize(tag));

        Assert.NotNull(copy);
        Assert.NotNull(copy.Array);
        Assert.Equal(2, copy.Array.Shape.Rank);
        Assert.Equal(6, copy.Array.Shape.ElementCount);
        Assert.Equal([2, 3], copy.Array.Dimensions);
        Assert.Equal([1, -2], copy.Array.LowerBounds);
        Assert.Equal("isNg", Assert.Single(copy.Array.Struct!.Fields).JsonName);
    }

    [Fact]
    public void Array_shape_converts_non_zero_indices_to_row_major_offsets()
    {
        var shape = ArrayShape.Create([2, 3], [1, -2]);

        Assert.Equal(0, shape.GetLinearOffset([1, -2]));
        Assert.Equal(5, shape.GetLinearOffset([2, 0]));
        Assert.Equal([2, -1], shape.GetIndices(4));
    }

    [Fact]
    public void Existing_enum_numeric_values_do_not_change_when_struct_is_added()
    {
        Assert.Equal(0, (int)IgwDataType.Bool);
        Assert.Equal(11, (int)IgwDataType.ByteArray);
        Assert.Equal(15, (int)IgwDataType.LBcd);
        Assert.True((int)IgwDataType.Struct > (int)IgwDataType.LBcd);
    }
}
