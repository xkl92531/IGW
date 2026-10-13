using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensDbArrayImporterTests
{
    [Fact]
    public void Struct_arrays_become_one_group_per_element_and_plain_arrays_stay_single_tags()
    {
        const string source = """
            DATA_BLOCK "DB2100_CellData"
            { S7_Optimized_Access := 'FALSE' }
            STRUCT
              Header : Int; // 工单头
              CellData : Array[1..10] of Struct
                DMCCode : String[50]; // 条码
                KValue : Real; // K值
                IsNG : Int; // 判定结果
              END_STRUCT;
              Codes : Array[-2..2,0..1] of String[20]; // 二维编码
            END_STRUCT;
            BEGIN
            END_DATA_BLOCK
            """;

        var result = SiemensDbTypeParser.Parse(source);

        // 结构体数组和普通结构体一样展开成标记组：每个元素一个组 CellData[1]..CellData[10]
        Assert.DoesNotContain(result.Root.Tags, tag => tag.Name == "CellData");
        Assert.Equal(Enumerable.Range(1, 10).Select(index => $"CellData[{index}]"),
            result.Root.SubGroups.Select(group => group.Name));
        var first = result.Root.SubGroups[0];
        Assert.Equal(["DMCCode", "KValue", "IsNG"], first.Tags.Select(tag => tag.Name));
        Assert.Equal("条码", first.Tags[0].Description);
        Assert.Equal("DB2100.DBB2", first.Tags[0].Address);                      // Header 之后
        Assert.Equal("DB2100.DBD54", first.Tags[1].Address);                     // String[50] 占 52 字节
        // 每个元素 52 + 4 + 2 = 58 字节，第 2 个元素从 60 开始
        Assert.Equal("DB2100.DBB60", result.Root.SubGroups[1].Tags[0].Address);

        var codes = Assert.Single(result.Root.Tags, tag => tag.Name == "Codes");
        Assert.Equal(IgwDataType.String, codes.DataType);
        Assert.Equal([5, 2], codes.Array!.Dimensions);
        Assert.Equal([-2, 0], codes.Array.LowerBounds);
        Assert.Equal(20, codes.Array.StringLength);
    }

    [Fact]
    public void Imports_named_udt_array_and_preserves_optimized_symbolic_address()
    {
        const string source = """
            TYPE "CellType"
            STRUCT
              Value : Real;
              State : Byte;
            END_STRUCT;
            END_TYPE
            DATA_BLOCK "ProductionDb"
            { S7_Optimized_Access := 'TRUE' }
            VAR
              Cells : Array[0..2] of "CellType";
            END_VAR
            BEGIN
            END_DATA_BLOCK
            """;

        var result = SiemensDbTypeParser.Parse(source);

        Assert.True(result.Optimized);
        Assert.Empty(result.Root.Tags);
        Assert.Equal(["Cells[0]", "Cells[1]", "Cells[2]"], result.Root.SubGroups.Select(group => group.Name));
        var second = result.Root.SubGroups[1];
        Assert.Equal(["Value", "State"], second.Tags.Select(tag => tag.Name));
        Assert.Equal("ProductionDb.Cells[1].Value", second.Tags[0].Address);      // 优化 DB 用符号地址
    }

    [Fact]
    public void Legacy_desktop_wrapper_returns_arrays_without_flattening()
    {
        const string source = "DATA_BLOCK DB7 STRUCT Values : Array[0..3] of Int; END_STRUCT BEGIN END_DATA_BLOCK";
        var result = SiemensDbTypeParser.Parse(source);

        var tag = Assert.Single(result.Root.Tags);
        Assert.Equal("DB7.DBW0", tag.Address);
        Assert.Equal([4], tag.Array!.Dimensions);
    }

    [Fact]
    public void Real_db2100_fixture_imports_the_struct_array_as_element_groups_and_validates()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(root, "src", "IGW.WebUi", "src", "utils",
            "fixtures", "siemens-db", "DB2100_电芯OCV_DataDB.db.txt");
        Assert.True(File.Exists(path), path);
        var result = SiemensDbTypeParser.Parse(File.ReadAllText(path));

        Assert.Equal(4 + 11 * 6, result.TagCount);
        Assert.Equal(11, result.Root.SubGroups.Count(group => group.Name.StartsWith("CellData[")));
        var last = result.Root.SubGroups.Single(group => group.Name == "CellData[10]");
        Assert.Equal(6, last.Tags.Count);
        Assert.Equal("MES返回NG原因(中文描述)", last.Tags.Single(tag => tag.Name == "ErrorMessage").Description);

        var project = new IgwProject
        {
            Channels = [new ChannelDef
            {
                Name = "S7", DriverId = "Siemens.S7",
                Devices = [new DeviceDef { Name = "PLC", Groups = [result.Root] }],
            }],
        };
        ArrayProjectValidator.NormalizeAndValidate(project);
    }

    [Fact]
    public void Rejects_recursive_named_udt_instead_of_recursing_forever()
    {
        const string source = """
            TYPE "RecursiveType"
            STRUCT
              Child : "RecursiveType";
            END_STRUCT;
            END_TYPE
            DATA_BLOCK "RecursiveDb"
            VAR
              Items : Array[0..1] of "RecursiveType";
            END_VAR
            BEGIN
            END_DATA_BLOCK
            """;

        var error = Assert.Throws<InvalidDataException>(() => SiemensDbTypeParser.Parse(source));
        Assert.Contains("递归循环", error.Message);
    }

    [Fact]
    public void Imports_nested_and_multidimensional_arrays_inside_an_array_of_structs()
    {
        const string source = """
            TYPE "BoltType"
            STRUCT
              Torque : Array[1..3] of Real;
              Codes : Array[0..1,-1..0] of Int;
            END_STRUCT;
            END_TYPE
            DATA_BLOCK "Fastening"
            { S7_Optimized_Access := 'TRUE' }
            VAR
              Bolts : Array[1..2] of "BoltType";
            END_VAR
            BEGIN
            END_DATA_BLOCK
            """;

        var result = SiemensDbTypeParser.Parse(source);

        // 结构体数组展开成 Bolts[1]、Bolts[2] 两个组；元素里的普通数组仍是一个数组点位
        Assert.Equal(["Bolts[1]", "Bolts[2]"], result.Root.SubGroups.Select(group => group.Name));
        var bolt = result.Root.SubGroups[0];
        var torque = Assert.Single(bolt.Tags, tag => tag.Name == "Torque");
        Assert.Equal([3], torque.Array!.Dimensions);
        Assert.Equal([1], torque.Array.LowerBounds);
        Assert.Equal("Fastening.Bolts[1].Torque", torque.Address);
        var codes = Assert.Single(bolt.Tags, tag => tag.Name == "Codes");
        Assert.Equal([2, 2], codes.Array!.Dimensions);
        Assert.Equal([0, -1], codes.Array.LowerBounds);
    }
}
