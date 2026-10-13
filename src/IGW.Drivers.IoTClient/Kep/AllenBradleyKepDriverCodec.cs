using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient.Kep;

public sealed class AllenBradleyKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "allen_bradley_controllogix_ethernet.DEVICE_PORT_NUMBER";
    private const string Slot = "allen_bradley_controllogix_ethernet.DEVICE_CPU_SLOT";
    private const string Path = "allen_bradley_controllogix_ethernet.DEVICE_PATH";
    public override string IgwDriverId => IoTAbDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Allen-Bradley ControlLogix Ethernet"];
    protected override string ExportDriverName => "Allen-Bradley ControlLogix Ethernet";
    protected override int DefaultPort => 44818;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        writer.WriteNumber(Slot, SettingInt(device.Settings, "slot", 0));
        if (Setting(device.Settings, "path") is { Length: > 0 } path) writer.WriteString(Path, path);
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        target.Settings["slot"] = Integer(source, Slot, 0).ToString();
        if (String(source, Path) is { Length: > 0 } path) target.Settings["path"] = path;
        Remove(unknown, Slot, Path);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
