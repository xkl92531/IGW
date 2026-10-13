using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using IGW.Core;
using IGW.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.RestApi;

public sealed record RealtimeFrame(string Event, string Id, string Data);

public static class IgwRealtimeSignalR
{
    public const string HubPath = "/hubs/igw-realtime";
    public static IServiceCollection AddIgwRealtimeSignalR(this IServiceCollection services)
    {
        services.AddSingleton<IgwRealtimeRuntime>();
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = RealtimeStreamApi.MaxRequestBytes;
            options.MaximumParallelInvocationsPerClient = 8;
            options.StreamBufferCapacity = 1;
            options.EnableDetailedErrors = false;
        });
        return services;
    }

    public static void MapIgwRealtimeSignalR(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        app.ServiceProvider.GetRequiredService<IgwRealtimeRuntime>().Initialize(host, app);
        app.MapHub<IgwRealtimeHub>(HubPath, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.ApplicationMaxBufferSize = RealtimeStreamApi.MaxRequestBytes;
            options.TransportMaxBufferSize = 128 * 1024;
            options.TransportSendTimeout = TagValueFrameWriter.DefaultWriteTimeout;
        });
    }
}

public sealed class IgwRealtimeHub(IgwRealtimeRuntime runtime) : Hub
{
    public Task<JsonElement> Request(string auth, string operation, JsonElement args, string revision) =>
        runtime.RequestAsync(auth, operation, args, revision, Context.GetHttpContext(), Context.ConnectionAborted);

    public ChannelReader<RealtimeFrame> Stream(string auth, TagValueStreamRequest request, CancellationToken cancellationToken) =>
        runtime.OpenStream(auth, request, Context.ConnectionAborted, cancellationToken);
}

public sealed class IgwRealtimeRuntime(ILogger<IgwRealtimeRuntime> logger)
{
    private IGatewayHost _host = null!;
    private IgwRealtimeRequestDispatcher _requests = null!;
    /// <summary>
    /// 连接上带的网页登录凭证(?access_token=)对应的账号，由 Program.cs 接上账号库。
    /// 每次写都重新验证：连接期间改权限、停用账号立即生效。没接或没带凭证 = 第三方系统。
    /// </summary>
    public Func<HttpContext, GatewayAccount?>? WebAccount { get; set; }
    internal void Initialize(IGatewayHost host, IEndpointRouteBuilder endpoints)
    {
        _host = host;
        _requests = new IgwRealtimeRequestDispatcher(host, endpoints);
    }

    internal async Task<JsonElement> RequestAsync(string auth, string operation, JsonElement args, string revision, HttpContext? connection, CancellationToken ct)
    {
        string safeOperation = SafeOperation(operation);
        long started = Stopwatch.GetTimestamp();
        string status = "success";
        string errorType = "";
        string errorCode = "";
        int? httpStatusCode = null;
        bool routineHealthPoll = string.Equals(safeOperation, "health", StringComparison.Ordinal);
        using var activity = routineHealthPoll ? null : GatewayTelemetry.StartActivity("igw.interface.signalr.request",
            new Dictionary<string, object?> { ["operation"] = safeOperation, ["target"] = IgwRealtimeSignalR.HubPath }, ActivityKind.Server);
        try
        {
            return await _requests.InvokeAsync(auth, operation, args, revision,
                () => connection is null ? null : WebAccount?.Invoke(connection), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
            activity?.SetStatus(ActivityStatusCode.Error, status);
            throw;
        }
        catch (HubException error)
        {
            status = "failure";
            errorType = error.GetType().Name;
            (int protocolStatus, string protocolCode) = IgwRealtimeAuthorization.Describe(error);
            httpStatusCode = protocolStatus;
            errorCode = protocolCode;
            activity?.SetStatus(ActivityStatusCode.Error, status);
            throw;
        }
        catch (Exception error)
        {
            status = "failure";
            errorType = error.GetType().Name;
            errorCode = "signalr_failure";
            httpStatusCode = 500;
            activity?.SetStatus(ActivityStatusCode.Error, status);
            throw;
        }
        finally
        {
            double durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            activity?.SetTag("igw.duration_ms", durationMs);
            // The UI polls health continuously. Successful polls add no diagnostic value and can
            // overwhelm real interface events; failed/cancelled health requests remain visible.
            if (!routineHealthPoll || status != "success")
            {
                GatewayTelemetry.Log(logger, status == "success" ? LogLevel.Information : LogLevel.Warning,
                    "interface", "SignalR 请求完成", new Dictionary<string, object?>
                    {
                        ["eventKind"] = "signalr_request",
                        ["module"] = "northbound",
                        ["feature"] = "signalr_request",
                        ["direction"] = "inbound",
                        ["protocol"] = "signalr",
                        ["operation"] = safeOperation,
                        ["route"] = IgwRealtimeSignalR.HubPath,
                        ["interfaceName"] = "IGW Realtime Request",
                        ["target"] = IgwRealtimeSignalR.HubPath,
                        ["transportStatus"] = status switch
                        {
                            "success" => "completed",
                            "cancelled" => "cancelled",
                            _ => "failed"
                        },
                        ["status"] = status,
                        ["httpStatusCode"] = httpStatusCode,
                        ["errorType"] = errorType,
                        ["errorCode"] = errorCode,
                        ["durationMs"] = durationMs
                    });
            }
        }
    }

    internal ChannelReader<RealtimeFrame> OpenStream(string auth, TagValueStreamRequest body, CancellationToken connectionAborted, CancellationToken streamAborted)
    {
        var channel = Channel.CreateBounded<RealtimeFrame>(new BoundedChannelOptions(1)
        {
            SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
        });
        _ = PumpAsync();
        return channel.Reader;

        async Task PumpAsync()
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(connectionAborted, streamAborted);
            var ct = stop.Token;
            long started = Stopwatch.GetTimestamp();
            string status = "completed";
            string errorType = "";
            string errorCode = "";
            int? httpStatusCode = null;
            int requestedTags = body.TagIds?.Count ?? 0;
            int acceptedTags = 0;
            int missingTags = 0;
            using var activity = GatewayTelemetry.StartActivity("igw.interface.signalr.stream",
                new Dictionary<string, object?> { ["target"] = IgwRealtimeSignalR.HubPath, ["tagCount"] = requestedTags }, ActivityKind.Server);
            try
            {
                var principal = IgwRealtimeAuthorization.Authenticate(auth, _host.Project.Security);
                string securityRevision = JsonHash.Of(_host.Project.Security);
                if (_host.Reloading) throw IgwRealtimeAuthorization.Error(503, "project_reloading");
                var validation = TagValueStreamRequestValidator.Validate(body, _host.Project.Settings.TagStream);
                if (!validation.IsValid) throw IgwRealtimeAuthorization.Error(400, validation.ErrorCode!);
                var request = validation.Request!;
                GuardBranches(principal);
                var project = _host.Project;
                string revision = _host.RealtimeRevision;
                if (!string.Equals(request.ProjectRevision, revision, StringComparison.Ordinal))
                    throw IgwRealtimeAuthorization.Error(409, "project_revision_mismatch");
                var coordinator = _host.RealtimeStreams;
                if (_host.Hub is null || coordinator is null)
                    throw IgwRealtimeAuthorization.Error(503, "realtime_stream_unavailable");
                RealtimeStreamLease lease;
                try
                {
                    lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(request.ClientId,
                        principal?.Username ?? "anonymous", revision, request.TagIds, request.Topics,
                        request.IncludeSnapshot, request.HeartbeatSeconds), ct);
                }
                catch (RealtimeAdmissionException ex)
                {
                    throw IgwRealtimeAuthorization.Error(ex.Code switch
                    {
                        "project-revision-mismatch" => 409, "too-many-streams" => 429,
                        "stream-disabled" => 503, _ => 400,
                    }, ex.Code);
                }
                await using (lease)
                {
                    acceptedTags = lease.AcceptedTagIds.Count;
                    missingTags = lease.MissingTagIds.Count;
                    var writer = new TagValueFrameWriter(async (frame, token) =>
                    {
                        // Revalidate changed security settings even on an already open socket.
                        string currentSecurity = JsonHash.Of(_host.Project.Security);
                        if (!string.Equals(currentSecurity, securityRevision, StringComparison.Ordinal))
                        {
                            principal = IgwRealtimeAuthorization.Authenticate(auth, _host.Project.Security);
                            securityRevision = currentSecurity;
                            GuardBranches(principal);
                        }
                        int bytes = JsonSerializer.SerializeToUtf8Bytes(frame, TagValueStreamJson.Options).Length;
                        if (bytes > 2 * TagValueFrameWriter.MaxEventBytes)
                            throw IgwRealtimeAuthorization.Error(413, "frame_too_large");
                        await channel.Writer.WriteAsync(frame, token);
                        return bytes;
                    }, lease.StreamId.ToString("N"));
                    await writer.WriteHelloAsync(new TagStreamHello(TagValueStreamRequestValidator.ProtocolVersion,
                        lease.StreamId.ToString("N"), project.Name.Length > 1024 ? project.Name[..1024] : project.Name,
                        revision, lease.AcceptedTagIds.Count, lease.MissingTagIds.Count,
                        lease.MissingTagIds.Take(32).ToArray(), DateTime.UtcNow), ct);
                    coordinator.RecordSent(lease.StreamId, null, writer.LastFrameBytes);
                    await RealtimeStreamApi.StreamMessagesAsync(writer, lease, coordinator, revision, request.HeartbeatSeconds, ct);
                }

                void GuardBranches(PublicPlcPrincipal? user)
                {
                    if (!PublicPlcApiPolicy.AreReadBranchesAllowed(user, request.TagIds)
                        || (request.Topics.Count > 0 && user?.ReadBranches is { Count: > 0 }))
                        throw IgwRealtimeAuthorization.Error(403, "read_branch_forbidden");
                }
            }
            catch (OperationCanceledException) { status = "cancelled"; }
            catch (ChannelClosedException) { status = "cancelled"; }
            catch (HubException ex)
            {
                status = "failure";
                errorType = ex.GetType().Name;
                (int protocolStatus, string protocolCode) = IgwRealtimeAuthorization.Describe(ex);
                httpStatusCode = protocolStatus;
                errorCode = protocolCode;
                activity?.SetStatus(ActivityStatusCode.Error);
                channel.Writer.TryComplete(ex);
            }
            catch (TimeoutException ex) { status = "timeout"; errorType = ex.GetType().Name; errorCode = "stream_write_timeout"; httpStatusCode = 408; activity?.SetStatus(ActivityStatusCode.Error); channel.Writer.TryComplete(IgwRealtimeAuthorization.Error(408, "stream_write_timeout")); }
            catch (Exception ex)
            {
                status = "failure";
                errorType = ex.GetType().Name;
                errorCode = "stream_failed";
                httpStatusCode = 500;
                activity?.SetStatus(ActivityStatusCode.Error);
                logger.LogWarning(ex, "SignalR 实时流传输失败，连接将释放并由客户端重建");
                channel.Writer.TryComplete(IgwRealtimeAuthorization.Error(500, "stream_failed"));
            }
            finally
            {
                double durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                activity?.SetTag("igw.duration_ms", durationMs);
                GatewayTelemetry.Log(logger, status is "completed" or "cancelled" ? LogLevel.Information : LogLevel.Warning,
                    "interface", "SignalR 实时流结束", new Dictionary<string, object?>
                    {
                        ["eventKind"] = "signalr_stream",
                        ["module"] = "northbound",
                        ["feature"] = "signalr_stream",
                        ["direction"] = "outbound",
                        ["protocol"] = "signalr",
                        ["operation"] = "stream",
                        ["route"] = IgwRealtimeSignalR.HubPath,
                        ["interfaceName"] = "IGW Realtime Stream",
                        ["target"] = IgwRealtimeSignalR.HubPath,
                        ["status"] = status,
                        ["transportStatus"] = status,
                        ["durationMs"] = durationMs,
                        ["requestedTagCount"] = requestedTags,
                        ["tagCount"] = acceptedTags,
                        ["missingTagCount"] = missingTags,
                        ["errorType"] = errorType,
                        ["errorCode"] = errorCode,
                        ["httpStatusCode"] = httpStatusCode
                    });
                channel.Writer.TryComplete();
            }
        }
    }

    private static string SafeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 64) return "unknown";
        return operation.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? operation
            : "unknown";
    }
}
