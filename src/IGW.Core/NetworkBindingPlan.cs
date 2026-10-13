using System.Net;
using IGW.Abstractions;

namespace IGW.Core;

public sealed class NetworkBindingLease : IAsyncDisposable, IDisposable
{
    private readonly BoundTcpRelay _relay;

    internal NetworkBindingLease(BoundTcpRelay relay, string driverId, DnsEndPoint target)
    {
        _relay = relay;
        DriverId = driverId;
        Target = target;
    }

    public string DriverId { get; }
    public DnsEndPoint Target { get; }
    public NetworkRelayConnection? BoundConnection => _relay.BoundConnection;
    public IPAddress? LastBoundSourceAddress => _relay.LastBoundSourceAddress;
    public event Action? BoundConnectionChanged
    {
        add => _relay.BoundConnectionChanged += value;
        remove => _relay.BoundConnectionChanged -= value;
    }
    public IPEndPoint LoopbackEndpoint => _relay.LoopbackEndpoint;
    public NetworkRelaySnapshot Snapshot() => _relay.Snapshot();
    public void Dispose() => _relay.Dispose();
    public ValueTask DisposeAsync() => _relay.DisposeAsync();
}

public sealed class PreparedNetworkDevice : IAsyncDisposable, IDisposable
{
    internal PreparedNetworkDevice(ChannelDef channel, DeviceDef device, NetworkBindingLease? lease)
    {
        Channel = channel;
        Device = device;
        Lease = lease;
    }

    public ChannelDef Channel { get; }
    public DeviceDef Device { get; }
    public NetworkBindingLease? Lease { get; }
    public void Dispose() => Lease?.Dispose();
    public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public static class NetworkBindingPlan
{
    private sealed record TcpTarget(string DriverId, DnsEndPoint Endpoint, TimeSpan Timeout, string? OpcUaPath);

    private static readonly IReadOnlyDictionary<string, int> DeviceEndpointPorts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Siemens.S7"] = 102,
            ["Modbus.Tcp"] = 502,
            ["Omron.Fins"] = 9600,
            ["Omron.Cip"] = 44818,
            ["AB.Eip"] = 44818,
            ["Mitsubishi.Mc"] = 5000,
            ["Mqtt.Client"] = 1883,
        };

    public static PreparedNetworkDevice Prepare(
        ChannelDef channel,
        DeviceDef device,
        INetworkAdapterCatalog adapters)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(adapters);

        var target = ParseTarget(channel, device);
        if (target is null) return new PreparedNetworkDevice(channel, device, null);
        // 目标是环回地址(本机模拟器/本机服务)时,任何物理网卡源地址都无法到达——
        // 绑定必然以 WSAEADDRNOTAVAIL(10049) 失败。跳过中继,由操作系统选择环回源。
        if (IsLoopbackTarget(target.Endpoint))
            return new PreparedNetworkDevice(channel, device, null);

        var initialResolution = NetworkAdapterResolver.Resolve(channel.Settings, adapters.Available());
        if (initialResolution.Kind == NetworkAdapterResolutionKind.SystemDefault)
            return new PreparedNetworkDevice(channel, device, null);

        var listen = IsEtherNetIp(target.DriverId)
            ? RelayListenRequirement.EtherNetIp
            : RelayListenRequirement.Ephemeral;
        var relay = BoundTcpRelay.Start(
            $"{channel.Name}.{device.Name}".Trim('.'),
            target.Endpoint,
            () => NetworkAdapterResolver.Resolve(channel.Settings, adapters.Available()),
            target.Timeout,
            listen);
        var lease = new NetworkBindingLease(relay, target.DriverId, target.Endpoint);

        try
        {
            var runtimeChannel = CloneChannel(channel);
            var runtimeDevice = CloneDevice(device);
            if (string.Equals(target.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new UriBuilder("opc.tcp", relay.LoopbackEndpoint.Address.ToString(),
                    relay.LoopbackEndpoint.Port, target.OpcUaPath ?? "").Uri.ToString();
                runtimeChannel.Settings["url"] = uri;
                runtimeDevice.Endpoint = uri;
            }
            else if (IsEtherNetIp(target.DriverId))
            {
                runtimeDevice.Endpoint = relay.LoopbackEndpoint.Address.ToString();
            }
            else
            {
                runtimeDevice.Endpoint = $"{relay.LoopbackEndpoint.Address}:{relay.LoopbackEndpoint.Port}";
            }

            return new PreparedNetworkDevice(runtimeChannel, runtimeDevice, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static (string Host, string SourceAddress, bool RouteVerified)? ProbeEndpoint(
        ChannelDef channel, DeviceDef device, NetworkBindingLease? binding)
    {
        if (!string.Equals(channel.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase)) return null;
        var target = ParseTarget(channel, device);
        if (target is null) return null;
        // Only the connected socket proves which remote/source pair the relay actually used.
        if (binding is not null)
            return binding.BoundConnection is { } connection
                ? (connection.RemoteAddress.ToString(), connection.SourceAddress.ToString(), true)
                : (binding.Target.Host, "", false);
        return (target.Endpoint.Host, "", false);
    }

    private static TcpTarget? ParseTarget(ChannelDef channel, DeviceDef device)
    {
        if (string.Equals(channel.DriverId, "OpcUa.Client", StringComparison.OrdinalIgnoreCase))
        {
            var raw = channel.Settings.GetValueOrDefault("url", "").Trim();
            if (raw.Length == 0) raw = (device.Endpoint ?? "").Trim();
            if (raw.Length == 0)
                throw new InvalidOperationException("OPC UA Client 通道未配置上游服务器地址，无法建立网卡绑定转发。");
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "opc.tcp://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                throw new FormatException($"OPC UA 地址无效：{raw}");
            var port = uri.IsDefaultPort ? 4840 : uri.Port;
            return new TcpTarget(channel.DriverId, new DnsEndPoint(uri.Host, port),
                TimeSpan.FromMilliseconds(Timeout(channel, device, 10_000)),
                uri.AbsolutePath == "/" ? "" : uri.AbsolutePath);
        }

        if (!DeviceEndpointPorts.TryGetValue(channel.DriverId, out var defaultPort)) return null;
        if (string.Equals(channel.DriverId, "Siemens.S7", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(device.Settings.GetValueOrDefault("port"), out int configuredPort)) defaultPort = configuredPort;
        var endpoint = ParseHostPort(device.Endpoint, defaultPort);
        if (endpoint.Port is < 1 or > 65535) throw new FormatException("设备 TCP 端口必须在 1..65535 范围内");
        var defaultTimeout = IsEtherNetIp(channel.DriverId) ? 5_000 : 2_000;
        if (string.Equals(channel.DriverId, "Mqtt.Client", StringComparison.OrdinalIgnoreCase))
            defaultTimeout = 5_000;
        return new TcpTarget(channel.DriverId, endpoint,
            TimeSpan.FromMilliseconds(Timeout(channel, device, defaultTimeout)), null);
    }

    private static bool IsLoopbackTarget(DnsEndPoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Host, out var address))
            return IPAddress.IsLoopback(address);
        return string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static DnsEndPoint ParseHostPort(string raw, int defaultPort)
    {
        var endpoint = (raw ?? "").Trim();
        if (endpoint.Length == 0) throw new FormatException("设备 Endpoint 为空。");

        if (Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return new DnsEndPoint(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port);

        throw new FormatException($"设备 Endpoint 无效：{raw}");
    }

    private static int Timeout(ChannelDef channel, DeviceDef device, int fallback)
    {
        if (device.Settings.TryGetValue("timeoutMs", out var dv) && int.TryParse(dv, out var dn) && dn > 0)
            return dn;
        if (channel.Settings.TryGetValue("timeoutMs", out var cv) && int.TryParse(cv, out var cn) && cn > 0)
            return cn;
        return fallback;
    }

    private static bool IsEtherNetIp(string driverId) =>
        string.Equals(driverId, "AB.Eip", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(driverId, "Omron.Cip", StringComparison.OrdinalIgnoreCase);

    private static ChannelDef CloneChannel(ChannelDef source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        DriverId = source.DriverId,
        Description = source.Description,
        Settings = new Dictionary<string, string>(source.Settings, StringComparer.OrdinalIgnoreCase),
        Devices = source.Devices
    };

    private static DeviceDef CloneDevice(DeviceDef source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Endpoint = source.Endpoint,
        Description = source.Description,
        Settings = new Dictionary<string, string>(source.Settings, StringComparer.OrdinalIgnoreCase),
        TemplateRef = source.TemplateRef,
        Delta = source.Delta,
        Groups = source.Groups,
        Tags = source.Tags
    };
}
