using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IGW.Core;

public sealed record NetworkAdapterInfo(string Id, string Name, string Ipv4, int PrefixLength)
{
    public string Label => $"{Name} — {Ipv4}";
}

public enum NetworkAdapterResolutionKind
{
    SystemDefault,
    Bound,
    Unavailable,
}

public sealed record NetworkAdapterResolution(
    NetworkAdapterResolutionKind Kind,
    NetworkAdapterInfo? Adapter,
    IPAddress? LocalAddress,
    string Message);

public interface INetworkAdapterCatalog
{
    IReadOnlyList<NetworkAdapterInfo> Available();
}

public sealed class NetworkAdapterResolver : INetworkAdapterCatalog
{
    // 网卡列表常驻缓存：Windows 上枚举网卡及其 IP 属性每次要数百毫秒，启动/重建时几百台设备会连续调用。
    // 只有操作系统通知网络地址或连通性变化(DHCP 换地址、拔插网线、禁用网卡等)时才重新枚举，
    // 断线重连时中继仍能拿到新地址；另设 5 分钟兜底刷新，防止个别环境漏发变化通知。
    private static readonly long SafetyRefreshMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private sealed record Snapshot(long At, IReadOnlyList<NetworkAdapterInfo> Rows);
    private static Snapshot? s_cache;

    static NetworkAdapterResolver()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Volatile.Write(ref s_cache, null);
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Volatile.Write(ref s_cache, null);
        }
        catch { /* 不支持变化通知的平台只依赖兜底刷新 */ }
    }

    public IReadOnlyList<NetworkAdapterInfo> Available()
    {
        var cached = Volatile.Read(ref s_cache);
        long now = Environment.TickCount64;
        if (cached is not null && now - cached.At < SafetyRefreshMs) return cached.Rows;
        IReadOnlyList<NetworkAdapterInfo> rows = Enumerate();
        Volatile.Write(ref s_cache, new Snapshot(now, rows));
        return rows;
    }

    private static IReadOnlyList<NetworkAdapterInfo> Enumerate()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ToIpv4Rows)
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Ipv4, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static NetworkAdapterResolution Resolve(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<NetworkAdapterInfo> available)
    {
        string displayName = Value(settings, "networkAdapter");
        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.Equals("默认值", StringComparison.OrdinalIgnoreCase)
            || displayName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return new(NetworkAdapterResolutionKind.SystemDefault, null, null, "由操作系统选择本地源地址");

        string id = Value(settings, "networkAdapterId");
        string savedIpv4 = Value(settings, "networkAdapterIpv4");
        NetworkAdapterInfo? match;

        if (!string.IsNullOrWhiteSpace(id))
        {
            var sameAdapter = available.Where(row =>
                row.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            match = sameAdapter.FirstOrDefault(row =>
                row.Ipv4.Equals(savedIpv4, StringComparison.OrdinalIgnoreCase));
            if (match is null && sameAdapter.Length == 1)
            {
                match = sameAdapter[0];
                if (!IPAddress.TryParse(match.Ipv4, out var changedAddress)
                    || changedAddress.AddressFamily != AddressFamily.InterNetwork)
                    return Unavailable(displayName, match.Ipv4, "配置的网卡没有可用 IPv4");
                return new(NetworkAdapterResolutionKind.Bound, match, changedAddress,
                    $"网卡地址已变化，已使用当前地址 {match.Label}（原 {savedIpv4}）");
            }
            if (match is null && sameAdapter.Length > 1)
                return Unavailable(displayName, savedIpv4, "该网卡存在多个 IPv4，原地址已不可用，请重新选择明确地址");
            if (match is null)
                return Unavailable(displayName, savedIpv4, "配置的网卡当前不可用");
        }
        else
        {
            var named = available.Where(row => row.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length == 0)
                return Unavailable(displayName, savedIpv4, "旧工程配置的网卡当前不可用");
            if (named.Length != 1)
                return Unavailable(displayName, savedIpv4, "该网卡存在多个 IPv4，请重新选择明确地址");
            match = named[0];
        }

        if (!IPAddress.TryParse(match.Ipv4, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return Unavailable(displayName, match.Ipv4, "配置的网卡没有可用 IPv4");

        return new(NetworkAdapterResolutionKind.Bound, match, address,
            $"本地源地址 {match.Label}");
    }

    private static IEnumerable<NetworkAdapterInfo> ToIpv4Rows(NetworkInterface adapter)
    {
        IPInterfaceProperties properties;
        try { properties = adapter.GetIPProperties(); }
        catch { yield break; }

        foreach (var address in properties.UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            yield return new NetworkAdapterInfo(adapter.Id, adapter.Name, address.Address.ToString(), address.PrefixLength);
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value?.Trim() ?? "" : "";

    private static NetworkAdapterResolution Unavailable(string name, string ipv4, string reason)
    {
        string configured = string.IsNullOrWhiteSpace(ipv4) ? name : $"{name} — {ipv4}";
        return new(NetworkAdapterResolutionKind.Unavailable, null, null, $"{reason}：{configured}");
    }
}
