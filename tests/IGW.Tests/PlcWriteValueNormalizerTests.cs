using System.Text;
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class PlcWriteValueNormalizerTests
{
    static PlcWriteValueNormalizerTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void S7_string_is_truncated_by_utf8_bytes_without_splitting_chinese_character()
    {
        var def = new TagDef { DataType = IgwDataType.String, StringLength = 10 };

        string actual = Assert.IsType<string>(PlcWriteValueNormalizer.Normalize(def, "ABC中文测试"));

        Assert.Equal("ABC中文", actual);
        Assert.True(Encoding.UTF8.GetByteCount(actual) <= 10);
    }

    [Fact]
    public void Wstring_is_truncated_by_character_capacity()
    {
        var def = new TagDef { DataType = IgwDataType.WString, StringLength = 4 };

        Assert.Equal("AB中文", PlcWriteValueNormalizer.Normalize(def, "AB中文测试"));
    }

    [Fact]
    public void Wstring_truncation_does_not_split_a_surrogate_pair()
    {
        var def = new TagDef { DataType = IgwDataType.WString, StringLength = 2 };

        Assert.Equal("A😀", PlcWriteValueNormalizer.Normalize(def, "A😀B"));
    }

    [Fact]
    public void Explicit_gb18030_string_uses_configured_byte_capacity()
    {
        var def = new TagDef
        {
            DataType = IgwDataType.String,
            StringLength = 4,
            TextEncoding = PlcTextEncoding.Gb18030,
        };

        string actual = Assert.IsType<string>(PlcWriteValueNormalizer.Normalize(def, "中文A"));

        Assert.Equal("中文", actual);
        Assert.Equal(4, Encoding.GetEncoding(54936).GetByteCount(actual));
    }

    [Fact]
    public void Non_string_value_is_unchanged()
    {
        var def = new TagDef { DataType = IgwDataType.Float };

        Assert.Equal(12.5f, PlcWriteValueNormalizer.Normalize(def, 12.5f));
    }

    [Fact]
    public void Int16_value_from_json_number_is_normalized_to_short()
    {
        var def = new TagDef { DataType = IgwDataType.Int16 };

        object actual = PlcWriteValueNormalizer.Normalize(def, 1d);

        Assert.Equal((short)1, Assert.IsType<short>(actual));
    }

    [Fact]
    public void Int16_value_from_mes_response_string_is_normalized_to_short()
    {
        var def = new TagDef { DataType = IgwDataType.Int16 };

        object actual = PlcWriteValueNormalizer.Normalize(def, "2");

        Assert.Equal((short)2, Assert.IsType<short>(actual));
    }
}
