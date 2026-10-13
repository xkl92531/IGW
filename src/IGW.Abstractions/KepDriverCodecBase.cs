using System.Text.Json;

namespace IGW.Abstractions;

/// <summary>KEP 通道/设备的公共属性映射；协议程序集只补充自身属性。</summary>
public abstract class KepDriverCodecBase : IKepDriverCodec
{
    protected const string DriverProperty = "servermain.MULTIPLE_TYPES_DEVICE_DRIVER";
    protected const string DeviceAddressProperty = "servermain.DEVICE_ID_STRING";
    protected const string NetworkAdapterProperty = "servermain.CHANNEL_ETHERNET_COMMUNICATIONS_NETWORK_ADAPTER_STRING";

    public abstract string IgwDriverId { get; }
    public abstract IReadOnlyCollection<string> KepDriverNames { get; }
    protected abstract string ExportDriverName { get; }
    protected virtual int DefaultPort => 0;

    public virtual KepCompatibilityReport Inspect(ChannelDef channel) => new([
        new KepCompatibilityIssue(channel.Name, "target_kep_round_trip_required", KepIssueSeverity.Warning,
            $"{ExportDriverName} mapping requires verification against the target KEP version."),
    ]);

    public virtual void WriteChannelProperties(Utf8JsonWriter writer, ChannelDef channel)
    {
        writer.WriteString(DriverProperty, ExportDriverName);
        if (Setting(channel.Settings, "networkAdapter") is { Length: > 0 } adapter
            && !string.Equals(adapter, "默认值", StringComparison.Ordinal))
            writer.WriteString(NetworkAdapterProperty, adapter);
    }

    public virtual void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device)
    {
        (string host, int port) = SplitEndpoint(device.Endpoint, DefaultPort);
        writer.WriteString("servermain.DEVICE_CHANNEL_ASSIGNMENT", channel.Name);
        writer.WriteString(DriverProperty, ExportDriverName);
        writer.WriteString(DeviceAddressProperty, host);
        writer.WriteBoolean("servermain.DEVICE_DATA_COLLECTION",
            !string.Equals(Setting(device.Settings, "dataCollection"), "禁用", StringComparison.OrdinalIgnoreCase));
        writer.WriteBoolean("servermain.DEVICE_SIMULATED", false);
        WritePort(writer, port);
    }

    public virtual void ReadChannelProperties(
        JsonElement source,
        ChannelDef target,
        IDictionary<string, JsonElement> unknown)
    {
        target.DriverId = IgwDriverId;
        if (String(source, NetworkAdapterProperty) is { Length: > 0 } adapter)
            target.Settings["networkAdapter"] = adapter;
        Remove(unknown, DriverProperty, NetworkAdapterProperty);
    }

    public virtual void ReadDeviceProperties(
        JsonElement source,
        DeviceDef target,
        IDictionary<string, JsonElement> unknown)
    {
        string host = String(source, DeviceAddressProperty) ?? "";
        int port = ReadPort(source);
        target.Endpoint = port > 0 && host.Length > 0 ? $"{host}:{port}" : host;
        if (source.TryGetProperty("servermain.DEVICE_DATA_COLLECTION", out JsonElement enabled)
            && enabled.ValueKind is JsonValueKind.False)
            target.Settings["dataCollection"] = "禁用";
        Remove(unknown, DriverProperty, DeviceAddressProperty, "servermain.DEVICE_CHANNEL_ASSIGNMENT",
            "servermain.DEVICE_DATA_COLLECTION", "servermain.DEVICE_SIMULATED");
        RemovePort(unknown);
    }

    protected virtual void WritePort(Utf8JsonWriter writer, int port) { }
    protected virtual int ReadPort(JsonElement source) => DefaultPort;
    protected virtual void RemovePort(IDictionary<string, JsonElement> unknown) { }

    protected static (string Host, int Port) SplitEndpoint(string? endpoint, int defaultPort)
    {
        string value = endpoint?.Trim() ?? "";
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return (uri.Host, uri.Port > 0 ? uri.Port : defaultPort);
        int colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out int port)) return (value[..colon], port);
        return (value, defaultPort);
    }

    protected static string? String(JsonElement source, string property) =>
        source.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    protected static int Integer(JsonElement source, string property, int fallback) =>
        source.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed : fallback;
    protected static bool Boolean(JsonElement source, string property, bool fallback) =>
        source.TryGetProperty(property, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;
    protected static string Setting(IReadOnlyDictionary<string, string> settings, string key, string fallback = "") =>
        settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    protected static int SettingInt(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
        int.TryParse(Setting(settings, key), out int value) ? value : fallback;
    protected static void Remove(IDictionary<string, JsonElement> unknown, params string[] names)
    {
        foreach (string name in names) unknown.Remove(name);
    }
}
