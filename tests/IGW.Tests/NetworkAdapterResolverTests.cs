using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class NetworkAdapterResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("默认值")]
    [InlineData("Default")]
    public void Default_selection_leaves_source_routing_to_the_operating_system(string? value)
    {
        var settings = new Dictionary<string, string>();
        if (value is not null) settings["networkAdapter"] = value;

        var result = NetworkAdapterResolver.Resolve(settings, []);

        Assert.Equal(NetworkAdapterResolutionKind.SystemDefault, result.Kind);
        Assert.Null(result.LocalAddress);
    }

    [Fact]
    public void Explicit_id_and_ipv4_resolve_only_the_saved_address()
    {
        NetworkAdapterInfo[] adapters =
        [
            new("nic-1", "PLC LAN", "192.168.10.20", 24),
            new("nic-1", "PLC LAN", "192.168.20.20", 24),
        ];
        var settings = new Dictionary<string, string>
        {
            ["networkAdapter"] = "PLC LAN",
            ["networkAdapterId"] = "nic-1",
            ["networkAdapterIpv4"] = "192.168.10.20",
        };

        var result = NetworkAdapterResolver.Resolve(settings, adapters);

        Assert.Equal(NetworkAdapterResolutionKind.Bound, result.Kind);
        Assert.Equal("192.168.10.20", result.LocalAddress!.ToString());
        Assert.Equal("PLC LAN — 192.168.10.20", result.Adapter!.Label);
    }

    [Fact]
    public void Stable_adapter_id_follows_its_current_unique_ipv4_after_address_changes()
    {
        NetworkAdapterInfo[] adapters =
        [
            new("nic-1", "WLAN", "192.168.3.253", 24),
            new("nic-2", "VPN", "198.18.0.1", 30),
        ];
        var settings = new Dictionary<string, string>
        {
            ["networkAdapter"] = "WLAN",
            ["networkAdapterId"] = "nic-1",
            ["networkAdapterIpv4"] = "192.168.1.240",
        };

        var result = NetworkAdapterResolver.Resolve(settings, adapters);

        Assert.Equal(NetworkAdapterResolutionKind.Bound, result.Kind);
        Assert.Equal("192.168.3.253", result.LocalAddress!.ToString());
        Assert.Contains("地址已变化", result.Message);
    }

    [Fact]
    public void Stable_adapter_id_with_multiple_current_ipv4s_requires_an_explicit_reselection_when_saved_address_is_gone()
    {
        NetworkAdapterInfo[] adapters =
        [
            new("nic-1", "PLC LAN", "192.168.10.20", 24),
            new("nic-1", "PLC LAN", "192.168.20.20", 24),
        ];
        var settings = new Dictionary<string, string>
        {
            ["networkAdapter"] = "PLC LAN",
            ["networkAdapterId"] = "nic-1",
            ["networkAdapterIpv4"] = "192.168.30.20",
        };

        var result = NetworkAdapterResolver.Resolve(settings, adapters);

        Assert.Equal(NetworkAdapterResolutionKind.Unavailable, result.Kind);
        Assert.Contains("多个 IPv4", result.Message);
    }

    [Fact]
    public void Missing_explicit_adapter_fails_closed_instead_of_using_another_nic()
    {
        NetworkAdapterInfo[] adapters = [new("nic-2", "Office LAN", "192.168.10.30", 24)];
        var settings = new Dictionary<string, string>
        {
            ["networkAdapter"] = "PLC LAN",
            ["networkAdapterId"] = "removed",
            ["networkAdapterIpv4"] = "192.168.10.20",
        };

        var result = NetworkAdapterResolver.Resolve(settings, adapters);

        Assert.Equal(NetworkAdapterResolutionKind.Unavailable, result.Kind);
        Assert.Null(result.LocalAddress);
        Assert.Contains("PLC LAN", result.Message);
        Assert.Contains("192.168.10.20", result.Message);
    }

    [Fact]
    public void Legacy_name_is_migratable_only_when_it_matches_one_ipv4_row()
    {
        var settings = new Dictionary<string, string> { ["networkAdapter"] = "PLC LAN" };

        var unique = NetworkAdapterResolver.Resolve(settings,
            [new("nic-1", "PLC LAN", "10.0.0.2", 24)]);
        var ambiguous = NetworkAdapterResolver.Resolve(settings,
        [
            new("nic-1", "PLC LAN", "10.0.0.2", 24),
            new("nic-1", "PLC LAN", "10.0.1.2", 24),
        ]);

        Assert.Equal(NetworkAdapterResolutionKind.Bound, unique.Kind);
        Assert.Equal("10.0.0.2", unique.LocalAddress!.ToString());
        Assert.Equal(NetworkAdapterResolutionKind.Unavailable, ambiguous.Kind);
        Assert.Contains("多个 IPv4", ambiguous.Message);
    }
}
