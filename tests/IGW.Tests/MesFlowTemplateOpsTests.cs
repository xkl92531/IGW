using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class MesFlowTemplateOpsTests
{
    [Theory]
    [InlineData("Line.Plc.Station.DB1", false)]
    [InlineData("Line.Plc.Area.Station.DB1", false)]
    [InlineData("Line.Plc.Station.DB1", true)]
    [InlineData("Line.Plc.Factory.Area.Station.Process.DB1", true)]
    public void Apply_NestedTarget_RebindsFullPathAndUsesOnlyTargetFields(string targetPath, bool templateDevice)
    {
        var project = BuildNestedTarget(targetPath, templateDevice);
        var payload = project.MesUpload.FlowTemplates[0].Task.Flow!.Nodes[0].Payload;
        payload.Mode = MesPayloadModes.FieldMapping;
        payload.DataRanges = [];
        payload.Fields[0].FieldName = "isReady";
        payload.Fields.Add(new MesField { TagId = "Source.Plc.DB0.Legacy", FieldName = "legacy" });

        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", targetPath, null, out var report));

        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal(targetPath, task.GroupRef);
        Assert.Equal("Line.Plc", task.DeviceRef);
        Assert.False(task.Enabled);
        var mapped = task.Flow!.Nodes[0].Payload;
        var field = Assert.Single(mapped.Fields);
        Assert.Equal(targetPath + ".Ready", field.TagId);
        Assert.Equal("isReady", field.FieldName);
        var structure = Assert.Single(mapped.Structs);
        Assert.Equal(targetPath + ".CellData", structure.BaseGroup);
        Assert.Equal(["IsNG", "Error"], structure.Members.Select(member => member.TagId));
        Assert.DoesNotContain(report!.Issues, issue => issue.Ref.Contains("Legacy", StringComparison.Ordinal));
        Assert.Equal("Source.Plc.DB0.Ready", payload.Fields[0].TagId);
    }

    [Theory]
    [InlineData("Line.Plc.DB1", "Plc.DB1")]
    [InlineData("Line.Plc.Station.DB1", "Plc.Station.DB1")]
    [InlineData("Line.Plc.Factory.Area.Station.Process.DB1", "Plc.Factory.Area.Station.Process.DB1")]
    public void Apply_AnyTargetDepth_RemapsRangePathsAndPreservesCustomOutput(string targetPath, string outputPath)
    {
        var project = BuildNestedTarget(targetPath);
        project.MesUpload.FlowTemplates[0].Task.Flow!.Nodes[0].Payload.DataRanges.Add(new MesDataRange
        {
            SourcePath = "Source.Plc.DB0.CellData",
            OutputPathMode = "Custom",
            OutputPath = "request.measurements",
        });

        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", targetPath, null, out _));

        var ranges = Assert.Single(project.MesUpload.Tasks).Flow!.Nodes[0].Payload.DataRanges;
        Assert.Equal(targetPath, ranges[0].SourcePath);
        Assert.Equal(outputPath, ranges[0].OutputPath);
        Assert.Equal(targetPath + ".CellData", ranges[1].SourcePath);
        Assert.Equal("request.measurements", ranges[1].OutputPath);
    }

    [Fact]
    public void Apply_ExtractedSecondLevelTemplate_ReplacesSecondLevelTaskAndRemapsRanges()
    {
        const string targetPath = "Line.Plc.Station.DB1";
        var project = BuildNestedTarget(targetPath);
        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", targetPath, null, out _));
        var existing = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal("Plc.Station.DB1", existing.Flow!.Nodes[0].Payload.DataRanges[0].OutputPath);
        Assert.Null(MesFlowTemplateOps.Extract(project, existing.Id, "二级模板"));
        var device = project.Channels[0].Devices[0];
        device.Groups[0].SubGroups.Add(new TagGroupDef { Name = "DB2", Tags = [new TagDef { Name = "Ready" }] });
        project.MesUpload.Tasks.Add(new MesTask {
            Id = "target-id", Name = "目标流程", GroupRef = "Line.Plc.Station.DB2", DeviceRef = "Line.Plc", Enabled = true,
            Flow = new MesFlow { Nodes = [new MesNode { Id = "old", Type = "Upload" }] },
        });

        Assert.Null(MesFlowTemplateOps.Apply(project, "二级模板", "Line.Plc.Station.DB2", null, true, out _));

        var task = project.MesUpload.Tasks[1];
        Assert.Equal("target-id", task.Id);
        Assert.Equal("目标流程", task.Name);
        Assert.Equal("Line.Plc.Station.DB2", task.GroupRef);
        Assert.False(task.Enabled);
        var payload = task.Flow!.Nodes[0].Payload;
        Assert.Equal("Line.Plc.Station.DB2", Assert.Single(payload.DataRanges).SourcePath);
        Assert.Equal("Plc.Station.DB2", payload.DataRanges[0].OutputPath);
        Assert.Equal("Line.Plc.Station.DB2.Ready", Assert.Single(payload.Fields).TagId);
        Assert.Equal(targetPath, existing.Flow.Nodes[0].Payload.DataRanges[0].SourcePath);
    }

    [Fact]
    public void ApplyBulk_NestedTargets_ValidatesAllBeforeReplacingEmptyTask()
    {
        const string targetPath = "Line.Plc.Station.DB1";
        var project = BuildNestedTarget(targetPath);
        var empty = new MesTask { Id = "empty", Name = "空流程", GroupRef = targetPath, DeviceRef = "Line.Plc", Flow = new MesFlow() };
        project.MesUpload.Tasks.Add(empty);

        var rejected = MesFlowTemplateOps.ApplyBulk(project, "标准模板", [targetPath, "Line.Plc.Station.Missing"]);
        Assert.Single(rejected.Errors);
        Assert.Empty(rejected.Reports);
        Assert.Same(empty, Assert.Single(project.MesUpload.Tasks));

        var applied = MesFlowTemplateOps.ApplyBulk(project, "标准模板", [targetPath]);
        Assert.Empty(applied.Errors);
        Assert.Single(applied.Reports);
        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal("empty", task.Id);
        Assert.False(task.Enabled);
        Assert.Single(task.Flow!.Nodes);
        var duplicate = MesFlowTemplateOps.ApplyBulk(project, "标准模板", [targetPath]);
        Assert.Single(duplicate.Errors);
        Assert.Same(task, Assert.Single(project.MesUpload.Tasks));
    }

    private static IgwProject BuildNestedTarget(string targetPath, bool templateDevice = false)
    {
        var project = BuildProjectWithExistingTask();
        project.MesUpload.Tasks.Clear();
        var device = project.Channels[0].Devices[0];
        var root = device.Groups[0];
        root.SubGroups[0].Tags.Add(new TagDef { Name = "Error" });
        foreach (var name in targetPath.Split('.').Skip(2).SkipLast(1).Reverse())
            root = new TagGroupDef { Name = name, Tags = [new TagDef { Name = "ParentOnly" }], SubGroups = [root,
                new TagGroupDef { Name = "Sibling", Tags = [new TagDef { Name = "SiblingOnly" }] }] };
        device.Groups = [root];
        if (templateDevice)
        {
            project.Templates.Add(new DeviceTemplate { Name = "设备模板", Groups = device.Groups });
            device.TemplateRef = "设备模板";
            device.Groups = [];
        }
        return project;
    }

    [Fact]
    public void Templates_remap_PLC_condition_paths_but_preserve_backend_paths()
    {
        var project = BuildProjectWithExistingTask();
        var task = project.MesUpload.FlowTemplates[0].Task;
        const string path = "Source.Plc.DB0.Ready";
        task.Listener.InputMappings = [new() { SourcePath = path, TargetPath = "barcode" }];
        task.Flow!.Nodes.Add(new() { Type = "Condition", Id = "test-condition", Branches = [new() { Conditions = [
            new() { Source = "Tag", TagId = path }, new() { Source = "Input", Path = path, TagId = path }
        ] }] });
        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out _));
        var result = project.MesUpload.Tasks[0];
        Assert.Equal(path, result.Listener.InputMappings[0].SourcePath);
        var conditions = result.Flow!.Nodes.Single(n => n.Id == "test-condition").Branches[0].Conditions;
        Assert.Equal("Line.Plc.DB1.Ready", conditions[0].TagId);
        Assert.Equal(path, conditions[1].Path);
        Assert.Equal(path, conditions[1].TagId);
    }

    [Fact]
    public void Apply_remaps_only_Tag_extra_field_paths_and_reports_missing_or_external_sources()
    {
        var project = BuildProjectWithExistingTask();
        var template = project.MesUpload.FlowTemplates[0].Task;
        template.Payload.ExtraFields = [new() { Name = "rootTag", Source = "Tag", Path = "Source.Plc.DB0.Ready" }];
        template.Flow!.Nodes[0].Payload.ExtraFields = [
            new() { Name = "tag", Source = "Tag", Path = "Source.Plc.DB0.Ready" },
            new() { Name = "missing", Source = "Tag", Path = "Source.Plc.DB0.Missing" },
            new() { Name = "external", Source = "Tag", Path = "Elsewhere.Plc.DB.Value" },
            new() { Name = "input", Source = "Input", Path = "Source.Plc.DB0.Ready" },
            new() { Name = "fixed", Source = "Fixed", Value = "Source.Plc.DB0.Ready", Path = "Source.Plc.DB0.Ready" },
        ];
        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out var report));
        var result = project.MesUpload.Tasks[0];
        Assert.Equal("Line.Plc.DB1.Ready", result.Payload.ExtraFields[0].Path);
        var fields = result.Flow!.Nodes[0].Payload.ExtraFields;
        Assert.Equal("Line.Plc.DB1.Ready", fields[0].Path);
        Assert.Equal("Line.Plc.DB1.Missing", fields[1].Path);
        Assert.Equal("Elsewhere.Plc.DB.Value", fields[2].Path);
        Assert.All(fields.Skip(3), field => Assert.Equal("Source.Plc.DB0.Ready", field.Path));
        Assert.Equal("Source.Plc.DB0.Ready", fields[4].Value);
        Assert.Contains(report!.Issues, issue => issue.Kind == "missing" && issue.Ref == "Line.Plc.DB1.Missing");
        Assert.Contains(report.Issues, issue => issue.Kind == "external" && issue.Ref == "Elsewhere.Plc.DB.Value");
        Assert.DoesNotContain(report.Issues, issue => issue.Ref == "Source.Plc.DB0.Ready");
        Assert.Equal("Source.Plc.DB0.Ready", template.Payload.ExtraFields[0].Path);
    }

    [Fact]
    public void Apply_remaps_each_PLC_array_length_tag_to_the_target_group()
    {
        var project = BuildProjectWithExistingTask();
        var range = project.MesUpload.FlowTemplates[0].Task.Flow!.Nodes[0].Payload.DataRanges[0];
        range.ArrayLengthMode = "Tag";
        range.ArrayLengthTagIds = ["Source.Plc.DB0.Ready"];
        Assert.Null(MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out _));
        var result = project.MesUpload.Tasks[0].Flow!.Nodes[0].Payload.DataRanges[0];
        Assert.Equal(["Line.Plc.DB1.Ready"], result.ArrayLengthTagIds);
    }
    [Fact]
    public void Apply_DefaultMode_RejectsExistingTaskWithoutMutation()
    {
        var project = BuildProjectWithExistingTask();
        var before = project.MesUpload.Tasks[0].Flow!.Nodes[0].Title;

        var error = MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, out var report);

        Assert.Contains("已有采集任务", error);
        Assert.Null(report);
        Assert.Equal(before, project.MesUpload.Tasks[0].Flow!.Nodes[0].Title);
    }

    [Fact]
    public void Apply_ExplicitReplacement_PreservesIdentityBindingAndDisablesTask()
    {
        var project = BuildProjectWithExistingTask();

        var error = MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out var report);

        Assert.Null(error);
        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal("existing-id", task.Id);
        Assert.Equal("现有流程", task.Name);
        Assert.Equal("Line.Plc.DB1", task.GroupRef);
        Assert.Equal("Line.Plc", task.DeviceRef);
        Assert.False(task.Enabled);
        Assert.Equal("模板上报", Assert.Single(task.Flow!.Nodes).Title);
        var payload = task.Flow.Nodes[0].Payload;
        Assert.Equal("Line.Plc.DB1.Ready", Assert.Single(payload.Fields).TagId);
        Assert.Equal("Line.Plc.DB1.CellData", Assert.Single(payload.Structs).BaseGroup);
        Assert.Equal("Structures", payload.Structs[0].AliasName);
        Assert.Equal("Line.Plc.DB1", Assert.Single(payload.DataRanges).SourcePath);
        Assert.Equal("Plc.DB1", payload.DataRanges[0].OutputPath);
        Assert.Equal("现有流程", report!.TaskName);
    }

    [Fact]
    public void Apply_InvalidTemplateWithReplacement_DoesNotRemoveExistingTask()
    {
        var project = BuildProjectWithExistingTask();

        var error = MesFlowTemplateOps.Apply(project, "不存在", "Line.Plc.DB1", null, true, out var report);

        Assert.Contains("流程模板不存在", error);
        Assert.Null(report);
        Assert.Equal("existing-id", Assert.Single(project.MesUpload.Tasks).Id);
        Assert.Equal("旧节点", project.MesUpload.Tasks[0].Flow!.Nodes[0].Title);
    }

    [Fact]
    public void ApplyBulk_ReplacesEmptyTaskAndPreservesIdentity()
    {
        var project = BuildProjectWithExistingTask();
        project.MesUpload.Tasks[0].Flow = new MesFlow();

        var (errors, reports) = MesFlowTemplateOps.ApplyBulk(project, "标准模板", ["Line.Plc.DB1"]);

        Assert.Empty(errors);
        Assert.Single(reports);
        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal("existing-id", task.Id);
        Assert.Equal("现有流程", task.Name);
        Assert.False(task.Enabled);
        Assert.Equal("模板上报", Assert.Single(task.Flow!.Nodes).Title);
    }

    [Fact]
    public void ApplyBulk_RejectsConfiguredFlowWithoutMutation()
    {
        var project = BuildProjectWithExistingTask();
        var before = project.MesUpload.Tasks[0].Flow!.Nodes[0].Title;

        var (errors, reports) = MesFlowTemplateOps.ApplyBulk(project, "标准模板", ["Line.Plc.DB1"]);

        Assert.Single(errors);
        Assert.Contains("已有采集任务", errors[0].Message);
        Assert.Empty(reports);
        Assert.Equal(before, project.MesUpload.Tasks[0].Flow!.Nodes[0].Title);
        Assert.Equal("existing-id", project.MesUpload.Tasks[0].Id);
    }

    [Fact]
    public void Apply_FieldMapping_RebuildsUploadFromTargetDbAndPreservesJsonNames()
    {
        var project = BuildProjectWithExistingTask();
        var target = project.Channels[0].Devices[0].Groups[0];
        target.Tags =
        [
            new TagDef { Name = "Ready", Address = "DB1.DBW0" },
            new TagDef { Name = "Temperature", Address = "DB1.DBD2" },
        ];
        target.SubGroups =
        [
            new TagGroupDef
            {
                Name = "CellData_0",
                Tags =
                [
                    new TagDef { Name = "IsNG", Address = "DB1.DBW6" },
                    new TagDef { Name = "ErrorMessage", Address = "DB1.DBB8" },
                ],
            },
            new TagGroupDef
            {
                Name = "CellData_1",
                Tags =
                [
                    new TagDef { Name = "IsNG", Address = "DB1.DBW108" },
                    new TagDef { Name = "ErrorMessage", Address = "DB1.DBB110" },
                ],
            },
        ];

        var templatePayload = project.MesUpload.FlowTemplates[0].Task.Flow!.Nodes[0].Payload;
        templatePayload.Mode = MesPayloadModes.FieldMapping;
        templatePayload.DataRanges = [];
        templatePayload.Fields =
        [
            new MesField { TagId = "Source.Plc.DB0.Ready", FieldName = "isReady" },
            new MesField { TagId = "Source.Plc.DB0.LegacyValue", FieldName = "legacy" },
        ];
        templatePayload.Structs =
        [
            new MesStruct
            {
                Kind = "Array",
                BaseGroup = "Source.Plc.DB0.CellData",
                AliasName = "Structures",
                CountMode = "Enum",
                FixedCount = 1,
                Members =
                [
                    new MesField { TagId = "IsNG", FieldName = "isNg" },
                    new MesField { TagId = "LegacyMember", FieldName = "legacyMember" },
                ],
            },
        ];

        var error = MesFlowTemplateOps.Apply(project, "标准模板", "Line.Plc.DB1", null, true, out var report);

        Assert.Null(error);
        var payload = Assert.Single(project.MesUpload.Tasks).Flow!.Nodes[0].Payload;
        Assert.Equal(MesPayloadModes.FieldMapping, payload.Mode);
        Assert.Collection(payload.Fields.OrderBy(field => field.TagId),
            field =>
            {
                Assert.Equal("Line.Plc.DB1.Ready", field.TagId);
                Assert.Equal("isReady", field.FieldName);
            },
            field =>
            {
                Assert.Equal("Line.Plc.DB1.Temperature", field.TagId);
                Assert.Equal("Temperature", field.FieldName);
            });
        var item = Assert.Single(payload.Structs);
        Assert.Equal("Array", item.Kind);
        Assert.Equal("Line.Plc.DB1.CellData", item.BaseGroup);
        Assert.Equal("Structures", item.AliasName);
        Assert.Equal("Enum", item.CountMode);
        Assert.Equal(0, item.StartIndex);
        Assert.Equal(2, item.FixedCount);
        Assert.Collection(item.Members.OrderBy(member => member.TagId),
            member =>
            {
                Assert.Equal("ErrorMessage", member.TagId);
                Assert.Equal("", member.FieldName);
            },
            member =>
            {
                Assert.Equal("IsNG", member.TagId);
                Assert.Equal("isNg", member.FieldName);
            });
        Assert.DoesNotContain(payload.Fields, field => field.TagId.Contains("Legacy", StringComparison.Ordinal));
        Assert.DoesNotContain(item.Members, member => member.TagId.Contains("Legacy", StringComparison.Ordinal));
        Assert.DoesNotContain(report!.Issues, issue => issue.Ref.Contains("Legacy", StringComparison.Ordinal));
    }

    private static IgwProject BuildProjectWithExistingTask()
    {
        var existing = new MesTask
        {
            Id = "existing-id",
            Name = "现有流程",
            GroupRef = "Line.Plc.DB1",
            DeviceRef = "Line.Plc",
            Enabled = true,
            Flow = new MesFlow
            {
                Nodes = [new MesNode { Id = "old", Type = "Upload", Title = "旧节点" }],
            },
        };
        var templateTask = new MesTask
        {
            Id = "template-id",
            Name = "模板流程",
            GroupRef = "Source.Plc.DB0",
            DeviceRef = "Source.Plc",
            Enabled = true,
            Flow = new MesFlow
            {
                Nodes =
                [
                    new MesNode
                    {
                        Id = "upload",
                        Type = "Upload",
                        Title = "模板上报",
                        Payload = new MesPayload
                        {
                            Mode = MesPayloadModes.DataRanges,
                            DataRanges =
                            [
                                new MesDataRange
                                {
                                    SourcePath = "Source.Plc.DB0",
                                    OutputPath = "Plc.DB0",
                                    OutputPathMode = "Auto",
                                },
                            ],
                            Fields = [new MesField { TagId = "Source.Plc.DB0.Ready", FieldName = "Ready" }],
                            Structs =
                            [
                                new MesStruct
                                {
                                    BaseGroup = "Source.Plc.DB0.CellData",
                                    AliasName = "Structures",
                                    Members = [new MesField { TagId = "IsNG", FieldName = "isNg" }],
                                },
                            ],
                        },
                    },
                ],
            },
        };

        return new IgwProject
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
                                    Name = "DB1",
                                    Tags = [new TagDef { Name = "Ready", Address = "DB1.DBW0" }],
                                    SubGroups =
                                    [
                                        new TagGroupDef
                                        {
                                            Name = "CellData",
                                            Tags = [new TagDef { Name = "IsNG", Address = "DB1.DBW2" }],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
            MesUpload = new MesUploadSettings
            {
                Tasks = [existing],
                FlowTemplates =
                [
                    new MesFlowTemplate
                    {
                        Name = "标准模板",
                        SourceGroupRef = "Source.Plc.DB0",
                        Task = templateTask,
                    },
                ],
            },
        };
    }
}

public sealed class MesTaskEnablementTests
{
    [Fact]
    public void Project_validation_rejects_enabled_ancestor_and_descendant_tasks()
    {
        var project = ProjectWithTasks(
            new MesTask { Id = "op030", Name = "OP030", GroupRef = "Line.PLC.OP030", Enabled = true },
            new MesTask { Id = "entry", Name = "进站", GroupRef = "Line.PLC.OP030.进站", Enabled = true });

        var report = ProjectStore.Check(project);

        Assert.Contains(report.Errors, error =>
            error.Contains("Line.PLC.OP030", StringComparison.Ordinal) &&
            error.Contains("Line.PLC.OP030.进站", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_validation_allows_enabled_sibling_tasks()
    {
        var project = ProjectWithTasks(
            new MesTask { Id = "entry", Name = "进站", GroupRef = "Line.PLC.OP030.进站", Enabled = true },
            new MesTask { Id = "exit", Name = "出站", GroupRef = "Line.PLC.OP030.出站", Enabled = true });

        var report = ProjectStore.Check(project);

        Assert.DoesNotContain(report.Errors, error => error.Contains("父子", StringComparison.Ordinal));
    }

    private static IgwProject ProjectWithTasks(params MesTask[] tasks) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "OpcUa.Client",
                Devices = [new DeviceDef { Name = "PLC" }],
            },
        ],
        MesUpload = new MesUploadSettings { Tasks = [.. tasks] },
    };
}

public sealed class MesTaskTopologyRegressionTests
{
    [Fact]
    public void Reconcile_PreservesFlowAndClearsUploadRanges_WhenTaskDirectoryStillExists()
    {
        var project = BuildProject();
        var upload = new MesNode
        {
            Id = "upload",
            Type = "Upload",
            Title = "上报",
            Http = new MesHttp { Url = "http://mes/OcvJudgment/JudgeBatch" },
            Payload = new MesPayload
            {
                Mode = MesPayloadModes.DataRanges,
                DataRanges = [new MesDataRange { SourcePath = "Line.Plc.DB1.RemovedStruct", OutputPath = "Structures" }],
                Append = { ["station"] = "A" },
            },
        };
        project.MesUpload.Tasks =
        [
            new MesTask
            {
                Id = "keep-flow",
                GroupRef = "Line.Plc.DB1",
                DeviceRef = "Line.Plc",
                Flow = new MesFlow
                {
                    Nodes = [new MesNode { Id = "trigger", Type = "Trigger" }, upload],
                    Edges = [new MesEdge { From = "trigger", To = "upload" }],
                },
            },
        ];

        var result = MesTaskTopology.Reconcile(project);

        Assert.Equal(0, result.RemovedTasks);
        Assert.Equal(1, result.ClearedPayloads);
        Assert.Equal(1, result.ClearedRanges);
        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal(2, task.Flow!.Nodes.Count);
        Assert.Single(task.Flow.Edges);
        Assert.Equal("http://mes/OcvJudgment/JudgeBatch", upload.Http.Url);
        Assert.Empty(upload.Payload.DataRanges);
        Assert.Equal("A", upload.Payload.Append["station"]);
        var validation = ProjectStore.Check(project);
        Assert.True(validation.Ok, string.Join(" | ", validation.Errors));
    }

    [Fact]
    public void Reconcile_RemovesTaskOnlyWhenItsBoundDirectoryDisappears()
    {
        var project = BuildProject();
        project.MesUpload.Tasks =
        [
            new MesTask
            {
                Id = "keep",
                GroupRef = "Line.Plc.DB1",
                Payload = new MesPayload
                {
                    Mode = MesPayloadModes.DataRanges,
                    DataRanges = [new MesDataRange { SourcePath = "Line.Plc.DB1" }],
                },
            },
            new MesTask { Id = "gone", GroupRef = "Line.Plc.RemovedDB" },
        ];

        var result = MesTaskTopology.Reconcile(project);

        Assert.Equal(1, result.RemovedTasks);
        Assert.Equal(0, result.ClearedPayloads);
        var task = Assert.Single(project.MesUpload.Tasks);
        Assert.Equal("keep", task.Id);
        Assert.Single(task.Payload.DataRanges);
    }

    private static IgwProject BuildProject() => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "Siemens.S7",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Groups =
                        [
                            new TagGroupDef
                            {
                                Name = "DB1",
                                Tags = [new TagDef { Name = "Ready", Address = "DB1.DBX0.0" }],
                            },
                        ],
                    },
                ],
            },
        ],
    };
}
