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

/// <summary>
/// 固定值：上报节点里的固定值可选类型，和点位字段在 JSON 同一层；
/// 「固定值」节点接在上报前面时合并进上报 JSON 顶层(不包进结构体)，后面没有上报时当作接口返回，由回写写回 PLC。
/// </summary>
public sealed class MesFixedValueTests
{
    private const string Result = "Line.Plc.DB.Result";
    private const string Barcode = "Line.Plc.DB.Barcode";
    private const string Msg = "Line.Plc.DB.Msg";

    [Theory]
    [InlineData(MesFixedValueTypes.Text, "OP051", "\"OP051\"")]
    [InlineData(MesFixedValueTypes.Int, "3", "3")]
    [InlineData(MesFixedValueTypes.Decimal, "1.50", "1.50")]
    [InlineData(MesFixedValueTypes.Bool, "true", "true")]
    [InlineData(MesFixedValueTypes.Bool, "0", "false")]
    public void Fixed_values_are_written_with_their_type(string type, string value, string json)
    {
        var item = new MesFixedValue { Name = "x", DataType = type, Value = value };
        Assert.Equal(json, MesFixedValueTypes.ToJson(item)!.ToJsonString());
    }

    [Theory]
    [InlineData(MesFixedValueTypes.Int, "1.5")]
    [InlineData(MesFixedValueTypes.Decimal, "abc")]
    [InlineData(MesFixedValueTypes.Bool, "yes")]
    public void Wrong_values_are_refused_instead_of_being_sent(string type, string value) =>
        Assert.Throws<InvalidDataException>(() => MesFixedValueTypes.ToJson(new MesFixedValue { Name = "x", DataType = type, Value = value }));

    [Fact]
    public void Names_must_be_present_and_unique()
    {
        Assert.Throws<InvalidDataException>(() => MesFixedValueTypes.Validate([new MesFixedValue { Name = " " }]));
        Assert.Throws<InvalidDataException>(() => MesFixedValueTypes.Validate([new MesFixedValue { Name = "a" }, new MesFixedValue { Name = "a" }]));
    }

    [Fact]
    public async Task Upload_node_fixed_values_sit_next_to_the_point_fields()
    {
        await using var hub = Hub();
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), NoWrite, NullLogger.Instance, httpMessageHandler: handler);
        var task = new MesTask
        {
            Payload = new MesPayload
            {
                Mode = MesPayloadModes.FieldMapping,
                Fields = [new MesField { TagId = Barcode }],
                Append = new() { ["Legacy"] = "A" },
                FixedValues =
                [
                    new MesFixedValue { Name = "WorkStation", Value = "OP051" },
                    new MesFixedValue { Name = "LineNo", DataType = MesFixedValueTypes.Int, Value = "3" },
                    new MesFixedValue { Name = "Passed", DataType = MesFixedValueTypes.Bool, Value = "true" },
                ],
            },
            Steps = [new MesStep { Http = new MesHttp { Url = "http://fixed.test/upload" } }],
        };

        await engine.TestRunAsync(task, default);

        Assert.Equal("{\"Barcode\":123,\"Legacy\":\"A\",\"WorkStation\":\"OP051\",\"LineNo\":3,\"Passed\":true}", handler.Bodies.Single());
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("ItemId")]
    public async Task Fixed_data_node_before_an_upload_is_sent_with_the_db_json_at_the_top_level(string fieldKeyMode)
    {
        await using var hub = Hub();
        var handler = new Handler();
        var engine = Engine(hub, handler, NoWrite, Flow(
            [Fixed(new() { Name = "Result", DataType = MesFixedValueTypes.Int, Value = "1" }, new() { Name = "Msg", Value = "OK" }),
             new MesNode
             {
                 Id = "upload", Type = "Upload", Http = new MesHttp { Url = "http://fixed.test/upload" },
                 Payload = new MesPayload
                 {
                     Mode = MesPayloadModes.FieldMapping, FieldKeyMode = fieldKeyMode,
                     Fields = [new MesField { TagId = Barcode, ParameterCode = fieldKeyMode == "ItemId" ? Barcode : null }, new MesField { TagId = Result, ParameterCode = fieldKeyMode == "ItemId" ? Result : null }],
                     Structs = [new MesStruct { Kind = "Object", BaseGroup = "Line.Plc.DB.Udt", AliasName = "Udt", Members = [new MesField { TagId = "A" }] }],
                 },
             }]));
        try
        {
            await Until(() => !handler.Bodies.IsEmpty);
            using var json = JsonDocument.Parse(handler.Bodies.First());
            var root = json.RootElement;
            if (fieldKeyMode == "ItemId")
            {
                Assert.Equal(123, root.GetProperty("parameterDic").GetProperty(Barcode).GetInt32());
                Assert.Equal(0, root.GetProperty("parameterDic").GetProperty(Result).GetInt32());
            }
            else Assert.Equal(123, root.GetProperty("Barcode").GetInt32());
            Assert.Equal(1, root.GetProperty("Result").GetInt32());          // 只覆盖外层同名字段
            Assert.Equal("OK", root.GetProperty("Msg").GetString());         // 和 DB 字段在同一层
            Assert.False(root.GetProperty("Udt").TryGetProperty("Msg", out _)); // 不包进结构体
        }
        finally { await engine.DisposeAsync(); }
    }

    [Theory]
    [InlineData("ItemId", "FieldMapping", "parameterDic", true)]
    [InlineData("ItemId", "FieldMapping", " PARAMETERDIC ", true)]
    [InlineData("Name", "FieldMapping", "parameterDic", false)]
    [InlineData("ItemId", "DataRanges", "parameterDic", false)]
    public async Task Fixed_data_cannot_replace_the_ItemId_dictionary_before_http(string fieldKeyMode, string mode, string name, bool blocked)
    {
        await using var hub = Hub();
        var handler = new Handler();
        var flow = Flow([Fixed(new MesFixedValue { Name = name, Value = "replacement" }), new MesNode
        {
            Id = "upload", Type = "Upload", Http = new() { Url = "http://fixed.test/upload" },
            Payload = new()
            {
                Mode = mode, FieldKeyMode = fieldKeyMode, Fields = [new() { TagId = Result, ParameterCode = fieldKeyMode == "ItemId" ? Result : null }],
                DataRanges = mode == "DataRanges" ? [new() { SourcePath = Result, Scope = "Tag" }] : [],
            },
        }]);
        var project = new IgwProject
        {
            Channels = [new() { Name = "Line", Devices = [new() { Name = "Plc", Groups = [new()
            {
                Name = "DB", Tags = [new() { Name = "Result", DataType = IgwDataType.Int16 }],
            }] }] }],
        };
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings
        {
            Enabled = true, Tasks = [new() { Id = "t", Name = "t", CollectIntervalMs = 1000, Flow = flow }],
        }, NoWrite, NullLogger.Instance, httpMessageHandler: handler, catalog: MesDataRangeCatalog.Build(project));
        await Until(() => engine.Snapshot().Single().Nodes.SingleOrDefault(n => n.Id == "upload")?.State is "ok" or "fail");

        var upload = engine.Snapshot().Single().Nodes.Single(n => n.Id == "upload");
        if (blocked)
        {
            Assert.Empty(handler.Bodies);
            Assert.Equal("fail", upload.State);
            Assert.Contains("parameterDic", upload.Info, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal("ok", upload.State);
            using var json = JsonDocument.Parse(handler.Bodies.First());
            Assert.Equal("replacement", json.RootElement.GetProperty("parameterDic").GetString());
        }
    }

    [Fact]
    public async Task Fixed_data_node_without_an_upload_is_written_back_to_the_plc_by_the_writeback_node()
    {
        await using var hub = Hub();
        var written = new ConcurrentDictionary<string, object>();
        var handler = new Handler();
        var engine = Engine(hub, handler, (id, value, _) => { written[id] = value; return Task.FromResult(true); }, Flow(
            [Fixed(new() { Name = "Result", DataType = MesFixedValueTypes.Int, Value = "1", TagId = Result },
                   new() { Name = "Msg", Value = "OK", TagId = Msg },
                   new() { Name = "NotWritten", Value = "x" }),
             new MesNode { Id = "judge", Type = "Branch", JudgeField = "Msg", JudgeValue = "OK" },
             new MesNode { Id = "write", Type = "ResponseWriteback" }],
            judgeSuccessTo: "write"));
        try
        {
            await Until(() => written.Count >= 2);
            Assert.Equal("1", Convert.ToString(written[Result]));
            Assert.Equal("OK", Convert.ToString(written[Msg]));
            Assert.Empty(handler.Bodies);                                   // 没有调任何接口
            var node = Assert.Single(engine.Snapshot()).Nodes.Single(n => n.Id == "judge");
            Assert.Contains("走 success", node.Info);                        // 判成败按固定值判断
        }
        finally { await engine.DisposeAsync(); }
    }

    private static MesNode Fixed(params MesFixedValue[] values) => new() { Id = "fixed", Type = "FixedData", FixedValues = [.. values] };

    private static MesFlow Flow(MesNode[] nodes, string? judgeSuccessTo = null)
    {
        var all = new List<MesNode> { new() { Id = "trigger", Type = "Trigger" } };
        all.AddRange(nodes);
        var edges = new List<MesEdge>();
        for (int i = 0; i < all.Count - 1; i++)
        {
            if (all[i].Type == "Branch")
            {
                edges.Add(new MesEdge { From = all[i].Id, FromPort = "success", To = judgeSuccessTo! });
                break;
            }
            edges.Add(new MesEdge { From = all[i].Id, To = all[i + 1].Id });
        }
        return new MesFlow { Nodes = all, Edges = edges };
    }

    private static MesUploadEngine Engine(DataHub hub, Handler handler, Func<string, object, CancellationToken, Task<bool>> write, MesFlow flow) =>
        new(hub, new MesUploadSettings
        {
            Enabled = true, LogDbPath = "",
            Tasks = [new MesTask { Id = "t", Name = "t", Enabled = true, CollectIntervalMs = 100, Flow = flow }],
        }, write, NullLogger.Instance, httpMessageHandler: handler);

    private static readonly Func<string, object, CancellationToken, Task<bool>> NoWrite = (_, _, _) => Task.FromResult(true);

    private static DataHub Hub()
    {
        var hub = new DataHub();
        void Add(string id, IgwDataType type, object value)
        {
            hub.Register(id, new TagDef { Name = id.Split('.').Last(), DataType = type, Access = TagAccess.ReadWrite, StringLength = 20 });
            hub.Update(id, DataValue.GoodAt(value, DateTime.UtcNow));
        }
        Add(Barcode, IgwDataType.Int32, 123);
        Add(Result, IgwDataType.Int16, (short)0);
        Add(Msg, IgwDataType.String, "");
        Add("Line.Plc.DB.Udt.A", IgwDataType.Int16, (short)5);
        return hub;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Enqueue(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}", Encoding.UTF8, "application/json") };
        }
    }
}
