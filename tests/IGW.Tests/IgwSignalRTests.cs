using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class IgwSignalRTests
{
    [Fact]
    public async Task Request_and_stream_emit_one_bounded_lifecycle_summary_each()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        Assert.True((await wire.RequestAsync("devices", new { })).TryGetProperty("result", out _));
        await wire.SendAsync(new { type = 4, invocationId = "telemetry", target = "Stream", arguments = new object[] { "", fixture.StreamRequest("telemetry", false) } });
        Assert.Equal("hello", (await wire.ReceiveAsync()).GetProperty("item").GetProperty("event").GetString());
        await wire.SendAsync(new { type = 5, invocationId = "telemetry" });
        await fixture.WaitForStreamsAsync(0);
        await fixture.WaitForTelemetryAsync(2);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> records = fixture.Telemetry.ByCategory("interface");
        IReadOnlyDictionary<string, object?> request = Assert.Single(records,
            record => Equals(record.GetValueOrDefault("igw_event_kind"), "signalr_request"));
        IReadOnlyDictionary<string, object?> stream = Assert.Single(records,
            record => Equals(record.GetValueOrDefault("igw_event_kind"), "signalr_stream"));
        Assert.Equal("northbound", request["igw_module"]);
        Assert.Equal("signalr_request", request["igw_feature"]);
        Assert.Equal("inbound", request["igw_direction"]);
        Assert.Equal("signalr", request["igw_protocol"]);
        Assert.Equal("/hubs/igw-realtime", request["igw_route"]);
        Assert.Equal("devices", request["igw_operation"]);
        Assert.Equal("success", request["igw_status"]);
        Assert.Equal("completed", request["igw_transport_status"]);
        Assert.Equal("/hubs/igw-realtime", request["igw_target"]);
        Assert.Equal("signalr_stream", stream["igw_feature"]);
        Assert.True(Equals(stream["igw_status"], "cancelled"),
            $"status={stream["igw_status"]}; errorType={stream.GetValueOrDefault("igw_error_type")}");
        Assert.Equal("cancelled", stream["igw_transport_status"]);
        Assert.True(Convert.ToDouble(stream["igw_duration_ms"]) >= 0);
    }

    [Fact]
    public async Task Successful_health_poll_does_not_emit_an_interface_log()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();

        Assert.True((await wire.RequestAsync("health", new { })).TryGetProperty("result", out _));

        Assert.DoesNotContain(fixture.Telemetry.ByCategory("interface"),
            record => Equals(record.GetValueOrDefault("igw_operation"), "health"));
    }

    [Fact]
    public async Task Websocket_handshake_and_snapshot_use_native_SignalR_frames_and_cancel_releases_lease()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        await wire.SendAsync(new { type = 4, invocationId = "s", target = "Stream", arguments = new object[] { "", fixture.StreamRequest() } });
        var events = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var frame = await wire.ReceiveAsync();
            Assert.Equal(2, frame.GetProperty("type").GetInt32());
            var item = frame.GetProperty("item");
            events.Add(item.GetProperty("event").GetString()!);
            Assert.EndsWith($":{i}", item.GetProperty("id").GetString());
            Assert.DoesNotContain("event:", item.GetProperty("data").GetString());
            if (i == 3)
            {
                var value = JsonDocument.Parse(item.GetProperty("data").GetString()!).RootElement.GetProperty("values")[0];
                Assert.Equal("9223372036854775807", value.GetProperty("value").GetString());
            }
        }
        Assert.Equal(new[] { "hello", "snapshot-begin", "snapshot-chunk", "snapshot-complete" }, events);
        await wire.SendAsync(new { type = 5, invocationId = "s" });
        await fixture.WaitForStreamsAsync(0);
        var reply = await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value" } });
        Assert.Equal("9223372036854775807", reply.GetProperty("result")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Invocation_auth_is_current_and_branch_and_write_revision_guards_cannot_be_bypassed()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "secret", CanWrite = false, ReadBranches = ["Line.Plc"] }] };
        await using var wire = await fixture.ConnectAsync();
        AssertError(await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value" } }), "igw:401:authentication_required");
        wire.Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:secret"));
        Assert.True((await wire.RequestAsync("tagScanTimings", new { tagIds = new[] { "Line.Plc.Value" } })).TryGetProperty("result", out _));
        AssertError(await wire.RequestAsync("values", new { ids = new[] { "Line.PlcSibling.Secret" } }), "igw:403:read_branch_forbidden");
        AssertError(await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "7" }), "igw:403:write_forbidden");
        fixture.Host.Project.Security.Users[0].CanWrite = true;
        AssertError(await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "7" }, "stale"), "igw:409:project_changed");
        Assert.Equal(0, fixture.Host.Driver.Writes);
        fixture.Host.Project.Security.Users[0].Password = "rotated";
        AssertError(await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value" } }), "igw:401:authentication_required");
    }

    [Fact]
    public async Task Logged_in_web_users_need_project_edit_to_write_plc_values_over_the_realtime_channel()
    {
        await using var fixture = await Fixture.StartAsync();
        var viewer = new GatewayAccount { Username = "viewer", Permissions = { [GatewayModules.Project] = AccessLevel.View } };
        var editor = new GatewayAccount { Username = "editor", Permissions = { [GatewayModules.Project] = AccessLevel.Edit } };
        fixture.WebAccounts["viewer-token"] = viewer;
        fixture.WebAccounts["editor-token"] = editor;

        await using (var wire = await fixture.ConnectAsync("viewer-token"))
        {
            Assert.True((await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value" } })).TryGetProperty("result", out _));
            AssertError(await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "7" }), "igw:403:edit_forbidden");
            Assert.Equal(0, fixture.Host.Driver.Writes);

            // 权限每次写都重新判断：连接不断开，管理员改成可修改后立即能写
            viewer.Permissions[GatewayModules.Project] = AccessLevel.Edit;
            Assert.True((await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "7" })).TryGetProperty("result", out _));
            Assert.Equal(1, fixture.Host.Driver.Writes);
        }

        await using (var wire = await fixture.ConnectAsync("editor-token"))
            Assert.True((await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "8" })).TryGetProperty("result", out _));
        // 第三方系统(没带网页登录凭证)照旧按接口账号判断
        await using (var wire = await fixture.ConnectAsync())
            Assert.True((await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "9" })).TryGetProperty("result", out _));
        Assert.Equal(3, fixture.Host.Driver.Writes);
    }

    [Fact]
    public async Task Write_result_preserves_Int64_normalization_and_PLC_failure_detail()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        var reply = await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "9223372036854775806" }, fixture.Host.ProjectRevision);
        Assert.True(reply.TryGetProperty("result", out var result), reply.ToString());
        Assert.Equal("9223372036854775806", result.GetProperty("normalizedValue").GetString());
        Assert.Equal(9223372036854775806L, fixture.Host.Driver.Value);
        fixture.Host.Driver.WriteStatus = StatusCode.BadNotWritable;
        var failed = await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = "3" });
        AssertError(failed, "igw:502:");
        var encoded = failed.GetProperty("error").GetString()!.Split("igw:", 2)[1].Split(':', 3)[2];
        var payload = JsonDocument.Parse(Convert.FromBase64String(encoded)).RootElement;
        Assert.Equal("BadNotWritable", payload.GetProperty("quality").GetString());
        Assert.Equal("0x803B0000", payload.GetProperty("statusHex").GetString());
    }

    [Fact]
    public async Task Unknown_operations_and_forbidden_stream_topics_are_rejected()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        AssertError(await wire.RequestAsync("/api/v1/project", new { }), "igw:400:unknown_operation");
        AssertError(await wire.RequestAsync("values", new { ids = new[] { 123 } }), "igw:400:invalid_arguments");
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "pw", CanWrite = false, ReadBranches = ["Other"] }] };
        wire.Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:pw"));
        await wire.SendAsync(new { type = 4, invocationId = "s", target = "Stream", arguments = new object[] { wire.Auth, fixture.StreamRequest() } });
        AssertError(await wire.ReceiveAsync(), "igw:403:read_branch_forbidden");
        Assert.Equal(0, fixture.Host.RealtimeStreams.SnapshotMetrics().ActiveStreams);
    }

    [Fact]
    public async Task Failed_request_summary_keeps_a_safe_status_and_error_code()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();

        AssertError(await wire.RequestAsync("/api/v1/project", new { }), "igw:400:unknown_operation");
        await fixture.WaitForTelemetryAsync(1);

        IReadOnlyDictionary<string, object?> record = Assert.Single(fixture.Telemetry.ByCategory("interface"));
        Assert.Equal("signalr_request", record["igw_event_kind"]);
        Assert.Equal("failure", record["igw_status"]);
        Assert.Equal(400, record["igw_http_status_code"]);
        Assert.Equal("unknown_operation", record["igw_error_code"]);
        Assert.DoesNotContain("eyJ", string.Join('|', record.Values));
    }

    [Fact]
    public async Task Missing_arguments_and_comma_embedded_tag_ids_do_not_escape_validation()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        AssertError(await wire.RequestAsync("write", new { id = "Line.Plc.Value" }), "igw:400:invalid_arguments");
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "pw", CanWrite = false, ReadBranches = ["Line.Plc"] }] };
        wire.Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:pw"));
        AssertError(await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value,Secret.Tag" } }), "igw:400:invalid_arguments");
    }

    [Fact]
    public async Task Runtime_offers_only_WebSockets_and_does_not_map_the_old_SSE_endpoint()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Client();
        using var negotiate = await client.PostAsync("/hubs/igw-realtime/negotiate?negotiateVersion=1", null);
        var json = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("WebSockets", Assert.Single(json.GetProperty("availableTransports").EnumerateArray()).GetProperty("transport").GetString());
        using var old = await client.PostAsync(PublicPlcApiPolicy.RealtimeStreamPath, null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task Stream_rechecks_changed_read_permissions_before_sending_more_point_values()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "pw", CanWrite = false, ReadBranches = ["Line.Plc"] }] };
        await using var wire = await fixture.ConnectAsync();
        wire.Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:pw"));
        await wire.SendAsync(new { type = 4, invocationId = "s", target = "Stream", arguments = new object[] { wire.Auth, fixture.StreamRequest(includeSnapshot: false) } });
        Assert.Equal("hello", (await wire.ReceiveAsync()).GetProperty("item").GetProperty("event").GetString());
        fixture.Host.Project.Security.Users[0].ReadBranches = ["Other"];
        fixture.Host.Hub.Update("Line.Plc.Value", DataValue.GoodAt(42L, DateTime.UtcNow));
        AssertError(await wire.ReceiveAsync(), "igw:403:read_branch_forbidden");
        await fixture.WaitForStreamsAsync(0);
    }

    [Fact]
    public async Task Cancelling_one_stream_keeps_other_lease_and_project_retirement_is_explicit()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        foreach (string id in new[] { "s1", "s2" })
        {
            await wire.SendAsync(new { type = 4, invocationId = id, target = "Stream", arguments = new object[] { "", fixture.StreamRequest(id, false) } });
            Assert.Equal("hello", (await wire.ReceiveAsync()).GetProperty("item").GetProperty("event").GetString());
        }
        await fixture.WaitForStreamsAsync(2);
        await wire.SendAsync(new { type = 5, invocationId = "s1" });
        await fixture.WaitForStreamsAsync(1);
        fixture.Host.Hub.Update("Line.Plc.Value", DataValue.GoodAt(99L, DateTime.UtcNow));
        await ReceiveStreamEventAsync("tag-delta");
        await fixture.Host.RealtimeStreams.RetireGenerationAsync("next-revision", default);
        var changed = await ReceiveStreamEventAsync("project-changed");
        Assert.Equal("next-revision", JsonDocument.Parse(changed.GetProperty("item").GetProperty("data").GetString()!).RootElement.GetProperty("projectRevision").GetString());
        await fixture.WaitForStreamsAsync(0);

        async Task<JsonElement> ReceiveStreamEventAsync(string expectedEvent)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            const int maximumFrames = 16;
            for (int frameIndex = 0; frameIndex < maximumFrames; frameIndex++)
            {
                var frame = await wire.ReceiveAsync(deadline.Token);
                if (frame.GetProperty("type").GetInt32() == 3) continue; // Stream completion may already be queued.
                Assert.Equal(2, frame.GetProperty("type").GetInt32());
                Assert.Equal("s2", frame.GetProperty("invocationId").GetString());
                string? actualEvent = frame.GetProperty("item").GetProperty("event").GetString();
                // Retirement cannot retract s2 deltas already queued on the websocket.
                if (expectedEvent == "project-changed" && actualEvent == "tag-delta") continue;
                Assert.Equal(expectedEvent, actualEvent);
                return frame;
            }
            Assert.Fail($"s2 did not deliver {expectedEvent} within {maximumFrames} frames.");
            return default;
        }
    }

    [Fact]
    public async Task Array_shape_slice_and_write_acknowledgement_preserve_integer_values_and_bounds()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        fixture.Host.Hub.Update("Line.Plc.Array", DataValue.GoodAt(fixture.Host.Driver.ArrayValue, DateTime.UtcNow));
        var shapeReply = await wire.RequestAsync("arrayInfo", new { id = "Line.Plc.Array" });
        Assert.True(shapeReply.TryGetProperty("result", out var shape), shapeReply.ToString());
        Assert.Equal(1, shape.GetProperty("lowerBounds")[0].GetInt32());
        Assert.Equal(2, shape.GetProperty("dimensions")[0].GetInt32());
        var slice = (await wire.RequestAsync("arraySlice", new { id = "Line.Plc.Array", start = new[] { 1 }, length = new[] { 2 } })).GetProperty("result");
        Assert.Equal("9223372036854775807", slice.GetProperty("values")[0].GetString());
        AssertError(await wire.RequestAsync("writeArraySlice", new { id = "Line.Plc.Array", start = new[] { 0 }, length = new[] { 2 }, values = new[] { "1", "2" } }), "igw:400:");
        AssertError(await wire.RequestAsync("writeArraySlice", new { id = "Line.Plc.Array", start = new[] { 1 }, length = new[] { 2 }, values = new[] { "1", "2" } }, "stale"), "igw:409:project_changed");
        Assert.Equal(0, fixture.Host.Driver.ArrayWrites);
        var ack = await wire.RequestAsync("writeArraySlice", new { id = "Line.Plc.Array", start = new[] { 1 }, length = new[] { 2 }, values = new[] { "9223372036854775806", "4" } });
        Assert.True(ack.TryGetProperty("result", out var result), ack.ToString());
        Assert.Equal(2, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(9223372036854775806L, fixture.Host.Driver.ArrayValue.GetBoxedValue(0));
    }

    [Fact]
    public async Task Runtime_response_size_is_bounded_and_connection_stays_usable_after_rejection()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Host.Hub.Register("Manual.Big", new TagDef { Name = "Big", Address = "big", DataType = IgwDataType.String });
        fixture.Host.Hub.Update("Manual.Big", DataValue.GoodAt(new string('x', 9 * 1024 * 1024), DateTime.UtcNow));
        await using var wire = await fixture.ConnectAsync();
        AssertError(await wire.RequestAsync("values", new { ids = new[] { "Manual.Big" } }), "igw:413:response_too_large");
        Assert.True((await wire.RequestAsync("values", new { ids = new[] { "Line.Plc.Value" } })).TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("health")]
    [InlineData("devices")]
    [InlineData("tags")]
    [InlineData("mes")]
    [InlineData("mesLog")]
    [InlineData("mesTimeline")]
    [InlineData("anomalies")]
    [InlineData("connectionDiagnostics")]
    [InlineData("scanTimings")]
    public async Task Runtime_query_allowlist_resolves_existing_handlers(string operation)
    {
        await using var fixture = await Fixture.StartAsync();
        await using var wire = await fixture.ConnectAsync();
        var reply = await wire.RequestAsync(operation, new { });
        Assert.True(reply.TryGetProperty("result", out var value), reply.ToString());
        Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public async Task Qc_stream_is_passive_and_reads_the_plc_only_when_asked()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.WaitForConnectedAsync();
        await using var wire = await fixture.ConnectAsync();
        await wire.SendAsync(new { type = 4, invocationId = "s", target = "Stream", arguments = new object[] { "", fixture.StreamRequest("qc", false) } });
        Assert.Equal("hello", (await wire.ReceiveAsync()).GetProperty("item").GetProperty("event").GetString());
        // 打开 QC 不让点位进入周期采集。
        Assert.False(fixture.Host.Hub.IsActive("Line.Plc.Value", 30_000));

        fixture.Host.Driver.Value = 5;
        var reply = await wire.RequestAsync("readValues", new { ids = new[] { "Line.Plc.Value" } });
        Assert.True(reply.TryGetProperty("result", out var result), reply.ToString());
        Assert.Equal(1, result.GetProperty("goodCount").GetInt32());
        Assert.Equal("5", result.GetProperty("values")[0].GetProperty("value").ToString());
        Assert.True(fixture.Host.Hub.TryGet("Line.Plc.Value", out var cached));
        Assert.Equal(5L, cached.Value);
        // 刷新是一次性读取，读完不留下采集需求。
        Assert.False(fixture.Host.Hub.IsActive("Line.Plc.Value", 30_000));

        var write = await wire.RequestAsync("write", new { id = "Line.Plc.Value", value = 8 });
        Assert.True(write.TryGetProperty("result", out _), write.ToString());
        // 单点写入只下发这一次写入与回读，不激活持续采集。
        Assert.False(fixture.Host.Hub.IsActive("Line.Plc.Value", 30_000));
        Assert.True(fixture.Host.Hub.TryGet("Line.Plc.Value", out var written));
        Assert.Equal(8L, written.Value);
    }

    [Fact]
    public async Task Qc_plc_read_respects_branch_permissions_and_argument_limits()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "secret", CanWrite = false, ReadBranches = ["Line.Plc"] }] };
        await using var wire = await fixture.ConnectAsync();
        AssertError(await wire.RequestAsync("readValues", new { ids = new[] { "Line.Plc.Value" } }), "igw:401:authentication_required");
        wire.Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:secret"));
        AssertError(await wire.RequestAsync("readValues", new { ids = new[] { "Line.PlcSibling.Secret" } }), "igw:403:read_branch_forbidden");
        AssertError(await wire.RequestAsync("readValues", new { ids = new[] { 123 } }), "igw:400:invalid_arguments");
    }

    [Fact]
    public async Task Http_plc_read_is_allowed_for_readers_and_checks_every_body_tag_branch()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.WaitForConnectedAsync();
        fixture.Host.Project.Security = new SecuritySettings { Enabled = true, Users = [new UserDef { Username = "reader", Password = "secret", CanWrite = false, ReadBranches = ["Line.Plc"] }] };
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:secret")));

        using var allowed = await client.PostAsJsonAsync("/api/v1/values/read", new { ids = new[] { "Line.Plc.Value" } });
        Assert.Equal(System.Net.HttpStatusCode.OK, allowed.StatusCode);

        // 请求体里的点位中间件看不到，必须由接口自己检查，不能借 POST 读到无权分支。
        using var denied = await client.PostAsJsonAsync("/api/v1/values/read", new { ids = new[] { "Line.Plc.Value", "Other.Plc.Secret" } });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Stream_admission_and_revision_errors_preserve_protocol_status_codes()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Host.Project.Settings.TagStream.MaxStreams = 1;
        await using var wire = await fixture.ConnectAsync();
        await wire.SendAsync(new { type = 4, invocationId = "s1", target = "Stream", arguments = new object[] { "", fixture.StreamRequest("s1", false) } });
        Assert.Equal("hello", (await wire.ReceiveAsync()).GetProperty("item").GetProperty("event").GetString());
        await wire.SendAsync(new { type = 4, invocationId = "s2", target = "Stream", arguments = new object[] { "", fixture.StreamRequest("s2", false) } });
        AssertError(await wire.ReceiveAsync(), "igw:429:too-many-streams");
        await wire.SendAsync(new { type = 4, invocationId = "old", target = "Stream", arguments = new object[] { "", new { protocolVersion = 1, clientId = "old", projectRevision = "old", tagIds = new[] { "Line.Plc.Value" } } } });
        AssertError(await wire.ReceiveAsync(), "igw:409:project_revision_mismatch");
        Assert.Equal(1, fixture.Host.RealtimeStreams.SnapshotMetrics().ActiveStreams);
    }

    private static void AssertError(JsonElement reply, string prefix) => Assert.Contains(prefix, reply.GetProperty("error").GetString());

    private sealed class Fixture(WebApplication app, TestHost host, TelemetryTestLogger<IgwRealtimeRuntime> telemetry) : IAsyncDisposable
    {
        public TestHost Host { get; } = host;
        public TelemetryTestLogger<IgwRealtimeRuntime> Telemetry { get; } = telemetry;
        /// <summary>模拟 Program.cs 的网页登录凭证：?access_token= 对应的网页账号。</summary>
        public Dictionary<string, GatewayAccount> WebAccounts { get; } = [];
        public static async Task<Fixture> StartAsync()
        {
            var host = new TestHost();
            await host.Engine.StartAsync();
            host.Hub.Update("Line.Plc.Value", DataValue.GoodAt(long.MaxValue, DateTime.UtcNow));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var telemetry = new TelemetryTestLogger<IgwRealtimeRuntime>();
            builder.Services.AddSingleton<ILogger<IgwRealtimeRuntime>>(telemetry);
            builder.Services.AddIgwRealtimeSignalR();
            var app = builder.Build();
            // 与 Program.cs 相同的鉴权中间件，覆盖直接 HTTP 调用的权限边界。
            app.Use(async (context, next) =>
            {
                var authorization = PublicPlcApiPolicy.Authorize(context, host.Project.Security);
                if (!authorization.IsAllowed)
                {
                    context.Response.StatusCode = authorization.StatusCode!.Value;
                    return;
                }
                if (authorization.User is not null)
                    context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] = authorization.User;
                await next();
            });
            app.MapIgwApi(host);
            app.MapIgwRealtimeSignalR(host);
            var fixture = new Fixture(app, host, telemetry);
            app.Services.GetRequiredService<IgwRealtimeRuntime>().WebAccount = context =>
                fixture.WebAccounts.GetValueOrDefault(context.Request.Query["access_token"].ToString());
            await app.StartAsync();
            return fixture;
        }
        public HttpClient Client() => app.GetTestClient();
        public object StreamRequest(string clientId = "wire-test", bool includeSnapshot = true) => new { protocolVersion = 1, clientId, projectRevision = Host.RealtimeRevision, tagIds = new[] { "Line.Plc.Value" }, includeSnapshot, heartbeatSeconds = 5 };
        public async Task<Wire> ConnectAsync(string? accessToken = null)
        {
            string query = accessToken is null ? "" : "?access_token=" + Uri.EscapeDataString(accessToken);
            var socket = await app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/hubs/igw-realtime" + query), CancellationToken.None);
            var wire = new Wire(socket);
            await wire.SendAsync(new { protocol = "json", version = 1 });
            Assert.Equal("{}", (await wire.ReceiveAsync()).ToString());
            return wire;
        }
        public async Task WaitForConnectedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Host.Engine.DeviceStatuses().Any(device => device.State != ConnectionState.Connected))
                await Task.Delay(10, timeout.Token);
        }
        public async Task WaitForStreamsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Host.RealtimeStreams.SnapshotMetrics().ActiveStreams != count) await Task.Delay(10, timeout.Token);
        }
        public async Task WaitForTelemetryAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Telemetry.ByCategory("interface").Count < count) await Task.Delay(10, timeout.Token);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); await Host.RealtimeStreams.DisposeAsync(); await Host.Engine.DisposeAsync(); await Host.Hub.DisposeAsync(); }
    }

    private sealed class Wire(WebSocket socket) : IAsyncDisposable
    {
        private readonly Queue<string> _messages = new();
        private string _pending = "";
        private int _sequence;
        public string Auth { get; set; } = "";
        public Task SendAsync(object value) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + '\u001e'), WebSocketMessageType.Text, true, CancellationToken.None);
        public async Task<JsonElement> ReceiveAsync(CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                while (_messages.Count == 0)
                {
                    var buffer = new byte[64 * 1024];
                    var received = await socket.ReceiveAsync(buffer, timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Socket closed");
                    _pending += Encoding.UTF8.GetString(buffer, 0, received.Count);
                    var split = _pending.Split('\u001e');
                    foreach (var message in split[..^1]) _messages.Enqueue(message);
                    _pending = split[^1];
                }
                var json = JsonDocument.Parse(_messages.Dequeue()).RootElement.Clone();
                if (json.TryGetProperty("type", out var type) && type.GetInt32() == 6) continue;
                return json;
            }
        }
        public async Task<JsonElement> RequestAsync(string operation, object args, string revision = "")
        {
            string id = (++_sequence).ToString();
            await SendAsync(new { type = 1, invocationId = id, target = "Request", arguments = new object[] { Auth, operation, args, revision } });
            while (true) { var reply = await ReceiveAsync(); if (reply.TryGetProperty("invocationId", out var actual) && actual.GetString() == id) return reply; }
        }
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { socket.Abort(); }
            finally { socket.Dispose(); }
        }
    }

    private sealed class TestDriver : IDeviceDriver, IArrayCapableDriver
    {
        public string DriverId => "signalr-test";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public long Value = long.MaxValue;
        public int Writes;
        public int ArrayWrites;
        public ArrayRuntimeValue ArrayValue = PrimitiveArrayValue<long>.Create(ArrayShape.Create([2], [1]), new[] { long.MaxValue, 2L }, elementType: IgwDataType.Int64);
        public ArrayDriverCapabilities ArrayCapabilities { get; } = new(true, false, true, true, true, true, 100, 1024);
        public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
        {
            ArrayWrites++;
            ArrayValue = request.Value;
            return Task.FromResult(new ArrayWriteResult(StatusCode.Good, 2, []));
        }
        public StatusCode WriteStatus = StatusCode.Good;
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(t => new TagSample(t.TagId, DataValue.GoodAt(t.Def.Array is null ? (object)Value : ArrayValue, DateTime.UtcNow))).ToArray());
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            Writes += writes.Count;
            if (WriteStatus.IsGood) Value = Convert.ToInt64(writes[0].Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(w => new WriteResult { TagId = w.TagId, Status = WriteStatus }).ToArray());
        }
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error) { result = new TagAddress(0, 0, 8); error = null; return true; }
    }
    private sealed class Factory(TestDriver driver) : IDriverFactory
    {
        public string DriverId => "signalr-test";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => driver;
    }
    private sealed class TestHost : IGatewayHost
    {
        public TestHost()
        {
            var registry = new DriverRegistry(); registry.Register(new Factory(Driver));
            Engine = new RuntimeEngine(Project, registry, Hub, NullLoggerFactory.Instance);
            RealtimeStreams = new RealtimeStreamCoordinator(Hub, RealtimeRevision, Project.Settings.TagStream, NullLogger<RealtimeStreamCoordinator>.Instance);
        }
        public TestDriver Driver { get; } = new();
        public DataHub Hub { get; } = new();
        public RuntimeEngine Engine { get; }
        public string ProjectRevision => "project-revision-1";
        public string RealtimeRevision => "realtime-revision-1";
        public RealtimeStreamCoordinator RealtimeStreams { get; }
        public IgwProject Project { get; } = new() { Channels = [new ChannelDef { Name = "Line", DriverId = "signalr-test", Devices = [new DeviceDef { Name = "Plc", Tags = [new TagDef { Name = "Value", Address = "1", DataType = IgwDataType.Int64, Access = TagAccess.ReadWrite, ScanRateMs = 100 }, new TagDef { Name = "Array", Address = "2", DataType = IgwDataType.Int64, Access = TagAccess.ReadWrite, Array = new ArrayDef { ElementType = IgwDataType.Int64, Dimensions = [2], LowerBounds = [1], ElementStrideBytes = 8 } }] }] }] };
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters { get; } = new NetworkAdapterResolver();
        public int OpcUaSessions => 0;
        public bool Reloading => false;
        public Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowseResult> BrowseOpcUaBranchAsync(BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public object ComponentsSnapshot() => new { };
        public object LicenseSnapshot() => new { blocked = false };
        public Task<object> ActivateLicenseAsync(string code) => throw new NotSupportedException();
        public Task ReloadAsync(IgwProject newProject) => throw new NotSupportedException();
        public object MesStatusSnapshot() => new { };
        public object MesLogSnapshot(int limit) => Array.Empty<object>();
        public IReadOnlyList<MesTaskStatus> MesTaskStatuses() => [];
        public IReadOnlyList<MesFlowEvent> MesTimeline(string taskName) => [];
        public Task<object> MesTestAsync(MesTask task, CancellationToken ct) => throw new NotSupportedException();
    }
}
