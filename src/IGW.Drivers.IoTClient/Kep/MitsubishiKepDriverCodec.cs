using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient.Kep;

public sealed class MitsubishiKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "mitsubishi_ethernet.DEVICE_PORT_NUMBER";
    private const string Frame = "mitsubishi_ethernet.DEVICE_FRAME";
    public override string IgwDriverId => IoTMitsubishiDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Mitsubishi Ethernet"];
    protected override string ExportDriverName => "Mitsubishi Ethernet";
    protected override int DefaultPort => 6000;

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        writer.WriteString(Frame, Setting(device.Settings, "mcVersion", "Qna_3E"));
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        target.Settings["mcVersion"] = String(source, Frame) ?? "Qna_3E";
        Remove(unknown, Frame);
    }

    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
