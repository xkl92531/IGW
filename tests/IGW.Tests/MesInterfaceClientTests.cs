using System.Net.WebSockets;
using System.Text;
using IGW.Abstractions;
using IGW.Northbound.Mes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class MesInterfaceClientTests
{
    [Fact]
    public async Task SignalR_subscribes_receives_native_json_and_sends_hub_reply()
    {
        var state = new HubState();
        await using var server = await HubServer.Start(state);
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var messages = new List<MesInterfaceMessage>();
        var run = client.RunAsync(server.Settings(), null, "[\"line-1\"]", async (message, ct) =>
        {
            messages.Add(message);
            await client.SendAsync("[\"r-1\",true]", ct);
        }, cancel.Token);
        Assert.Equal(("r-1", true), await state.Replied.Task.WaitAsync(cancel.Token));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("line-1", state.Subscription);
        Assert.Equal("secret", state.Header);
        Assert.Equal("{\"requestId\":\"r-1\",\"barcode\":\"中文\"}", Assert.Single(messages).Data);
    }

    [Fact]
    public async Task SignalR_registers_handler_before_start_and_surfaces_durable_callback_failure()
    {
        var state = new HubState { SendOnConnect = true };
        await using var server = await HubServer.Start(state);
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings(); settings.SubscribeMethod = "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = new IOException("inbox unavailable");
        var error = await Assert.ThrowsAsync<IOException>(() => client.RunAsync(settings, null, "unused{legacy-template", (_, _) => throw failure, deadline.Token));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task SignalR_unwraps_json_string_arguments_and_rejects_large_payloads()
    {
        var state = new HubState { SendOnConnect = true, SendString = true };
        await using var server = await HubServer.Start(state);
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings(); settings.SubscribeMethod = "";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        MesInterfaceMessage? received = null;
        var run = client.RunAsync(settings, null, "", (message, _) =>
        {
            received = message;
            cancel.Cancel();
            return Task.CompletedTask;
        }, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("{\"requestId\":\"r-string\"}", received?.Data);
        settings.MaxMessageBytes = 8;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(settings, null, "", NoMessage, deadline.Token));
    }

    [Fact]
    public async Task SignalR_subscription_requires_json_argument_array()
    {
        var state = new HubState();
        await using var server = await HubServer.Start(state);
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(server.Settings(), null, "{\"line\":1}", NoMessage, CancellationToken.None));
    }

    [Fact]
    public async Task SignalR_slow_inbox_overload_terminates_visibly_instead_of_growing_an_unbounded_queue()
    {
        var state = new HubState { SendOnConnect = true, BurstCount = 200 };
        await using var server = await HubServer.Start(state);
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings(); settings.SubscribeMethod = "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(settings, null, "",
            (_, ct) => Task.Delay(Timeout.Infinite, ct), deadline.Token));
    }

    [Fact]
    public async Task WebSocket_delivers_fragmented_utf8_once_and_sends_subscription_and_concurrent_replies()
    {
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            Assert.Equal("secret", context.Request.Headers["X-Test"]);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var subscription = await Receive(socket, context.RequestAborted);
            var body = Encoding.UTF8.GetBytes("{\"barcode\":\"中文\"}");
            await socket.SendAsync(body.AsMemory(0, 13), WebSocketMessageType.Text, false, context.RequestAborted);
            await socket.SendAsync(body.AsMemory(13), WebSocketMessageType.Text, true, context.RequestAborted);
            var first = await Receive(socket, context.RequestAborted);
            var second = await Receive(socket, context.RequestAborted);
            received.TrySetResult([subscription, first, second]);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var messages = new List<MesInterfaceMessage>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.RunAsync(server.Settings("WebSocket"), null, "{\"subscribe\":true}", async (message, ct) =>
        {
            messages.Add(message);
            await Task.WhenAll(client.SendAsync("{\"ack\":1}", ct), client.SendAsync("{\"ack\":2}", ct));
        }, deadline.Token);
        Assert.Equal("{\"barcode\":\"中文\"}", Assert.Single(messages).Data);
        var sent = await received.Task.WaitAsync(deadline.Token);
        Assert.Equal("{\"subscribe\":true}", sent[0]);
        Assert.Equal(new[] { "{\"ack\":1}", "{\"ack\":2}" }, sent.Skip(1).Order());
    }

    [Fact]
    public async Task Sse_parses_multiline_data_and_persistent_ids_without_sending_subscription()
    {
        await using var server = await Server.Start(async context =>
        {
            Assert.Equal("GET", context.Request.Method);
            Assert.Equal("text/event-stream", context.Request.Headers.Accept);
            Assert.Equal("old", context.Request.Headers["Last-Event-ID"]);
            Assert.Equal("secret", context.Request.Headers["X-Test"]);
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            await context.Response.WriteAsync("\uFEFF: heartbeat\r\nid: next\r\nevent: changed\r\ndata: {\r\ndata: \"code\":\"中文\"}\r\nignored: yes\r\n\r\ndata: second\n\nid: bad\0id\n\ndata: third\n\nid:\n\ndata:\n\ndata: incomplete", context.RequestAborted);
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var messages = new List<MesInterfaceMessage>();
        await client.RunAsync(server.Settings("SSE"), "old", "must not be sent", (message, _) =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(new[]
        {
            new MesInterfaceMessage("{\n\"code\":\"中文\"}", "next", "changed"),
            new MesInterfaceMessage("second", "next", null),
            new MesInterfaceMessage("third", "next", null),
            new MesInterfaceMessage("", "", null)
        }, messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("{}", CancellationToken.None));
    }

    [Theory]
    [InlineData("data: 1234567890123456789012345678901234567890\n\n")]
    [InlineData("data: a\ndata: a\ndata: a\ndata: a\ndata: a\n\n")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task Sse_bounds_both_long_lines_and_accumulated_event_bytes(string payload)
    {
        await using var server = await Server.Start(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(payload);
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings("SSE"); settings.MaxMessageBytes = 32;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(settings, null, "", NoMessage, CancellationToken.None));
    }

    [Theory]
    [InlineData("SSE")]
    [InlineData("WebSocket")]
    public async Task Idle_connections_timeout_and_user_cancellation_interrupts_reads(string protocol)
    {
        await using var server = await Server.Start(async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try { await Receive(socket, context.RequestAborted); } catch (Exception) { }
            }
            else
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.StartAsync();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { }
            }
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings(protocol); settings.IdleTimeoutSec = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => client.RunAsync(settings, null, "", NoMessage, CancellationToken.None));
        settings.IdleTimeoutSec = 90;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RunAsync(settings, null, "", NoMessage, cancel.Token));
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("oversize")]
    [InlineData("invalid-utf8")]
    public async Task WebSocket_rejects_invalid_messages(string scenario)
    {
        await using var server = await Server.Start(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var bytes = scenario == "invalid-utf8" ? new byte[] { 0xff } : new byte[40];
            await socket.SendAsync(bytes.AsMemory(), scenario == "binary" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, context.RequestAborted);
            try { await Receive(socket, context.RequestAborted); } catch (Exception) { }
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var settings = server.Settings("WebSocket"); settings.MaxMessageBytes = 32;
        var error = await Record.ExceptionAsync(() => client.RunAsync(settings, null, "", NoMessage, CancellationToken.None));
        Assert.NotNull(error);
        Assert.True(error is InvalidDataException or DecoderFallbackException or WebSocketException, error.ToString());
    }

    [Fact]
    public async Task Sse_rejects_non_stream_content_type()
    {
        await using var server = await Server.Start(async context => { context.Response.ContentType = "application/json"; await context.Response.WriteAsync("{}"); });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(server.Settings("SSE"), null, "", NoMessage, CancellationToken.None));
    }

    [Fact]
    public async Task Sse_preserves_resume_id_and_rejects_sends_while_connected()
    {
        await using var server = await Server.Start(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\r\rdata: two\r\r");
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        var messages = new List<MesInterfaceMessage>();
        await client.RunAsync(server.Settings("SSE"), "resume", "", async (message, ct) =>
        {
            messages.Add(message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("{}", ct));
        }, CancellationToken.None);
        Assert.Equal(new[] { new MesInterfaceMessage("one", "resume", null), new MesInterfaceMessage("two", "resume", null) }, messages);
    }

    [Theory]
    [InlineData("SSE")]
    [InlineData("WebSocket")]
    public async Task Dispose_interrupts_pending_receive_and_prevents_reuse(string protocol)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await Server.Start(async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                connected.TrySetResult();
                try { await Receive(socket, context.RequestAborted); } catch (Exception) { }
            }
            else
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.StartAsync();
                connected.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { }
            }
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = client.RunAsync(server.Settings(protocol), null, "", NoMessage, deadline.Token);
        await connected.Task.WaitAsync(deadline.Token);
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RunAsync(server.Settings(protocol), null, "", NoMessage, CancellationToken.None));
    }

    [Fact]
    public async Task WebSocket_close_discards_unfinished_fragment()
    {
        await using var server = await Server.Start(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"partial\":").AsMemory(), WebSocketMessageType.Text, false, context.RequestAborted);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        await client.RunAsync(server.Settings("WebSocket"), null, "", NoMessage, CancellationToken.None);
    }

    [Theory]
    [InlineData("bad", "http://127.0.0.1", 100, 1)]
    [InlineData("WebSocket", "http://127.0.0.1", 100, 1)]
    [InlineData("SSE", "ws://127.0.0.1", 100, 1)]
    [InlineData("SSE", "relative", 100, 1)]
    [InlineData("SSE", "http://127.0.0.1", 0, 1)]
    [InlineData("SSE", "http://127.0.0.1", int.MaxValue, 1)]
    [InlineData("SSE", "http://127.0.0.1", 100, 0)]
    public async Task Invalid_settings_fail_before_connecting(string protocol, string url, int maximum, int idle)
    {
        using var http = new HttpClient();
        await using var client = new MesInterfaceClient(http);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.RunAsync(new MesInterfaceListener
        { Protocol = protocol, Url = url, MaxMessageBytes = maximum, IdleTimeoutSec = idle }, null, "", NoMessage, CancellationToken.None));
    }

    private static Task NoMessage(MesInterfaceMessage message, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Unexpected message: " + message.Data);

    private static async Task<string> Receive(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), ct);
        return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }

    public sealed class HubState
    {
        public bool SendOnConnect { get; init; }
        public bool SendString { get; init; }
        public int BurstCount { get; init; } = 1;
        public string? Subscription { get; set; }
        public string? Header { get; set; }
        public TaskCompletionSource<(string, bool)> Replied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ListenerTestHub(HubState state) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            state.Header = Context.GetHttpContext()?.Request.Headers["X-Test"].ToString();
            if (state.SendOnConnect)
                for (var i = 0; i < state.BurstCount; i++) await SendMessage();
            await base.OnConnectedAsync();
        }

        public async Task Subscribe(string line)
        {
            state.Subscription = line;
            await SendMessage();
        }

        public Task Reply(string id, bool success)
        {
            state.Replied.TrySetResult((id, success));
            return Task.CompletedTask;
        }

        private Task SendMessage() => state.SendString
            ? Clients.Caller.SendAsync("ReceiveBarcode", "{\"requestId\":\"r-string\"}")
            : Clients.Caller.SendAsync("ReceiveBarcode", new { requestId = "r-1", barcode = "中文" });
    }

    private sealed class HubServer(WebApplication app) : IAsyncDisposable
    {
        public MesInterfaceListener Settings() => new()
        {
            Protocol = "SignalR", Url = app.Urls.Single() + "/backend", ReceiveMethod = "ReceiveBarcode",
            SubscribeMethod = "Subscribe", ReplyMethod = "Reply", Headers = new() { ["X-Test"] = "secret" }, IdleTimeoutSec = 5
        };

        public static async Task<HubServer> Start(HubState state)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(state);
            builder.Services.AddSignalR();
            var app = builder.Build();
            app.MapHub<ListenerTestHub>("/backend");
            await app.StartAsync();
            return new HubServer(app);
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(deadline.Token);
            await app.DisposeAsync();
        }
    }

    private sealed class Server(WebApplication app) : IAsyncDisposable
    {
        public MesInterfaceListener Settings(string protocol) => new()
        {
            Protocol = protocol,
            Url = protocol == "WebSocket" ? app.Urls.Single().Replace("http:", "ws:") : app.Urls.Single(),
            Headers = new() { ["X-Test"] = "secret" },
            IdleTimeoutSec = 5
        };

        public static async Task<Server> Start(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            app.Run(handler);
            await app.StartAsync();
            return new Server(app);
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(deadline.Token);
            await app.DisposeAsync();
        }
    }
}
