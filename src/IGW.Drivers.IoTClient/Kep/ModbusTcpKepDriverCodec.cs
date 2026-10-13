using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient.Kep;

public sealed class ModbusTcpKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "modbus_ethernet.DEVICE_PORT_NUMBER";
    private const string Unit = "modbus_ethernet.DEVICE_UNIT_ID";
    private const string ZeroBased = "modbus_ethernet.DEVICE_ZERO_BASED_ADDRESSING";
    private const string WordOrder = "modbus_ethernet.DEVICE_WORD_ORDER";
    public override string IgwDriverId => IoTModbusDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Modbus TCP/IP Ethernet"];
    protected override string ExportDriverName => "Modbus TCP/IP Ethernet";
    protected override int DefaultPort => 502;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        writer.WriteNumber(Unit, SettingInt(device.Settings, "unitId", 1));
        writer.WriteBoolean(ZeroBased, string.Equals(Setting(device.Settings, "zeroBased"), "启用", StringComparison.OrdinalIgnoreCase));
        writer.WriteString(WordOrder, Setting(device.Settings, "wordOrder", Setting(channel.Settings, "wordOrder", "ABCD")));
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        target.Settings["unitId"] = Integer(source, Unit, 1).ToString();
        target.Settings["zeroBased"] = Boolean(source, ZeroBased, false) ? "启用" : "禁用";
        target.Settings["wordOrder"] = String(source, WordOrder) ?? "ABCD";
        Remove(unknown, Unit, ZeroBased, WordOrder);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
