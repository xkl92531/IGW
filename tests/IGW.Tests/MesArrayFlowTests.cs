using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesArrayFlowTests
{
    [Fact]
    public async Task Evidence_recognized_sparse_array_uploads_and_writes_back_real_leaf_ids()
    {
        var sparse = new TagGroupDef
        {
            Name = "AnyBusinessName",
            SubGroups = new[] { (Name: "first", Index: 1), (Name: "third", Index: 3), (Name: "ninth", Index: 9) }
                .Select(item => new TagGroupDef
                {
                    Name = item.Name,
                    Tags =
                    [
                        new TagDef
                        {
                            Name = "Value",
                            Address = $"ns=3;s=\"DB\".\"SparseValues\"[{item.Index}].\"Value\"",
                            DataType = IgwDataType.Int16,
                            Access = TagAccess.ReadWrite,
                        },
                    ],
                }).ToList(),
        };
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
                            Groups = [new TagGroupDef { Name = "OP200Data", SubGroups = [sparse] }],
                        },
                    ],
                },
            ],
        };
        var catalog = MesDataRangeCatalog.Build(project);
        Assert.True(catalog.TryGetLogicalArray("Line.Plc.OP200Data.AnyBusinessName", out var logical));
        Assert.Equal(LogicalArrayShapeKind.Sparse, logical.Descriptor.ShapeKind);
        Assert.Equal([1, 3, 9], logical.Descriptor.ExistingIndices.Select(index => index[0]));

        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = "Line.Plc.OP200Data.AnyBusinessName",
                    OutputPath = "values",
                },
            ],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, catalog);
        await using var hub = new DataHub();
        var leaves = project.Channels[0].Devices[0].Groups.SelectMany(FlattenTags).ToArray();
        var valuesByGroup = new Dictionary<string, short>
        {
            ["first"] = 101,
            ["third"] = 103,
            ["ninth"] = 109,
        };
        foreach (var leaf in leaves)
        {
            hub.Register(leaf.TagId, leaf.Definition);
            string group = leaf.TagId.Split('.')[^2];
            hub.Update(leaf.TagId, DataValue.GoodAt(valuesByGroup[group], DateTime.UtcNow));
        }

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);
        using var request = JsonDocument.Parse(body.Json);
        var values = request.RootElement.GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal(101, values[0].GetProperty("Value").GetInt16());
        Assert.Equal(103, values[1].GetProperty("Value").GetInt16());
        Assert.Equal(109, values[2].GetProperty("Value").GetInt16());

        using var response = JsonDocument.Parse("""
            {"values":[{"Value":201},{"Value":203},{"Value":209}]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            leaves.Select(leaf => leaf.TagId), compiled);
        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.AnyBusinessName.third.Value" && write.Value == "203");
    }

    [Fact]
    public async Task Evidence_recognized_multidimensional_array_preserves_shape_and_real_write_paths()
    {
        var tuples = new[] { (Group: "north", Row: 1, Column: 2), (Group: "east", Row: 1, Column: 3),
            (Group: "south", Row: 2, Column: 2), (Group: "west", Row: 2, Column: 3) };
        var matrix = new TagGroupDef
        {
            Name = "AnyMatrix",
            SubGroups = tuples.Select(item => new TagGroupDef
            {
                Name = item.Group,
                Tags =
                [
                    new TagDef
                    {
                        Name = "Value",
                        Address = $"ns=3;s=\"DB\".\"Matrix\"[{item.Row},{item.Column}].\"Value\"",
                        DataType = IgwDataType.Int16,
                        Access = TagAccess.ReadWrite,
                    },
                ],
            }).ToList(),
        };
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
                            Groups = [new TagGroupDef { Name = "OP200Data", SubGroups = [matrix] }],
                        },
                    ],
                },
            ],
        };
        var catalog = MesDataRangeCatalog.Build(project);
        Assert.True(catalog.TryGetLogicalArray("Line.Plc.OP200Data.AnyMatrix", out var logical));
        Assert.Equal([2, 2], logical.Descriptor.Dimensions);
        Assert.Equal([1, 2], logical.Descriptor.LowerBounds);

        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges = [new MesDataRange { SourcePath = logical.SourcePath, OutputPath = "matrix" }],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, catalog);
        await using var hub = new DataHub();
        foreach (var leaf in project.Channels[0].Devices[0].Groups.SelectMany(FlattenTags))
        {
            hub.Register(leaf.TagId, leaf.Definition);
            var tuple = tuples.Single(item => item.Group == leaf.TagId.Split('.')[^2]);
            hub.Update(leaf.TagId, DataValue.GoodAt((short)(tuple.Row * 10 + tuple.Column), DateTime.UtcNow));
        }

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);
        using var request = JsonDocument.Parse(body.Json);
        var values = request.RootElement.GetProperty("matrix");
        Assert.Equal(12, values[0][0].GetProperty("Value").GetInt16());
        Assert.Equal(23, values[1][1].GetProperty("Value").GetInt16());

        using var response = JsonDocument.Parse("""
            {"matrix":[[{"Value":112},{"Value":113}],[{"Value":122},{"Value":123}]]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            project.Channels[0].Devices[0].Groups.SelectMany(FlattenTagIds), compiled);
        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.AnyMatrix.west.Value" && write.Value == "123");
    }

    [Fact]
    public async Task Evidence_recognized_nested_arrays_preserve_recursive_json_and_writeback()
    {
        var outer = new TagGroupDef { Name = "BusinessRows" };
        for (int row = 0; row < 2; row++)
        {
            var rowGroup = new TagGroupDef { Name = row == 0 ? "alpha" : "omega" };
            for (int sample = 1; sample <= 2; sample++)
            {
                rowGroup.SubGroups.Add(new TagGroupDef
                {
                    Name = sample == 1 ? "low" : "high",
                    Tags =
                    [
                        new TagDef
                        {
                            Name = "Pressure",
                            Address = $"ns=3;s=\"DB\".\"Rows\"[{row}].\"Samples\"[{sample}].\"Pressure\"",
                            DataType = IgwDataType.Int16,
                            Access = TagAccess.ReadWrite,
                        },
                    ],
                });
            }
            outer.SubGroups.Add(rowGroup);
        }
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
                            Groups = [new TagGroupDef { Name = "OP200Data", SubGroups = [outer] }],
                        },
                    ],
                },
            ],
        };
        var catalog = MesDataRangeCatalog.Build(project);
        Assert.True(catalog.TryGetLogicalArray("Line.Plc.OP200Data.BusinessRows", out var logical));
        Assert.NotNull(logical.Descriptor.Members.Single().NestedArray);

        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges = [new MesDataRange { SourcePath = logical.SourcePath, OutputPath = "rows" }],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, catalog);
        await using var hub = new DataHub();
        short value = 1;
        foreach (var leaf in project.Channels[0].Devices[0].Groups.SelectMany(FlattenTags))
        {
            hub.Register(leaf.TagId, leaf.Definition);
            hub.Update(leaf.TagId, DataValue.GoodAt(value++, DateTime.UtcNow));
        }
        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);
        using var request = JsonDocument.Parse(body.Json);
        var rows = request.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(2, rows[0].GetProperty("Samples").GetArrayLength());

        using var response = JsonDocument.Parse("""
            {"rows":[{"Samples":[{"Pressure":11},{"Pressure":12}]},{"Samples":[{"Pressure":21},{"Pressure":22}]}]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            project.Channels[0].Devices[0].Groups.SelectMany(FlattenTagIds), compiled);
        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.BusinessRows.omega.high.Pressure" && write.Value == "22");
    }

    [Fact]
    public async Task Numeric_child_groups_are_one_struct_array_for_upload_slice_and_writeback()
    {
        var project = IndexedGroupProject("Bolts", ["0", "1", "2"]);
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = "Line.Plc.OP200Data.Bolts",
                    Scope = "Array",
                    OutputPath = "bolts",
                    ArrayStart = [1],
                    ArrayLengths = [2],
                },
            ],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(project));
        await using var hub = new DataHub();
        RegisterIndexedGroupValues(project, hub);

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);

        using var request = JsonDocument.Parse(body.Json);
        var rows = request.RootElement.GetProperty("bolts");
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(11.5, rows[0].GetProperty("Torque").GetDouble());
        Assert.Equal(2, rows[0].GetProperty("Result").GetInt16());

        using var response = JsonDocument.Parse("""
            {"bolts":[
              {"Torque":21.5,"Result":3},
              {"Torque":22.5,"Result":4}
            ]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            project.Channels[0].Devices[0].Groups
                .SelectMany(FlattenTagIds), compiled);

        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.Bolts.1.Torque" && write.Value == "21.5");
        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.Bolts.2.Result" && write.Value == "4");
    }

    [Fact]
    public async Task Nested_numeric_child_groups_preserve_nested_json_arrays()
    {
        var project = IndexedGroupProject("Bolts", ["0", "1"]);
        var bolts = project.Channels[0].Devices[0].Groups[0].SubGroups.Single();
        foreach (var element in bolts.SubGroups)
        {
            element.SubGroups.Add(new TagGroupDef
            {
                Name = "Samples",
                SubGroups = Enumerable.Range(0, 2).Select(index => new TagGroupDef
                {
                    Name = index.ToString(),
                    Tags = [new TagDef { Name = "Pressure", DataType = IgwDataType.Int16 }],
                }).ToList(),
            });
        }
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges = [new MesDataRange { SourcePath = "Line.Plc.OP200Data.Bolts", OutputPath = "bolts" }],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(project));
        await using var hub = new DataHub();
        RegisterIndexedGroupValues(project, hub);

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);

        using var request = JsonDocument.Parse(body.Json);
        var rows = request.RootElement.GetProperty("bolts");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(JsonValueKind.Array, rows[0].GetProperty("Samples").ValueKind);
        Assert.Equal(2, rows[0].GetProperty("Samples").GetArrayLength());
        Assert.True(rows[0].GetProperty("Samples")[0].TryGetProperty("Pressure", out _));

        using var response = JsonDocument.Parse("""
            {"bolts":[
              {"Samples":[{"Pressure":91},{"Pressure":92}]},
              {"Samples":[{"Pressure":93},{"Pressure":94}]}
            ]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            project.Channels[0].Devices[0].Groups.SelectMany(FlattenTagIds), compiled);
        Assert.Contains(writes, write =>
            write.TagId == "Line.Plc.OP200Data.Bolts.1.Samples.0.Pressure" && write.Value == "93");
    }

    [Theory]
    [InlineData("CellData[0]", "CellData[1]")]
    [InlineData("CellData_0", "CellData_1")]
    public async Task Existing_indexed_group_formats_remain_one_array(string first, string second)
    {
        var project = IndexedGroupProject("DB", [first, second], elementsDirectlyUnderRoot: true);
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = "Line.Plc.OP200Data",
                    Scope = "Group",
                    OutputPath = "payload",
                },
            ],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(project));
        await using var hub = new DataHub();
        RegisterIndexedGroupValues(project, hub);

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);

        using var request = JsonDocument.Parse(body.Json);
        var rows = request.RootElement.GetProperty("payload").GetProperty("CellData");
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(2, rows.GetArrayLength());
    }

    [Fact]
    public async Task Direct_struct_array_range_preserves_slice_shape_aliases_and_reverse_write_paths()
    {
        var definition = CellArray();
        var project = Project(definition);
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = "Line.Plc.DB.Cells",
                    OutputPath = "structures",
                    ArrayStart = [2],
                    ArrayLengths = [2],
                },
            ],
        };
        var compiled = MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(project));
        await using var hub = new DataHub();
        hub.Register("Line.Plc.DB.Cells", project.Channels[0].Devices[0].Groups[0].Tags[0]);
        hub.Update("Line.Plc.DB.Cells", DataValue.GoodAt(CellValues(), DateTime.UtcNow));

        var body = MesRangeJsonWriter.Build(compiled, new Dictionary<string, string>(), hub,
            _ => ConnectionState.Connected);

        using var request = JsonDocument.Parse(body.Json);
        var rows = request.RootElement.GetProperty("structures");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("B", rows[0].GetProperty("dmcCode").GetString());
        Assert.Equal(1, rows[0].GetProperty("isNg").GetInt16());

        using var response = JsonDocument.Parse("""
            {"structures":[
              {"dmcCode":"B2","isNg":2,"ngReason":"NG-2"},
              {"dmcCode":"C2","isNg":3,"ngReason":"NG-3"}
            ]}
            """);
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null,
            ["Line.Plc.DB.Cells"], compiled);

        Assert.Contains(writes, write => write.TagId == "Line.Plc.DB.Cells[2].ErrorMessage" && write.Value == "NG-2");
        Assert.Contains(writes, write => write.TagId == "Line.Plc.DB.Cells[3].IsNG" && write.Value == "3");
    }

    [Fact]
    public void Direct_array_range_rejects_out_of_bounds_slice()
    {
        var definition = CellArray();
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = "Line.Plc.DB.Cells",
                    OutputPath = "structures",
                    ArrayStart = [3],
                    ArrayLengths = [2],
                },
            ],
        };

        Assert.Throws<IndexOutOfRangeException>(() =>
            MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(Project(definition))));
    }

    [Fact]
    public async Task Array_condition_supports_any_all_slice_and_struct_member_alias()
    {
        await using var hub = new DataHub();
        var definition = CellArray();
        var tag = Project(definition).Channels[0].Devices[0].Groups[0].Tags[0];
        hub.Register("Line.Plc.DB.Cells", tag);
        hub.Update("Line.Plc.DB.Cells", DataValue.GoodAt(CellValues(), DateTime.UtcNow));
        var condition = new MesCondition
        {
            TagId = "Line.Plc.DB.Cells",
            ArrayStart = [2],
            ArrayLengths = [2],
            ArrayMemberPath = "isNg",
            ArrayQuantifier = "Any",
        };

        Assert.True(MesArrayConditionEvaluator.TrySelect(hub, condition, out var selected));
        Assert.True(MesArrayConditionEvaluator.Evaluate(selected, "Any", value => Convert.ToInt16(value) == 1));
        Assert.False(MesArrayConditionEvaluator.Evaluate(selected, "All", value => Convert.ToInt16(value) == 1));
        Assert.Equal(MesArrayConditionEvaluator.Fingerprint(selected), MesArrayConditionEvaluator.Fingerprint(selected.Slice(
            new ArraySlice(selected.Shape.LowerBounds, selected.Shape.Dimensions))));
    }

    [Fact]
    public void Project_validation_and_topology_keep_valid_direct_array_ranges()
    {
        var project = Project(CellArray());
        var range = new MesDataRange
        {
            SourcePath = "Line.Plc.DB.Cells", Scope = "Array", OutputPath = "structures",
            ArrayStart = [1], ArrayLengths = [2],
        };
        project.MesUpload.Tasks =
        [
            new MesTask
            {
                Id = "array-task", Name = "array-task", GroupRef = "Line.Plc.DB", DeviceRef = "Line.Plc",
                Payload = new MesPayload { Mode = MesPayloadModes.DataRanges, DataRanges = [range] },
            },
        ];

        var check = ProjectStore.Check(project);
        var topology = MesTaskTopology.Reconcile(project);

        Assert.True(check.Ok, string.Join(" | ", check.Errors));
        Assert.Equal(0, topology.ClearedRanges);
        Assert.Single(project.MesUpload.Tasks[0].Payload.DataRanges);
    }

    [Fact]
    public void Project_validation_and_topology_keep_numeric_group_array_ranges()
    {
        var project = IndexedGroupProject("Bolts", ["0", "1", "2"]);
        project.MesUpload.Tasks =
        [
            new MesTask
            {
                Id = "numeric-array", Name = "numeric-array", GroupRef = "Line.Plc.OP200Data",
                DeviceRef = "Line.Plc",
                Payload = new MesPayload
                {
                    Mode = MesPayloadModes.DataRanges,
                    DataRanges =
                    [
                        new MesDataRange
                        {
                            SourcePath = "Line.Plc.OP200Data.Bolts", Scope = "Array", OutputPath = "bolts",
                            ArrayStart = [1], ArrayLengths = [2], ArrayMemberPath = "Torque",
                        },
                    ],
                },
            },
        ];

        var check = ProjectStore.Check(project);
        var topology = MesTaskTopology.Reconcile(project);

        Assert.True(check.Ok, string.Join(" | ", check.Errors));
        Assert.Equal(0, topology.ClearedRanges);
        Assert.Single(project.MesUpload.Tasks[0].Payload.DataRanges);
    }

    [Fact]
    public async Task Response_writeback_confirms_virtual_array_member_from_parent_snapshot()
    {
        const string parentId = "Line.Plc.DB.Cells";
        var project = Project(CellArray());
        await using var hub = new DataHub();
        hub.Register(parentId, project.Channels[0].Devices[0].Groups[0].Tags[0]);
        hub.Update(parentId, DataValue.GoodAt(CellValues(), DateTime.UtcNow));
        var writes = new ConcurrentQueue<string>();
        var payload = new MesPayload
        {
            Mode = MesPayloadModes.DataRanges,
            DataRanges =
            [
                new MesDataRange
                {
                    SourcePath = parentId, Scope = "Array", OutputPath = "structures",
                    ArrayStart = [2], ArrayLengths = [1],
                },
            ],
        };
        var task = new MesTask
        {
            Id = "array-write", Name = "array-write", GroupRef = "Line.Plc.DB", DeviceRef = "Line.Plc",
            Enabled = true, CollectIntervalMs = 20, ConfirmRewrite = true,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode { Id = "trigger", Type = "Trigger" },
                    new MesNode
                    {
                        Id = "upload", Type = "Upload", Payload = payload,
                        Http = new MesHttp { Url = "http://localhost/mes" },
                    },
                    new MesNode
                    {
                        Id = "write", Type = "ResponseWriteback",
                        WriteBack = new MesWriteBack { Confirm = true, Retries = 0 },
                    },
                ],
                Edges =
                [
                    new MesEdge { From = "trigger", To = "upload" },
                    new MesEdge { From = "upload", To = "write" },
                ],
            },
        };
        var handler = new ResponseHandler("""
            {"httpStatusCode":200,"isSuccess":true,"backResult":{"structures":[{"ngReason":"updated"}]}}
            """);
        await using var engine = new MesUploadEngine(hub,
            new MesUploadSettings { Enabled = true, Tasks = [task] },
            (id, _, _) =>
            {
                writes.Enqueue(id);
                hub.Update(parentId, DataValue.GoodAt(CellValues("updated"), DateTime.UtcNow));
                return Task.FromResult(true);
            }, NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project),
            deviceState: _ => ConnectionState.Connected, httpMessageHandler: handler);

        await WaitUntilAsync(() => writes.Contains("Line.Plc.DB.Cells[2].ErrorMessage"), TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => engine.Snapshot().Single().Nodes.Any(node => node.Id == "write" && node.State == "ok"),
            TimeSpan.FromSeconds(2));
    }

    private static ArrayDef CellArray() => new()
    {
        ElementType = IgwDataType.Struct,
        Dimensions = [3],
        LowerBounds = [1],
        ElementStrideBytes = 56,
        Struct = new StructDef
        {
            Name = "CellData",
            SizeBytes = 56,
            Fields =
            [
                new StructFieldDef { Name = "DMCCode", JsonName = "dmcCode", DataType = IgwDataType.String, StringLength = 16, ByteOffset = 0 },
                new StructFieldDef { Name = "IsNG", JsonName = "isNg", DataType = IgwDataType.Int16, ByteOffset = 18 },
                new StructFieldDef { Name = "ErrorMessage", JsonName = "ngReason", DataType = IgwDataType.String, StringLength = 32, ByteOffset = 20 },
            ],
        },
    };

    private static StructArrayValue CellValues(string errorAtTwo = "old")
    {
        var definition = CellArray();
        var shape = definition.Shape;
        return StructArrayValue.Create(shape, definition.Struct!,
            new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["DMCCode"] = PrimitiveArrayValue<string>.Create(shape, ["A", "B", "C"], elementType: IgwDataType.String),
                ["IsNG"] = PrimitiveArrayValue<short>.Create(shape, [0, 1, 0]),
                ["ErrorMessage"] = PrimitiveArrayValue<string>.Create(shape, ["", errorAtTwo, ""], elementType: IgwDataType.String),
            });
    }

    private static IgwProject Project(ArrayDef definition) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Name = "DB",
                                Tags =
                                [
                                    new TagDef
                                    {
                                        Name = "Cells",
                                        Address = "DB1.DBB0",
                                        DataType = IgwDataType.Struct,
                                        Access = TagAccess.ReadWrite,
                                        Array = definition,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待 MES 数组流程测试超时。");
            await Task.Delay(10);
        }
    }

    private static IgwProject IndexedGroupProject(
        string containerName,
        IReadOnlyList<string> elementNames,
        bool elementsDirectlyUnderRoot = false)
    {
        TagGroupDef Element(string name, int index) => new()
        {
            Name = name,
            Tags =
            [
                new TagDef
                {
                    Name = "Torque", Address = $"ns=3;s=\"Data\".\"{containerName}\"[{index}].\"Torque\"",
                    DataType = IgwDataType.Double, Access = TagAccess.ReadWrite,
                },
                new TagDef
                {
                    Name = "Result", Address = $"ns=3;s=\"Data\".\"{containerName}\"[{index}].\"Result\"",
                    DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite,
                },
            ],
        };

        var elements = elementNames.Select((name, index) => Element(name, index)).ToList();
        var root = new TagGroupDef
        {
            Name = "OP200Data",
            Tags = [],
            SubGroups = elementsDirectlyUnderRoot
                ? elements
                : [new TagGroupDef { Name = containerName, Tags = [], SubGroups = elements }],
        };
        return new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "OpcUa.Client",
                    Devices = [new DeviceDef { Name = "Plc", Groups = [root] }],
                },
            ],
        };
    }

    private static void RegisterIndexedGroupValues(IgwProject project, DataHub hub)
    {
        foreach (var tag in project.Channels[0].Devices[0].Groups.SelectMany(FlattenTags))
        {
            hub.Register(tag.TagId, tag.Definition);
            object value = tag.Definition.Name == "Torque"
                ? 10.5 + tag.Index
                : (short)(1 + tag.Index);
            hub.Update(tag.TagId, DataValue.GoodAt(value, DateTime.UtcNow));
        }
    }

    private static IEnumerable<(string TagId, TagDef Definition, int Index)> FlattenTags(TagGroupDef root)
    {
        return Walk(root, "Line.Plc", 0);

        static IEnumerable<(string TagId, TagDef Definition, int Index)> Walk(
            TagGroupDef group,
            string parent,
            int inheritedIndex)
        {
            string path = parent + "." + group.Name;
            int index = int.TryParse(group.Name, out int numeric)
                ? numeric
                : MesIndexedGroupName.TryParse(group.Name, out _, out int parsed) ? parsed : inheritedIndex;
            foreach (var tag in group.Tags)
                yield return (path + "." + tag.Name, tag, index);
            foreach (var child in group.SubGroups)
            foreach (var item in Walk(child, path, index))
                yield return item;
        }
    }

    private static IEnumerable<string> FlattenTagIds(TagGroupDef root) =>
        FlattenTags(root).Select(item => item.TagId);

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
