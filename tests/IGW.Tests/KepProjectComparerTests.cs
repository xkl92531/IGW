using System.Text.Json;
using IGW.KepProjectProbe;
using Xunit;

namespace IGW.Tests;

public sealed class KepProjectComparerTests
{
    [Fact]
    public void Ordering_only_differences_are_ignored()
    {
        using JsonDocument expected = JsonDocument.Parse(ProjectJson("TagA", "R1", "TagB", "R2"));
        using JsonDocument actual = JsonDocument.Parse(ProjectJson("TagB", "R2", "TagA", "R1"));

        KepComparisonReport report = KepProjectComparer.Compare(expected.RootElement, actual.RootElement);

        Assert.True(report.Equivalent);
        Assert.Empty(report.Differences);
    }

    [Fact]
    public void Driver_address_and_missing_tag_differences_include_complete_paths()
    {
        using JsonDocument expected = JsonDocument.Parse(ProjectJson("TagA", "R1", "TagB", "R2"));
        string changed = ProjectJson("TagA", "R99", "TagC", "R3")
            .Replace("\"Simulator\"", "\"Siemens TCP/IP Ethernet\"", StringComparison.Ordinal);
        using JsonDocument actual = JsonDocument.Parse(changed);

        KepComparisonReport report = KepProjectComparer.Compare(expected.RootElement, actual.RootElement);

        Assert.False(report.Equivalent);
        Assert.Contains(report.Differences, item => item.Path ==
            "project/channels[PLC8]/servermain.MULTIPLE_TYPES_DEVICE_DRIVER");
        Assert.Contains(report.Differences, item => item.Path ==
            "project/channels[PLC8]/devices[CPU]/tags[TagA]/servermain.TAG_ADDRESS");
        Assert.Contains(report.Differences, item => item.Path ==
            "project/channels[PLC8]/devices[CPU]/tags[TagB]" && item.Kind == "missing");
    }

    private static string ProjectJson(string firstName, string firstAddress, string secondName, string secondAddress) => $$"""
        {
          "project": {
            "common.ALLTYPES_NAME": "Factory",
            "channels": [{
              "common.ALLTYPES_NAME": "PLC8",
              "servermain.MULTIPLE_TYPES_DEVICE_DRIVER": "Simulator",
              "devices": [{
                "common.ALLTYPES_NAME": "CPU",
                "tags": [
                  { "common.ALLTYPES_NAME": "{{firstName}}", "servermain.TAG_ADDRESS": "{{firstAddress}}", "servermain.TAG_DATA_TYPE": 4 },
                  { "common.ALLTYPES_NAME": "{{secondName}}", "servermain.TAG_ADDRESS": "{{secondAddress}}", "servermain.TAG_DATA_TYPE": 4 }
                ],
                "tag_groups": []
              }]
            }]
          }
        }
        """;
}
