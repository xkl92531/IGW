using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Drivers.OpcUaClient.Kep;

public sealed class OpcUaClientKepDriverCodec : KepDriverCodecBase
{
    private const string Endpoint = "opcua_client.DEVICE_ENDPOINT_URL";
    private const string Security = "opcua_client.CHANNEL_SECURITY_POLICY";
    private const string Anonymous = "opcua_client.CHANNEL_ANONYMOUS";
    private const string Username = "opcua_client.CHANNEL_USERNAME";
    private const string Timeout = "opcua_client.CHANNEL_TIMEOUT_MILLISECONDS";
    public override string IgwDriverId => OpcUaClientDriverFactory.Id;
    public override IReadOnlyCollection<string> KepDriverNames { get; } = ["OPC UA Client"];
    protected override string ExportDriverName => "OPC UA Client";
    protected override int DefaultPort => 4840;

    public override KepCompatibilityReport Inspect(ChannelDef channel)
    {
        var issues = base.Inspect(channel).Issues.ToList();
        if (Setting(channel.Settings, "password").Length > 0)
            issues.Add(new KepCompatibilityIssue(channel.Name, "kep_secret_requires_manual_configuration",
                KepIssueSeverity.Error, "OPC UA password is encrypted in the IGW extension and must be configured in KEP."));
        return new KepCompatibilityReport(issues);
    }

    public override void WriteChannelProperties(Utf8JsonWriter writer, ChannelDef channel)
    {
        base.WriteChannelProperties(writer, channel);
        writer.WriteString(Security, Setting(channel.Settings, "securityPolicy", "None"));
        writer.WriteBoolean(Anonymous, !string.Equals(Setting(channel.Settings, "anonymous", "true"), "false", StringComparison.OrdinalIgnoreCase));
        if (Setting(channel.Settings, "username") is { Length: > 0 } username) writer.WriteString(Username, username);
        writer.WriteNumber(Timeout, SettingInt(channel.Settings, "timeoutMs", 10_000));
    }

    public override void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        base.WriteDeviceProperties(writer, channel, device);
        string endpoint = Setting(channel.Settings, "url", device.Endpoint);
        writer.WriteString(Endpoint, endpoint);
    }

    public override void ReadChannelProperties(JsonElement source, ChannelDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadChannelProperties(source, target, unknown);
        target.Settings["securityPolicy"] = String(source, Security) ?? "None";
        target.Settings["anonymous"] = Boolean(source, Anonymous, true) ? "true" : "false";
        if (String(source, Username) is { Length: > 0 } username) target.Settings["username"] = username;
        target.Settings["timeoutMs"] = Integer(source, Timeout, 10_000).ToString();
        Remove(unknown, Security, Anonymous, Username, Timeout);
    }

    public override void ReadDeviceProperties(JsonElement source, DeviceDef target, IDictionary<string, JsonElement> unknown)
    {
        base.ReadDeviceProperties(source, target, unknown);
        if (String(source, Endpoint) is { Length: > 0 } endpoint) target.Endpoint = endpoint;
        Remove(unknown, Endpoint);
    }
}
