using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayShapeRecognizerTests
{
    [Fact]
    public void Native_array_definition_has_explicit_confidence_without_leaf_expansion()
    {
        var group = new TagGroupDef
        {
            Name = "DB",
            Tags =
            [
                new TagDef
                {
                    Id = "cells",
                    Name = "Cells",
                    Address = "DB1.Cells",
                    DataType = IgwDataType.Int16,
                    IsArray = true,
                    Array = new ArrayDef
                    {
                        ElementType = IgwDataType.Int16,
                        Dimensions = [2, 3],
                        LowerBounds = [1, -2],
                        ElementStrideBytes = 2,
                    },
                },
            ],
        };

        var descriptor = AssertDescriptor(ProjectArrayRecognitionCatalog.Build(Project(group)), "Line.Plc.DB.Cells");

        Assert.Equal(ArrayRecognitionConfidence.Explicit, descriptor.Confidence);
        Assert.Equal(LogicalArrayShapeKind.Rectangular, descriptor.ShapeKind);
        Assert.Equal([2, 3], descriptor.Dimensions);
        Assert.Equal([1, -2], descriptor.LowerBounds);
        Assert.Equal(ArrayBindingKind.NativeTag, descriptor.BindingKind);
        Assert.Empty(descriptor.Leaves);
    }

    [Fact]
    public void Physical_addresses_not_business_names_define_the_array()
    {
        var array = new TagGroupDef
        {
            Name = "AnyBusinessName",
            SubGroups =
            [
                Element("first", 5, "ActualArray"),
                Element("second", 6, "ActualArray"),
            ],
        };

        var descriptor = AssertDescriptor(
            ProjectArrayRecognitionCatalog.Build(Project(DbWith(array))),
            "Line.Plc.DB.AnyBusinessName");

        Assert.Equal([5], descriptor.LowerBounds);
        Assert.Equal([2], descriptor.Dimensions);
        Assert.Equal(ArrayRecognitionConfidence.Proven, descriptor.Confidence);
        Assert.Equal("OpcUaNodeId", descriptor.SourceFormat);
        Assert.Equal(["Value"], descriptor.Members.Select(member => member.Path));
    }

    [Theory]
    [InlineData("Matrix[1,2]", "Matrix[1,3]", "Matrix[2,2]", "Matrix[2,3]")]
    [InlineData("Matrix[1][2]", "Matrix[1][3]", "Matrix[2][2]", "Matrix[2][3]")]
    public void Multidimensional_bracket_forms_produce_the_same_rank(params string[] indexedNames)
    {
        var matrix = new TagGroupDef
        {
            Name = "MatrixContainer",
            SubGroups = indexedNames.Select((indexed, offset) => new TagGroupDef
            {
                Name = "row-" + offset,
                Tags =
                [
                    new TagDef
                    {
                        Name = "Value",
                        Address = $"DB1.{indexed}.Value",
                        DataType = IgwDataType.Int16,
                    },
                ],
            }).ToList(),
        };

        var descriptor = AssertDescriptor(
            ProjectArrayRecognitionCatalog.Build(Project(DbWith(matrix))),
            "Line.Plc.DB.MatrixContainer");

        Assert.Equal(LogicalArrayShapeKind.Rectangular, descriptor.ShapeKind);
        Assert.Equal([2, 2], descriptor.Dimensions);
        Assert.Equal([1, 2], descriptor.LowerBounds);
    }

    [Fact]
    public void Gapped_indices_are_sparse_and_preserve_only_real_elements()
    {
        var sparse = new TagGroupDef
        {
            Name = "SparseContainer",
            SubGroups = [Element("a", 1, "Sparse"), Element("b", 3, "Sparse"), Element("c", 9, "Sparse")],
        };

        var descriptor = AssertDescriptor(
            ProjectArrayRecognitionCatalog.Build(Project(DbWith(sparse))),
            "Line.Plc.DB.SparseContainer");

        Assert.Equal(LogicalArrayShapeKind.Sparse, descriptor.ShapeKind);
        Assert.Equal([1], descriptor.LowerBounds);
        Assert.Equal([9], descriptor.Dimensions);
        Assert.Equal([1, 3, 9], descriptor.ExistingIndices.Select(index => index[0]));
    }

    [Fact]
    public void Unequal_nested_lengths_are_jagged_and_keep_a_nested_member()
    {
        var outer = new TagGroupDef
        {
            Name = "OuterContainer",
            SubGroups =
            [
                OuterElement("alpha", 0, [0, 1]),
                OuterElement("beta", 1, [0, 1, 2]),
            ],
        };

        var descriptor = AssertDescriptor(
            ProjectArrayRecognitionCatalog.Build(Project(DbWith(outer))),
            "Line.Plc.DB.OuterContainer");

        Assert.Equal(LogicalArrayShapeKind.Jagged, descriptor.ShapeKind);
        var nested = Assert.Single(descriptor.Members, member => member.NestedArray is not null).NestedArray!;
        Assert.Equal("Inner", nested.Name);
        Assert.Equal(LogicalArrayShapeKind.Jagged, nested.ShapeKind);
        Assert.Contains(nested.Leaves, leaf => leaf.Indices.SequenceEqual([1, 2]));
    }

    [Fact]
    public void Numeric_display_groups_without_address_evidence_are_not_arrays()
    {
        var numeric = new TagGroupDef
        {
            Name = "OrdinaryFolders",
            SubGroups =
            [
                new TagGroupDef { Name = "0", Tags = [new TagDef { Name = "A", DataType = IgwDataType.Int16 }] },
                new TagGroupDef { Name = "1", Tags = [new TagDef { Name = "A", DataType = IgwDataType.Int16 }] },
            ],
        };

        var catalog = ProjectArrayRecognitionCatalog.Build(Project(DbWith(numeric)));

        Assert.False(catalog.TryGet("Line.Plc.DB.OrdinaryFolders", out _));
        Assert.Contains(catalog.Diagnostics, item =>
            item.SourcePath == "Line.Plc.DB.OrdinaryFolders" && item.Code == "ArrayEvidenceMissing");
    }

    [Fact]
    public void Mismatched_member_types_are_not_merged()
    {
        var first = Element("first", 0, "Mixed");
        var second = Element("second", 1, "Mixed");
        second.Tags[0].DataType = IgwDataType.Float;
        var mixed = new TagGroupDef { Name = "MixedContainer", SubGroups = [first, second] };

        var catalog = ProjectArrayRecognitionCatalog.Build(Project(DbWith(mixed)));

        Assert.False(catalog.TryGet("Line.Plc.DB.MixedContainer", out _));
        Assert.Contains(catalog.Diagnostics, item => item.Code == "ArraySchemaConflict");
    }

    private static LogicalArrayDescriptor AssertDescriptor(ProjectArrayRecognitionCatalog catalog, string path)
    {
        Assert.True(catalog.TryGet(path, out var descriptor), string.Join(" | ", catalog.Diagnostics));
        return descriptor;
    }

    private static TagGroupDef DbWith(TagGroupDef child) => new() { Name = "DB", SubGroups = [child] };

    private static TagGroupDef Element(string displayName, int index, string physicalName) => new()
    {
        Name = displayName,
        Tags =
        [
            new TagDef
            {
                Name = "Value",
                Address = $"ns=3;s=\"DB\".\"{physicalName}\"[{index}].\"Value\"",
                DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite,
            },
        ],
    };

    private static TagGroupDef OuterElement(string displayName, int outer, int[] inner) => new()
    {
        Name = displayName,
        Tags =
        [
            new TagDef
            {
                Name = "Result",
                Address = $"ns=3;s=\"DB\".\"Outer\"[{outer}].\"Result\"",
                DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite,
            },
        ],
        SubGroups =
        [
            new TagGroupDef
            {
                Name = "anything",
                SubGroups = inner.Select(value => new TagGroupDef
                {
                    Name = "item-" + value,
                    Tags =
                    [
                        new TagDef
                        {
                            Name = "Pressure",
                            Address = $"ns=3;s=\"DB\".\"Outer\"[{outer}].\"Inner\"[{value}].\"Pressure\"",
                            DataType = IgwDataType.Float,
                            Access = TagAccess.ReadWrite,
                        },
                    ],
                }).ToList(),
            },
        ],
    };

    private static IgwProject Project(TagGroupDef root) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                Devices = [new DeviceDef { Name = "Plc", Groups = [root] }],
            },
        ],
    };
}
