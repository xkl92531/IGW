using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using IGW.Northbound.RestApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 现场 ST430 数据收集 DB(非优化)：字段名后带 TIA 属性 { S7_SetPoint := 'False'}，
/// UDT 数组里套结构体、结构体里再套结构体数组。UDT 数组和普通结构体一样展开成标记组(名字[下标])；
/// 「配置数据」生成的字段/结构映射按层嵌套，上报和回写都按同样的层级。
/// </summary>
public sealed class SiemensDbNestedUdtTests
{
    private const string Db = """
        DATA_BLOCK "ST430_Data Collection_A(数据收集)"
        { DB_Accessible_From_OPC_UA := 'FALSE' ;
         DB_Accessible_From_Webserver := 'FALSE' ;
         S7_Optimized_Access := 'FALSE' }
        VERSION : 0.1
        NON_RETAIN
           STRUCT
              MESFlag : Int;   // MES标志位
              PLCFlag : Int;   // PLC标志位
              TrayCode : String[30];   // 托盘码
              TimeStamp : String[20];   // 时间戳
              Batch1 : String[30];   // A胶物料批次号
              Batch2 : String[30];   // B胶物料批次号
              CellModeNumber : Int;   // 电芯模块数量
              CellModeGlueDataArray { S7_SetPoint := 'False'} : Array[0..10] of "MES_GlueData";   // 电芯模块数据结构体
              Result : Int;   // 结果
              ErrorCode : String[40];   // 错误码
              GLueData : Struct
                 GlueNum : Int;   // 涂胶数量
                 AmbientTemperature : Real;   // 环境温度
                 AmbientHumidity : Real;   // 环境湿度
                 GlueUseNumber : DInt;   // 胶头使用次数
              END_STRUCT;
              Space : String[68];   // 信息
           END_STRUCT;
        BEGIN
        END_DATA_BLOCK
        """;

    // 现场从 TIA 导出的 MES_GlueData.udt
    private const string Udt = """
        TYPE "MES_GlueData"
        VERSION : 0.1
           STRUCT
              GlueData : Struct   // 模块
                 CellNumber : Int;   // 数量
                 CellGlueData : Array[1..5] of Struct   // 电芯
                    GlueLength_L : Real;   // 涂胶长度
                    GlueWidth1_L : Real;   // 涂胶宽度1
                    GlueWidth2_L : Real;   // 涂胶宽度2
                    GlueWidth3_L : Real;   // 涂胶宽度3
                    GlueWidth4_L : Real;   // 涂胶宽度4
                    GlueWidth5_L : Real;   // 涂胶宽度5
                    GlueWeight_L : Real;   // 涂胶重量
                    ABGlueRatio_L : Real;   // 出胶比例
                    GlueSpeed_L : Real;   // 出胶速度
                    GlueFlowRate_L : Real;   // 出胶流量
                    AHeat_L : Real;   // A管温度
                    BHeat_L : Real;   // B管温度
                    GlueLength_R : Real;   // 涂胶长度
                    GlueWidth1_R : Real;   // 涂胶宽度1
                    GlueWidth2_R : Real;   // 涂胶宽度2
                    GlueWidth3_R : Real;   // 涂胶宽度3
                    GlueWidth4_R : Real;   // 涂胶宽度4
                    GlueWidth5_R : Real;   // 涂胶宽度5
                    GlueWeight_R : Real;   // 涂胶重量
                    ABGlueRatio_R : Real;   // 出胶比例
                    GlueSpeed_R : Real;   // 出胶速度
                    GlueFlowRate_R : Real;   // 出胶流量
                    GlueAirPressure_R : Real;   // A出胶气压
                    GlueBirPressure_R : Real;   // B出胶气压
                    AHeat_R : Real;   // A管温度
                    BHeat_R : Real;   // B管温度
                    WorkMode : Int;   // 工作模式
                    GlueNum_L : Int;   // 涂胶编号L
                    GlueNum_R : Int;   // 涂胶编号R
                    Space : String[58];   // 信息
                 END_STRUCT;
                 OffsetData : Struct   // 视觉偏移数据
                    XAxisOffset : Real;   // 偏移值_X
                    YAxisOffset : Real;   // 偏移值_Y
                    AAxisOffset : Real;   // 偏移值_A
                 END_STRUCT;
              END_STRUCT;
           END_STRUCT;

        END_TYPE
        """;

    private const string Block = "L.P.ST430";

    [Fact]
    public void Udt_array_becomes_element_groups_like_a_normal_struct_with_offsets_matching_tia()
    {
        var result = SiemensDbTypeParser.Parse(Db, 4306, [Udt]);
        Assert.Empty(result.Skipped);

        // 11 个元素组 CellModeGlueDataArray[0]..[10]，每个组 GlueData → CellNumber / CellGlueData[1..5] / OffsetData
        var elements = result.Root.SubGroups.Where(group => group.Name.StartsWith("CellModeGlueDataArray[")).ToArray();
        Assert.Equal(Enumerable.Range(0, 11).Select(index => $"CellModeGlueDataArray[{index}]"), elements.Select(group => group.Name));
        var glue = Assert.Single(elements[0].SubGroups);
        Assert.Equal("GlueData", glue.Name);
        Assert.Equal("DB4306.DBW124", glue.Tags.Single(tag => tag.Name == "CellNumber").Address);            // TIA 124.0
        Assert.Equal(["CellGlueData[1]", "CellGlueData[2]", "CellGlueData[3]", "CellGlueData[4]", "CellGlueData[5]", "OffsetData"],
            glue.SubGroups.Select(group => group.Name));
        var cell1 = glue.SubGroups[0];
        Assert.Equal(30, cell1.Tags.Count);
        Assert.Equal("DB4306.DBD126", cell1.Tags[0].Address);                                              // GlueLength_L 126.0
        Assert.Equal("DB4306.DBD130", cell1.Tags.Single(tag => tag.Name == "GlueWidth1_L").Address);
        Assert.Equal("DB4306.DBD296", glue.SubGroups[1].Tags[0].Address);                                  // 每个电芯 170 字节
        Assert.Equal("DB4306.DBD976", glue.SubGroups.Single(group => group.Name == "OffsetData").Tags[0].Address); // 124 + 852
        // 每个 UDT 元素 864 字节：第 2 个元素从 988 开始，数组之后的 Result 在 124 + 11×864 = 9628
        Assert.Equal("DB4306.DBW988", elements[1].SubGroups[0].Tags.Single(tag => tag.Name == "CellNumber").Address);
        Assert.Equal("DB4306.DBW9628", result.Root.Tags.Single(tag => tag.Name == "Result").Address);
        Assert.Equal("DB4306.DBW9672", result.Root.SubGroups.Single(group => group.Name == "GLueData").Tags[0].Address);
    }

    [Fact]
    public void Missing_udt_is_reported_once_and_later_points_are_not_imported_at_wrong_addresses()
    {
        var result = SiemensDbTypeParser.Parse(Db, 4306);

        var missing = Assert.Single(result.Skipped, issue => issue.Path == "CellModeGlueDataArray");
        Assert.Equal("MES_GlueData", missing.Type);
        Assert.Equal("DB4306.DBW122", result.Root.Tags.Single(tag => tag.Name == "CellModeNumber").Address);
        Assert.DoesNotContain(result.Root.Tags, tag => tag.Name is "Result" or "ErrorCode" or "Space");
        Assert.DoesNotContain(result.Root.SubGroups, group => group.Name == "GLueData");
        Assert.Contains(result.Skipped, issue => issue.Path == "Result" && issue.Reason.Contains("CellModeGlueDataArray"));
    }

    [Fact]
    public void Generated_field_mapping_nests_structs_the_way_they_are_nested_in_the_db()
    {
        var catalog = MesDataRangeCatalog.Build(Project(out _));
        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = Block, Scope = "Group" }], catalog, Block);

        Assert.DoesNotContain(fields, field => field.TagId.Contains("CellModeGlueDataArray"));
        var modules = structs.Single(item => item.AliasName == "CellModeGlueDataArray");
        Assert.Equal(("Array", "Enum", 0, 11), (modules.Kind, modules.CountMode, modules.StartIndex, modules.FixedCount));
        Assert.Empty(modules.Members);
        var glue = Assert.Single(modules.Structs);
        Assert.Equal(("Object", "GlueData", "GlueData"), (glue.Kind, glue.BaseGroup, glue.AliasName));
        Assert.Equal(["CellNumber"], glue.Members.Select(member => member.TagId));
        var cells = glue.Structs.Single(item => item.AliasName == "CellGlueData");
        Assert.Equal(("Array", "Enum", "CellGlueData", 1, 5), (cells.Kind, cells.CountMode, cells.BaseGroup, cells.StartIndex, cells.FixedCount));
        Assert.Equal(30, cells.Members.Count);
        Assert.Equal(["XAxisOffset", "YAxisOffset", "AAxisOffset"],
            glue.Structs.Single(item => item.AliasName == "OffsetData").Members.Select(member => member.TagId));
        Assert.Equal(["GlueNum", "AmbientTemperature", "AmbientHumidity", "GlueUseNumber"],
            structs.Single(item => item.AliasName == "GLueData").Members.Select(member => member.TagId));
    }

    [Fact]
    public void Data_tree_expands_the_udt_array_into_elements_and_their_structs()
    {
        var catalog = MesDataRangeCatalog.Build(Project(out _));
        var top = MesRangeTreeApi.Children(catalog, Block, Block, null, null, null).Items;
        var modules = top.Single(item => item.Name == "CellModeGlueDataArray");
        Assert.Equal(("Array", true), (modules.Scope, modules.HasChildren));      // 能展开

        var elements = MesRangeTreeApi.Children(catalog, Block, modules.SourcePath, null, null, null).Items;
        Assert.Equal(Enumerable.Range(0, 11).Select(index => $"CellModeGlueDataArray[{index}]"), elements.Select(item => item.Name));
        var glue = Assert.Single(MesRangeTreeApi.Children(catalog, Block, elements[0].SourcePath, null, null, null).Items);
        Assert.Equal(("GlueData", "Struct"), (glue.Name, glue.Scope));
        var inside = MesRangeTreeApi.Children(catalog, Block, glue.SourcePath, null, null, null).Items;
        Assert.Equal(["CellNumber", "CellGlueData", "OffsetData"], inside.Select(item => item.Name));
        Assert.True(inside.Single(item => item.Name == "CellGlueData").HasChildren);
    }

    [Fact]
    public async Task Nested_structs_are_uploaded_and_written_back_level_by_level()
    {
        var project = Project(out var tags);
        var catalog = MesDataRangeCatalog.Build(project);
        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = Block, Scope = "Group" }], catalog, Block);
        await using var hub = new DataHub();
        foreach (var (id, tag) in tags)
        {
            hub.Register(id, tag);
            hub.Update(id, DataValue.GoodAt(tag.DataType switch
            {
                IgwDataType.Float => (object)1.5f,
                IgwDataType.String => "T",
                IgwDataType.Int32 => 7,
                _ => (short)2,
            }, DateTime.UtcNow));
        }
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var payload = new MesPayload { Mode = MesPayloadModes.FieldMapping, Fields = fields, Structs = structs };
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new MesStep { Http = new MesHttp { Url = "http://nested.test/upload" } }] }, default);

        using var json = JsonDocument.Parse(handler.Body);
        var modules = json.RootElement.GetProperty("CellModeGlueDataArray");
        Assert.Equal(11, modules.GetArrayLength());
        var glue = modules[10].GetProperty("GlueData");
        Assert.Equal(2, glue.GetProperty("CellNumber").GetInt32());
        var cells = glue.GetProperty("CellGlueData");
        Assert.Equal(5, cells.GetArrayLength());
        Assert.Equal(1.5, cells[4].GetProperty("GlueWidth3_R").GetDouble());
        Assert.Equal("T", cells[4].GetProperty("Space").GetString());
        Assert.Equal(1.5, glue.GetProperty("OffsetData").GetProperty("AAxisOffset").GetDouble());
        Assert.Equal(2, json.RootElement.GetProperty("GLueData").GetProperty("GlueNum").GetInt32());
        Assert.Equal("T", json.RootElement.GetProperty("TrayCode").GetString());

        // 接口改了几个值原样返回：回写按同样的层级找回原点位
        string changed = handler.Body.Replace("\"CellNumber\":2", "\"CellNumber\":9");
        using var response = JsonDocument.Parse(changed);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null, hub.ListTagIds());
        Assert.Contains(writes, item => item.TagId == $"{Block}.CellModeGlueDataArray[3].GlueData.CellNumber" && item.Value == "9");
        Assert.Contains(writes, item => item.TagId == $"{Block}.CellModeGlueDataArray[3].GlueData.CellGlueData[5].GlueWidth3_R");
        Assert.Contains(writes, item => item.TagId == $"{Block}.CellModeGlueDataArray[0].GlueData.OffsetData.XAxisOffset");
    }

    [Fact]
    public async Task Struct_inside_a_struct_is_uploaded_nested_and_written_back_to_the_same_points()
    {
        const string db = """
            DATA_BLOCK "DB50"
            { S7_Optimized_Access := 'FALSE' }
               STRUCT
                  Code : Int;
                  Station : Struct
                     Speed : Int;
                     Inner : Struct
                        X : Int;
                        Y : Real;
                     END_STRUCT;
                  END_STRUCT;
               END_STRUCT;
            BEGIN
            END_DATA_BLOCK
            """;
        var block = SiemensDbTypeParser.Parse(db).Root;
        block.Name = "DB50";
        var project = new IgwProject { Channels = [new ChannelDef { Name = "L", Devices = [new DeviceDef { Name = "P", Groups = [block] }] }] };
        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = "L.P.DB50", Scope = "Group" }], MesDataRangeCatalog.Build(project), "L.P.DB50");
        var station = Assert.Single(structs);
        Assert.Equal(["Speed"], station.Members.Select(member => member.TagId));
        var inner = Assert.Single(station.Structs);                        // 字段/结构映射里也套着
        Assert.Equal(("Object", "Inner"), (inner.Kind, inner.BaseGroup));
        Assert.Equal(["X", "Y"], inner.Members.Select(member => member.TagId));

        await using var hub = new DataHub();
        void Add(string id, IgwDataType type, object value)
        {
            hub.Register(id, new TagDef { Name = id.Split('.').Last(), DataType = type, Access = TagAccess.ReadWrite });
            hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow));
        }
        Add("L.P.DB50.Code", IgwDataType.Int16, (short)1);
        Add("L.P.DB50.Station.Speed", IgwDataType.Int16, (short)2);
        Add("L.P.DB50.Station.Inner.X", IgwDataType.Int16, (short)3);
        Add("L.P.DB50.Station.Inner.Y", IgwDataType.Float, 4.5f);
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var payload = new MesPayload { Mode = MesPayloadModes.FieldMapping, Fields = fields, Structs = structs };
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new MesStep { Http = new MesHttp { Url = "http://nested.test/upload" } }] }, default);

        Assert.Equal("{\"Code\":1,\"Station\":{\"Speed\":2,\"Inner\":{\"X\":3,\"Y\":4.5}}}", handler.Body);

        using var response = JsonDocument.Parse("{\"Code\":9,\"Station\":{\"Speed\":8,\"Inner\":{\"X\":7,\"Y\":6.5}}}");
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null, hub.ListTagIds());
        Assert.Contains(writes, item => item.TagId == "L.P.DB50.Station.Inner.X" && item.Value == "7");
        Assert.Contains(writes, item => item.TagId == "L.P.DB50.Station.Inner.Y" && item.Value == "6.5");
        Assert.Contains(writes, item => item.TagId == "L.P.DB50.Station.Speed" && item.Value == "8");
    }

    [Fact]
    public async Task Flat_member_paths_saved_by_older_versions_still_upload_nested()
    {
        await using var hub = new DataHub();
        foreach (var (id, value) in new[] { ("L.P.DB50.Station.Speed", 2), ("L.P.DB50.Station.Inner.X", 3) })
        {
            hub.Register(id, new TagDef { Name = id.Split('.').Last(), DataType = IgwDataType.Int16 });
            hub.Update(id, DataValue.GoodAt((short)value, DateTime.UtcNow));
        }
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.FieldMapping,
            Structs = [new MesStruct { Kind = "Object", BaseGroup = "L.P.DB50.Station", AliasName = "Station",
                Members = [new MesField { TagId = "Speed" }, new MesField { TagId = "Inner.X" }] }],
        };
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new MesStep { Http = new MesHttp { Url = "http://nested.test/upload" } }] }, default);

        Assert.Equal("{\"Station\":{\"Speed\":2,\"Inner\":{\"X\":3}}}", handler.Body);
    }

    private static IgwProject Project(out List<(string Id, TagDef Tag)> tags)
    {
        var block = SiemensDbTypeParser.Parse(Db, 4306, [Udt]).Root;
        block.Name = "ST430";
        tags = [];
        Collect(block, Block, tags);
        return new IgwProject { Channels = [new ChannelDef { Name = "L", Devices = [new DeviceDef { Name = "P", Groups = [block] }] }] };
    }

    private static void Collect(TagGroupDef group, string path, List<(string, TagDef)> tags)
    {
        foreach (var tag in group.Tags) tags.Add(($"{path}.{tag.Name}", tag));
        foreach (var child in group.SubGroups) Collect(child, $"{path}.{child.Name}", tags);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}", Encoding.UTF8, "application/json") };
        }
    }
}
