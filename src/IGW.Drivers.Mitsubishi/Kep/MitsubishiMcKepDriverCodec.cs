using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.Mitsubishi.Kep;

/// <summary>原生 MC 驱动的 KEP 映射；运行时当前优先使用 IoTClient 实现。</summary>
public sealed class MitsubishiMcKepDriverCodec : KepDriverCodecBase
{
    private const string Port = "mitsubishi_ethernet.DEVICE_PORT_NUMBER";
    public override string IgwDriverId => MitsubishiMcDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["Mitsubishi Ethernet"];
    protected override string ExportDriverName => "Mitsubishi Ethernet";
    protected override int DefaultPort => 5000;
    protected override void WritePort(Utf8JsonWriter writer, int port) => writer.WriteNumber(Port, port);
    protected override int ReadPort(JsonElement source) => Integer(source, Port, DefaultPort);
    protected override void RemovePort(IDictionary<string, JsonElement> unknown) => unknown.Remove(Port);
}
