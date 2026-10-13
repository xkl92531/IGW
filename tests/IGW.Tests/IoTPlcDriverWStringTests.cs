using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class IoTPlcDriverWStringTests
{
    [Fact]
    public void Invalid_address_is_a_failed_read_not_a_good_empty_string()
    {
        var result = IoTPlcDeviceDriver.ReadSiemensWString(
            "not-a-db-address", _ => (true, (ushort)0, ""));

        Assert.False(result.Success);
        Assert.Contains("地址", result.Error);
    }

    [Fact]
    public void Length_read_failure_is_preserved()
    {
        var result = IoTPlcDeviceDriver.ReadSiemensWString("DB1.DBW20", address =>
            address.EndsWith("DBW20", StringComparison.Ordinal)
                ? (true, (ushort)20, "")
                : (false, (ushort)0, "transport timeout"));

        Assert.False(result.Success);
        Assert.Contains("transport timeout", result.Error);
    }

    [Fact]
    public void Current_length_cannot_exceed_declared_capacity()
    {
        var words = new Dictionary<string, ushort>
        {
            ["DB1.DBW20"] = 2,
            ["DB1.DBW22"] = 3,
        };

        var result = IoTPlcDeviceDriver.ReadSiemensWString(
            "DB1.DBW20", address => (true, words[address], ""));

        Assert.False(result.Success);
        Assert.Contains("长度", result.Error);
    }

    [Fact]
    public void Partial_character_read_is_failed_instead_of_returning_a_good_prefix()
    {
        var words = new Dictionary<string, ushort>
        {
            ["DB1.DBW20"] = 10,
            ["DB1.DBW22"] = 2,
            ["DB1.DBW24"] = 'A',
        };

        var result = IoTPlcDeviceDriver.ReadSiemensWString("DB1.DBW20", address =>
            words.TryGetValue(address, out ushort value)
                ? (true, value, "")
                : (false, (ushort)0, "second character failed"));

        Assert.False(result.Success);
        Assert.Contains("second character failed", result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Valid_empty_and_unicode_values_are_good()
    {
        var empty = IoTPlcDeviceDriver.ReadSiemensWString("DB1.DBW20", address =>
            (true, address.EndsWith("DBW20", StringComparison.Ordinal) ? (ushort)10 : (ushort)0, ""));
        Assert.True(empty.Success);
        Assert.Equal("", empty.Value);

        var words = new Dictionary<string, ushort>
        {
            ["DB1.DBW20"] = 10,
            ["DB1.DBW22"] = 2,
            ["DB1.DBW24"] = 'A',
            ["DB1.DBW26"] = '你',
        };
        var unicode = IoTPlcDeviceDriver.ReadSiemensWString(
            "DB1.DBW20", address => (true, words[address], ""));
        Assert.True(unicode.Success);
        Assert.Equal("A你", unicode.Value);
    }
}
