using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class MesRangeOptionsApiTests
{
    [Fact]
    public void Expanded_ten_thousand_element_array_is_one_paged_range()
    {
        var elements = Enumerable.Range(0, 10_000).Select(index => new TagGroupDef
        {
            Name = "element-" + index,
            Tags =
            [
                new TagDef
                {
                    Name = "Value",
                    Address = $"ns=3;s=\"DB\".\"Huge\"[{index}].\"Value\"",
                    DataType = IgwDataType.Int16,
                    Access = TagAccess.ReadWrite,
                },
            ],
        }).ToList();
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "OpcUa.Client",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Groups =
                            [
                                new TagGroupDef
                                {
                                    Name = "OP200Data",
                                    SubGroups = [new TagGroupDef { Name = "BusinessValues", SubGroups = elements }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var result = MesRangeOptionsApi.Search(project,
            "Line.Plc.OP200Data.BusinessValues", null, null, 50);

        var array = Assert.Single(result.Items);
        Assert.Equal("Array", array.Scope);
        Assert.Equal(10_000, array.TagCount);
        Assert.Equal(10_000, array.ExistingElementCount);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public void Search_is_stably_paged_and_rejects_invalid_limits()
    {
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Groups = Enumerable.Range(0, 5).Select(index => new TagGroupDef
                            {
                                Name = "DB" + index,
                                Tags = [new TagDef { Name = "Value", DataType = IgwDataType.Int16 }],
                            }).ToList(),
                        },
                    ],
                },
            ],
        };

        var first = MesRangeOptionsApi.Search(project, "Line.Plc", "DB", null, 2);
        var second = MesRangeOptionsApi.Search(project, "Line.Plc", "DB", first.NextCursor, 2);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.DoesNotContain(second.Items, item => first.Items.Any(previous => previous.SourcePath == item.SourcePath));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MesRangeOptionsApi.Search(project, null, null, null, 501));
    }
}
