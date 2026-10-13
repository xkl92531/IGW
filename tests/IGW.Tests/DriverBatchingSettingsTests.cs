using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class DriverBatchingSettingsTests
{
    [Fact]
    public void Missing_or_unknown_mode_uses_auto_and_leaves_legacy_gap_in_the_source_dictionary()
    {
        var legacy = new Dictionary<string, string> { ["maxGap"] = "8" };
        var missing = DriverBatchingSettings.Parse(legacy);
        var unknown = DriverBatchingSettings.Parse(new Dictionary<string, string> { ["batchingMode"] = "adaptive", ["maxGap"] = "8" });

        Assert.Equal(DriverBatchingMode.Auto, missing.Mode);
        Assert.Equal(0, missing.MaxGap);
        Assert.Equal(DriverBatchingMode.Auto, unknown.Mode);
        Assert.Equal(0, unknown.MaxGap);
        Assert.Equal("8", legacy["maxGap"]);
    }

    [Theory]
    [InlineData("-1", 0)]
    [InlineData("18", 18)]
    [InlineData("9999", 4096)]
    [InlineData("invalid", 0)]
    public void Explicit_manual_mode_clamps_the_legacy_gap(string rawGap, int expected)
    {
        var parsed = DriverBatchingSettings.Parse(new Dictionary<string, string>
        {
            ["batchingMode"] = "manual",
            ["maxGap"] = rawGap,
        });

        Assert.Equal(DriverBatchingMode.Manual, parsed.Mode);
        Assert.Equal(expected, parsed.MaxGap);
    }

    [Fact]
    public void Manual_gap_is_an_additional_upper_bound_for_the_siemens_classic_protocol_rule()
    {
        var first = Binding("Line.Plc.First", "DB1.DBW0");
        var second = Binding("Line.Plc.Second", "DB1.DBW10");
        var automatic = SiemensClassicReadPlanner.Plan([first, second], 880);
        var manual = SiemensClassicReadPlanner.Plan([first, second], 880,
            DriverBatchingSettings.Parse(new Dictionary<string, string>
            {
                ["batchingMode"] = "manual", ["maxGap"] = "4",
            }));

        Assert.Single(automatic);
        Assert.Equal(2, manual.Count);
    }

    private static TagBinding Binding(string tagId, string address)
    {
        var definition = new TagDef { Name = tagId.Split('.').Last(), Address = address, DataType = IgwDataType.UInt16 };
        Assert.True(new SiemensScanAddressParser().TryParse(definition, out var parsed, out _));
        return new TagBinding { TagId = tagId, Def = definition, Address = parsed };
    }
}
