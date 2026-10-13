using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayProjectValidatorTests
{
    [Theory]
    [InlineData(new int[] { 2, 3 }, new int[] { 0 }, "维度与下界数量不一致")]
    [InlineData(new int[] { 2, 0 }, new int[] { 0, 0 }, "数组维度必须大于零")]
    public void Rejects_invalid_shapes(int[] dimensions, int[] lowerBounds, string message)
    {
        var project = ProjectWithArray(dimensions, lowerBounds);

        var error = Assert.Throws<InvalidDataException>(
            () => ArrayProjectValidator.NormalizeAndValidate(project));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Legacy_is_array_without_shape_is_not_silently_read_as_scalar()
    {
        var project = ProjectWithLegacyArray(IgwDataType.Int16, "DB1.DBW0");

        var error = Assert.Throws<InvalidDataException>(
            () => ArrayProjectValidator.NormalizeAndValidate(project));

        Assert.Contains("数组长度缺失", error.Message);
    }

    [Fact]
    public void Missing_lower_bounds_are_normalized_to_zero_and_array_flag_is_set()
    {
        var project = ProjectWithArray([2, 3], []);
        var tag = project.Channels[0].Devices[0].Tags[0];
        tag.IsArray = false;

        ArrayProjectValidator.NormalizeAndValidate(project);

        Assert.True(tag.IsArray);
        Assert.Equal([0, 0], tag.Array!.LowerBounds);
    }

    [Fact]
    public void Rejects_overlapping_struct_fields()
    {
        var project = ProjectWithArray([2], [0]);
        var tag = project.Channels[0].Devices[0].Tags[0];
        tag.DataType = IgwDataType.Struct;
        tag.Array!.ElementType = IgwDataType.Struct;
        tag.Array.ElementStrideBytes = 4;
        tag.Array.Struct = new StructDef
        {
            Id = "Overlap",
            Name = "Overlap",
            SizeBytes = 4,
            Fields =
            [
                new StructFieldDef { Name = "A", DataType = IgwDataType.Int32, ByteOffset = 0 },
                new StructFieldDef { Name = "B", DataType = IgwDataType.Int16, ByteOffset = 2 },
            ],
        };

        var error = Assert.Throws<InvalidDataException>(
            () => ArrayProjectValidator.NormalizeAndValidate(project));

        Assert.Contains("字段布局重叠", error.Message);
    }

    private static IgwProject ProjectWithArray(int[] dimensions, int[] lowerBounds)
    {
        return ProjectWithTag(new TagDef
        {
            Id = "array",
            Name = "ArrayTag",
            Address = "DB1.DBW0",
            DataType = IgwDataType.Int16,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Int16,
                Dimensions = dimensions,
                LowerBounds = lowerBounds,
                ElementStrideBytes = 2,
            },
        });
    }

    private static IgwProject ProjectWithLegacyArray(IgwDataType type, string address)
    {
        return ProjectWithTag(new TagDef
        {
            Id = "legacy",
            Name = "LegacyArray",
            Address = address,
            DataType = type,
            IsArray = true,
        });
    }

    private static IgwProject ProjectWithTag(TagDef tag)
    {
        return new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel",
                    Name = "Channel",
                    DriverId = "simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device",
                            Name = "Device",
                            Tags = [tag],
                        },
                    ],
                },
            ],
        };
    }
}
