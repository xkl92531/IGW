using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.EtherNetIP.Kep;

public sealed class OmronCipKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "omron_nj_ethernet.DEVICE_PORT_NUMBER";
    private const string Path = "omron_nj_ethernet.DEVICE_PATH";
    public override string IgwDriverId => OmronCipDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Omron NJ Ethernet"];
    protected override string ExportDriverName => "Omron NJ Ethernet";
    protected override int DefaultPort => 44818;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        if (Setting(device.Settings, "path") is { Length: > 0 } path) writer.WriteString(Path, path);
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        if (String(source, Path) is { Length: > 0 } path) target.Settings["path"] = path;
        Remove(unknown, Path);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
