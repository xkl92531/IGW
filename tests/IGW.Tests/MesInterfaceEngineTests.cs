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

public sealed class MesInterfaceEngineTests
{
    [Fact]
    public async Task Listener_maps_typed_upload_and_response_retries_same_body_and_ignores_unchanged_business_fields()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-interface-" + Guid.NewGuid());
        using var handler = new StreamHandler();
        await using var hub = new DataHub();
        hub.Register("Line.Plc.Result", new() { DataType = IgwDataType.Int32, Access = TagAccess.ReadWrite }, "Line.Plc");
        var writes = new ConcurrentQueue<object>();
        var task = new MesTask { Id = "listener", Name = "listener", Trigger = new() { Mode = "InterfaceEvent" },
            Listener = new() { Protocol = "SSE", Url = "http://local/events", IdField = "task", ChangeFields = ["data.sn"],
                InputMappings = [new() { SourcePath = "data", TargetPath = "product" }] },
            Flow = new() { Nodes = [new() { Id = "trigger", Type = "Trigger" },
                new() { Id = "upload", Type = "Upload", Http = new() { Url = "http://local/report", SuccessKey = "result",
                    SuccessValue = "0", JsonTemplate = "{\"sn\":\"${mapped.product.sn}\",\"count\":\"${input.data.count}\"}",
                    ResponseMappings = [new() { SourcePath = "reply.code", TargetPath = "result" }] } },
                new() { Id = "write", Type = "ResponseWriteback", WriteBack = new() { Confirm = false,
                    FromResponse = [new() { ResponseField = "result", TagId = "Line.Plc.Result" }] } }],
                Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }] } };
        try
        {
            await using var engine = new MesUploadEngine(hub, new() { Tasks = [task], LogDbPath = "" },
                (_, value, _) => { writes.Enqueue(value); return Task.FromResult(true); }, NullLogger.Instance,
                baseDir: directory, httpMessageHandler: handler);
            await handler.Feed("1", "{\"task\":\"1\",\"data\":{\"sn\":\"001\\\"X\",\"count\":4}}");
            await Until(() => writes.Count == 1);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(handler.Bodies.First(), handler.Bodies.Last());
            using var body = JsonDocument.Parse(handler.Bodies.First());
            Assert.Equal(4, body.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("001\"X", body.RootElement.GetProperty("sn").GetString());
            await handler.Feed("2", "{\"task\":\"2\",\"data\":{\"sn\":\"001\\\"X\",\"count\":9}}");
            await handler.Feed("3", "{\"task\":\"3\",\"data\":{\"sn\":\"NEXT\",\"count\":2}}");
            await Until(() => writes.Count == 2);
            Assert.Equal(3, handler.Bodies.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task Until(Func<bool> done)
    { using var timeout = new CancellationTokenSource(10000); while (!done()) await Task.Delay(10, timeout.Token); }

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly Pipe _pipe = new();
        public ConcurrentQueue<string> Bodies { get; } = new();
        public async Task Feed(string id, string data)
        { await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"id: {id}\ndata: {data}\n\n")); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/events")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_pipe.Reader.AsStream(leaveOpen: true)) };
                response.Content.Headers.ContentType = new("text/event-stream"); return response;
            }
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct));
            return new(Bodies.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"reply\":{\"code\":0}}") };
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _pipe.Writer.Complete(); _pipe.Reader.Complete(); } base.Dispose(disposing); }
    }
}
