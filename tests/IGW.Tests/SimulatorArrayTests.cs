using IGW.Abstractions;
using IGW.Drivers.Simulator;
using Xunit;

namespace IGW.Tests;

public sealed class SimulatorArrayTests
{
    [Fact]
    public async Task Simulator_round_trips_multidimensional_struct_array_slice()
    {
        await using var driver = new SimulatorDeviceDriver();
        await driver.ConnectAsync(CancellationToken.None);
        var binding = CellStructArray();
        var first = Assert.IsType<StructArrayValue>((await driver.ReadAsync([binding], CancellationToken.None))[0].Value.Value);
        var originalIsNg = Assert.IsType<PrimitiveArrayValue<short>>(first.Members["IsNG"]);
        var untouchedBefore = originalIsNg.Values.Span[
            checked((int)first.Shape.GetLinearOffset([2, 0]))];
        var slice = Assert.IsType<StructArrayValue>(first.Slice(new ArraySlice([1, -1], [1, 2])));
        var changed = SetMember(slice, "IsNG", [1, 2]);

        var write = await driver.WriteArrayAsync(
            new ArrayWriteRequest(binding, new ArraySlice([1, -1], [1, 2]), changed),
            CancellationToken.None);
        var whole = Assert.IsType<StructArrayValue>((await driver.ReadAsync([binding], CancellationToken.None))[0].Value.Value);
        var isNg = Assert.IsType<PrimitiveArrayValue<short>>(whole.Members["IsNG"]);

        Assert.True(write.Status.IsGood);
        Assert.Equal((short)2, isNg.Values.Span[checked((int)whole.Shape.GetLinearOffset([1, 0]))]);
        Assert.Equal(untouchedBefore, isNg.Values.Span[checked((int)whole.Shape.GetLinearOffset([2, 0]))]);
    }

    [Theory]
    [InlineData(IgwDataType.Bool)]
    [InlineData(IgwDataType.Byte)]
    [InlineData(IgwDataType.Char)]
    [InlineData(IgwDataType.Int16)]
    [InlineData(IgwDataType.UInt16)]
    [InlineData(IgwDataType.Int32)]
    [InlineData(IgwDataType.UInt32)]
    [InlineData(IgwDataType.Int64)]
    [InlineData(IgwDataType.UInt64)]
    [InlineData(IgwDataType.Float)]
    [InlineData(IgwDataType.Double)]
    [InlineData(IgwDataType.String)]
    [InlineData(IgwDataType.WString)]
    [InlineData(IgwDataType.Bcd)]
    [InlineData(IgwDataType.LBcd)]
    [InlineData(IgwDataType.ByteArray)]
    public async Task Simulator_generates_every_primitive_array_type(IgwDataType type)
    {
        await using var driver = new SimulatorDeviceDriver();
        await driver.ConnectAsync(CancellationToken.None);
        var tag = new TagDef
        {
            Name = type.ToString(),
            Address = type.ToString(),
            DataType = type,
            Array = new ArrayDef
            {
                ElementType = type,
                Dimensions = [3],
                LowerBounds = [0],
                ElementStrideBytes = type is IgwDataType.Double or IgwDataType.Int64 or IgwDataType.UInt64 ? 8 : 4,
                StringLength = type is IgwDataType.String or IgwDataType.WString ? 32 : null,
            },
        };
        var binding = new TagBinding { TagId = $"C.D.{type}", Def = tag, Address = new TagAddress(0, 0, 1) };

        var value = (await driver.ReadAsync([binding], CancellationToken.None))[0].Value.Value;

        var array = Assert.IsAssignableFrom<ArrayRuntimeValue>(value);
        Assert.Equal(type, array.ElementType);
        Assert.Equal(3, array.Count);
    }

    private static StructArrayValue SetMember(StructArrayValue source, string name, short[] values)
    {
        var members = source.Members.ToDictionary(static item => item.Key, static item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        members[name] = PrimitiveArrayValue<short>.Create(source.Shape, values);
        return StructArrayValue.Create(source.Shape, source.Schema, members,
            source.ElementQuality.ToArray());
    }

    private static TagBinding CellStructArray()
    {
        var schema = new StructDef
        {
            Id = "Cell",
            Name = "Cell",
            SizeBytes = 6,
            Fields =
            [
                new StructFieldDef { Name = "Voltage", DataType = IgwDataType.Float, ByteOffset = 0 },
                new StructFieldDef { Name = "IsNG", JsonName = "isNg", DataType = IgwDataType.Int16, ByteOffset = 4 },
            ],
        };
        var tag = new TagDef
        {
            Name = "CellData",
            Address = "CellData",
            DataType = IgwDataType.Struct,
            Access = TagAccess.ReadWrite,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Struct,
                Dimensions = [2, 3],
                LowerBounds = [1, -2],
                ElementStrideBytes = 6,
                Struct = schema,
            },
        };
        return new TagBinding { TagId = "C.D.CellData", Def = tag, Address = new TagAddress(0, 0, 1) };
    }
}
