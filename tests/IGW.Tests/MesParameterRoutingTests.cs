using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesParameterRoutingTests
{
    [Fact]
    public async Task Codes_are_literal_dictionary_keys_and_legacy_ItemId_uncoded_fields_keep_aliases()
    {
        var payload = Payload("""
            {"fieldKeyMode":"ItemId","fields":[
              {"tagId":"Line.Plc.DB.Value","fieldName":"old","parameterCode":" A.B ","scale":2},
              {"tagId":"Line.Plc.DB.Other","fieldName":"other"}]}
            """);
        var json = await Upload(payload);
        Assert.Equal(4, json.GetProperty("parameterDic").GetProperty(" A.B ").GetInt32());
        Assert.Equal(3, json.GetProperty("other").GetInt32());
        Assert.False(json.TryGetProperty("old", out _));
        Assert.False(json.GetProperty("parameterDic").TryGetProperty("Line.Plc.DB.Other", out _));
    }

    [Theory]
    [InlineData("[2]", "[5]")]
    [InlineData("_2", "_5")]
    [InlineData(".2", ".5")]
    public async Task Dictionary_struct_flattens_nested_actual_element_paths_and_ignores_member_codes(string outer, string inner)
    {
        var payload = Payload("""
            {"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","countMode":"Enum","startIndex":1,"fixedCount":20,"asDictionary":true,
              "members":[{"tagId":"Count","parameterCode":"old-code"}],
              "structs":[{"baseGroup":"Cells","aliasName":"cells","countMode":"Enum","startIndex":1,"fixedCount":20,
                "members":[{"tagId":"Value","scale":2,"parameterCode":"old-code"}]}]}]}
            """);
        string parent = "Line.Plc.DB.Items" + outer;
        string child = parent + ".Cells" + inner + ".Value";
        var json = await Upload(payload, (parent + ".Count", 7), (child, 11));
        Assert.Equal(["parameterDic"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, json.GetProperty("parameterDic").GetProperty(parent + ".Count").GetInt32());
        Assert.Equal(22, json.GetProperty("parameterDic").GetProperty(child).GetInt32());
        using var response = JsonDocument.Parse(json.GetRawText());
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", [parent + ".Count", child]);
        Assert.Contains(new MesResponseWritePlanItem(child, "11"), plan);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public async Task Nested_dictionary_child_leaves_ordinary_parent_members_in_original_hierarchy()
    {
        var payload = Payload("""
            {"structs":[{"kind":"Object","baseGroup":"Line.Plc.DB.Station","aliasName":"station",
              "members":[{"tagId":"Counter"},{"tagId":"Result","parameterCode":"R.1","scale":2}],
              "structs":[{"kind":"Object","baseGroup":"Child","aliasName":"child","asDictionary":true,
                "members":[{"tagId":"Value"}]}]}]}
            """);
        var json = await Upload(payload, ("Line.Plc.DB.Station.Counter", 7), ("Line.Plc.DB.Station.Result", 9), ("Line.Plc.DB.Station.Child.Value", 11));
        Assert.Equal(["Counter"], json.GetProperty("station").EnumerateObject().Select(p => p.Name));
        Assert.Equal(18, json.GetProperty("parameterDic").GetProperty("R.1").GetInt32());
        Assert.Equal(11, json.GetProperty("parameterDic").GetProperty("Line.Plc.DB.Station.Child.Value").GetInt32());
        using var response = JsonDocument.Parse("""{"data":{"station":{"Counter":8},"parameterDic":{"R.1":20,"Line.Plc.DB.Station.Child.Value":12}}}""");
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", ["Line.Plc.DB.Station.Counter", "Line.Plc.DB.Station.Result", "Line.Plc.DB.Station.Child.Value"]);
        Assert.Equal(3, plan.Count);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Station.Result", "10"), plan);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Station.Child.Value", "12"), plan);
    }

    [Fact]
    public async Task Repeated_member_code_across_array_targets_is_rejected_before_request()
    {
        var payload = Payload("""
            {"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","fixedCount":2,"startIndex":2,"takeIndex":true,
              "members":[{"tagId":"Value","parameterCode":"SAME"}]}]}
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => Upload(payload));
        var project = new IgwProject { MesUpload = new() { Tasks = [new() { Id = "t", Name = "t", Payload = payload }] } };
        Assert.Contains(ProjectStore.Check(project).Errors, error => error.Contains("SAME"));
    }

    [Fact]
    public async Task Coded_field_conflicts_with_reserved_root_but_uncoded_legacy_dictionary_name_is_allowed()
    {
        var payload = Payload("""{"fields":[{"tagId":"Line.Plc.DB.Value","parameterCode":"V"}],"append":{"parameterDic":"bad"}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Upload(payload));
        payload.Fields[0].ParameterCode = null;
        Assert.Equal("bad", (await Upload(payload)).GetProperty("parameterDic").GetString());
    }

    [Fact]
    public async Task No_routed_values_means_no_empty_dictionary_even_in_legacy_ItemId()
    {
        Assert.Empty((await Upload(Payload("""{"fieldKeyMode":"ItemId"}"""))).EnumerateObject());
    }

    [Fact]
    public void Explicit_parameter_code_equal_to_an_uncoded_point_path_maps_to_the_coded_target_only()
    {
        var payload = Payload("""
            {"fields":[{"tagId":"Line.Plc.DB.Value","parameterCode":"Line.Plc.DB.Other","scale":2},
              {"tagId":"Line.Plc.DB.Other","fieldName":"other"}]}
            """);
        using var response = JsonDocument.Parse("""{"parameterDic":{"Line.Plc.DB.Other":10},"other":6}""");
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", ["Line.Plc.DB.Value", "Line.Plc.DB.Other"]);
        Assert.Equal(2, plan.Count);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Value", "5"), plan);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Other", "6"), plan);
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("data", false)]
    [InlineData("value", true)]
    [InlineData("nested", false)]
    public void Custom_response_dictionary_keeps_explicit_code_ownership_and_independent_original_alias(string wrapper, bool originalAlias)
    {
        var payload = Payload("""
            {"fields":[{"tagId":"Line.Plc.DB.Value","parameterCode":"Line.Plc.DB.Other","scale":2},
              {"tagId":"Line.Plc.DB.Other","fieldName":"other"}]}
            """);
        string body = originalAlias ? """{"customDic":{"Line.Plc.DB.Other":10},"other":6}""" : """{"customDic":{"Line.Plc.DB.Other":10}}""";
        string wrapped = wrapper switch { "data" => "{\"data\":" + body + "}", "value" => "{\"value\":" + body + "}",
            "nested" => "{\"value\":{\"backResult\":" + body + "}}", _ => body };
        using var response = JsonDocument.Parse(wrapped);
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "customDic", ["Line.Plc.DB.Value", "Line.Plc.DB.Other"]);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Value", "5"), plan);
        if (originalAlias) Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Other", "6"), plan);
        else Assert.DoesNotContain(plan, item => item.TagId == "Line.Plc.DB.Other");
        Assert.Equal(originalAlias ? 2 : 1, plan.Count);
    }

    [Theory]
    [InlineData("Empty", "direct", false)]
    [InlineData("Unrelated", "direct", false)]
    [InlineData("Both", "direct", false)]
    [InlineData("Reverse", "direct", false)]
    [InlineData("Empty", "data", false)]
    [InlineData("Unrelated", "value", false)]
    [InlineData("Both", "nested", false)]
    [InlineData("Both", "data", true)]
    [InlineData("Reverse", "value", true)]
    [InlineData("Empty", "nested", true)]
    public void Standard_and_custom_dictionaries_are_separate_from_ordinary_root_fields(string standard, string wrapper, bool fullIdAlias)
    {
        var payload = new MesPayload { Fields = [new() { TagId = "Line.Plc.DB.Value", ParameterCode = "Line.Plc.DB.Other", Scale = 2 },
            new() { TagId = "Line.Plc.DB.Other", FieldName = fullIdAlias ? "Line.Plc.DB.Other" : "other" }] };
        string fallback = standard switch { "Unrelated" => "{\"unmapped\":1}", "Both" or "Reverse" => "{\"Line.Plc.DB.Other\":8}", _ => "{}" };
        string dictionaries = standard == "Reverse"
            ? "\"customDic\":{\"Line.Plc.DB.Other\":10},\"parameterDic\":" + fallback
            : "\"parameterDic\":" + fallback + ",\"customDic\":{\"Line.Plc.DB.Other\":10}";
        string root = "{" + dictionaries + (fullIdAlias ? ",\"Line.Plc.DB.Other\":6" : "") + "}";
        string body = wrapper switch { "data" => "{\"data\":" + root + "}", "value" => "{\"value\":" + root + "}",
            "nested" => "{\"value\":{\"backResult\":" + root + "}}", _ => root };
        using var response = JsonDocument.Parse(body);
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "customDic", ["Line.Plc.DB.Value", "Line.Plc.DB.Other"]);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Value", "5"), plan);
        if (fullIdAlias) Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Other", "6"), plan);
        else Assert.DoesNotContain(plan, item => item.TagId == "Line.Plc.DB.Other");
        Assert.Equal(fullIdAlias ? 2 : 1, plan.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_templates_ignore_unused_automatic_routing_metadata_in_generated_context(bool typed)
    {
        var payload = TemplateMetadata();
        var http = TemplateHttp(typed);
        var json = await Upload(payload, http);
        Assert.Equal(["first", "second", "third"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, json.GetProperty("first").GetInt32());
        Assert.Equal(3, json.GetProperty("second").GetInt32());
        Assert.Equal(3, json.GetProperty("third").GetInt32());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Save_validation_uses_each_effective_template_context_for_task_and_flow(bool typed, bool flow)
    {
        var task = new MesTask { Id = "t", Name = "t", Payload = TemplateMetadata(), Steps = [new() { Http = TemplateHttp(typed) }] };
        if (flow) task.Flow = new() { Nodes = [new() { Id = "u", Type = "Upload", Payload = TemplateMetadata(), Http = TemplateHttp(typed) }] };
        var project = new IgwProject { MesUpload = new() { Tasks = [task] } };
        Assert.Empty(ProjectStore.Check(project).Errors);
        if (flow) task.Flow!.Nodes.Add(new() { Id = "automatic", Type = "Upload", Payload = TemplateMetadata() });
        else task.Steps.Add(new());
        Assert.NotEmpty(ProjectStore.Check(project).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_precompile_uses_explicit_upload_node_context_and_ignores_unused_task_routing(bool typed)
    {
        await using var hub = new DataHub();
        foreach (var (id, value) in new[] { ("Line.Plc.DB.Value", 2), ("Line.Plc.DB.Other", 3) })
        { hub.Register(id, new() { DataType = IgwDataType.Int32 }); hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow)); }
        var handler = new Handler();
        var http = TemplateHttp(typed); http.Url = "http://routing.test/template";
        var task = new MesTask { Id = "templates", Name = "templates", Payload = TemplateMetadata(), CollectIntervalMs = 1000, Flow = new()
        {
            Nodes = [new() { Id = "trigger", Type = "Trigger" }, new() { Id = "upload", Type = "Upload", Payload = TemplateMetadata(), Http = http }],
            Edges = [new() { From = "trigger", To = "upload" }],
        } };
        await using (var engine = new MesUploadEngine(hub, new() { Enabled = true, Tasks = [task] }, (_, _, _) => Task.FromResult(true), NullLogger.Instance, httpMessageHandler: handler))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (handler.Body.Length == 0) await Task.Delay(10, timeout.Token);
        }
        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal(3, json.RootElement.GetProperty("second").GetInt32());
    }

    private static MesPayload TemplateMetadata() => Payload("""
        {"fields":[{"tagId":"Line.Plc.DB.Value","fieldName":"alias","parameterCode":"UNUSED"},
          {"tagId":"Line.Plc.DB.Other","fieldName":"parameterDic"},
          {"tagId":"Line.Plc.DB.Other","fieldName":"third","parameterCode":"UNUSED"}]}
        """);
    private static MesHttp TemplateHttp(bool typed) => typed
        ? new() { JsonTemplate = "{\"first\":\"${fields.alias}\",\"second\":\"${fields.parameterDic}\",\"third\":\"${fields.third}\"}" }
        : new() { BodyTemplate = "{\"first\":{alias},\"second\":{parameterDic},\"third\":{third}}" };

    [Fact]
    public async Task Custom_body_template_keeps_alias_placeholder_contract_for_coded_fields()
    {
        var payload = Payload("""{"fields":[{"tagId":"Line.Plc.DB.Value","fieldName":"alias","parameterCode":"V"}]}""");
        var json = await Upload(payload, new MesHttp { BodyTemplate = "{\"custom\":{alias}}" });
        Assert.Equal(["custom"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, json.GetProperty("custom").GetInt32());
    }

    [Fact]
    public void Save_validation_uses_actual_enum_elements_instead_of_assuming_bracket_paths()
    {
        var payload = Payload("""
            {"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","countMode":"Enum","fixedCount":20,"startIndex":1,
              "members":[{"tagId":"Value","parameterCode":"ONE"}]}]}
            """);
        var group = new TagGroupDef { Name = "DB", SubGroups = [new() { Name = "Items_2", Tags = [new() { Name = "Value", DataType = IgwDataType.Int32 }] }] };
        var project = new IgwProject { Channels = [new() { Name = "Line", DriverId = "simulator", Devices = [new() { Name = "Plc", Groups = [group] }] }],
            MesUpload = new() { Tasks = [new() { Id = "t", Name = "t", Payload = payload }] } };
        Assert.Empty(ProjectStore.Check(project).Errors);
        group.SubGroups.Add(new() { Name = "Items_3", Tags = [new() { Name = "Value", DataType = IgwDataType.Int32 }] });
        Assert.Contains(ProjectStore.Check(project).Errors, error => error.Contains("ONE"));
    }

    [Fact]
    public async Task Coded_List_members_do_not_shift_original_member_writeback_positions()
    {
        var payload = Payload("""
            {"structs":[{"kind":"List","baseGroup":"Line.Plc.DB","aliasName":"list",
              "members":[{"tagId":"Value","parameterCode":"V","scale":2},{"tagId":"Other"}]}]}
            """);
        var json = await Upload(payload);
        Assert.Equal([3], json.GetProperty("list").EnumerateArray().Select(value => value.GetInt32()));
        using var response = JsonDocument.Parse("""{"list":[9],"parameterDic":{"V":10}}""");
        var plan = MesResponseWritePlanner.Plan(payload, response.RootElement, "parameterDic", ["Line.Plc.DB.Value", "Line.Plc.DB.Other"]);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Value", "5"), plan);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Other", "9"), plan);
    }

    [Fact]
    public void Uncoded_original_structures_do_not_enumerate_array_paths_for_dictionary_validation()
    {
        var payload = Payload("""{"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","countMode":"Enum","members":[{"tagId":"Value"}]}]}""");
        var routes = MesParameterRouting.Routes(payload, (_, _) => throw new InvalidOperationException("Uncoded mappings do not need dictionary paths."));
        Assert.Empty(routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_nested_UDT_array_dictionary_preserves_nonzero_indices_scaling_and_bad_leaf_quality(bool bad)
    {
        var leafSchema = new StructDef { Fields = [new() { Name = "Value", DataType = IgwDataType.Int32 }] };
        var cells = new ArrayDef { ElementType = IgwDataType.Struct, Struct = leafSchema, LowerBounds = [5], Dimensions = [2] };
        var schema = new StructDef { Fields = [new() { Name = "Cells", DataType = IgwDataType.Struct, Array = cells }] };
        var array = new ArrayDef { ElementType = IgwDataType.Struct, Struct = schema, LowerBounds = [2], Dimensions = [2] };
        var memberShape = ArrayShape.Create([2, 2], [2, 5]);
        var leaves = StructArrayValue.Create(memberShape, leafSchema, new Dictionary<string, ArrayRuntimeValue>
            { ["Value"] = PrimitiveArrayValue<int>.Create(memberShape, [11, 12, 21, 22], bad ? [StatusCode.Good, StatusCode.Bad, StatusCode.Good, StatusCode.Good] : null) });
        var value = StructArrayValue.Create(array.Shape, schema, new Dictionary<string, ArrayRuntimeValue> { ["Cells"] = leaves });
        await using var hub = new DataHub();
        hub.Register("Line.Plc.DB.Items", new() { DataType = IgwDataType.Struct, IsArray = true, Array = array });
        hub.Update("Line.Plc.DB.Items", DataValue.GoodAt(value, DateTime.UtcNow));
        var payload = Payload("""
            {"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","fixedCount":2,"startIndex":2,"asDictionary":true,
              "structs":[{"baseGroup":"Cells","aliasName":"cells","fixedCount":2,"startIndex":5,
                "members":[{"tagId":"Value","scale":2}]}]}]}
            """);
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true), NullLogger.Instance, httpMessageHandler: handler);
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new() { Http = new() { Url = "http://routing.test/native" } }] }, default);
        using var json = JsonDocument.Parse(handler.Body);
        var dictionary = json.RootElement.GetProperty("parameterDic");
        Assert.Equal(4, dictionary.EnumerateObject().Count());
        Assert.Equal(22, dictionary.GetProperty("Line.Plc.DB.Items[2].Cells[5].Value").GetInt32());
        if (bad) Assert.Equal(JsonValueKind.Null, dictionary.GetProperty("Line.Plc.DB.Items[2].Cells[6].Value").ValueKind);
        else Assert.Equal(24, dictionary.GetProperty("Line.Plc.DB.Items[2].Cells[6].Value").GetInt32());
        Assert.Equal(44, dictionary.GetProperty("Line.Plc.DB.Items[3].Cells[6].Value").GetInt32());
        var plan = MesResponseWritePlanner.Plan(payload, json.RootElement, "parameterDic", ["Line.Plc.DB.Items"]);
        Assert.Contains(new MesResponseWritePlanItem("Line.Plc.DB.Items[3].Cells[6].Value", "22"), plan);
        Assert.Equal(bad ? 3 : 4, plan.Count);
    }

    [Fact]
    public async Task Tag_count_dictionary_writeback_uses_the_paths_and_scale_of_the_uploaded_snapshot()
    {
        await using var hub = new DataHub();
        foreach (var (id, value) in new[] { ("Line.Plc.DB.Count", 2), ("Line.Plc.DB.Items[2].Value", 11), ("Line.Plc.DB.Items[3].Value", 22) })
        { hub.Register(id, new() { DataType = IgwDataType.Int32, Access = TagAccess.ReadWrite }); hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow)); }
        var payload = Payload("""
            {"structs":[{"baseGroup":"Line.Plc.DB.Items","aliasName":"items","startIndex":2,"fixedCount":1,"countMode":"Tag","countTagId":"Line.Plc.DB.Count","asDictionary":true,
              "members":[{"tagId":"Value","scale":2}]}]}
            """);
        var handler = new Handler(body =>
        {
            hub.Update("Line.Plc.DB.Count", DataValue.GoodAt(0, DateTime.UtcNow));
            var response = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            response["parameterDic"]!["Line.Plc.DB.Items[2].Value"] = 14;
            response["parameterDic"]!["Line.Plc.DB.Items[3].Value"] = 24;
            return response.ToJsonString();
        });
        var writes = new System.Collections.Concurrent.ConcurrentQueue<(string Id, string Value)>();
        var task = new MesTask { Id = "snapshot", Name = "snapshot", CollectIntervalMs = 1000, ConfirmRewrite = false, Flow = new()
        {
            Nodes = [new() { Id = "trigger", Type = "Trigger" }, new() { Id = "upload", Type = "Upload", Payload = payload, Http = new() { Url = "http://routing.test/snapshot" } },
                new() { Id = "write", Type = "ResponseWriteback", WriteBack = new() { Confirm = false } }],
            Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }],
        } };
        await using (var engine = new MesUploadEngine(hub, new() { Enabled = true, Tasks = [task] },
            (id, value, _) => { writes.Enqueue((id, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!)); return Task.FromResult(true); }, NullLogger.Instance, httpMessageHandler: handler))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (writes.Count < 2) await Task.Delay(10, timeout.Token);
        }
        Assert.Contains(("Line.Plc.DB.Items[2].Value", "7"), writes);
        Assert.Contains(("Line.Plc.DB.Items[3].Value", "12"), writes);
    }

    private static MesPayload Payload(string json) => JsonSerializer.Deserialize<MesPayload>(json, ProjectStore.Options)!;
    private static Task<JsonElement> Upload(MesPayload payload, params (string Id, int Value)[] points) => Upload(payload, new MesHttp(), points);
    private static async Task<JsonElement> Upload(MesPayload payload, MesHttp http, params (string Id, int Value)[] points)
    {
        await using var hub = new DataHub();
        foreach (var (id, value) in points.Concat(new[] { ("Line.Plc.DB.Value", 2), ("Line.Plc.DB.Other", 3) }))
        { hub.Register(id, new() { DataType = IgwDataType.Int32 }); hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow)); }
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new(), (_, _, _) => Task.FromResult(true), NullLogger.Instance, httpMessageHandler: handler);
        http.Url = "http://routing.test/upload";
        await engine.TestRunAsync(new MesTask { Payload = payload, Steps = [new() { Http = http }] }, default);
        return JsonDocument.Parse(handler.Body).RootElement.Clone();
    }
    private sealed class Handler(Func<string, string>? respond = null) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Body = await request.Content!.ReadAsStringAsync(ct); return new(HttpStatusCode.OK) { Content = new StringContent(respond?.Invoke(Body) ?? "{}") }; }
    }
}
