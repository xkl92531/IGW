using System.Reflection;
using IGW.Abstractions;
using IGW.Northbound.OpcUaServer;
using Xunit;

namespace IGW.Tests;

public class SiemensNorthboundTests
{
    [Theory]
    [InlineData(IgwDataType.Dtl)]
    [InlineData(IgwDataType.Ltod)]
    [InlineData(IgwDataType.S7Char)]
    public void Communication_failure_shell_publishes_null_String_elements_without_throwing(IgwDataType type)
    {
        var definition = new ArrayDef { ElementType = type, Dimensions = [2], LowerBounds = [0] };
        var shell = ArrayBadShell.CreateBadShell(definition, IGW.Abstractions.StatusCode.BadCommFailure)!;
        var wire = Assert.IsType<string[]>(Encode(shell));
        Assert.All(wire, Assert.Null);
        Assert.True(shell.OverallQuality.IsBad);
    }

    private static readonly Type Codec = typeof(IgwJsonStructure).Assembly.GetType("IGW.Northbound.OpcUaServer.IgwOpcUaArrayCodec")!;
    private static ArrayRuntimeValue Decode(object input, ArrayDef definition) =>
        (ArrayRuntimeValue)Codec.GetMethod("Decode")!.Invoke(null, [input, definition])!;
    private static Array Encode(ArrayRuntimeValue input) => (Array)Codec.GetMethod("Encode")!.Invoke(null, [input])!;

    [Theory]
    [InlineData(IgwDataType.Dtl, "2024-02-29 12:34:56.123456789")]
    [InlineData(IgwDataType.Ldt, "2024-02-29 12:34:56.123456789")]
    [InlineData(IgwDataType.Dt, "2024-02-29 12:34:56.123")]
    [InlineData(IgwDataType.Date, "2024-02-29")]
    [InlineData(IgwDataType.Tod, "12:34:56.123")]
    [InlineData(IgwDataType.Ltod, "12:34:56.123456789")]
    [InlineData(IgwDataType.Time, "T#-42ms")]
    [InlineData(IgwDataType.Ltime, "LT#-42ns")]
    [InlineData(IgwDataType.S5Time, "S5T#100ms")]
    [InlineData(IgwDataType.S7Char, "A")]
    [InlineData(IgwDataType.WChar, "中")]
    [InlineData(IgwDataType.DateTime, "2024-02-29T12:34:56.1234567Z")]
    public void Semantic_arrays_are_canonical_UA_String_arrays_in_both_directions(IgwDataType type, string text)
    {
        var definition = new ArrayDef { ElementType = type, Dimensions = [1, 2], LowerBounds = [-2, 3] };
        var input = new string[,] { { text, text } };
        var runtime = Decode(input, definition);
        Assert.Equal(SiemensValueCodec.Normalize(type, text), runtime.GetBoxedValue(1));
        var wire = Encode(runtime);
        Assert.IsType<string[,]>(wire);
        Assert.Equal(2, wire.Rank);
        var readback = Decode(wire, definition);
        Assert.Equal(runtime.GetBoxedValue(1), readback.GetBoxedValue(1));
    }

    [Theory]
    [InlineData(IgwDataType.SInt, "-128", typeof(sbyte))]
    [InlineData(IgwDataType.Word, "65535", typeof(ushort))]
    [InlineData(IgwDataType.Timer, "65535", typeof(ushort))]
    [InlineData(IgwDataType.Counter, "65535", typeof(ushort))]
    [InlineData(IgwDataType.DWord, "4294967295", typeof(uint))]
    [InlineData(IgwDataType.LWord, "18446744073709551615", typeof(ulong))]
    public void Semantic_numeric_arrays_keep_declared_exact_UA_integer_width(IgwDataType type, string text, Type clrType)
    {
        var definition = new ArrayDef { ElementType = type, Dimensions = [1], LowerBounds = [0] };
        var runtime = Decode(new[] { text }, definition);
        var wire = Encode(runtime);
        Assert.Equal(clrType, wire.GetType().GetElementType());
        Assert.Equal(SiemensValueCodec.Normalize(type, text), wire.GetValue(0));
        Assert.Equal(runtime.GetBoxedValue(0), Decode(wire, definition).GetBoxedValue(0));
    }

    [Fact]
    public void Semantic_array_write_rejects_invalid_calendar_instead_of_accepting_arbitrary_text()
    {
        var definition = new ArrayDef { ElementType = IgwDataType.Dtl, Dimensions = [1], LowerBounds = [0] };
        var exception = Assert.Throws<TargetInvocationException>(() => Decode(new[] { "2024-02-30 12:34:56.123456789" }, definition));
        Assert.IsNotType<NotSupportedException>(exception.InnerException);
        Assert.True(exception.InnerException is ArgumentException or FormatException);
    }
}
