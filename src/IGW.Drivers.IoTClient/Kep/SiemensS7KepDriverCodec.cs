using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient.Kep;

public sealed class SiemensS7KepDriverCodec : KepDriverCodecBase
{
    private const string Port = "siemens_tcpip_ethernet.DEVICE_COMMUNICATIONS_PORT_NUMBER";
    private const string Rack = "siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_RACK";
    private const string Slot = "siemens_tcpip_ethernet.DEVICE_S7_COMMUNICATIONS_CPU_SLOT";
    public override string IgwDriverId => IoTSiemensDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Siemens TCP/IP Ethernet"];
    protected override string ExportDriverName => "Siemens TCP/IP Ethernet";
    protected override int DefaultPort => 102;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        writer.WriteNumber("servermain.DEVICE_MODEL", ModelCode(Setting(device.Settings, "model", "S7-1500")));
        writer.WriteNumber(Rack, SettingInt(device.Settings, "rack", 0));
        writer.WriteNumber(Slot, SettingInt(device.Settings, "slot", 1));
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        target.Settings["model"] = Integer(source, "servermain.DEVICE_MODEL", 4) switch
        { 0 => "S7-200", 1 => "S7-300", 2 => "S7-400", 3 => "S7-1200", _ => "S7-1500" };
        target.Settings["rack"] = Integer(source, Rack, 0).ToString();
        target.Settings["slot"] = Integer(source, Slot, 1).ToString();
        Remove(unknown, "servermain.DEVICE_MODEL", Rack, Slot);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
    private static int ModelCode(string model) => model.Trim().ToUpperInvariant() switch
    { "S7-200" or "S7-200SMART" => 0, "S7-300" => 1, "S7-400" => 2, "S7-1200" => 3, _ => 4 };
}
