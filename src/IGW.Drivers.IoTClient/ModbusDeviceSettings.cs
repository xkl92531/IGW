using System.Net;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

internal sealed record ModbusDeviceSettings(string Host, int Port, byte UnitId, string WordOrder,
    int TimeoutMs, bool ZeroBased, int RegisterBlock, int CoilBlock, string WriteMode)
{
    internal static ModbusDeviceSettings Parse(ChannelDef channel, DeviceDef device)
    {
        string Get(string key, string fallback) => device.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : channel.Settings.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        int Number(string key, int fallback, int min, int max)
        {
            if (!int.TryParse(Get(key, fallback.ToString()), out var number) || number < min || number > max)
                throw new FormatException($"Modbus {key} 必须在 {min}-{max} 范围内");
            return number;
        }
        var endpoint = device.Endpoint?.Trim() ?? "";
        string host;
        int port = 502;
        if (endpoint.StartsWith('['))
        {
            int end = endpoint.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(endpoint[1..end], out _))
                throw new FormatException("Modbus Endpoint IPv6 格式错误");
            host = endpoint[1..end];
            if (end + 1 < endpoint.Length && (endpoint[end + 1] != ':' ||
                !int.TryParse(endpoint[(end + 2)..], out port))) throw new FormatException("Modbus Endpoint 端口错误");
        }
        else
        {
            var parts = endpoint.Split(':');
            host = parts[0];
            if (parts.Length > 2 || (parts.Length == 2 && !int.TryParse(parts[1], out port)))
                throw new FormatException("Modbus Endpoint 应为 host:port");
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new FormatException("Modbus Endpoint 主机或端口错误");
        var order = Get("wordOrder", "ABCD").ToUpperInvariant();
        if (order is not ("ABCD" or "CDAB" or "BADC" or "DCBA")) throw new FormatException("Modbus wordOrder 非法");
        var mode = Get("writeMode", "auto").ToLowerInvariant();
        if (mode is not ("auto" or "single" or "multiple")) throw new FormatException("Modbus writeMode 非法");
        var zero = Get("zeroBased", "禁用").ToLowerInvariant();
        if (zero is not ("启用" or "禁用" or "true" or "false" or "1" or "0"))
            throw new FormatException("Modbus zeroBased 非法");
        return new(host, port, checked((byte)Number("unitId", 1, 0, 255)), order,
            Number("timeoutMs", 2000, 1, int.MaxValue), zero is "启用" or "true" or "1",
            Number("regBlock", 120, 1, 125), Number("coilBlock", 512, 1, 2000), mode);
    }
}
