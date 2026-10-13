using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesInterfaceRecoveryTests
{
    [Fact]
    public async Task WebSocket_baseline_and_unchanged_receipts_do_not_block_following_changes()
    {
        string directory = NewDirectory();
        var receipts = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                // All four messages are queued before receipt processing finishes.
                foreach (var message in new[]
                {
                    "{\"requestId\":\"baseline\",\"barcode\":\"A\"}",
                    "{\"requestId\":\"changed\",\"barcode\":\"B\"}",
                    "{\"requestId\":\"same\",\"barcode\":\"B\"}",
                    "{\"requestId\":\"following\",\"barcode\":\"C\"}",
                })
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
                var replies = new string[4];
                for (int index = 0; index < replies.Length; index++)
                    replies[index] = await Receive(socket, context.RequestAborted);
                receipts.TrySetResult(replies);
                await Receive(socket, context.RequestAborted);
            }
            catch (Exception error)
            {
                receipts.TrySetException(error);
            }
        });
        await using var hub = new DataHub();
        var task = new MesTask
        {
            Id = "receipt-recovery", Trigger = new() { Mode = "InterfaceEvent" },
            Listener = new()
            {
                Protocol = "WebSocket", Url = server.Url.Replace("http:", "ws:"), ReconnectMs = 100,
                IdField = "requestId", ChangeFields = ["barcode"], TriggerOnFirst = false,
                ResponseTemplate = "{\"requestId\":\"${input.requestId}\",\"barcode\":\"${input.barcode}\"}",
                IgnoreResponseTemplate = "{\"ignored\":\"${input.requestId}\"}",
            },
            Flow = new() { Nodes = [new() { Id = "trigger", Type = "Trigger" }] },
        };
        try
        {
            await using var engine = new MesUploadEngine(hub, new() { Tasks = [task], LogDbPath = "" },
                (_, _, _) => Task.FromResult(true), NullLogger.Instance, baseDir: directory);
            var replies = await receipts.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var baseline = JsonDocument.Parse(replies[0]);
            using var changed = JsonDocument.Parse(replies[1]);
            using var unchanged = JsonDocument.Parse(replies[2]);
            using var following = JsonDocument.Parse(replies[3]);
            Assert.Equal("baseline", baseline.RootElement.GetProperty("ignored").GetString());
            Assert.Equal("changed", changed.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("B", changed.RootElement.GetProperty("barcode").GetString());
            Assert.Equal("same", unchanged.RootElement.GetProperty("ignored").GetString());
            Assert.Equal("following", following.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("C", following.RootElement.GetProperty("barcode").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Linear_condition_keeps_message_pending_until_PLC_gate_allows_upload()
    {
        string directory = NewDirectory();
        int uploads = 0;
        await using var server = await Server.Start(async context =>
        {
            if (context.Request.Path == "/events")
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("id: 1\ndata: {\"requestId\":\"one\"}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { }
                return;
            }
            Interlocked.Increment(ref uploads);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"httpStatusCode\":200,\"isSuccess\":true}", context.RequestAborted);
        });
        await using var hub = new DataHub();
        hub.Register("Line.Plc.Gate", new() { DataType = IgwDataType.Int32 }, "Line.Plc");
        hub.Update("Line.Plc.Gate", DataValue.GoodAt(0, DateTime.UtcNow));
        var task = new MesTask
        {
            Id = "conditional-listener", Trigger = new() { Mode = "InterfaceEvent" },
            Listener = new() { Protocol = "SSE", Url = server.Url + "/events", IdField = "requestId", ChangeFields = ["requestId"], ReconnectMs = 100 },
            Steps = [new() { Name = "gated-upload", Conditions = [new() { TagId = "Line.Plc.Gate", Op = "Eq", Value = "1" }],
                Http = new() { Url = server.Url + "/report", JsonTemplate = "{\"id\":\"${input.requestId}\"}" } }],
        };
        try
        {
            await using var engine = new MesUploadEngine(hub, new() { Tasks = [task], LogDbPath = "" },
                (_, _, _) => Task.FromResult(true), NullLogger.Instance, baseDir: directory);
            using var journal = new MesInterfaceInbox(Path.Combine(directory, "mes-interface-inbox.db"));
            await Until(() => journal.Pending(task.Id)?.Id == "one");
            // Observe more than two retry intervals after durable receipt while the gate stays false.
            await Task.Delay(300);
            Assert.Equal(0, Volatile.Read(ref uploads));
            Assert.Equal("one", journal.Pending(task.Id)?.Id);
            hub.Update("Line.Plc.Gate", DataValue.GoodAt(1, DateTime.UtcNow));
            await Until(() => journal.Pending(task.Id) is null);
            Assert.Equal(1, Volatile.Read(ref uploads));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Pending_messages_block_behavior_changes_but_allow_resuming_same_configuration()
    {
        string directory = NewDirectory();
        try
        {
            using var inbox = new MesInterfaceInbox(Path.Combine(directory, "inbox.db"));
            inbox.Configure("task", "original-mapping");
            var pending = inbox.Append("task", "one", "{\"barcode\":\"A\"}", "cursor-1", 2);
            Assert.Throws<InvalidOperationException>(() => inbox.Configure("task", "different-mapping"));
            inbox.Configure("task", "original-mapping");
            Assert.Equal(pending.Sequence, inbox.Pending("task")?.Sequence);
            Assert.Equal("cursor-1", inbox.LastEventId("task"));
            inbox.Commit(pending.Sequence, "[\"A\"]", "{\"accepted\":true}");
            inbox.Configure("task", "different-mapping");
            Assert.Null(inbox.Pending("task"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Reopened_journal_preserves_pending_order_frozen_requests_baseline_and_duplicate_receipts()
    {
        string directory = NewDirectory();
        string path = Path.Combine(directory, "inbox.db");
        long pendingSequence;
        try
        {
            using (var inbox = new MesInterfaceInbox(path))
            {
                inbox.Configure("task", "mapping-v1");
                var first = inbox.Append("task", "done", "{\"barcode\":\"A\"}", "cursor-1", 2);
                inbox.Commit(first.Sequence, "[\"A\"]", "{\"state\":\"ready\"}");
                var next = inbox.Append("task", "pending", "{\"barcode\":\"B\"}", "cursor-2", 2);
                pendingSequence = next.Sequence;
                Assert.Equal("{\"requestId\":\"original\"}", inbox.FreezeBody(next.Sequence, "upload", "{\"requestId\":\"original\"}"));
            }
            using (var reopened = new MesInterfaceInbox(path))
            {
                reopened.Configure("task", "mapping-v1");
                Assert.Throws<InvalidOperationException>(() => reopened.Configure("task", "mapping-v2"));
                Assert.Equal(pendingSequence, reopened.Pending("task")?.Sequence);
                Assert.Equal("[\"A\"]", reopened.Baseline("task"));
                Assert.Equal("cursor-2", reopened.LastEventId("task"));
                Assert.Equal("{\"requestId\":\"original\"}", reopened.FreezeBody(pendingSequence, "upload", "{\"requestId\":\"changed-after-restart\"}"));
                var replay = reopened.Append("task", "done", "{\"barcode\":\"A\"}", "cursor-1", 2);
                Assert.True(replay.Completed);
                Assert.Equal("{\"state\":\"ready\"}", replay.Ack);
                Assert.Equal("cursor-2", reopened.LastEventId("task"));
                Assert.Throws<InvalidDataException>(() => reopened.Append("task", "done", "{\"barcode\":\"different\"}", null, 2));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-interface-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task Until(Func<bool> done)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!done()) await Task.Delay(10, deadline.Token);
    }

    private static async Task<string> Receive(WebSocket socket, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Connection closed before receipt.");
            output.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class Server(WebApplication app) : IAsyncDisposable
    {
        public string Url => app.Urls.Single();
        public static async Task<Server> Start(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            app.Run(handler);
            await app.StartAsync();
            return new(app);
        }
        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(deadline.Token);
            await app.DisposeAsync();
        }
    }
}
