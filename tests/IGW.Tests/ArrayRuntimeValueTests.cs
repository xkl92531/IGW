using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayRuntimeValueTests
{
    [Fact]
    public void Quality_counts_are_cached_on_the_snapshot()
    {
        var value = PrimitiveArrayValue<short>.Create(
            ArrayShape.Create([4], [0]), [1, 2, 3, 4],
            [StatusCode.Good, StatusCode.BadTimeout, StatusCode.Uncertain, StatusCode.Good]);

        Assert.Equal(2, value.GoodCount);
        Assert.Equal(1, value.UncertainCount);
        Assert.Equal(1, value.BadCount);
    }

    [Fact]
    public void Mixed_element_quality_is_uncertain_and_bad_values_project_by_type()
    {
        var value = PrimitiveArrayValue<short>.Create(
            ArrayShape.Create([3], [1]), [10, 20, 30],
            [StatusCode.Good, StatusCode.BadTimeout, StatusCode.Good]);

        Assert.Equal(QualityClass.Uncertain, value.OverallQuality.Class);
        Assert.Equal(new object?[] { (short)10, null, (short)30 }, ArrayValueCodec.ToWireValues(value));
        var slice = Assert.IsType<PrimitiveArrayValue<short>>(
            value.Slice(new ArraySlice([2], [2])));
        Assert.Equal(new short[] { 20, 30 }, slice.Values.ToArray());
        Assert.Equal([2], slice.Shape.LowerBounds);
    }

    [Fact]
    public void Bad_strings_are_visible_as_bad_instead_of_looking_empty()
    {
        var value = PrimitiveArrayValue<string>.Create(
            ArrayShape.Create([2], [0]), ["ok", "stale"],
            [StatusCode.Good, StatusCode.BadCommFailure]);

        Assert.Equal(new object?[] { "ok", "Bad" }, ArrayValueCodec.ToWireValues(value));
    }

    [Fact]
    public void Factory_owns_its_buffers_and_published_values_are_immutable()
    {
        short[] source = [1, 2];
        StatusCode[] quality = [StatusCode.Good, StatusCode.Good];
        var value = PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), source, quality);

        source[0] = 99;
        quality[0] = StatusCode.Bad;

        Assert.Equal(new short[] { 1, 2 }, value.Values.ToArray());
        Assert.Equal(StatusCode.Good, value.ElementQuality.Span[0]);
    }

    [Fact]
    public void Data_value_compares_array_content_hashes_without_scalar_conversion()
    {
        var shape = ArrayShape.Create([2], [0]);
        var first = PrimitiveArrayValue<int>.Create(shape, [10, 20]);
        var same = PrimitiveArrayValue<int>.Create(shape, [10, 20]);
        var changed = PrimitiveArrayValue<int>.Create(shape, [10, 21]);
        var now = DateTime.UtcNow;

        Assert.True(new DataValue(first, StatusCode.Good, now, now)
            .ValueEquals(new DataValue(same, StatusCode.Good, now, now)));
        Assert.False(new DataValue(first, StatusCode.Good, now, now)
            .ValueEquals(new DataValue(changed, StatusCode.Good, now, now)));
    }
}
