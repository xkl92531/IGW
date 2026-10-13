using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>流程条件节点的取值来源与比较语义；动作节点统一用回写，只看条件是否放行。</summary>
public sealed class MesFlowCommandTests
{
    private const string Target = "Line.Plc.Ack", Source = "Line.Plc.Value";

    [Theory]
    [InlineData("Tag", "Eq", "9", true)]
    [InlineData("Tag", "Ne", "9", false)]
    [InlineData("Input", "Ne", "9", true)]
    [InlineData("Response", "Ne", "9", true)]
    public async Task Unmatched_missing_and_bad_quality_conditions_never_act(string source, string op, string expected, bool good)
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(7, good);
        var task = Definition(new() { Source = source, TagId = Source, Path = "missing", Op = op, Value = expected });
        int writes = 0;
        await using var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); });
        await Until(() => engine.Snapshot().Single().Nodes.Any(n => n.Id == "trigger" && n.Hits >= 3));
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(7)]
    [InlineData("7")]
    public async Task PLC_scalar_number_or_string_can_match(object value)
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(value);
        var writes = new ConcurrentQueue<object>();
        var task = Definition(new() { TagId = Source, Value = "7" });
        await using var engine = Engine(hub, task, directory.Path, (_, raw, _) => { writes.Enqueue(raw); return Task.FromResult(true); });
        await Until(() => writes.Count > 0);
        Assert.Equal("1", writes.First());
    }

    [Theory]
    [InlineData("Input", "data.kind")]
    [InlineData("Mapped", "product.kind")]
    public async Task Listener_conditions_gate_actions_by_message_fields(string source, string path)
    {
        using var directory = new TempDirectory();
        await using var hub = Hub("unused");
        using var handler = new StreamHandler();
        var task = Definition(new() { Source = source, Path = path, Value = "7" });
        task.Trigger.Mode = "InterfaceEvent";
        task.Listener = new() { Protocol = "SSE", Url = "http://local/events", IdField = "id", ChangeFields = ["id"], ReconnectMs = 100,
            InputMappings = [new() { SourcePath = "data", TargetPath = "product" }] };
        int writes = 0;
        await using var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, handler);
        await handler.Feed("{\"id\":\"ignored\",\"data\":{\"kind\":8}}");
        await handler.Feed("{\"id\":\"accepted\",\"data\":{\"kind\":7}}");
        // 消息按收件顺序处理：出现第一次回写时被忽略的那条已经处理完，之后不应再有回写。
        await Until(() => Volatile.Read(ref writes) >= 1);
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref writes));
    }

    [Theory]
    [InlineData("Response", "product.program")]
    [InlineData("Variable", "program")]
    public async Task HTTP_response_and_captured_variable_are_current_execution_sources(string source, string path)
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(99);
        var task = Definition(new() { Source = source, Path = path, Value = "7" });
        var upload = new MesNode { Id = "upload", Type = "Upload", Http = new() { Url = "http://local/report" },
            Captures = [new() { ResponseField = "product.program", VarName = "program" }] };
        task.Flow!.Nodes.Add(upload); task.Flow.Edges[0].To = "upload"; task.Flow.Edges.Add(new() { From = "upload", To = "condition" });
        int writes = 0;
        using var handler = new Handler("{\"httpStatusCode\":200,\"isSuccess\":true,\"product\":{\"program\":7}}");
        await using var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, handler);
        await Until(() => Volatile.Read(ref writes) > 0);
    }

    [Fact]
    public void Old_condition_defaults_survive_unknown_sources_disable_the_task_and_empty_paths_are_rejected()
    {
        var old = JsonSerializer.Deserialize<MesCondition>("{\"TagId\":\"PLC.Value\",\"Value\":\"1\"}")!;
        Assert.Equal("Tag", old.Source); Assert.Equal("", old.Path);
        MesFlowCommandValidation.Validate(Definition(new() { TagId = Source, Value = "1" }));
        var unknown = Definition(new() { Source = "Unknown", Path = "x" });
        MesFlowCommandValidation.Validate(unknown);   // 不拒绝加载，由 MES 引擎停用并给出原因
        Assert.Equal("含本版本不支持的配置(取值来源 Unknown)，任务已停用", MesUnsupportedConfig.Describe(unknown));
        Assert.Throws<InvalidDataException>(() => MesFlowCommandValidation.Validate(Definition(new() { Source = "Input", Path = " " })));
    }

    [Fact]
    public async Task Second_HTTP_call_cannot_reuse_first_response_fields_or_capture()
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(7);
        var task = Definition(new() { Source = "Response", Path = "product.program", Op = "Ne", Value = "8" });
        task.Flow!.Nodes.Add(new() { Id = "first", Type = "Upload", Http = new() { Url = "http://local/first" } });
        task.Flow.Nodes.Add(new() { Id = "second", Type = "Upload", Http = new() { Url = "http://local/second" } });
        task.Flow.Edges[0].To = "first";
        task.Flow.Edges.Add(new() { From = "first", To = "second" });
        task.Flow.Edges.Add(new() { From = "second", To = "condition" });
        int writes = 0;
        using var handler = new ResponseSequenceHandler();
        await using var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, handler);
        await Until(() => handler.Requests >= 4);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Theory]
    [InlineData("Input", "kind")]
    [InlineData("Mapped", "kind")]
    public async Task Listener_Changed_compares_committed_messages_and_restores_baseline_after_restart(string source, string path)
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(7);
        var task = Definition(new() { Source = source, Path = path, Op = "Changed" });
        task.Trigger.Mode = "InterfaceEvent";
        task.Listener = new() { Protocol = "SSE", Url = "http://local/events", IdField = "id", ChangeFields = ["id"], ReconnectMs = 100 };
        int writes = 0;
        using (var handler = new StreamHandler())
        await using (var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, handler))
        {
            await handler.Feed("{\"id\":\"1\",\"kind\":7}");
            await handler.Feed("{\"id\":\"2\",\"kind\":8}");
            await Until(() => Volatile.Read(ref writes) == 1);
            await Until(() => engine.Snapshot().Single().Nodes.Any(n => n.Id == "trigger" && n.Info.Contains("下一次字段变化")));
        }
        using (var handler = new StreamHandler())
        await using (var engine = Engine(hub, task, directory.Path, (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); }, handler))
        {
            await handler.Feed("{\"id\":\"3\",\"kind\":9}");
            await Until(() => Volatile.Read(ref writes) == 2);
        }
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task Native_boolean_condition_uses_typed_comparison()
    {
        using var directory = new TempDirectory();
        await using var hub = Hub(true);
        int writes = 0;
        await using var engine = Engine(hub, Definition(new() { TagId = Source, Value = "true" }), directory.Path,
            (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); });
        await Until(() => Volatile.Read(ref writes) == 1);
    }

    private static MesUploadEngine Engine(DataHub hub, MesTask task, string directory,
        Func<string, object, CancellationToken, Task<bool>> write, HttpMessageHandler? handler = null) =>
        new(hub, new() { Tasks = [task], LogDbPath = "" }, write, NullLogger.Instance, baseDir: directory, httpMessageHandler: handler);

    private static MesTask Definition(MesCondition condition)
    {
        var task = new MesTask { Id = "conditions", CollectIntervalMs = 30, Flow = new() { Nodes = [new() { Id = "trigger", Type = "Trigger" },
            new() { Id = "action", Type = "Writeback", WriteBack = new() { Manual = [new() { TagId = Target, Value = "1" }] } },
            new() { Id = "condition", Type = "Condition", Branches = [new() { Conditions = [condition] }] }],
            Edges = [new() { From = "trigger", To = "condition" }, new() { From = "condition", FromPort = "b0", To = "action" }] } };
        return task;
    }

    private static DataHub Hub(object value, bool good = true)
    {
        var hub = new DataHub();
        hub.Register(Target, new() { DataType = IgwDataType.Int32, Access = TagAccess.ReadWrite }, "Line.Plc");
        hub.Register(Source, new() { DataType = IgwDataType.String }, "Line.Plc");
        hub.Update(Source, new(value, good ? StatusCode.Good : StatusCode.Bad, DateTime.UtcNow, DateTime.UtcNow));
        return hub;
    }

    private static async Task Until(Func<bool> done)
    { using var timeout = new CancellationTokenSource(6000); while (!done()) await Task.Delay(10, timeout.Token); }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "igw-conditions-" + Guid.NewGuid());
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }

    private sealed class ResponseSequenceHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath == "/first"
                ? "{\"httpStatusCode\":200,\"isSuccess\":true,\"product\":{\"program\":7}}" : "{\"httpStatusCode\":200,\"isSuccess\":true}") });
        }
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly Pipe _pipe = new();
        public async Task Feed(string data) => await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: " + data + "\n\n"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_pipe.Reader.AsStream(leaveOpen: true)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _pipe.Writer.Complete(); _pipe.Reader.Complete(); } base.Dispose(disposing); }
    }
}
