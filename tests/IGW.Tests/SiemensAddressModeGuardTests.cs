using IGW.Abstractions;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensAddressModeGuardTests
{
    private static ChannelDef Channel(string driver = "Siemens.S7") => new() { Name = "C", DriverId = driver };
    private static DeviceDef Device(params string[] addresses) => new()
    {
        Name = "D",
        Groups = [new TagGroupDef { Name = "G", Tags = [.. addresses.Select((a, i) => new TagDef { Name = "T" + i, Address = a })] }],
    };

    [Fact] public void Classic_only_passes() => Assert.Null(SiemensAddressModeGuard.Check(Channel(), Device("DB1.DBX0.0", "I0.1", "MW10")));
    [Fact] public void Symbolic_only_passes() => Assert.Null(SiemensAddressModeGuard.Check(Channel(), Device("DB490.Type.Error1", "Db.Path")));
    [Fact] public void Other_drivers_are_ignored() => Assert.Null(SiemensAddressModeGuard.Check(Channel("Modbus.Tcp"), Device("DB1.DBX0.0", "Db.Path")));

    [Fact]
    public void Mixed_is_rejected_with_examples()
    {
        string? error = SiemensAddressModeGuard.Check(Channel(), Device("DB1.DBX0.0", "Db.Path"));
        Assert.NotNull(error);
        Assert.Contains("DB1.DBX0.0", error);
        Assert.Contains("Db.Path", error);
    }

    [Fact]
    public void Oversized_unparseable_address_is_ignored()
    {
        // "DB99999999999.DBX0.0" 的 DB 号超出 int 范围，SiemensAddressDescriptor.Parse 内部
        // int.Parse 会抛 OverflowException；应被守卫忽略（交给驱动报错），不参与 Classic/Symbolic 判定。
        string? error = SiemensAddressModeGuard.Check(Channel(), Device("DB1.DBX0.0", "I0.1", "DB99999999999.DBX0.0"));
        Assert.Null(error);
    }

    [Fact]
    public void Root_level_tag_participates_in_classification()
    {
        var device = new DeviceDef
        {
            Name = "D",
            Tags = [new TagDef { Name = "Root0", Address = "DB1.DBX0.0" }],
            Groups = [new TagGroupDef { Name = "G", Tags = [new TagDef { Name = "T0", Address = "Db.Path" }] }],
        };

        string? error = SiemensAddressModeGuard.Check(Channel(), device);
        Assert.NotNull(error);
        Assert.Contains("DB1.DBX0.0", error);
        Assert.Contains("Db.Path", error);
    }

    [Fact]
    public void Deeply_nested_subgroup_tag_participates_in_classification()
    {
        var device = new DeviceDef
        {
            Name = "D",
            Groups =
            [
                new TagGroupDef
                {
                    Name = "G1",
                    Tags = [new TagDef { Name = "T0", Address = "DB1.DBX0.0" }],
                    SubGroups =
                    [
                        new TagGroupDef
                        {
                            Name = "G2",
                            SubGroups =
                            [
                                new TagGroupDef
                                {
                                    Name = "G3",
                                    Tags = [new TagDef { Name = "T1", Address = "Db.Path" }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        string? error = SiemensAddressModeGuard.Check(Channel(), device);
        Assert.NotNull(error);
        Assert.Contains("DB1.DBX0.0", error);
        Assert.Contains("Db.Path", error);
    }
}
