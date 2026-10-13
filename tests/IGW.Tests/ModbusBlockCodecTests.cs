using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class ModbusBlockCodecTests
{
    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.DCBA)]
    public void Encoded_value_round_trips_through_wire_order(WordOrder order)
    {
        byte[] wire = ModbusBlockCodec.Encode(1234.5f, IgwDataType.Float, order);

        Assert.Equal(4, wire.Length);
        Assert.Equal(1234.5f, Assert.IsType<float>(
            ModbusBlockCodec.Decode(wire, IgwDataType.Float, order)));
    }
}
