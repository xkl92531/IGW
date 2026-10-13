using System.Text.Json;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class GatewayTelemetryTests
{
    [Fact]
    public void Structured_event_redacts_credentials_and_keeps_stable_dimensions()
    {
        IReadOnlyDictionary<string, object?> record = GatewayTelemetry.Build(
            "interface",
            "POST completed",
            new Dictionary<string, object?>
            {
                ["device"] = "PLC8.OP140",
                ["target"] = "http://user:pass@host/api?token=secret",
                ["authorization"] = "Bearer secret",
                ["durationMs"] = 12.5,
                ["status"] = "success"
            });

        Assert.Equal("interface", record["igw_category"]);
        Assert.Equal("integration", record["igw_module"]);
        Assert.Equal("interface_call", record["igw_feature"]);
        Assert.Equal("interface_event", record["igw_event_kind"]);
        Assert.Equal("PLC8.OP140", record["igw_device"]);
        Assert.Equal("http://host/api", record["igw_target"]);
        Assert.Equal(12.5, record["igw_duration_ms"]);
        Assert.Equal("success", record["igw_status"]);
        string json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pass@", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
    }
}
