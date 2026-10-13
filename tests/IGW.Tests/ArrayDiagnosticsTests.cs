using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayDiagnosticsTests
{
    [Fact]
    public void Partial_array_failure_reports_ranges_without_marking_device_offline()
    {
        var shape = ArrayShape.Create([200], [1]);
        var quality = Enumerable.Repeat(StatusCode.Good, 200).ToArray();
        for (int index = 99; index <= 118; index++) quality[index] = StatusCode.BadCommFailure;
        var value = PrimitiveArrayValue<short>.Create(shape, new short[200], quality);
        var definition = new ArrayDef
        {
            ElementType = IgwDataType.Int16, Dimensions = [200], LowerBounds = [1], ElementStrideBytes = 2,
        };

        var snapshot = ArrayHealthDiagnostics.Summarize("Line.Plc.DB.Values", value, definition,
            new ArrayDriverCapabilities(true, true, true, true, true, false, 64, 128));

        Assert.Equal("PartialOnline", snapshot.State);
        Assert.Contains("[100..119]", snapshot.Message);
        Assert.Equal(180, snapshot.Good);
        Assert.Equal(20, snapshot.Bad);
        Assert.Equal(4, snapshot.Blocks);
    }
}
