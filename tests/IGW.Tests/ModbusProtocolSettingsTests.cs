using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class ModbusProtocolSettingsTests
{
    [Theory]
    [InlineData("C:0", IgwDataType.Bool, 0, 0, 1)]
    [InlineData("DI:65535", IgwDataType.Bool, 1, 65535, 1)]
    [InlineData("IR:65534", IgwDataType.UInt32, 3, 65534, 2)]
    [InlineData("HR:0.3", IgwDataType.Bool, 4, 0, 1)]
    [InlineData("40001", IgwDataType.Int16, 4, 0, 1)]
    [InlineData("465536", IgwDataType.Int16, 4, 65535, 1)]
    public void Explicit_and_legacy_addresses_resolve_to_wire_offsets(string text, IgwDataType type, int area, int offset, int length)
    {
        Assert.True(new IoTModbusAddressParser().TryParse(text, type, out var parsed, out var error), error);
        Assert.Equal(new TagAddress(area, offset, length, type == IgwDataType.Bool ? area is 0 or 1 ? 0 : 3 : -1), parsed);
    }

    [Theory]
    [InlineData("HR:65535", IgwDataType.UInt32)]
    [InlineData("465537", IgwDataType.Int16)]
    [InlineData("40001.3", IgwDataType.Int16)]
    [InlineData("00001.3", IgwDataType.Bool)]
    [InlineData("HR:1.16", IgwDataType.Bool)]
    [InlineData("C:1", IgwDataType.UInt16)]
    [InlineData("HR:-1", IgwDataType.UInt16)]
    public void Invalid_address_spans_and_bit_types_are_rejected(string text, IgwDataType type) =>
        Assert.False(new IoTModbusAddressParser().TryParse(text, type, out _, out _));

    [Fact]
    public void Explicit_offsets_ignore_legacy_zero_base_setting()
    {
        var parser = new IoTModbusAddressParser(true);
        Assert.True(parser.TryParse("40001", IgwDataType.Int16, out var legacy, out _));
        Assert.True(parser.TryParse("HR:0", IgwDataType.Int16, out var explicitAddress, out _));
        Assert.Equal(1, legacy.Offset);
        Assert.Equal(0, explicitAddress.Offset);
    }

    [Theory]
    [InlineData("unitId", "256")]
    [InlineData("unitId", "-1")]
    [InlineData("unitId", "bad")]
    [InlineData("regBlock", "126")]
    [InlineData("coilBlock", "2001")]
    [InlineData("timeoutMs", "0")]
    [InlineData("wordOrder", "invalid")]
    [InlineData("writeMode", "invalid")]
    public void Invalid_device_setting_is_not_silently_defaulted(string key, string value) =>
        Assert.Throws<FormatException>(() => Create("127.0.0.1:502", new() { [key] = value }));

    [Theory]
    [InlineData(":502")]
    [InlineData("127.0.0.1:bad")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:502:3")]
    public void Invalid_endpoint_is_rejected(string endpoint) =>
        Assert.Throws<FormatException>(() => Create(endpoint, new()));

    internal static IoTModbusDeviceDriver Create(string endpoint, Dictionary<string, string> settings,
        Dictionary<string, string>? channel = null) => (IoTModbusDeviceDriver)new IoTModbusDriverFactory().CreateDevice(
        new ChannelDef { Name = "Channel", DriverId = "Modbus.Tcp", Settings = channel ?? new() },
        new DeviceDef { Name = "Device", Endpoint = endpoint, Settings = settings });

    [Fact]
    public void Descriptor_exposes_every_supported_device_control()
    {
        var keys = new IoTModbusDriverFactory().Describe().DeviceSettings.Select(s => s.Key).ToArray();
        foreach (var key in new[] { "unitId", "zeroBased", "wordOrder", "regBlock", "coilBlock", "writeMode", "timeoutMs" })
            Assert.Contains(key, keys);
    }

    [Fact]
    public async Task Blank_legacy_device_settings_inherit_channel_or_default()
    {
        await using var driver = Create("127.0.0.1", new() { ["wordOrder"] = " ", ["zeroBased"] = "", ["timeoutMs"] = "" },
            new() { ["wordOrder"] = "CDAB", ["timeoutMs"] = "1000" });
        Assert.True(driver.AddressParser.TryParse("40001", IgwDataType.Int16, out var address, out _));
        Assert.Equal(0, address.Offset);
    }
}
