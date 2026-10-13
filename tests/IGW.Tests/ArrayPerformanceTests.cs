using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayPerformanceTests
{
    [Fact]
    public async Task Million_primitive_elements_use_one_slot_and_qc_slice_has_bounded_allocations()
    {
        const int count = 1_000_000;
        await using var hub = new DataHub();
        var definition = new TagDef
        {
            Name = "Values", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Int16, Dimensions = [count], LowerBounds = [1], ElementStrideBytes = 2,
            },
        };
        hub.Register("Line.Plc.DB.Values", definition);
        var runtime = PrimitiveArrayValue<short>.Create(definition.Array.Shape, new short[count]);
        hub.Update("Line.Plc.DB.Values", DataValue.GoodAt(runtime, DateTime.UtcNow));

        _ = runtime.Slice(new ArraySlice([1], [100]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20; iteration++)
            _ = runtime.Slice(new ArraySlice([iteration * 100 + 1], [100]));
        long allocatedPerIteration = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;

        Assert.Single(hub.SortedTagIds);
        Assert.True(allocatedPerIteration < 100_000, $"QC slice allocated {allocatedPerIteration:N0} B/iteration");
    }
}
