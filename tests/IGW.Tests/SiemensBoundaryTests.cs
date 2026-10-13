using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public class SiemensBoundaryTests
{
    [Fact]
    public void Rest_scalar_write_preserves_full_integer_and_calendar_text()
    {
        using var timestamp = JsonDocument.Parse("\"DTL#2026-09-05-13:40:44.123456789\"");
        Assert.Equal("2026-09-05 13:40:44.123456789", IgwRestApi.JsonToValue(timestamp.RootElement, IgwDataType.Dtl));
        using var integer = JsonDocument.Parse("\"18446744073709551615\"");
        Assert.Equal(ulong.MaxValue, IgwRestApi.JsonToValue(integer.RootElement, IgwDataType.UInt64));
        using var character = JsonDocument.Parse("128");
        Assert.Equal("€", IgwRestApi.JsonToValue(character.RootElement, IgwDataType.S7Char));
    }

    [Fact]
    public void Semantic_array_json_and_element_edit_roundtrip_as_canonical_values()
    {
        var definition = new ArrayDef { ElementType = IgwDataType.Dtl, Dimensions = [2], LowerBounds = [-1] };
        using var input = JsonDocument.Parse("[\"DTL#2026-09-05-13:40:44.123456789\",\"2026-09-05 13:40:45.000000001\"]");
        var array = ArrayJsonValueConverter.FromJson(definition, definition.Shape, input.RootElement);
        Assert.Equal("2026-09-05 13:40:44.123456789", ArrayValueCodec.ToWireValues(array)[0]);
        var edited = ArrayValueEditor.ReplaceSingle(array, [-1], [], "DTL#2026-09-05-13:40:44.999999999");
        Assert.Equal("2026-09-05 13:40:44.999999999", edited.GetBoxedValue(0));
        var bad = ArrayBadShell.CreateBadShell(definition, StatusCode.BadCommFailure);
        Assert.NotNull(bad);
        Assert.Equal(IgwDataType.Dtl, bad.ElementType);
        Assert.All(ArrayValueCodec.ToWireValues(bad), Assert.Null);
    }

    [Fact]
    public void Dcs_write_uses_the_same_siemens_conversion_as_regular_rest()
    {
        using var input = JsonDocument.Parse("\"T#1h2m3s\"");
        Assert.True(DcsTagValueConverter.TryConvertWriteValue(input.RootElement, new TagDef { DataType = IgwDataType.Time }, out var value, out var error), error);
        Assert.Equal("T#3723000ms", value);
    }

    [Fact]
    public void Browser_values_keep_integers_beyond_double_precision_exact()
    {
        Assert.Equal("18446744073709551615", BrowserValueJson.Normalize(ulong.MaxValue));
        Assert.Equal("-9223372036854775808", BrowserValueJson.Normalize(long.MinValue));
        Assert.Equal(9007199254740991L, BrowserValueJson.Normalize(9007199254740991L));
        var array = PrimitiveArrayValue<ulong>.Create(ArrayShape.Create([2], [0]), [1, ulong.MaxValue], elementType: IgwDataType.LWord);
        Assert.Equal(new object[] { 1UL, "18446744073709551615" }, BrowserValueJson.ArrayValues(array));
    }
}
