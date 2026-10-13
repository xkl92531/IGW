using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayAddressEvidenceParserTests
{
    [Theory]
    [InlineData(
        "ns=3;s=\"Unit\".\"Bolts\"[4].\"Torque\"",
        "OpcUaNodeId",
        "ns=3;s=\"Unit\".\"Bolts\"[{0}].\"Torque\"",
        "Torque",
        4)]
    [InlineData(
        "DB1.Cells[1,2].Value",
        "Symbolic",
        "DB1.Cells[{0},{1}].Value",
        "Value",
        1, 2)]
    [InlineData(
        "DB1.Cells[1][2].Value",
        "Symbolic",
        "DB1.Cells[{0}][{1}].Value",
        "Value",
        1, 2)]
    [InlineData(
        "DB1.Cells[-2].Value",
        "Symbolic",
        "DB1.Cells[{0}].Value",
        "Value",
        -2)]
    [InlineData(
        "ns=3;s=\"DB\".\"Outer\"[2].\"Inner\"[5].\"Pressure\"",
        "OpcUaNodeId",
        "ns=3;s=\"DB\".\"Outer\"[{0}].\"Inner\"[{1}].\"Pressure\"",
        "Inner.Pressure",
        2, 5)]
    public void Parse_preserves_format_pattern_member_and_indices(
        string address,
        string format,
        string pattern,
        string member,
        params int[] indices)
    {
        Assert.True(ArrayAddressEvidenceParser.TryParse(address, out var actual));

        Assert.Equal(format, actual.SourceFormat);
        Assert.Equal(pattern, actual.CanonicalPattern);
        Assert.Equal(member, actual.MemberPath);
        Assert.Equal(indices, actual.FlattenedIndices);
    }

    [Fact]
    public void Parse_preserves_each_multidimensional_index_vector()
    {
        Assert.True(ArrayAddressEvidenceParser.TryParse(
            "DB1.Matrix[1,2].Nested[-3,4].Value",
            out var actual));

        Assert.Collection(
            actual.IndexVectors,
            vector => Assert.Equal([1, 2], vector),
            vector => Assert.Equal([-3, 4], vector));
        Assert.Equal("Nested.Value", actual.MemberPath);
    }

    [Theory]
    [InlineData("Recipe_0.Value")]
    [InlineData("Folder.0.Value")]
    [InlineData("Text[abc]")]
    [InlineData("Text[1,]")]
    [InlineData("")]
    public void Parse_rejects_names_and_invalid_brackets_without_physical_index_evidence(string address)
    {
        Assert.False(ArrayAddressEvidenceParser.TryParse(address, out _));
    }
}
