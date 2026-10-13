using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ExpandedArrayMigrationTests
{
    [Fact]
    public void Preview_is_non_mutating_and_apply_merges_only_contiguous_siblings()
    {
        var tags = new List<TagDef>
        {
            Tag("v0", "Values[0]", "DB1.DBW0"),
            Tag("v1", "Values[1]", "DB1.DBW2"),
            Tag("v2", "Values[2]", "DB1.DBW4"),
        };
        var preview = Assert.Single(ExpandedArrayMigration.Preview(tags));
        Assert.True(preview.CanApply);
        Assert.Equal(3, tags.Count);

        var array = ExpandedArrayMigration.Apply(tags, preview);
        Assert.Single(tags);
        Assert.Equal("Values", array.Name);
        Assert.Equal([3], array.Array!.Dimensions);
        Assert.Equal(2, array.Array.ElementStrideBytes);
    }

    [Fact]
    public void Gapped_indices_are_previewed_but_not_applied()
    {
        var tags = new List<TagDef>
        {
            Tag("v0", "Values[0]", "DB1.DBW0"),
            Tag("v2", "Values[2]", "DB1.DBW4"),
        };
        var preview = Assert.Single(ExpandedArrayMigration.Preview(tags));
        Assert.False(preview.CanApply);
        Assert.Contains("不连续", preview.Reason);
    }

    private static TagDef Tag(string id, string name, string address) => new()
    {
        Id = id, Name = name, Address = address, DataType = IgwDataType.Int16,
        Access = TagAccess.ReadWrite, ScanRateMs = 100,
    };
}
