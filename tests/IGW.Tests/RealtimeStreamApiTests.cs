using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class RealtimeStreamApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_failure_drains_pending_read_before_disposing_the_stream(bool cancelled)
    {
        await using var fixture = await Fixture.StartAsync([]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string revision = JsonHash.Of(fixture.Project);
        await using var lease = await fixture.Coordinator.OpenAsync(new RealtimeStreamOpenRequest(
            "heartbeat-exit", "reader", revision, [], ["devices"], false, 5), stop.Token);
        Exception expected = cancelled ? new OperationCanceledException(stop.Token) : new IOException("connection closed");
        var writer = new TagValueFrameWriter((frame, token) =>
        {
            if (frame.Event == "hello") return Task.FromResult(1);
            if (cancelled) stop.Cancel();
            return Task.FromException<int>(expected);
        }, lease.StreamId.ToString("N"));
        await writer.WriteHelloAsync(new TagStreamHello(1, lease.StreamId.ToString("N"),
            "test", revision, 0, 0, [], DateTime.UtcNow), stop.Token);

        // A zero heartbeat forces a send while MoveNextAsync is still waiting for data.
        Exception? actual = await Record.ExceptionAsync(() => RealtimeStreamApi.StreamMessagesAsync(
            writer, lease, fixture.Coordinator, revision, 0, stop.Token));

        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(actual);
        else Assert.Same(expected, actual);
        await lease.DisposeAsync();
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().ActiveStreams);
    }

    [Fact]
    public async Task Authorized_stream_writes_hello_and_complete_snapshot_before_waiting_for_deltas()
    {
        await using var fixture = await Fixture.StartAsync(["Line.Plc.Tag"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPlcApiPolicy.RealtimeStreamPath)
        {
            Content = JsonContent.Create(fixture.Request("Line.Plc.Tag")),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var events = await ReadEventsAsync(reader, 4, timeout.Token);
        Assert.Equal(["hello", "snapshot-begin", "snapshot-chunk", "snapshot-complete"], events);
    }

    [Fact]
    public async Task Unauthorized_branch_is_rejected_before_sse_headers_without_echoing_tag_name()
    {
        await using var fixture = await Fixture.StartAsync(["Secret.Plc.Password"], ["Line.Plc"]);

        using var response = await fixture.Client.PostAsJsonAsync(
            PublicPlcApiPolicy.RealtimeStreamPath,
            fixture.Request("Secret.Plc.Password"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Secret.Plc.Password", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sibling_prefix_branch_is_rejected_before_sse_headers()
    {
        await using var fixture = await Fixture.StartAsync(["Line.Plc.DB100.Value"], ["Line.Plc.DB10"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPlcApiPolicy.RealtimeStreamPath)
        {
            Content = JsonContent.Create(fixture.Request("Line.Plc.DB100.Value")),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Stale_project_revision_is_conflict_before_sse_headers()
    {
        await using var fixture = await Fixture.StartAsync(["Line.Plc.Tag"]);
        var body = fixture.Request("Line.Plc.Tag");
        body.ProjectRevision = "stale";

        using var response = await fixture.Client.PostAsJsonAsync(PublicPlcApiPolicy.RealtimeStreamPath, body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Network_stream_bounds_delta_frames_and_releases_session_after_client_disconnect()
    {
        string[] tagIds = Enumerable.Range(0, 600).Select(index => $"Line.Plc.Tag{index}").ToArray();
        await using var fixture = await Fixture.StartAsync(tagIds);
        var body = fixture.Request(tagIds);
        body.IncludeSnapshot = false;
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPlcApiPolicy.RealtimeStreamPath)
        {
            Content = JsonContent.Create(body),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        foreach (string tagId in tagIds)
            fixture.Hub.Update(tagId, DataValue.GoodAt(9, DateTime.UtcNow));

        var received = new HashSet<string>(StringComparer.Ordinal);
        var deltaSizes = new List<int>();
        while (received.Count < tagIds.Length)
        {
            var frame = await ReadFrameAsync(reader, timeout.Token);
            if (frame.Event != "tag-delta") continue;
            using var json = JsonDocument.Parse(frame.Data);
            var values = json.RootElement.GetProperty("values");
            deltaSizes.Add(values.GetArrayLength());
            foreach (var value in values.EnumerateArray())
                received.Add(value.GetProperty("id").GetString()!);
        }

        Assert.All(deltaSizes, count => Assert.InRange(count, 1, TagValueSseWriter.MaxDeltaValuesPerFrame));
        Assert.Equal(tagIds.Length, received.Count);
        await stream.DisposeAsync();
        response.Dispose();

        for (int attempt = 0; attempt < 100 && fixture.Coordinator.SnapshotMetrics().ActiveStreams != 0; attempt++)
            await Task.Delay(20);
        Assert.Equal(0, fixture.Coordinator.SnapshotMetrics().ActiveStreams);
    }

    [Fact]
    public async Task Hello_with_ten_thousand_missing_maximum_length_ids_is_byte_bounded_and_reports_count()
    {
        await using var fixture = await Fixture.StartAsync([]);
        string suffix = new('x', TagValueStreamRequestValidator.MaxTagIdLength - 16);
        string[] missing = Enumerable.Range(0, 10_000)
            .Select(index => $"Missing.{index:D5}.{suffix}")
            .ToArray();
        var body = fixture.Request(missing);
        body.IncludeSnapshot = false;
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPlcApiPolicy.RealtimeStreamPath)
        {
            Content = JsonContent.Create(body),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        var hello = await ReadFrameAsync(reader, timeout.Token);
        using var json = JsonDocument.Parse(hello.Data);

        Assert.Equal("hello", hello.Event);
        Assert.Equal(10_000, json.RootElement.GetProperty("missingTagCount").GetInt32());
        Assert.InRange(json.RootElement.GetProperty("missingTagIdSample").GetArrayLength(), 1, 100);
        Assert.True(Encoding.UTF8.GetByteCount($"event: hello\ndata: {hello.Data}\n\n") <=
                    TagValueSseWriter.MaxEventBytes);
    }

    [Fact]
    public async Task Complete_missing_ids_are_available_through_an_owner_scoped_paged_endpoint()
    {
        string[] missing = Enumerable.Range(0, 120).Select(index => $"Missing.Tag{index:D3}").ToArray();
        await using var fixture = await Fixture.StartAsync([]);
        var body = fixture.Request(missing);
        body.IncludeSnapshot = false;
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicPlcApiPolicy.RealtimeStreamPath)
        {
            Content = JsonContent.Create(body),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var hello = await ReadFrameAsync(reader, timeout.Token);
        using var helloJson = JsonDocument.Parse(hello.Data);
        string streamId = helloJson.RootElement.GetProperty("streamId").GetString()!;

        using var page = await fixture.Client.GetAsync(
            $"{PublicPlcApiPolicy.RealtimeStreamMissingPath}?streamId={streamId}&skip=10&take=3",
            timeout.Token);
        using var json = JsonDocument.Parse(await page.Content.ReadAsStringAsync(timeout.Token));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(120, json.RootElement.GetProperty("missingCount").GetInt32());
        Assert.Equal(["Missing.Tag010", "Missing.Tag011", "Missing.Tag012"],
            json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    private static async Task<string[]> ReadEventsAsync(
        StreamReader reader,
        int count,
        CancellationToken cancellationToken)
    {
        var events = new List<string>(count);
        while (events.Count < count)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) events.Add(line["event: ".Length..]);
        }
        return events.ToArray();
    }

    private static async Task<(string Event, string Data)> ReadFrameAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string eventName = "";
        string data = "";
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) throw new EndOfStreamException();
            if (line.Length == 0 && eventName.Length > 0) return (eventName, data);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line["data: ".Length..];
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            WebApplication app,
            HttpClient client,
            DataHub hub,
            RealtimeStreamCoordinator coordinator,
            IgwProject project,
            IReadOnlyList<string> readBranches)
        {
            App = app;
            Client = client;
            Hub = hub;
            Coordinator = coordinator;
            Project = project;
            ReadBranches = readBranches;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }
        public DataHub Hub { get; }
        public RealtimeStreamCoordinator Coordinator { get; }
        public IgwProject Project { get; }
        public IReadOnlyList<string> ReadBranches { get; }

        public static async Task<Fixture> StartAsync(
            IReadOnlyList<string> tagIds,
            IReadOnlyList<string>? readBranches = null)
        {
            var project = new IgwProject { Name = "Realtime test" };
            var hub = new DataHub(shards: 1, busCapacityPerShard: 16_384);
            foreach (string tagId in tagIds)
            {
                hub.Register(tagId, new TagDef { Name = tagId, Address = tagId, DataType = IgwDataType.Int32 });
                hub.Update(tagId, DataValue.GoodAt(7, DateTime.UtcNow));
            }
            string revision = JsonHash.Of(project);
            var coordinator = new RealtimeStreamCoordinator(
                hub, revision, project.Settings.TagStream, NullLogger<RealtimeStreamCoordinator>.Instance);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            var branches = readBranches ?? [];
            app.Use(async (context, next) =>
            {
                context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] =
                    new PublicPlcPrincipal("reader", false, branches);
                await next();
            });
            app.MapRealtimeStreamApi(() => project, () => hub, () => coordinator);
            await app.StartAsync();
            return new Fixture(app, app.GetTestClient(), hub, coordinator, project, branches);
        }

        public TagValueStreamRequest Request(params string[] tagIds) => new()
        {
            ProtocolVersion = 1,
            ClientId = "test-client",
            ProjectRevision = JsonHash.Of(Project),
            TagIds = tagIds.ToList(),
            IncludeSnapshot = true,
            HeartbeatSeconds = 5,
        };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Coordinator.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }
}
