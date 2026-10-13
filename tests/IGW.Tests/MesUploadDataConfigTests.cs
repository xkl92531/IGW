using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 上报节点「配置数据」：可以只选数据块里的单个点；「报文高级设置」按数据范围的结构逐项改名、选择是否上报。
/// 报文沿用数据范围的嵌套格式(设备 → DB → 点)，没有改名时与旧版完全一致。
/// </summary>
public sealed class MesUploadDataConfigTests
{
    private static IgwProject Project() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Name = "DB10",
                                Tags =
                                [
                                    new TagDef { Name = "Counter", Address = "DB10.DBW0", DataType = IgwDataType.Int16 },
                                    new TagDef { Name = "Temp", Address = "DB10.DBD2", DataType = IgwDataType.Float },
                                    new TagDef
                                    {
                                        Name = "Cells", Address = "DB10.DBX10.0", DataType = IgwDataType.Struct, IsArray = true,
                                        Array = new ArrayDef
                                        {
                                            ElementType = IgwDataType.Struct, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 4,
                                            Struct = new StructDef
                                            {
                                                Name = "Cell", SizeBytes = 4,
                                                Fields =
                                                [
                                                    new StructFieldDef { Name = "Volt", DataType = IgwDataType.Int16, ByteOffset = 0 },
                                                    new StructFieldDef { Name = "Ok", DataType = IgwDataType.Bool, ByteOffset = 2, BitOffset = 0 },
                                                ],
                                            },
                                        },
                                    },
                                ],
                                SubGroups =
                                [
                                    new TagGroupDef
                                    {
                                        Name = "UDT1",
                                        Tags =
                                        [
                                            new TagDef { Name = "A", Address = "DB10.DBW20", DataType = IgwDataType.Int16 },
                                            new TagDef { Name = "B", Address = "DB10.DBW22", DataType = IgwDataType.Int16 },
                                        ],
                                    },
                                    new TagGroupDef { Name = "Bin[0]", Tags = [new TagDef { Name = "Code", Address = "DB10.DBW30", DataType = IgwDataType.Int16 },
                                        new TagDef { Name = "Ok", Address = "DB10.DBW34", DataType = IgwDataType.Int16 }] },
                                    new TagGroupDef { Name = "Bin[1]", Tags = [new TagDef { Name = "Code", Address = "DB10.DBW32", DataType = IgwDataType.Int16 },
                                        new TagDef { Name = "Ok", Address = "DB10.DBW36", DataType = IgwDataType.Int16 }] },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static async Task<JsonElement> BuildAsync(IgwProject project, params MesDataRange[] ranges)
    {
        var catalog = MesDataRangeCatalog.Build(project);
        var compiled = MesRangePayloadCompiler.Compile(new MesPayload { Mode = MesPayloadModes.DataRanges, DataRanges = [.. ranges] }, catalog);
        await using var hub = new DataHub();
        FillHub(hub, project);
        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub, _ => ConnectionState.Connected);
        return JsonDocument.Parse(body.Json).RootElement.Clone();
    }

    private static void FillHub(DataHub hub, IgwProject project)
    {
        foreach (var (id, def) in Leaves(project))
        {
            hub.Register(id, def);
            hub.Update(id, DataValue.GoodAt(def.DataType == IgwDataType.Float ? 1.5f : (object)(short)7, DateTime.UtcNow));
        }
        // 原生结构体数组：值是驱动读回来的整块数组，自带结构定义
        var cells = project.Channels[0].Devices[0].Groups[0].Tags.Single(tag => tag.Name == "Cells");
        var shape = cells.Array!.Shape;
        hub.Register("Line.Plc.DB10.Cells", cells);
        hub.Update("Line.Plc.DB10.Cells", DataValue.GoodAt(StructArrayValue.Create(shape, cells.Array.Struct!,
            new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Volt"] = PrimitiveArrayValue<short>.Create(shape, [11, 12]),
                ["Ok"] = PrimitiveArrayValue<bool>.Create(shape, [true, false]),
            }), DateTime.UtcNow));
    }

    private static IEnumerable<(string Id, TagDef Def)> Leaves(IgwProject project)
    {
        IEnumerable<(string, TagDef)> Walk(TagGroupDef group, string path)
        {
            foreach (var tag in group.Tags.Where(tag => tag.Array is null)) yield return (path + "." + tag.Name, tag);
            foreach (var sub in group.SubGroups)
            foreach (var item in Walk(sub, path + "." + sub.Name))
                yield return item;
        }
        return project.Channels[0].Devices[0].Groups.SelectMany(group => Walk(group, "Line.Plc." + group.Name));
    }

    [Fact]
    public async Task Single_tags_are_uploaded_nested_under_device_and_block()
    {
        var json = await BuildAsync(Project(),
            new MesDataRange { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag" },
            new MesDataRange { SourcePath = "Line.Plc.DB10.UDT1.A", Scope = "Tag" });

        Assert.Equal(7, json.GetProperty("Plc").GetProperty("DB10").GetProperty("Counter").GetInt16());
        Assert.Equal(7, json.GetProperty("Plc").GetProperty("DB10").GetProperty("UDT1").GetProperty("A").GetInt16());
        Assert.False(json.GetProperty("Plc").GetProperty("DB10").TryGetProperty("Temp", out _));
    }

    [Fact]
    public async Task Renames_apply_to_plain_tags_struct_members_and_array_element_members_without_touching_the_project()
    {
        var project = Project();
        var json = await BuildAsync(project, new MesDataRange
        {
            SourcePath = "Line.Plc.DB10",
            Names = new()
            {
                ["Counter"] = "count", ["UDT1"] = "station", ["UDT1.A"] = "alpha",
                ["Bin[].Code"] = "code", ["Cells[].Volt"] = "voltage",
            },
        });

        var block = json.GetProperty("Plc").GetProperty("DB10");
        Assert.Equal(7, block.GetProperty("count").GetInt16());
        Assert.False(block.TryGetProperty("Counter", out _));
        Assert.Equal(7, block.GetProperty("station").GetProperty("alpha").GetInt16());
        Assert.Equal(7, block.GetProperty("station").GetProperty("B").GetInt16());
        Assert.Equal(2, block.GetProperty("Bin").GetArrayLength());
        Assert.Equal(7, block.GetProperty("Bin")[1].GetProperty("code").GetInt16());
        Assert.Equal(12, block.GetProperty("Cells")[1].GetProperty("voltage").GetInt16());   // 真正发出去的报文里就是新名字
        var compiled = MesRangePayloadCompiler.Compile(new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges = [new MesDataRange { SourcePath = "Line.Plc.DB10", Names = new() { ["Cells[].Volt"] = "voltage" } }],
        }, MesDataRangeCatalog.Build(project));
        var cells = ((CompiledMesSourceObject)compiled.Ranges[0].Source).Properties.Single(item => item.Name == "Cells");
        Assert.Equal("voltage", ((CompiledMesSourceLogicalArray)cells.Value).Definition.Struct!.Fields[0].JsonName);
        Assert.Null(project.Channels[0].Devices[0].Groups[0].Tags[2].Array!.Struct!.Fields[0].JsonName);
    }

    [Fact]
    public async Task Without_renames_the_payload_is_unchanged_and_a_duplicate_name_is_rejected()
    {
        var json = await BuildAsync(Project(), new MesDataRange { SourcePath = "Line.Plc.DB10" });
        var block = json.GetProperty("Plc").GetProperty("DB10");
        Assert.Equal(["Counter", "Temp", "Cells", "UDT1", "Bin"], block.EnumerateObject().Select(item => item.Name).ToArray());

        var ex = Assert.Throws<InvalidDataException>(() => MesRangePayloadCompiler.Compile(new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges = [new MesDataRange { SourcePath = "Line.Plc.DB10", Names = new() { ["Counter"] = "Temp" } }],
        }, MesDataRangeCatalog.Build(Project())));
        Assert.Contains("重复字段:Temp", ex.Message);
    }

    [Fact]
    public async Task Members_inside_array_elements_can_be_left_out_of_the_payload()
    {
        var json = await BuildAsync(Project(), new MesDataRange
        {
            SourcePath = "Line.Plc.DB10",
            Omit = ["Bin[].Ok", "Cells[].Ok"],
            Names = new() { ["Cells[].Volt"] = "voltage" },
        });

        var block = json.GetProperty("Plc").GetProperty("DB10");
        foreach (var bin in block.GetProperty("Bin").EnumerateArray())
            Assert.Equal(["Code"], bin.EnumerateObject().Select(item => item.Name).ToArray());
        foreach (var cell in block.GetProperty("Cells").EnumerateArray())
            Assert.Equal(["voltage"], cell.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(11, block.GetProperty("Cells")[0].GetProperty("voltage").GetInt16());
        Assert.Equal(7, block.GetProperty("Counter").GetInt16());           // 其它字段不受影响

        // 数组本身就是数据范围时，元素成员路径从 [] 开始
        var array = await BuildAsync(Project(), new MesDataRange { SourcePath = "Line.Plc.DB10.Cells", Scope = "Array", Omit = ["[].Volt"] });
        Assert.Equal(["Ok"], array.GetProperty("Plc").GetProperty("DB10").GetProperty("Cells")[0].EnumerateObject().Select(item => item.Name).ToArray());
    }

    [Theory]
    [InlineData("{}", "Counter")]
    [InlineData("{\"fieldKeyMode\":\"ItemId\"}", "Counter")]
    public async Task Configured_data_preserves_structures_when_only_plain_field_keys_change(string settings, string counterKey)
    {
        var project = Project();
        var catalog = MesDataRangeCatalog.Build(project);
        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = "Line.Plc.DB10", Scope = "Group", Excludes = ["Temp"] }], catalog, "Line.Plc.DB10");

        Assert.Equal(["Line.Plc.DB10.Counter"], fields.Select(field => field.TagId).ToArray());
        // 原生结构体数组点位(一个点位带完整结构)也按结构体生成：数组(套对象)，起止取 PLC 下界和长度
        var cells = structs.Single(item => item.AliasName == "Cells");
        Assert.Equal(("Array", "Fixed", "Line.Plc.DB10.Cells", 0, 2), (cells.Kind, cells.CountMode, cells.BaseGroup, cells.StartIndex, cells.FixedCount));
        Assert.Equal(["Volt", "Ok"], cells.Members.Select(member => member.TagId).ToArray());
        Assert.All(fields, field => Assert.Equal("", field.FieldName));           // 留空=用点名，和旧版一样
        var udt = structs.Single(item => item.AliasName == "UDT1");
        Assert.Equal(("Object", "Line.Plc.DB10.UDT1"), (udt.Kind, udt.BaseGroup));
        Assert.Equal(["A", "B"], udt.Members.Select(member => member.TagId).ToArray());
        var bin = structs.Single(item => item.AliasName == "Bin");
        Assert.Equal(("Array", "Enum", "Line.Plc.DB10.Bin", 0, 2), (bin.Kind, bin.CountMode, bin.BaseGroup, bin.StartIndex, bin.FixedCount));
        Assert.Equal(["Code", "Ok"], bin.Members.Select(member => member.TagId).ToArray());

        // 用生成的配置真正跑一次上报：报文平铺，结构体是对象，下标子组是对象数组，原生数组是 JSON 数组
        await using var hub = new DataHub();
        FillHub(hub, project);
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, httpMessageHandler: handler);
        var payload = JsonSerializer.Deserialize<MesPayload>(settings, ProjectStore.Options)!;
        payload.Mode = MesPayloadModes.FieldMapping;
        payload.Fields = fields;
        payload.Structs = structs;
        // 即使旧配置同时保留被结构体覆盖的点位，也不能额外生成完整 ID 顶层键。
        payload.Fields.Add(new MesField { TagId = "Line.Plc.DB10.Cells" });
        await engine.TestRunAsync(new MesTask
        {
            Payload = payload,
            Steps = [new MesStep { Http = new MesHttp { Url = "http://localhost/mes" } }],
        }, default);

        using var json = JsonDocument.Parse(handler.Body);
        var root = json.RootElement;
        bool itemId = false; // Legacy global setting is ignored; uncoded fields keep names.
        Assert.Equal([itemId ? "parameterDic" : counterKey, "Cells", "UDT1", "Bin"], root.EnumerateObject().Select(item => item.Name).ToArray());
        var ordinary = itemId ? root.GetProperty("parameterDic") : root;
        Assert.Equal(7, ordinary.GetProperty(counterKey).GetInt32());
        if (itemId) Assert.Equal(["Line.Plc.DB10.Counter"], ordinary.EnumerateObject().Select(p => p.Name));
        Assert.Equal(12, root.GetProperty("Cells")[1].GetProperty("Volt").GetInt32());
        Assert.True(root.GetProperty("Cells")[0].GetProperty("Ok").GetBoolean());
        Assert.Equal(7, root.GetProperty("UDT1").GetProperty("B").GetInt32());
        Assert.Equal(2, root.GetProperty("Bin").GetArrayLength());
        Assert.Equal(7, root.GetProperty("Bin")[1].GetProperty("Ok").GetInt32());
    }

    [Fact]
    public void A_struct_alone_becomes_an_object_and_an_array_keeps_its_selected_slice()
    {
        var catalog = MesDataRangeCatalog.Build(Project());
        var (fields, structs) = MesFieldMappingGenerator.Generate(
        [
            new MesDataRange { SourcePath = "Line.Plc.DB10.UDT1", Scope = "Struct", Excludes = ["B"] },
            new MesDataRange { SourcePath = "Line.Plc.DB10.Bin", Scope = "Array", ArrayStart = [1], ArrayLengths = [1] },
            new MesDataRange { SourcePath = "Line.Plc.DB10.Temp", Scope = "Tag" },
        ], catalog, "Line.Plc.DB10");

        Assert.Equal("Line.Plc.DB10.Temp", Assert.Single(fields).TagId);
        Assert.Equal(["A"], structs[0].Members.Select(member => member.TagId).ToArray());
        Assert.Equal((1, 1), (structs[1].StartIndex, structs[1].FixedCount));
    }

    [Fact]
    public void Indexed_groups_inside_a_container_group_become_an_enum_array_like_the_classic_glue_data()
    {
        static TagDef Tag(string name) => new() { Name = name, Address = "MEM", DataType = IgwDataType.Int32 };
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "PLC6", Devices = [new DeviceDef { Name = "PLC6", Groups = [new TagGroupDef
            {
                Name = "OP051", Tags = [Tag("Result")],
                SubGroups =
                [
                    new TagGroupDef { Name = "ShoulderGlue", SubGroups = [.. Enumerable.Range(1, 3).Select(i => new TagGroupDef
                        { Name = $"GlueData_{i}", Tags = [Tag("Result"), Tag("Position")] })] },
                    new TagGroupDef { Name = "Station", Tags = [Tag("A")] },
                ],
            }] }] }],
        };
        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = "PLC6.PLC6.OP051", Scope = "Group" }], MesDataRangeCatalog.Build(project), "PLC6.PLC6.OP051");

        Assert.Equal("PLC6.PLC6.OP051.Result", Assert.Single(fields).TagId);
        var glue = structs.Single(item => item.Kind == "Array");
        Assert.Equal(("Enum", "PLC6.PLC6.OP051.ShoulderGlue.GlueData", "GlueData", 1, 3),
            (glue.CountMode, glue.BaseGroup, glue.AliasName, glue.StartIndex, glue.FixedCount));
        Assert.Equal(["Result", "Position"], glue.Members.Select(member => member.TagId).ToArray());
        Assert.Equal("PLC6.PLC6.OP051.Station", structs.Single(item => item.Kind == "Object").BaseGroup);
    }

    [Theory]
    [InlineData("{}", "count", "temperature", "passed", "text", "missing")]
    [InlineData("{\"fieldKeyMode\":\"Name\"}", "count", "temperature", "passed", "text", "missing")]
    [InlineData("{\"fieldKeyMode\":\"\"}", "count", "temperature", "passed", "text", "missing")]
    [InlineData("{\"fieldKeyMode\":null}", "count", "temperature", "passed", "text", "missing")]
    [InlineData("{\"fieldKeyMode\":\"ItemId\"}", "count", "temperature", "passed", "text", "missing")]
    public async Task Field_key_mode_changes_only_keys_preserving_types_scaling_and_quality(
        string settings, string countKey, string tempKey, string boolKey, string textKey, string missingKey)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>(settings, ProjectStore.Options)!;
        payload.Mode = MesPayloadModes.FieldMapping;
        payload.Fields =
        [
            new() { TagId = "Line.Plc.DB10.Counter", FieldName = "count" },
            new() { TagId = "Line.Plc.DB10.Temp", FieldName = "temperature", Scale = 1.25 },
            new() { TagId = "Line.Plc.DB10.Passed", FieldName = "passed" },
            new() { TagId = "Line.Plc.DB10.Text", FieldName = "text" },
            new() { TagId = "Line.Plc.DB10.Missing", FieldName = "missing" },
        ];
        payload.Number = new() { Decimals = 2, Rounding = MesNumberRounding.Truncate };
        payload.Append["station"] = "S1";
        payload.FixedValues = [new() { Name = "quantity", DataType = MesFixedValueTypes.Int, Value = "3" }];

        var root = await UploadAsync(payload, hub =>
        {
            hub.Register("Line.Plc.DB10.Passed", new() { DataType = IgwDataType.Bool });
            hub.Update("Line.Plc.DB10.Passed", DataValue.GoodAt(true, DateTime.UtcNow));
            hub.Register("Line.Plc.DB10.Text", new() { DataType = IgwDataType.String });
            hub.Update("Line.Plc.DB10.Text", DataValue.GoodAt("00123", DateTime.UtcNow));
            hub.Register("Line.Plc.DB10.Missing", new() { DataType = IgwDataType.Int32 });
            hub.Update("Line.Plc.DB10.Missing", DataValue.BadAt(StatusCode.Bad, DateTime.UtcNow));
        });

        bool itemId = false; // Legacy global setting is ignored; uncoded fields keep names.
        Assert.Equal(itemId ? ["parameterDic", "station", "quantity"] : new[] { countKey, tempKey, boolKey, textKey, missingKey, "station", "quantity" },
            root.EnumerateObject().Select(p => p.Name));
        var ordinary = itemId ? root.GetProperty("parameterDic") : root;
        if (itemId) Assert.Equal([countKey, tempKey, boolKey, textKey, missingKey], ordinary.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, ordinary.GetProperty(countKey).GetInt32());
        Assert.Equal(1.87m, ordinary.GetProperty(tempKey).GetDecimal());
        Assert.True(ordinary.GetProperty(boolKey).GetBoolean());
        Assert.Equal("00123", ordinary.GetProperty(textKey).GetString());
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty(missingKey).ValueKind);
        Assert.Equal("S1", root.GetProperty("station").GetString());
        Assert.Equal(3, root.GetProperty("quantity").GetInt32());
        Assert.False(root.TryGetProperty("Line", out _)); // 完整 ID 的点号不是 JSON 层级
    }

    [Fact]
    public async Task ItemId_preserves_object_aliases_nested_members_and_lists()
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","structs":[
              {"kind":"Object","baseGroup":"Line.Plc.DB10","aliasName":"block","members":[
                {"tagId":"Counter","fieldName":"count"},{"tagId":"UDT1.A"}],
               "structs":[{"kind":"List","baseGroup":"UDT1","aliasName":"values","members":[{"tagId":"A"},{"tagId":"B"}]}]}
            ]}
            """, ProjectStore.Options)!;

        var root = await UploadAsync(payload);

        Assert.Equal(["block"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, root.GetProperty("block").GetProperty("count").GetInt32());
        Assert.Equal(7, root.GetProperty("block").GetProperty("UDT1").GetProperty("A").GetInt32());
        Assert.Equal([7, 7], root.GetProperty("block").GetProperty("values").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public async Task ItemId_does_not_change_DataRanges_mode()
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"DataRanges","fieldKeyMode":"ItemId","dataRanges":[{"sourcePath":"Line.Plc.DB10.Counter","scope":"Tag"}],
             "fields":[{"tagId":"Line.Plc.DB10.Temp","fieldName":"ignored"}]}
            """, ProjectStore.Options)!;

        var root = await UploadAsync(payload);

        Assert.Equal(["Plc"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, root.GetProperty("Plc").GetProperty("DB10").GetProperty("Counter").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemId_keeps_custom_template_priority(bool typedTemplate)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","fields":[{"tagId":"Line.Plc.DB10.Counter","fieldName":"count"}]}
            """, ProjectStore.Options)!;
        var root = await UploadAsync(payload, http: new MesHttp
        {
            BodyTemplate = "{\"custom\":{count}}",
            JsonTemplate = typedTemplate ? "{\"typed\":true}" : "",
        });

        if (typedTemplate)
        {
            Assert.Equal(["typed"], root.EnumerateObject().Select(p => p.Name));
            Assert.True(root.GetProperty("typed").GetBoolean());
        }
        else
        {
            Assert.Equal(["custom"], root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(7, root.GetProperty("custom").GetInt32());
        }
    }

    [Fact]
    public async Task ItemId_survives_project_save_and_reopen_for_task_and_upload_node()
    {
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        var payload = JsonSerializer.Deserialize<MesPayload>("""
            {"mode":"FieldMapping","fieldKeyMode":"ItemId","fields":[{"tagId":"Line.Plc.DB10.Counter","fieldName":"oldAlias"}]}
            """, ProjectStore.Options)!;
        project.MesUpload.Tasks = [new() { Id = "t", Name = "任务", GroupRef = "Line.Plc.DB10", Payload = payload,
            Flow = new() { Nodes = [new() { Id = "u", Type = "Upload", Payload = payload }] } }];
        string directory = Path.Combine(Path.GetTempPath(), "igw-mes-key-mode-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "project.igwproj");
        try
        {
            ProjectStore.Save(project, file);
            var restored = ProjectStore.Load(file).MesUpload.Tasks.Single();
            foreach (var saved in new[] { restored.Payload, restored.Flow!.Nodes.Single().Payload })
            {
                var root = await UploadAsync(saved);
                Assert.Equal(["oldAlias"], root.EnumerateObject().Select(p => p.Name));
                Assert.Equal(7, root.GetProperty("oldAlias").GetInt32());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    public async Task Invalid_field_key_mode_is_rejected_before_http(string value)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("{\"fieldKeyMode\":" + value + "}", ProjectStore.Options)!;
        await using var hub = new DataHub();
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, httpMessageHandler: handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => engine.TestRunAsync(new MesTask
        {
            Payload = payload, Steps = [new() { Http = new() { Url = "http://localhost/mes" } }],
        }, default));

        Assert.Contains("FieldKeyMode", error.Message);
        Assert.Equal("", handler.Body);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("node")]
    [InlineData("template")]
    public void Invalid_field_key_mode_is_rejected_by_project_validation(string location)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>("{\"fieldKeyMode\":\"Typo\"}", ProjectStore.Options)!;
        var project = new IgwProject();
        var task = new MesTask { Id = "t", Name = "任务" };
        if (location == "node") task.Flow = new() { Nodes = [new() { Id = "u", Type = "Upload", Payload = payload }] };
        else task.Payload = payload;
        if (location == "template") project.MesUpload.FlowTemplates = [new() { Id = "ft", Name = "模板", Task = task }];
        else project.MesUpload.Tasks = [task];

        Assert.Contains(ProjectStore.Check(project).Errors, error => error.Contains("FieldKeyMode"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ItemId_duplicate_plain_keys_cannot_be_saved(bool uploadNode, bool caseVariant)
    {
        var payload = DuplicateCounterFields("{\"fieldKeyMode\":\"ItemId\"}");
        foreach (var field in payload.Fields) field.ParameterCode = field.TagId;
        if (caseVariant) payload.Fields[1].ParameterCode = "line.plc.db10.counter";
        payload.Fields[1].TagId = "Line.Plc.DB10.Temp";
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        var task = new MesTask { Id = "t", Name = "任务", GroupRef = "Line.Plc.DB10" };
        if (uploadNode) task.Flow = new() { Nodes = [new() { Id = "u", Type = "Upload", Payload = payload }] };
        else task.Payload = payload;
        project.MesUpload.Tasks = [task];
        string directory = Path.Combine(Path.GetTempPath(), "igw-mes-duplicate-key-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "project.igwproj");
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => ProjectStore.Save(project, file));
            Assert.Contains("Line.Plc.DB10.Counter", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("重复", error.Message);
            Assert.False(File.Exists(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task ItemId_duplicate_plain_keys_are_rejected_before_http(double secondScale, bool caseVariant)
    {
        var payload = DuplicateCounterFields("{\"fieldKeyMode\":\"ItemId\"}");
        foreach (var field in payload.Fields) field.ParameterCode = field.TagId;
        if (caseVariant) payload.Fields[1].ParameterCode = "line.plc.db10.counter";
        payload.Fields[1].TagId = "Line.Plc.DB10.Temp";
        payload.Fields[1].Scale = secondScale;
        await using var hub = new DataHub();
        FillHub(hub, Project());
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, httpMessageHandler: handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => engine.TestRunAsync(new MesTask
        {
            Payload = payload, Steps = [new() { Http = new() { Url = "http://localhost/mes" } }],
        }, default));

        Assert.Contains("Line.Plc.DB10.Counter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("重复", error.Message);
        Assert.Equal("", handler.Body);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fieldKeyMode\":\"Name\"}")]
    [InlineData("{\"fieldKeyMode\":\"\"}")]
    [InlineData("{\"fieldKeyMode\":null}")]
    public async Task Name_mode_keeps_same_tag_with_different_aliases_and_scales(string settings)
    {
        var payload = DuplicateCounterFields(settings);
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        project.MesUpload.Tasks = [new() { Id = "t", Name = "任务", Payload = payload }];
        Assert.Empty(ProjectStore.Check(project).Errors);

        var root = await UploadAsync(payload);

        Assert.Equal(["original", "scaled"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, root.GetProperty("original").GetInt32());
        Assert.Equal(14, root.GetProperty("scaled").GetInt32());
    }

    [Fact]
    public async Task Name_mode_keeps_case_variant_tag_ids_with_distinct_aliases_and_scales()
    {
        var payload = DuplicateCounterFields("{\"fieldKeyMode\":\"Name\"}");
        payload.Fields[1].TagId = "line.plc.db10.counter";
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        project.MesUpload.Tasks = [new() { Id = "t", Name = "任务", Payload = payload }];
        Assert.Empty(ProjectStore.Check(project).Errors);

        var root = await UploadAsync(payload, hub =>
        {
            hub.Register("line.plc.db10.counter", new() { DataType = IgwDataType.Int32 });
            hub.Update("line.plc.db10.counter", DataValue.GoodAt(9, DateTime.UtcNow));
        });

        Assert.Equal(7, root.GetProperty("original").GetInt32());
        Assert.Equal(18, root.GetProperty("scaled").GetInt32());
    }

    [Theory]
    [InlineData("Counter", "Line.Plc.DB10", "Object", "Counter")]
    [InlineData("Cells", "Line.Plc.DB10.Cells", "Array", "Volt")]
    public async Task ItemId_duplicate_fields_covered_by_structure_do_not_block_upload(
        string tagName, string baseGroup, string kind, string member)
    {
        var payload = DuplicateCounterFields("{\"fieldKeyMode\":\"ItemId\"}");
        foreach (var field in payload.Fields) field.TagId = "Line.Plc.DB10." + tagName;
        payload.Structs = [new() { AliasName = "structure", BaseGroup = baseGroup, Kind = kind,
            FixedCount = 2, Members = [new() { TagId = member, FieldName = "value" }] }];
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        project.MesUpload.Tasks = [new() { Id = "t", Name = "任务", Payload = payload }];
        Assert.Empty(ProjectStore.Check(project).Errors);

        var root = await UploadAsync(payload);

        Assert.Equal(["structure"], root.EnumerateObject().Select(p => p.Name));
        var structure = root.GetProperty("structure");
        if (kind == "Array")
        {
            Assert.Equal(2, structure.GetArrayLength());
            Assert.Equal(11, structure[0].GetProperty("value").GetInt32());
        }
        else Assert.Equal(7, structure.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ItemId_DataRanges_ignores_duplicate_inactive_field_mappings()
    {
        var payload = DuplicateCounterFields("{\"fieldKeyMode\":\"ItemId\"}");
        payload.Mode = MesPayloadModes.DataRanges;
        payload.DataRanges = [new() { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag" }];

        var root = await UploadAsync(payload);

        Assert.Equal(["Plc"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, root.GetProperty("Plc").GetProperty("DB10").GetProperty("Counter").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("FieldMapping")]
    public async Task Legacy_ItemId_without_routed_points_does_not_emit_empty_dictionary(string mode)
    {
        var root = await UploadAsync(new MesPayload { Mode = mode, FieldKeyMode = "ItemId" });

        Assert.Empty(root.EnumerateObject());
    }

    [Fact]
    public async Task ItemId_dictionary_preserves_arrays_number_policy_and_missing_values()
    {
        var payload = new MesPayload
        {
            FieldKeyMode = "ItemId",
            Fields =
            [
                new() { TagId = "Line.Plc.DB10.Values" },
                new() { TagId = "Line.Plc.DB10.Cells" },
                new() { TagId = "Line.Plc.DB10.Unknown" },
                new() { TagId = "Line.Plc.DB10.parameterDic", FieldName = "parameterDic" },
            ],
            Number = new() { Decimals = 2, Rounding = MesNumberRounding.Truncate },
            ExtraFields = [new() { Name = "Product.Code", Value = "A" }],
        };
        foreach (var field in payload.Fields) field.ParameterCode = field.TagId;
        var root = await UploadAsync(payload, hub =>
        {
            var array = new ArrayDef { ElementType = IgwDataType.Double, Dimensions = [4], LowerBounds = [1] };
            hub.Register("Line.Plc.DB10.Values", new() { DataType = IgwDataType.Double, IsArray = true, Array = array });
            hub.Update("Line.Plc.DB10.Values", DataValue.GoodAt(
                PrimitiveArrayValue<double>.Create(array.Shape, [1.239, double.PositiveInfinity, double.NegativeInfinity, double.NaN]), DateTime.UtcNow));
            hub.Register("Line.Plc.DB10.parameterDic", new() { DataType = IgwDataType.Int32 });
            hub.Update("Line.Plc.DB10.parameterDic", DataValue.GoodAt(42, DateTime.UtcNow));
        });

        Assert.Equal(["parameterDic", "Product"], root.EnumerateObject().Select(p => p.Name));
        var ordinary = root.GetProperty("parameterDic");
        Assert.Equal(["Line.Plc.DB10.Values", "Line.Plc.DB10.Cells", "Line.Plc.DB10.Unknown", "Line.Plc.DB10.parameterDic"],
            ordinary.EnumerateObject().Select(p => p.Name));
        var values = ordinary.GetProperty("Line.Plc.DB10.Values");
        Assert.Equal(1.23m, values[0].GetDecimal());
        Assert.Equal(99999999, values[1].GetInt32());
        Assert.Equal(-99999999, values[2].GetInt32());
        Assert.Equal(JsonValueKind.Null, values[3].ValueKind);
        Assert.Equal(12, ordinary.GetProperty("Line.Plc.DB10.Cells")[1].GetProperty("Volt").GetInt32());
        Assert.True(ordinary.GetProperty("Line.Plc.DB10.Cells")[0].GetProperty("Ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("Line.Plc.DB10.Unknown").ValueKind);
        Assert.Equal(42, ordinary.GetProperty("Line.Plc.DB10.parameterDic").GetInt32());
        Assert.Equal("A", root.GetProperty("Product").GetProperty("Code").GetString());
    }

    [Theory]
    [InlineData("Object", "parameterDic")]
    [InlineData("Object", " parameterDic ")]
    [InlineData("Array", "ParameterDic")]
    [InlineData("List", "PARAMETERDIC")]
    [InlineData("Append", "parameterDic")]
    [InlineData("Append", " parameterDic ")]
    [InlineData("Append", "PARAMETERDIC")]
    [InlineData("FixedValues", " parameterDic ")]
    [InlineData("FixedValues", "ParameterDic")]
    [InlineData("ExtraFields", "parameterDic")]
    [InlineData("ExtraFields", "PARAMETERDIC.child")]
    public void ItemId_reserved_container_conflicts_are_rejected_by_save(string source, string name)
    {
        var payload = ReservedContainerPayload(source, name);
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        string directory = Path.Combine(Path.GetTempPath(), "igw-mes-reserved-key-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "project.igwproj");
        try
        {
            foreach (string location in new[] { "task", "node", "template" })
            {
                var task = new MesTask { Id = "t", Name = "任务", GroupRef = "Line.Plc.DB10" };
                if (location == "task") task.Payload = payload;
                else task.Flow = new() { Nodes = [new() { Id = "u", Type = "Upload", Payload = payload }] };
                project.MesUpload.Tasks = location == "template" ? [] : [task];
                project.MesUpload.FlowTemplates = location == "template" ? [new() { Id = "ft", Name = "模板", Task = task }] : [];
                var error = Assert.Throws<InvalidDataException>(() => ProjectStore.Save(project, file));
                Assert.Contains("parameterDic", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(file));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Object", "parameterDic")]
    [InlineData("Object", " parameterDic ")]
    [InlineData("Array", "ParameterDic")]
    [InlineData("List", "PARAMETERDIC")]
    [InlineData("Append", "parameterDic")]
    [InlineData("Append", " parameterDic ")]
    [InlineData("Append", "PARAMETERDIC")]
    [InlineData("FixedValues", " parameterDic ")]
    [InlineData("FixedValues", "ParameterDic")]
    [InlineData("ExtraFields", "parameterDic")]
    [InlineData("ExtraFields", "PARAMETERDIC.child")]
    public async Task ItemId_reserved_container_conflicts_are_rejected_by_actual_build(string source, string name)
    {
        var payload = ReservedContainerPayload(source, name);
        await using var hub = new DataHub();
        FillHub(hub, Project());
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, httpMessageHandler: handler);
        var buildError = await Assert.ThrowsAsync<InvalidDataException>(() => engine.TestRunAsync(new MesTask
        {
            Payload = payload, Steps = [new() { Http = new() { Url = "http://localhost/mes" } }],
        }, default));
        Assert.Contains("parameterDic", buildError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", handler.Body);
    }

    [Theory]
    [InlineData("Object")]
    [InlineData("Array")]
    [InlineData("List")]
    [InlineData("Append")]
    [InlineData("FixedValues")]
    public async Task ItemId_reserved_name_is_not_a_prefix_for_literal_outer_keys(string source)
    {
        var payload = ReservedContainerPayload(source, "parameterDic.literal");
        var project = Project();
        project.Channels[0].DriverId = "simulator";
        project.MesUpload.Tasks = [new() { Id = "t", Name = "任务", Payload = payload }];
        Assert.Empty(ProjectStore.Check(project).Errors);

        var root = await UploadAsync(payload);

        Assert.Equal(["parameterDic", "parameterDic.literal"], root.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(7, root.GetProperty("parameterDic").GetProperty("COUNTER").GetInt32());
    }

    [Theory]
    [InlineData("Name", "FieldMapping")]
    [InlineData("ItemId", "DataRanges")]
    public async Task Reserved_container_name_does_not_affect_other_modes(string fieldKeyMode, string mode)
    {
        var payload = new MesPayload { Mode = mode, FieldKeyMode = fieldKeyMode, Append = new() { ["parameterDic"] = "legacy" } };
        if (mode == "DataRanges") payload.DataRanges = [new() { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag" }];
        var root = await UploadAsync(payload);

        Assert.Equal("legacy", root.GetProperty("parameterDic").GetString());
    }

    private static MesPayload ReservedContainerPayload(string source, string name)
    {
        var payload = new MesPayload { Mode = "FieldMapping", FieldKeyMode = "ItemId", Fields = [new() { TagId = "Line.Plc.DB10.Counter", ParameterCode = "COUNTER" }] };
        switch (source)
        {
            case "Append": payload.Append[name] = "bad"; break;
            case "FixedValues": payload.FixedValues = [new() { Name = name, Value = "bad" }]; break;
            case "ExtraFields": payload.ExtraFields = [new() { Name = name, Value = "bad" }]; break;
            default: payload.Structs = [new() { Kind = source, AliasName = name, BaseGroup = "Line.Plc.DB10.UDT1", Members = [new() { TagId = "A" }] }]; break;
        }
        return payload;
    }

    private static MesPayload DuplicateCounterFields(string settings)
    {
        var payload = JsonSerializer.Deserialize<MesPayload>(settings, ProjectStore.Options)!;
        payload.Mode = MesPayloadModes.FieldMapping;
        payload.Fields =
        [
            new() { TagId = "Line.Plc.DB10.Counter", FieldName = "original" },
            new() { TagId = "Line.Plc.DB10.Counter", FieldName = "scaled", Scale = 2 },
        ];
        return payload;
    }

    private static async Task<JsonElement> UploadAsync(MesPayload payload, Action<DataHub>? setup = null, MesHttp? http = null)
    {
        var project = Project();
        await using var hub = new DataHub();
        FillHub(hub, project);
        setup?.Invoke(hub);
        var handler = new CapturingHandler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, httpMessageHandler: handler,
            catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected);
        http ??= new();
        http.Url = "http://localhost/mes";
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new() { Http = http }] }, default);
        using var json = JsonDocument.Parse(handler.Body);
        return json.RootElement.Clone();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}") };
        }
    }

    [Fact]
    public void Single_tag_ranges_pass_validation_and_cannot_carry_excludes()
    {
        List<string> Check(MesDataRange range)
        {
            var project = Project();
            project.MesUpload.Tasks = [new MesTask { Id = "t", Name = "任务", GroupRef = "Line.Plc.DB10",
                Payload = new MesPayload { Mode = MesPayloadModes.DataRanges, DataRanges = [range] } }];
            var errors = new List<string>();
            MesDataRangeValidator.Check(project, errors);
            return errors;
        }

        Assert.Empty(Check(new MesDataRange { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag" }));
        Assert.Contains(Check(new MesDataRange { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag", Excludes = ["x"] }),
            error => error.Contains("不能再排除字段"));
    }

    [Fact]
    public void Saving_or_loading_the_project_keeps_single_tag_ranges_and_their_renames()
    {
        var project = Project();
        var upload = new MesNode
        {
            Id = "upload", Type = "Upload",
            Payload = new MesPayload
            {
                Mode = MesPayloadModes.DataRanges,
                DataRanges =
                [
                    new MesDataRange { SourcePath = "Line.Plc.DB10.Counter", Scope = "Tag" },
                    new MesDataRange { SourcePath = "Line.Plc.DB10.UDT1", Scope = "Struct", Names = new() { ["A"] = "alpha" } },
                ],
            },
        };
        project.MesUpload.Tasks = [new MesTask { Id = "t", Name = "任务", GroupRef = "Line.Plc.DB10", Flow = new MesFlow { Nodes = [upload] } }];

        var result = MesTaskTopology.Reconcile(project);
        Assert.Equal(0, result.ClearedRanges);
        var restored = ProjectStore.Clone(project).MesUpload.Tasks[0].Flow!.Nodes[0].Payload.DataRanges;
        Assert.Equal(["Line.Plc.DB10.Counter", "Line.Plc.DB10.UDT1"], restored.Select(range => range.SourcePath).ToArray());
        Assert.Equal("alpha", restored[1].Names!["A"]);

        upload.Payload.DataRanges.Add(new MesDataRange { SourcePath = "Line.Plc.DB10.Gone", Scope = "Tag" });
        Assert.Equal(3, MesTaskTopology.Reconcile(project).ClearedRanges);   // 来源真的不存在时照旧清掉
    }

    [Fact]
    public void The_tree_lists_single_tags_structs_and_recognized_arrays_level_by_level()
    {
        var catalog = MesDataRangeCatalog.Build(Project());

        var root = MesRangeTreeApi.Children(catalog, "Line.Plc.DB10", null, null, null, null);
        Assert.Equal("Group", Assert.Single(root.Items).Scope);

        var children = MesRangeTreeApi.Children(catalog, "Line.Plc.DB10", "Line.Plc.DB10", null, null, null).Items;
        Assert.Equal(
            [("Counter", "Tag"), ("Temp", "Tag"), ("Cells", "Array"), ("UDT1", "Struct"), ("Bin", "Array")],
            children.Select(item => (item.Name, item.Scope)).ToArray());
        var cells = children.Single(item => item.Name == "Cells");
        Assert.Equal([2], cells.Dimensions);
        Assert.Equal(["Volt", "Ok"], cells.Members.Select(member => member.Path).ToArray());
        Assert.Equal([2], children.Single(item => item.Name == "Bin").Dimensions);

        var paged = MesRangeTreeApi.Children(catalog, "Line.Plc.DB10", "Line.Plc.DB10", null, null, 2);
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal("2", paged.NextCursor);
        Assert.Equal(5, paged.Total);

        var found = MesRangeTreeApi.Children(catalog, "Line.Plc.DB10", null, "a", null, null).Items;
        Assert.Contains(found, item => item.SourcePath == "Line.Plc.DB10.UDT1.A" && item.Scope == "Tag");
    }

    [Fact]
    public void The_tree_can_browse_other_devices_and_blocks_from_the_channel_level()
    {
        var project = Project();
        project.Channels.Add(new ChannelDef
        {
            Name = "Line2",
            Devices = [new DeviceDef { Name = "Plc2", Groups = [new TagGroupDef { Name = "DB20", Tags = [new TagDef { Name = "Code", Address = "DB20.DBW0" }] }] }],
        });

        var channels = MesRangeTreeApi.ProjectLevels(project, null, null, null).Items;
        Assert.Equal([("Line", "Channel", 9), ("Line2", "Channel", 1)], channels.Select(item => (item.SourcePath, item.Scope, item.TagCount)).ToArray());
        var devices = MesRangeTreeApi.ProjectLevels(project, "Line2", null, null).Items;
        Assert.Equal(("Line2.Plc2", "Device"), (Assert.Single(devices).SourcePath, devices[0].Scope));
        var blocks = MesRangeTreeApi.ProjectLevels(project, "Line2.Plc2", null, null).Items;
        Assert.Equal(("Line2.Plc2.DB20", "Group", 1, true), (Assert.Single(blocks).SourcePath, blocks[0].Scope, blocks[0].TagCount, blocks[0].HasChildren));
        Assert.Empty(MesRangeTreeApi.ProjectLevels(project, "Nope", null, null).Items);
        Assert.Throws<ArgumentException>(() => MesRangeTreeApi.ProjectLevels(project, "Line.Plc.DB10", null, null));

        // 数据块里面按它自己的数据块取子项，和任务绑定的是哪个 DB 无关
        var inner = MesRangeTreeApi.Children(MesDataRangeCatalog.Build(project), "Line2.Plc2.DB20", "Line2.Plc2.DB20", null, null, null).Items;
        Assert.Equal(("Line2.Plc2.DB20.Code", "Tag"), (Assert.Single(inner).SourcePath, inner[0].Scope));
    }

    [Fact]
    public void Field_mapping_takes_tags_and_structs_from_another_block()
    {
        var project = Project();
        project.Channels[0].Devices[0].Groups.Add(new TagGroupDef
        {
            Name = "DB20",
            Tags = [new TagDef { Name = "Code", Address = "DB20.DBW0", DataType = IgwDataType.Int16 }],
            SubGroups = [new TagGroupDef { Name = "Udt", Tags = [new TagDef { Name = "X", Address = "DB20.DBW2", DataType = IgwDataType.Int16 }] }],
        });
        var catalog = MesDataRangeCatalog.Build(project);

        var (fields, structs) = MesFieldMappingGenerator.Generate(
            [new MesDataRange { SourcePath = "Line.Plc.DB20.Code", Scope = "Tag" }, new MesDataRange { SourcePath = "Line.Plc.DB20.Udt", Scope = "Struct" }],
            catalog, "Line.Plc.DB10");

        Assert.Equal(["Line.Plc.DB20.Code"], fields.Select(field => field.TagId).ToArray());
        var udt = Assert.Single(structs);
        Assert.Equal(("Object", "Line.Plc.DB20.Udt"), (udt.Kind, udt.BaseGroup));
    }

    [Fact]
    public void Preview_describes_the_structure_by_original_names_and_reports_rename_conflicts()
    {
        var catalog = MesDataRangeCatalog.Build(Project());
        var preview = MesRangeTreeApi.Preview(_ => catalog,
            [new MesDataRange { SourcePath = "Line.Plc.DB10", Excludes = ["Temp"], Names = new() { ["Counter"] = "count" } }]);

        Assert.Null(preview.Error);
        var range = Assert.Single(preview.Ranges);
        Assert.Equal("Plc.DB10", range.OutputPath);
        var nodes = range.Root.Children.ToDictionary(node => node.Key);
        Assert.Equal("value", nodes["Counter"].Kind);
        Assert.True(nodes["Counter"].Excludable);
        Assert.True(nodes.ContainsKey("Temp"), "被排除的项也要列出来，才能重新勾选");
        Assert.Equal("object", nodes["UDT1"].Kind);
        Assert.Equal(["UDT1.A", "UDT1.B"], nodes["UDT1"].Children.Select(node => node.Key).ToArray());
        Assert.Equal("array", nodes["Bin"].Kind);
        Assert.Equal(2, nodes["Bin"].Length);
        Assert.Equal(["Bin[].Code", "Bin[].Ok"], nodes["Bin"].Children.Select(node => node.Key).ToArray());
        Assert.All(nodes["Bin"].Children, node => Assert.True(node.Omittable && !node.Excludable));
        Assert.All(nodes["Cells"].Children, node => Assert.True(node.Omittable));
        Assert.False(nodes["Counter"].Omittable);
        Assert.Equal(["Cells[].Volt", "Cells[].Ok"], nodes["Cells"].Children.Select(node => node.Key).ToArray());

        var conflict = MesRangeTreeApi.Preview(_ => catalog,
            [new MesDataRange { SourcePath = "Line.Plc.DB10", Names = new() { ["Counter"] = "Temp" } }]);
        Assert.Contains("重复字段", conflict.Error);
    }
}
