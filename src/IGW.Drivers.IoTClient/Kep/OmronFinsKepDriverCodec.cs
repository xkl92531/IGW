using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient.Kep;

public sealed class OmronFinsKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "omron_fins_ethernet.DEVICE_PORT_NUMBER";
    private const string WordOrder = "omron_fins_ethernet.DEVICE_WORD_ORDER";
    public override string IgwDriverId => IoTOmronFinsDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Omron FINS Ethernet"];
    protected override string ExportDriverName => "Omron FINS Ethernet";
    protected override int DefaultPort => 9600;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        writer.WriteString(WordOrder, Setting(device.Settings, "wordOrder", "ABCD"));
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        target.Settings["wordOrder"] = String(source, WordOrder) ?? "ABCD";
        Remove(unknown, WordOrder);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
