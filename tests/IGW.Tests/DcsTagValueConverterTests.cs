using System.Text.Json;
using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagValueConverterTests
{
    [Theory]
    [InlineData(0, IgwDataType.String, false, "String")]
    [InlineData(1, IgwDataType.Bool, false, "Boolean")]
    [InlineData(2, IgwDataType.Char, false, "Char")]
    [InlineData(3, IgwDataType.Byte, false, "Byte")]
    [InlineData(4, IgwDataType.Int16, false, "Short")]
    [InlineData(5, IgwDataType.UInt16, false, "Word")]
    [InlineData(6, IgwDataType.Int32, false, "Long")]
    [InlineData(7, IgwDataType.UInt32, false, "DWord")]
    [InlineData(8, IgwDataType.Float, false, "Float")]
    [InlineData(9, IgwDataType.Double, false, "Double")]
    [InlineData(12, IgwDataType.String, false, "Date")]
    [InlineData(20, IgwDataType.String, true, "StringArray")]
    [InlineData(21, IgwDataType.Bool, true, "BooleanArray")]
    [InlineData(23, IgwDataType.ByteArray, false, "ByteArray")]
    [InlineData(24, IgwDataType.Int16, true, "ShortArray")]
    [InlineData(25, IgwDataType.UInt16, true, "WordArray")]
    [InlineData(26, IgwDataType.Int32, true, "LongArray")]
    [InlineData(27, IgwDataType.UInt32, true, "DWordArray")]
    [InlineData(28, IgwDataType.Float, true, "FloatArray")]
    public void Maps_dcs_type_without_user_configuration(
        int sourceValue, IgwDataType target, bool isArray, string returnedName)
    {
        var source = (DcsTagDataType)sourceValue;
        Assert.True(DcsTagValueConverter.TryMapDefinition(source, out var mapped, out var error), error);
        Assert.Equal(target, mapped.DataType);
        Assert.Equal(isArray, mapped.IsArray);
        Assert.Equal(returnedName, DcsTagValueConverter.GetDcsTypeName(mapped));
    }

    [Fact]
    public void Rejects_unknown_dcs_type()
    {
        Assert.False(DcsTagValueConverter.TryMapDefinition((DcsTagDataType)999, out _, out var error));
        Assert.Contains("不支持", error);
    }

    [Fact]
    public void Converts_string_number_to_configured_short()
    {
        using var doc = JsonDocument.Parse("\"123\"");
        var tag = new TagDef { DataType = IgwDataType.Int16 };

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        Assert.Equal((short)123, value);
    }

    [Fact]
    public void Converts_numeric_boolean()
    {
        using var doc = JsonDocument.Parse("1");
        var tag = new TagDef { DataType = IgwDataType.Bool };

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        Assert.Equal(true, value);
    }

    [Fact]
    public void Rejects_byte_overflow_before_write()
    {
        using var doc = JsonDocument.Parse("256");
        var tag = new TagDef { DataType = IgwDataType.Byte };

        Assert.False(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out _, out var error));
        Assert.Contains("Byte", error);
    }

    [Fact]
    public void Rejects_one_bad_array_element_and_reports_index()
    {
        using var doc = JsonDocument.Parse("[1,70000,2]");
        var tag = new TagDef { DataType = IgwDataType.UInt16, IsArray = true };

        Assert.False(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out _, out var error));
        Assert.Contains("索引 1", error);
    }

    [Fact]
    public void Converts_typed_array()
    {
        using var doc = JsonDocument.Parse("[1,2,3]");
        var tag = new TagDef { DataType = IgwDataType.Int16, IsArray = true };

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        Assert.Equal(new short[] { 1, 2, 3 }, Assert.IsType<short[]>(value));
    }

    [Fact]
    public void Converts_persisted_array_to_runtime_value_and_wire_values()
    {
        using var doc = JsonDocument.Parse("[1,2,3]");
        var tag = new TagDef
        {
            DataType = IgwDataType.Int16,
            IsArray = true,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Int16,
                Dimensions = [3],
                LowerBounds = [1],
                ElementStrideBytes = 2,
            },
        };

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        var array = Assert.IsType<PrimitiveArrayValue<short>>(value);
        Assert.Equal(new short[] { 1, 2, 3 }, array.Values.ToArray());

        object? wire = DcsTagValueConverter.ToWireValue(
            new DataValue(array, StatusCode.Good, DateTime.UtcNow, DateTime.UtcNow));
        Assert.Equal(new object?[] { (short)1, (short)2, (short)3 }, Assert.IsType<object?[]>(wire));
    }

    [Fact]
    public void Converts_base64_byte_array()
    {
        using var doc = JsonDocument.Parse("\"AQID\"");
        var tag = new TagDef { DataType = IgwDataType.ByteArray };

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(value));
    }

    [Fact]
    public void Normalizes_date_to_iso_8601_and_preserves_date_identity()
    {
        using var doc = JsonDocument.Parse("\"2026-08-10 12:30:00+08:00\"");
        Assert.True(DcsTagValueConverter.TryMapDefinition(DcsTagDataType.Date, out var tag, out var mapError), mapError);

        Assert.True(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out var value, out var error), error);
        Assert.Equal("Date", DcsTagValueConverter.GetDcsTypeName(tag));
        Assert.Equal("2026-08-10T12:30:00.0000000+08:00", value);
    }

    [Fact]
    public void Rejects_null_write_value()
    {
        using var doc = JsonDocument.Parse("null");
        var tag = new TagDef { DataType = IgwDataType.String };

        Assert.False(DcsTagValueConverter.TryConvertWriteValue(doc.RootElement, tag, out _, out var error));
        Assert.Contains("String", error);
    }
}
