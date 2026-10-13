using System.Reflection;
using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesDynamicArrayLengthTests
{
    private const string ArrayId = "Line.Plc.DB.Values";
    private const string CountId = "Line.Plc.DB.Count";

    private static MesPayload Payload(string mode = "Tag", string lengths = "[]") => JsonSerializer.Deserialize<MesPayload>(
        $$"""{"Mode":"DataRanges","DataRanges":[{"Scope":"Array","SourcePath":"{{ArrayId}}","OutputPath":"values","ArrayStart":[1],"ArrayLengthMode":"{{mode}}","ArrayLengthTagIds":["{{CountId}}"],"ArrayLengths":{{lengths}}}]}""")!;

    private static (IgwProject Project, TagDef Array, TagDef Count) Fixture()
    {
        var array = new TagDef { Name = "Values", Address = "DB1.DBW0", Access = TagAccess.ReadWrite, DataType = IgwDataType.Int16, Array = new ArrayDef
            { ElementType = IgwDataType.Int16, Dimensions = [4], LowerBounds = [1], ElementStrideBytes = 2 } };
        var count = new TagDef { Name = "Count", Address = "DB1.DBW8", DataType = IgwDataType.Int16 };
        var project = new IgwProject { Channels = [new ChannelDef { Name = "Line", DriverId = "Simulator", Devices =
            [new DeviceDef { Name = "Plc", Groups = [new TagGroupDef { Name = "DB", Tags = [array, count] }] }] }] };
        return (project, array, count);
    }

    private static JsonDocument Build(MesUploadEngine engine, MesPayload payload)
    {
        var method = typeof(MesUploadEngine).GetMethod("BuildBody", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(MesPayload), typeof(string), typeof(Dictionary<string, string>)], null)!;
        return JsonDocument.Parse((string)method.Invoke(engine, [payload, "", new Dictionary<string, string>()])!);
    }

    [Fact]
    public async Task Each_upload_reads_the_count_again_and_zero_produces_an_empty_array()
    {
        var (project, array, count) = Fixture();
        await using var hub = new DataHub();
        hub.Register(ArrayId, array); hub.Register(CountId, count);
        hub.Update(ArrayId, DataValue.GoodAt(PrimitiveArrayValue<short>.Create(array.Array!.Shape,
            new short[] { 10, 20, 30, 40 }), DateTime.UtcNow));
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project),
            deviceState: _ => ConnectionState.Connected);
        var payload = Payload();
        foreach (short value in new short[] { 2, 3, 0 })
        {
            hub.Update(CountId, DataValue.GoodAt(value, DateTime.UtcNow));
            using var body = Build(engine, payload);
            Assert.Equal(value, body.RootElement.GetProperty("values").GetArrayLength());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task Invalid_count_stops_payload_creation(int value)
    {
        var (project, _, count) = Fixture();
        await using var hub = new DataHub();
        hub.Register(CountId, count);
        hub.Update(CountId, DataValue.GoodAt(value, DateTime.UtcNow));
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project),
            deviceState: _ => ConnectionState.Connected);
        Assert.Throws<TargetInvocationException>(() => Build(engine, Payload()));
    }

    [Fact]
    public void Automatic_and_manual_configuration_cannot_be_used_together()
    {
        var (project, _, _) = Fixture();
        Assert.ThrowsAny<ArgumentException>(() => MesRangePayloadCompiler.Compile(Payload(lengths: "[2]"), MesDataRangeCatalog.Build(project)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad")]
    [InlineData("offline")]
    [InlineData("fraction")]
    [InlineData("overflow")]
    public async Task Unavailable_or_noninteger_count_is_a_diagnostic_error(string scenario)
    {
        var (project, _, count) = Fixture();
        await using var hub = new DataHub();
        hub.Register(CountId, count);
        if (scenario != "missing") hub.Update(CountId, scenario == "bad"
            ? DataValue.BadAt(StatusCode.BadNotConnected, DateTime.UtcNow)
            : DataValue.GoodAt(scenario switch { "fraction" => (object)1.5, "overflow" => ulong.MaxValue, _ => (short)2 }, DateTime.UtcNow));
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project),
            deviceState: _ => scenario == "offline" ? ConnectionState.Disconnected : ConnectionState.Connected);
        var error = Assert.Throws<TargetInvocationException>(() => Build(engine, Payload()));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains(CountId, error.InnerException!.Message);
    }

    [Fact]
    public void Validation_and_topology_preserve_automatic_configuration_and_reject_wrong_tag_types()
    {
        var (project, _, count) = Fixture();
        var payload = Payload();
        project.MesUpload.Tasks = [new MesTask { Name = "Auto", GroupRef = "Line.Plc.DB", Payload = payload }];
        var result = ProjectStore.Check(project);
        Assert.True(result.Ok, string.Join(";", result.Errors));
        Assert.Equal(0, MesTaskTopology.Reconcile(project).ClearedRanges);
        Assert.Empty(payload.DataRanges[0].ArrayLengths);
        Assert.Equal("Tag", payload.DataRanges[0].ArrayLengthMode);
        count.DataType = IgwDataType.Float;
        Assert.False(ProjectStore.Check(project).Ok);
    }

    [Fact]
    public void Multidimensional_resolution_reads_shared_count_once_and_keeps_input_unchanged()
    {
        var (project, array, _) = Fixture();
        array.Array!.Dimensions = [4, 3]; array.Array.LowerBounds = [1, -2];
        var payload = Payload();
        var range = payload.DataRanges[0];
        range.ArrayStart = [2, -1]; range.ArrayLengthTagIds = [CountId, CountId];
        int reads = 0;
        MesRangePayloadCompiler.Compile(payload, MesDataRangeCatalog.Build(project), _ => { reads++; return 2; });
        Assert.Equal(1, reads);
        Assert.Empty(range.ArrayLengths);
        Assert.Equal([2, -1], range.ArrayStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_is_collected_even_when_outside_the_upload_range(bool useScanRegistry)
    {
        var (project, array, count) = Fixture();
        await using var hub = new DataHub();
        hub.Register(ArrayId, array); hub.Register(CountId, count);
        var registry = useScanRegistry ? new ScanDemandRegistry(hub) : null;
        var task = new MesTask { Name = "Auto", Enabled = true, Payload = Payload(), CollectIntervalMs = 10000 };
        await using (var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "", Tasks = [task] },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project), scanDemands: registry))
        {
            if (registry is null) Assert.True(hub.IsPinned(CountId));
            else Assert.Contains(registry.Snapshot("Line.Plc").EffectivePeriods, point => point.TagId == CountId);
        }
        Assert.False(hub.IsPinned(CountId));
        if (registry is not null) Assert.False(registry.HasDemand("Line.Plc"));
    }

    [Fact]
    public async Task Manual_length_remains_fixed_without_a_readable_count()
    {
        var (project, array, _) = Fixture();
        await using var hub = new DataHub();
        hub.Register(ArrayId, array);
        hub.Update(ArrayId, DataValue.GoodAt(PrimitiveArrayValue<short>.Create(array.Array!.Shape, [10, 20, 30, 40]), DateTime.UtcNow));
        var payload = Payload("Fixed", "[2]"); payload.DataRanges[0].ArrayLengthTagIds = [];
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected);
        using var body = Build(engine, payload);
        Assert.Equal(2, body.RootElement.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public async Task Expanded_structure_array_uses_PLC_count_and_actual_start()
    {
        var (project, _, count) = Fixture();
        var root = project.Channels[0].Devices[0].Groups[0];
        root.Tags = [count];
        root.SubGroups = [new TagGroupDef { Name = "Values", SubGroups = Enumerable.Range(1, 4).Select(i =>
            new TagGroupDef { Name = i.ToString(), Tags = [new TagDef { Name = "Code", DataType = IgwDataType.Int16,
                Address = $"ns=3;s=\"Data\".\"Values\"[{i}].\"Code\"" }] }).ToList() }];
        await using var hub = new DataHub();
        hub.Register(CountId, count);
        foreach (var group in root.SubGroups[0].SubGroups)
        {
            var id = ArrayId + "." + group.Name + ".Code";
            hub.Register(id, group.Tags[0]); hub.Update(id, DataValue.GoodAt(short.Parse(group.Name), DateTime.UtcNow));
        }
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected);
        var payload = Payload(); payload.DataRanges[0].ArrayStart = [2];
        hub.Update(CountId, DataValue.GoodAt((short)2, DateTime.UtcNow));
        using var body = Build(engine, payload);
        var values = body.RootElement.GetProperty("values");
        Assert.Equal(2, values.GetArrayLength());
        Assert.Equal(2, values[0].GetProperty("Code").GetInt32());
        Assert.Equal(3, values[1].GetProperty("Code").GetInt32());
    }

    [Fact]
    public async Task Response_writeback_uses_the_uploaded_slice_when_PLC_count_changes_during_HTTP()
    {
        var (project, array, count) = Fixture();
        await using var hub = new DataHub();
        hub.Register(ArrayId, array); hub.Register(CountId, count);
        hub.Update(ArrayId, DataValue.GoodAt(PrimitiveArrayValue<short>.Create(array.Array!.Shape, [10, 20, 30, 40]), DateTime.UtcNow));
        hub.Update(CountId, DataValue.GoodAt((short)2, DateTime.UtcNow));
        var task = new MesTask { Id = "auto", Name = "auto", Enabled = true, CollectIntervalMs = 1000,
            Flow = new MesFlow { Nodes = [new MesNode { Id = "trigger", Type = "Trigger" },
                new MesNode { Id = "upload", Type = "Upload", Payload = Payload(), Http = new MesHttp { Url = "http://localhost/mes" } },
                new MesNode { Id = "write", Type = "ResponseWriteback", WriteBack = new MesWriteBack { Confirm = false, Retries = 0 } }],
                Edges = [new MesEdge { From = "trigger", To = "upload" }, new MesEdge { From = "upload", To = "write" }] } };
        var writes = new ConcurrentQueue<string>();
        using var handler = new CallbackHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("values").GetArrayLength());
            hub.Update(CountId, DataValue.GoodAt((short)0, DateTime.UtcNow));
            // Third response element must never extend the originally uploaded slice.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"httpStatusCode":200,"isSuccess":true,"backResult":{"values":[11,22,33]}}""") };
        });
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings { LogDbPath = "", Tasks = [task] },
            (id, _, _) => { writes.Enqueue(id); return Task.FromResult(true); }, NullLogger.Instance,
            catalog: MesDataRangeCatalog.Build(project), deviceState: _ => ConnectionState.Connected, httpMessageHandler: handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!engine.Snapshot().Single().Nodes.Any(n => n.Id == "write" && n.State == "ok"))
            await Task.Delay(10, timeout.Token);
        Assert.Equal([ArrayId + "[1]", ArrayId + "[2]"], writes.ToArray());
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
