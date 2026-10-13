using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesParameterDictionaryWritebackTests
{
    private const string Input = "Line.Plc.DB.Input";
    private const string Returned = "Line.Plc.DB.Returned";

    [Theory]
    [InlineData("Name", "direct")]
    [InlineData("Name", "data")]
    [InlineData("Name", "value")]
    [InlineData("Name", "backResult")]
    [InlineData("Name", "nested")]
    [InlineData("ItemId", "direct")]
    [InlineData("ItemId", "data")]
    [InlineData("ItemId", "value")]
    [InlineData("ItemId", "backResult")]
    [InlineData("ItemId", "nested")]
    public async Task ResponseWriteback_matches_registered_dictionary_tags_absent_from_upload_fields(
        string fieldKeyMode, string wrapper)
    {
        await using var hub = Hub(Input, Returned);
        const string dictionary = "{\"parameterDic\":{\"Line.Plc.DB.Returned\":42}}";
        string response = wrapper switch
        {
            "direct" => dictionary,
            "nested" => "{\"value\":{\"backResult\":{\"data\":" + dictionary + "}}}",
            _ => "{\"" + wrapper + "\":" + dictionary + "}",
        };
        var handler = new ResponseHandler(response);
        var writes = new ConcurrentQueue<(string Id, string Value)>();
        var task = FlowTask(fieldKeyMode);

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal([(Returned, "42")], writes.ToArray());
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.DoesNotContain(Returned, request.RootElement.GetRawText());
    }

    [Fact]
    public async Task ResponseWriteback_uses_configured_response_dictionary_name()
    {
        await using var hub = Hub(Input, Returned);
        var task = FlowTask("Name");
        task.Flow!.Nodes.Single(node => node.Id == "upload").Http.ParameterDicName = "writeValues";
        var handler = new ResponseHandler("""{"data":{"writeValues":{"Line.Plc.DB.Returned":42}}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal([(Returned, "42")], writes.ToArray());
    }

    [Fact]
    public async Task ResponseWriteback_explicit_items_override_their_targets_and_keep_other_dictionary_tags()
    {
        const string custom = "Line.Plc.DB.Custom";
        const string other = "Line.Plc.DB.Other";
        await using var hub = Hub(Input, Returned, custom, other);
        var task = FlowTask("Name");
        task.Flow!.Nodes.Single(node => node.Id == "write").WriteBack = new()
        {
            Confirm = false,
            Manual = [new() { TagId = Returned, Value = "7" }],
            FromResponse = [new() { TagId = custom, ResponseField = "diagnostic" }],
        };
        var handler = new ResponseHandler("""
            {"diagnostic":9,"parameterDic":{"Line.Plc.DB.Returned":42,"Line.Plc.DB.Custom":88,"Line.Plc.DB.Other":43}}
            """);
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal(3, writes.Count);
        Assert.Contains((Returned, "7"), writes);
        Assert.Contains((custom, "9"), writes);
        Assert.Contains((other, "43"), writes);
        Assert.DoesNotContain(writes, write => write.Value is "42" or "88");
    }

    [Theory]
    [InlineData("Condition")]
    [InlineData("Branch")]
    public async Task Dictionary_tags_are_not_written_when_the_user_route_does_not_reach_response_writeback(string gateType)
    {
        await using var hub = Hub(Input, Returned);
        var task = FlowTask("Name");
        task.Flow!.Nodes.Insert(2, new MesNode
        {
            Id = "gate", Type = gateType,
            Branches = [new() { Conditions = [new() { Source = "Response", Path = "allowed", Op = "Eq", Value = "true" }] }],
            JudgeField = "allowed", JudgeOp = "Eq", JudgeValue = "true",
        });
        task.Flow.Edges =
        [
            new() { From = "trigger", To = "upload" },
            new() { From = "upload", To = "gate" },
            new() { From = "gate", FromPort = gateType == "Condition" ? "b0" : "success", To = "write" },
        ];
        var handler = new ResponseHandler("""{"allowed":false,"parameterDic":{"Line.Plc.DB.Returned":42}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "gate", gateType == "Condition" ? "wait" : "ok");

        Assert.NotNull(handler.RequestBody);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task ResponseWriteback_ignores_unknown_short_case_mismatched_and_non_scalar_dictionary_entries()
    {
        const string duplicateShortName = "Other.Plc.DB.Returned";
        const string nil = "Line.Plc.DB.Null";
        const string obj = "Line.Plc.DB.Object";
        const string array = "Line.Plc.DB.Array";
        const string logicalArray = "Line.Plc.DB.LogicalArray";
        await using var hub = Hub(Input, Returned, duplicateShortName, nil, obj, array);
        hub.Register(logicalArray, new() { DataType = IgwDataType.Int16, IsArray = true, Access = TagAccess.ReadWrite });
        var handler = new ResponseHandler("""
            {"parameterDic":{"Line.Plc.DB.Returned":42,"Returned":90,"line.plc.db.returned":91,
              "Line.Plc.DB.Unknown":92,"Line.Plc.DB.Null":null,"Line.Plc.DB.Object":{"x":1},
              "Line.Plc.DB.Array":[1,2],"Line.Plc.DB.LogicalArray":93}}
            """);
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, FlowTask("Name"), handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal([(Returned, "42")], writes.ToArray());
    }

    [Fact]
    public async Task Legacy_writeback_keeps_explicit_only_semantics_for_response_dictionaries()
    {
        await using var hub = Hub(Input, Returned);
        var task = FlowTask("Name");
        var write = task.Flow!.Nodes.Single(node => node.Id == "write");
        write.Type = "Writeback";
        write.WriteBack.Manual = [new() { TagId = Input, Value = "7" }];
        var handler = new ResponseHandler("""{"parameterDic":{"Line.Plc.DB.Returned":42}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal([(Input, "7")], writes.ToArray());
    }

    [Fact]
    public async Task ItemId_dictionary_mapping_keeps_inverse_scale_and_writes_each_target_once()
    {
        await using var hub = Hub(Input, Returned);
        var task = FlowTask("ItemId");
        task.Flow!.Nodes.Single(node => node.Id == "upload").Payload.Fields[0].Scale = 2;
        var handler = new ResponseHandler("""{"data":{"parameterDic":{"Line.Plc.DB.Input":20,"Line.Plc.DB.Returned":42}}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();

        await using (var engine = Engine(hub, task, handler, writes))
            await UntilNode(engine, "write", "ok");

        Assert.Equal(2, writes.Count);
        Assert.Contains((Input, "10"), writes);
        Assert.Contains((Returned, "42"), writes);
    }

    [Fact]
    public void Scalar_dictionary_resolution_does_not_enumerate_the_complete_tag_catalog()
    {
        using var response = JsonDocument.Parse("""{"parameterDic":{"Line.Plc.DB.Returned":42}}""");
        var tags = Enumerable.Range(0, 1).Select<int, string>(_ =>
            throw new InvalidOperationException("The complete tag catalog must not be enumerated for scalar resolution."));

        var plan = MesResponseWritePlanner.Plan(new MesPayload(), response.RootElement, "parameterDic", tags,
            isRegisteredScalarTag: id => id == Returned);

        Assert.Equal([new MesResponseWritePlanItem(Returned, "42")], plan);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("ItemId")]
    public async Task Unmapped_read_only_dictionary_tags_do_not_fail_writable_dictionary_writeback(string fieldKeyMode)
    {
        const string readOnly = "Line.Plc.DB.ReadOnly";
        await using var hub = Hub(Input, Returned);
        hub.Register(readOnly, new() { DataType = IgwDataType.Int16, Access = TagAccess.Read });
        hub.Update(readOnly, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var handler = new ResponseHandler("""{"parameterDic":{"Line.Plc.DB.ReadOnly":93,"Line.Plc.DB.Returned":42}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();
        string state;

        await using (var engine = new MesUploadEngine(hub,
            new MesUploadSettings { Enabled = true, Tasks = [FlowTask(fieldKeyMode)], LogDbPath = "" },
            (id, value, _, _) =>
            {
                writes.Enqueue((id, Convert.ToString(value, CultureInfo.InvariantCulture)!));
                return Task.FromResult(hub.TryGetDef(id, out var definition) && definition.Access == TagAccess.Read
                    ? StatusCode.BadNotWritable : StatusCode.Good);
            }, NullLogger.Instance, httpMessageHandler: handler))
        {
            await UntilNode(engine, "write", "ok", "fail");
            state = engine.Snapshot().Single().Nodes.Single(node => node.Id == "write").State;
        }

        Assert.Equal("ok", state);
        Assert.Equal([(Returned, "42")], writes.ToArray());
    }

    [Fact]
    public async Task Configured_read_only_payload_mapping_keeps_its_existing_write_failure_semantics()
    {
        await using var hub = Hub(Returned);
        hub.Register(Input, new() { DataType = IgwDataType.Int16, Access = TagAccess.Read });
        hub.Update(Input, DataValue.GoodAt((short)0, DateTime.UtcNow));
        var handler = new ResponseHandler("""{"parameterDic":{"Line.Plc.DB.Input":11,"Line.Plc.DB.Returned":42}}""");
        var writes = new ConcurrentQueue<(string Id, string Value)>();
        string state;

        await using (var engine = new MesUploadEngine(hub,
            new MesUploadSettings { Enabled = true, Tasks = [FlowTask("ItemId")], LogDbPath = "" },
            (id, value, _, _) =>
            {
                writes.Enqueue((id, Convert.ToString(value, CultureInfo.InvariantCulture)!));
                return Task.FromResult(hub.TryGetDef(id, out var definition) && definition.Access == TagAccess.Read
                    ? StatusCode.BadNotWritable : StatusCode.Good);
            }, NullLogger.Instance, httpMessageHandler: handler))
        {
            await UntilNode(engine, "write", "ok", "fail");
            state = engine.Snapshot().Single().Nodes.Single(node => node.Id == "write").State;
        }

        Assert.Equal("fail", state);
        Assert.Equal(2, writes.Count);
        Assert.Contains((Input, "11"), writes);
        Assert.Contains((Returned, "42"), writes);
    }

    private static DataHub Hub(params string[] tagIds)
    {
        var hub = new DataHub();
        foreach (string id in tagIds)
        {
            hub.Register(id, new() { Name = id.Split('.').Last(), DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
            hub.Update(id, DataValue.GoodAt((short)0, DateTime.UtcNow));
        }
        return hub;
    }

    private static MesTask FlowTask(string fieldKeyMode) => new()
    {
        Id = "parameter-dictionary", Name = "parameter-dictionary", Enabled = true,
        CollectIntervalMs = 100, ConfirmRewrite = false,
        Flow = new()
        {
            Nodes =
            [
                new() { Id = "trigger", Type = "Trigger" },
                new()
                {
                    Id = "upload", Type = "Upload", Http = new() { Url = "http://dictionary.test/upload" },
                    Payload = new() { FieldKeyMode = fieldKeyMode, Fields = [new() { TagId = Input, FieldName = "input" }] },
                },
                new() { Id = "write", Type = "ResponseWriteback", WriteBack = new() { Confirm = false } },
            ],
            Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }],
        },
    };

    private static MesUploadEngine Engine(DataHub hub, MesTask task, ResponseHandler handler,
        ConcurrentQueue<(string Id, string Value)> writes) => new(hub,
        new MesUploadSettings { Enabled = true, Tasks = [task], LogDbPath = "" },
        (id, value, _) =>
        {
            writes.Enqueue((id, Convert.ToString(value, CultureInfo.InvariantCulture)!));
            return Task.FromResult(true);
        }, NullLogger.Instance, httpMessageHandler: handler);

    private static async Task UntilNode(MesUploadEngine engine, string id, params string[] states)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!states.Contains(engine.Snapshot().Single().Nodes.SingleOrDefault(node => node.Id == id)?.State))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
