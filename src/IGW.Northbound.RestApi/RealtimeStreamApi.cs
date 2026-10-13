using System.Buffers;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.RestApi;

public static class RealtimeStreamApi
{
    internal const int MaxRequestBytes = 8 * 1024 * 1024;

    public static void MapRealtimeStreamApi(
        this IEndpointRouteBuilder app,
        Func<IgwProject> projectProvider,
        Func<DataHub?> hubProvider,
        Func<RealtimeStreamCoordinator?> coordinatorProvider,
        Func<string>? projectRevisionProvider = null,
        Func<bool>? reloadingProvider = null)
    {
        app.MapPost(PublicPlcApiPolicy.RealtimeStreamPath, async context =>
            await StreamAsync(context, projectProvider, hubProvider, coordinatorProvider,
                projectRevisionProvider, reloadingProvider));
        app.MapRealtimeStreamDiagnostics(coordinatorProvider);
    }

    public static void MapRealtimeStreamDiagnostics(
        this IEndpointRouteBuilder app, Func<RealtimeStreamCoordinator?> coordinatorProvider)
    {
        app.MapGet(PublicPlcApiPolicy.RealtimeStreamMetricsPath, () =>
        {
            var coordinator = coordinatorProvider();
            return coordinator is null
                ? Results.Json(new { error = "realtime_stream_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(coordinator.SnapshotMetrics());
        });
        app.MapGet(PublicPlcApiPolicy.RealtimeStreamMissingPath, (
            HttpContext context, string streamId, int? skip, int? take) =>
        {
            RealtimeStreamCoordinator? coordinator = coordinatorProvider();
            if (coordinator is null)
                return Results.Json(new { error = "realtime_stream_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!Guid.TryParseExact(streamId, "N", out Guid id))
                return Results.Json(new { error = "invalid_stream_id" },
                    statusCode: StatusCodes.Status400BadRequest);
            int pageSkip = Math.Max(0, skip ?? 0);
            int pageTake = Math.Clamp(take ?? 100, 1, 1_000);
            var principal = context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] as PublicPlcPrincipal;
            string userName = principal?.Username ?? "anonymous";
            if (!coordinator.TryGetMissingTagIds(id, userName, pageSkip, pageTake,
                    out int total, out IReadOnlyList<string> items))
                return Results.Json(new { error = "stream_not_found" },
                    statusCode: StatusCodes.Status404NotFound);
            return Results.Ok(new
            {
                streamId,
                missingCount = total,
                skip = pageSkip,
                take = pageTake,
                items,
            });
        });
    }

    private static async Task StreamAsync(
        HttpContext context,
        Func<IgwProject> projectProvider,
        Func<DataHub?> hubProvider,
        Func<RealtimeStreamCoordinator?> coordinatorProvider,
        Func<string>? projectRevisionProvider,
        Func<bool>? reloadingProvider)
    {
        if (reloadingProvider?.Invoke() == true)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "project_reloading");
            return;
        }
        TagValueStreamRequest? body;
        try
        {
            body = await ReadRequestAsync(context.Request, context.RequestAborted);
        }
        catch (RequestTooLargeException)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
            return;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json");
            return;
        }

        IgwProject project = projectProvider();
        var validation = TagValueStreamRequestValidator.Validate(body, project.Settings.TagStream);
        if (!validation.IsValid)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, validation.ErrorCode!);
            return;
        }

        var request = validation.Request!;
        var principal = context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] as PublicPlcPrincipal;
        if (!PublicPlcApiPolicy.AreReadBranchesAllowed(principal, request.TagIds)
            || !AreTopicsAllowed(principal, request.Topics))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "read_branch_forbidden");
            return;
        }

        string currentRevision = projectRevisionProvider?.Invoke() ?? JsonHash.Of(project);
        if (!string.Equals(request.ProjectRevision, currentRevision, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "project_revision_mismatch");
            return;
        }

        DataHub? hub = hubProvider();
        RealtimeStreamCoordinator? coordinator = coordinatorProvider();
        if (hub is null || coordinator is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "realtime_stream_unavailable");
            return;
        }

        RealtimeStreamLease lease;
        try
        {
            lease = await coordinator.OpenAsync(new RealtimeStreamOpenRequest(
                request.ClientId,
                principal?.Username ?? "anonymous",
                request.ProjectRevision,
                request.TagIds,
                request.Topics,
                request.IncludeSnapshot,
                request.HeartbeatSeconds), context.RequestAborted);
        }
        catch (RealtimeAdmissionException ex)
        {
            int status = ex.Code switch
            {
                "project-revision-mismatch" => StatusCodes.Status409Conflict,
                "too-many-streams" => StatusCodes.Status429TooManyRequests,
                "stream-disabled" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            };
            await WriteErrorAsync(context, status, ex.Code);
            return;
        }

        await using (lease)
        {
            TagValueSseWriter.ConfigureResponse(context.Response);
            var writer = new TagValueSseWriter(context.Response.Body, lease.StreamId.ToString("N"));
            try
            {
                await writer.WriteHelloAsync(new TagStreamHello(
                    TagValueStreamRequestValidator.ProtocolVersion,
                    lease.StreamId.ToString("N"),
                    BoundText(project.Name, 1_024),
                    currentRevision,
                    lease.AcceptedTagIds.Count,
                    lease.MissingTagIds.Count,
                    lease.MissingTagIds.Take(32).ToArray(),
                    DateTime.UtcNow), context.RequestAborted);
                coordinator.RecordSent(lease.StreamId, null, writer.LastFrameBytes);

                await StreamMessagesAsync(writer, lease, coordinator, currentRevision,
                    request.HeartbeatSeconds, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                context.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("IGW.RealtimeStream")
                    .LogWarning(ex, "实时数据流 {StreamId} 传输失败，连接将释放并由客户端重建", lease.StreamId);
                // 已确认网络写阻塞时不能再尝试向同一响应写错误帧，否则一个慢客户端
                // 会额外占用第二个写超时窗口；直接释放会话，由客户端退避重连。
                if (ex is not TimeoutException && context.Response.HasStarted && writer.LastSequence > 0)
                {
                    try
                    {
                        await writer.WriteTerminalErrorAsync(
                            "stream_write_failed",
                            "实时数据流传输失败，请重新连接。",
                            context.RequestAborted);
                    }
                    catch { }
                }
            }
        }
    }

    internal static async Task StreamMessagesAsync(
        TagValueFrameWriter writer,
        RealtimeStreamLease lease,
        RealtimeStreamCoordinator coordinator,
        string projectRevision,
        int heartbeatSeconds,
        CancellationToken cancellationToken)
    {
        using var readStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = lease.ReadAllAsync(readStop.Token).GetAsyncEnumerator(readStop.Token);
        SnapshotAccumulator? snapshot = null;
        Task<bool> pendingMove = enumerator.MoveNextAsync().AsTask();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task heartbeat = Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), cancellationToken);
                Task completed = await Task.WhenAny(pendingMove, heartbeat).ConfigureAwait(false);
                if (completed == pendingMove)
                {
                    if (!await pendingMove.ConfigureAwait(false)) break;
                    RealtimeOutboundMessage current = enumerator.Current;
                    long bytesBefore = writer.TotalBytes;
                    switch (current)
                    {
                        case RealtimeSnapshotBegin begin:
                            snapshot = new SnapshotAccumulator(begin.SnapshotId, begin.CapturedAtUtc, begin.TotalValues);
                            break;
                        case RealtimeSnapshotChunk chunk:
                            if (snapshot is null || !string.Equals(snapshot.Id, chunk.SnapshotId, StringComparison.Ordinal))
                                throw new InvalidDataException("snapshot_sequence_invalid");
                            snapshot.Values.AddRange(chunk.Values.Select(static value => ToValue(value.TagId, value.Value)));
                            break;
                        case RealtimeSnapshotComplete complete:
                            if (snapshot is null || !string.Equals(snapshot.Id, complete.SnapshotId, StringComparison.Ordinal))
                                throw new InvalidDataException("snapshot_sequence_invalid");
                            await writer.WriteSnapshotValuesAsync(
                                snapshot.Id, snapshot.CapturedAtUtc, snapshot.Values, cancellationToken);
                            snapshot = null;
                            break;
                        case RealtimeStateBatch batch:
                            await writer.WriteTagDeltaBatchesAsync(new TagStreamDelta(
                                DateTime.UtcNow,
                                batch.Values.Select(static value => ToValue(value.TagId, value.Value)).ToArray()), cancellationToken);
                            break;
                        case RealtimeOrderedEvent runtimeEvent:
                            await writer.WriteRuntimeEventAsync(runtimeEvent.Event, cancellationToken);
                            break;
                        case RealtimeResetRequired reset:
                            await writer.WriteResetRequiredAsync(new TagStreamResetRequired(
                                reset.Reason,
                                "增量流可能不完整，请重新获取快照。",
                                DateTime.UtcNow), cancellationToken);
                            break;
                        case RealtimeProjectChanged changed:
                            await writer.WriteProjectChangedAsync(new TagStreamProjectChanged(
                                changed.ProjectRevision,
                                "工程点表已经变化，请按新版本重新建立实时流。",
                                changed.ServerTimeUtc), cancellationToken);
                            coordinator.RecordSent(lease.StreamId, current, WrittenBytes(writer, bytesBefore));
                            return;
                    }
                    coordinator.RecordSent(lease.StreamId, current, WrittenBytes(writer, bytesBefore));
                    pendingMove = enumerator.MoveNextAsync().AsTask();
                }
                else
                {
                    var metrics = coordinator.SnapshotMetricsCached();
                    await writer.WriteHeartbeatAsync(new TagStreamHeartbeat(
                        DateTime.UtcNow,
                        writer.LastSequence,
                        projectRevision,
                        [],
                        new TagStreamSubscriptionHeartbeat(
                            metrics.Sessions.Sum(static session => (long)session.PendingStates + session.PendingEvents),
                            metrics.IngressDropped + metrics.Sessions.Sum(static session => session.DroppedEvents))),
                        cancellationToken);
                    coordinator.RecordSent(lease.StreamId, null, writer.LastFrameBytes);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            // An async iterator cannot be disposed while MoveNextAsync is still pending.
            // Heartbeat failures and cancellation must release the pending read first.
            readStop.Cancel();
            try { await pendingMove.ConfigureAwait(false); }
            catch (OperationCanceledException) when (readStop.IsCancellationRequested) { }
        }
    }

    private static TagStreamValue ToValue(string tagId, DataValue value) => new(
        tagId,
        BrowserValueJson.Normalize(value.Value),
        value.Kind.ToString(),
        value.Quality.SymbolicName,
        value.Quality.Code,
        value.IsGood,
        value.SourceTimestamp,
        value.ServerTimestamp);

    private static int WrittenBytes(TagValueFrameWriter writer, long bytesBefore) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, writer.TotalBytes - bytesBefore));

    private static string BoundText(string? value, int maxCharacters)
    {
        value ??= "";
        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }

    private static bool AreTopicsAllowed(PublicPlcPrincipal? principal, IReadOnlyList<string> topics) =>
        topics.Count == 0 || principal?.ReadBranches is not { Count: > 0 };

    private static async Task<TagValueStreamRequest?> ReadRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBytes) throw new RequestTooLargeException();
        int capacity = request.ContentLength is > 0 and <= MaxRequestBytes
            ? (int)request.ContentLength.Value
            : 4_096;
        await using var copy = new MemoryStream(capacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int total = 0;
            while (true)
            {
                int read = await request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total = checked(total + read);
                if (total > MaxRequestBytes) throw new RequestTooLargeException();
                await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            copy.Position = 0;
            return await JsonSerializer.DeserializeAsync<TagValueStreamRequest>(
                copy, TagValueStreamJson.Options, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string errorCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new { error = errorCode }, cancellationToken: context.RequestAborted);
    }

    private sealed class RequestTooLargeException : Exception { }

    private sealed class SnapshotAccumulator(string id, DateTime capturedAtUtc, int capacity)
    {
        public string Id { get; } = id;
        public DateTime CapturedAtUtc { get; } = capturedAtUtc;
        public List<TagStreamValue> Values { get; } = new(Math.Clamp(capacity, 0, 10_000));
    }
}
