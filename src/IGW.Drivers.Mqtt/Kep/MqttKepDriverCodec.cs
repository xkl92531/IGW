using IGW.Abstractions;

namespace IGW.Drivers.Mqtt.Kep;

public sealed class MqttKepDriverCodec : KepDriverCodecBase
{
    public override string IgwDriverId => MqttDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["IGW MQTT Client (unsupported by KEP)"];
    protected override string ExportDriverName => "IGW MQTT Client (unsupported by KEP)";
    protected override int DefaultPort => 1883;

    public override KepCompatibilityReport Inspect(ChannelDef channel) => new([
        new KepCompatibilityIssue(channel.Name, "driver_not_supported_by_kep", KepIssueSeverity.Error,
            "IGW MQTT Client has no verified equivalent KEP channel driver."),
    ]);
}
