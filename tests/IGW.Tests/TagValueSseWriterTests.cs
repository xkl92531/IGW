using System.Text;
using System.Text.Json;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace IGW.Tests;

public sealed class TagValueSseWriterTests
{
    [Fact]
    public void Configures_non_buffered_non_cached_sse_response_headers()
    {
        var context = new DefaultHttpContext();
        var compression = new TestHttpsCompressionFeature();
        context.Features.Set<IHttpsCompressionFeature>(compression);

        TagValueSseWriter.ConfigureResponse(context.Response);

        Assert.Equal("text/event-stream; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
        Assert.Equal(HttpsCompressionMode.DoNotCompress, compression.Mode);
    }

    [Fact]
    public async Task Hello_is_first_and_event_ids_are_monotonic()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");

        long helloSequence = await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        long deltaSequence = await writer.WriteDeltaAsync(
            new TagStreamDelta(
                new DateTime(2026, 8, 21, 2, 0, 1, DateTimeKind.Utc),
                []),
            CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(1, helloSequence);
        Assert.Equal(2, deltaSequence);
        Assert.StartsWith("event: hello\nid: stream-1:1\ndata: ", text, StringComparison.Ordinal);
        Assert.Contains("\n\nevent: delta\nid: stream-1:2\ndata: ", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Realtime_protocol_uses_explicit_snapshot_phases_and_tag_delta_names()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");

        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        await writer.WriteSnapshotBeginAsync(
            new TagStreamSnapshotBegin("snapshot-1", 1, 1, DateTime.UtcNow),
            CancellationToken.None);
        await writer.WriteSnapshotChunkAsync(
            new TagStreamSnapshotChunk("snapshot-1", 0, [Value("C.P.Tag")]),
            CancellationToken.None);
        await writer.WriteSnapshotCompleteAsync(
            new TagStreamSnapshotComplete("snapshot-1", 1, 1, DateTime.UtcNow),
            CancellationToken.None);
        await writer.WriteTagDeltaAsync(
            new TagStreamDelta(DateTime.UtcNow, [Value("C.P.Tag")]),
            CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(
            ["hello", "snapshot-begin", "snapshot-chunk", "snapshot-complete", "tag-delta"],
            text.Split('\n').Where(line => line.StartsWith("event: ", StringComparison.Ordinal))
                .Select(line => line["event: ".Length..]).ToArray());
        Assert.Equal(
            ["stream-1:1", "stream-1:2", "stream-1:3", "stream-1:4", "stream-1:5"],
            text.Split('\n').Where(line => line.StartsWith("id: ", StringComparison.Ordinal))
                .Select(line => line["id: ".Length..]).ToArray());
    }

    [Fact]
    public async Task Tag_deltas_are_split_into_ordered_frames_of_at_most_256_values()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        var values = Enumerable.Range(0, 600).Select(i => Value($"C.P.Tag{i}")).ToArray();

        await writer.WriteTagDeltaBatchesAsync(
            new TagStreamDelta(DateTime.UtcNow, values),
            CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        string[] events = text.Split('\n')
            .Where(line => line.StartsWith("event: ", StringComparison.Ordinal))
            .Select(line => line["event: ".Length..])
            .ToArray();
        int[] counts = text.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Skip(1)
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement.GetProperty("values").GetArrayLength())
            .ToArray();

        Assert.Equal(["hello", "tag-delta", "tag-delta", "tag-delta"], events);
        Assert.Equal([256, 256, 88], counts);
        Assert.All(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries), frame =>
            Assert.True(Encoding.UTF8.GetByteCount(frame + "\n\n") <= TagValueSseWriter.MaxEventBytes));
    }

    [Fact]
    public async Task Medium_sized_delta_values_are_split_by_serialized_utf8_bytes()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        string payload = new('中', 3_000);
        var values = Enumerable.Range(0, 256)
            .Select(index => Value($"C.P.Tag{index}", payload))
            .ToArray();

        await writer.WriteTagDeltaBatchesAsync(
            new TagStreamDelta(DateTime.UtcNow, values),
            CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        string[] frames = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(frames.Count(frame => frame.StartsWith("event: tag-delta", StringComparison.Ordinal)) > 1);
        Assert.All(frames, frame =>
            Assert.True(Encoding.UTF8.GetByteCount(frame + "\n\n") <= TagValueSseWriter.MaxEventBytes));
    }

    [Fact]
    public async Task One_oversized_delta_value_emits_value_too_large_and_keeps_stream_writable()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);

        await writer.WriteTagDeltaBatchesAsync(
            new TagStreamDelta(DateTime.UtcNow,
                [Value("C.P.Huge", new string('x', TagValueSseWriter.MaxEventBytes + 1))]),
            CancellationToken.None);
        await writer.WriteHeartbeatAsync(new TagStreamHeartbeat(
            DateTime.UtcNow, writer.LastSequence, "sha256:abc", [],
            new TagStreamSubscriptionHeartbeat(0, 0)), CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("event: value-too-large", text, StringComparison.Ordinal);
        Assert.Contains("event: heartbeat", text, StringComparison.Ordinal);
        Assert.All(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries), frame =>
            Assert.True(Encoding.UTF8.GetByteCount(frame + "\n\n") <= TagValueSseWriter.MaxEventBytes));
    }

    [Fact]
    public async Task Snapshot_values_are_rechunked_by_bytes_and_oversized_values_do_not_break_completion()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        var values = Enumerable.Range(0, 256)
            .Select(index => Value($"C.P.Tag{index}", new string('中', 3_000)))
            .Append(Value("C.P.Huge", new string('x', TagValueSseWriter.MaxEventBytes + 1)))
            .ToArray();

        await writer.WriteSnapshotValuesAsync(
            "snapshot-1", DateTime.UtcNow, values, CancellationToken.None);

        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("event: snapshot-begin", text, StringComparison.Ordinal);
        Assert.Contains("event: snapshot-complete", text, StringComparison.Ordinal);
        Assert.Contains("event: value-too-large", text, StringComparison.Ordinal);
        Assert.True(text.Split('\n').Count(line => line == "event: snapshot-chunk") > 1);
        Assert.All(text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries), frame =>
            Assert.True(Encoding.UTF8.GetByteCount(frame + "\n\n") <= TagValueSseWriter.MaxEventBytes));
    }

    [Fact]
    public async Task A_stalled_frame_write_is_cancelled_and_times_out()
    {
        await using var output = new BlockAfterFirstWriteStream();
        var writer = new TagValueSseWriter(output, "stream-1", TimeSpan.FromMilliseconds(50));
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => writer.WriteTagDeltaAsync(
            new TagStreamDelta(DateTime.UtcNow, [Value("C.P.Tag")]),
            CancellationToken.None));

        Assert.Equal("sse_write_timeout", error.Message);
        Assert.True(output.BlockedWriteCancellationObserved);
    }

    [Fact]
    public async Task Rejects_any_event_before_hello()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteHeartbeatAsync(
            new TagStreamHeartbeat(
                DateTime.UtcNow,
                0,
                "sha256:abc",
                [],
                new TagStreamSubscriptionHeartbeat(0, 0)),
            CancellationToken.None));

        Assert.Equal("hello_must_be_first", error.Message);
    }

    [Fact]
    public async Task Rejects_duplicate_hello()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteHelloAsync(Hello(), CancellationToken.None));

        Assert.Equal("hello_already_written", error.Message);
    }

    [Fact]
    public async Task Snapshot_chunk_is_limited_to_256_values()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        var values = Enumerable.Range(0, 257)
            .Select(i => Value($"C.P.Tag{i}"))
            .ToArray();

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            writer.WriteSnapshotAsync(
                new TagStreamSnapshot(0, 2, false, values),
                CancellationToken.None));

        Assert.Equal("snapshot", error.ParamName);
    }

    [Fact]
    public async Task Rejects_invalid_snapshot_chunk_shape()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteSnapshotAsync(
            new TagStreamSnapshot(1, 1, true, []),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteSnapshotAsync(
            new TagStreamSnapshot(0, 2, true, []),
            CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_an_event_larger_than_one_mebibyte_without_partial_write()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        long lengthBefore = output.Length;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteResetRequiredAsync(
                new TagStreamResetRequired(
                    "ProtocolError",
                    new string('x', TagValueSseWriter.MaxEventBytes + 1),
                    DateTime.UtcNow),
                CancellationToken.None));

        Assert.Equal("event_too_large", error.Message);
        Assert.Equal(lengthBefore, output.Length);
    }

    [Fact]
    public async Task Json_newlines_are_escaped_so_data_stays_on_one_line()
    {
        await using var output = new MemoryStream();
        var writer = new TagValueSseWriter(output, "stream-1");
        await writer.WriteHelloAsync(Hello(), CancellationToken.None);
        await writer.WriteResetRequiredAsync(
            new TagStreamResetRequired("ProtocolError", "line1\nline2", DateTime.UtcNow),
            CancellationToken.None);

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n');
        Assert.Contains(lines, line => line.StartsWith("data: ", StringComparison.Ordinal)
            && line.Contains("line1\\nline2", StringComparison.Ordinal));
        Assert.DoesNotContain("line2\"}", lines);
    }

    private static TagStreamHello Hello() => new(
        1,
        "stream-1",
        "Project",
        "sha256:abc",
        1,
        0,
        [],
        new DateTime(2026, 8, 21, 2, 0, 0, DateTimeKind.Utc));

    private static TagStreamValue Value(string id) => new(
        id,
        true,
        "Bool",
        "Good",
        0,
        true,
        DateTime.UtcNow,
        DateTime.UtcNow);

    private static TagStreamValue Value(string id, object value) => new(
        id,
        value,
        "Ref",
        "Good",
        0,
        true,
        DateTime.UtcNow,
        DateTime.UtcNow);

    private sealed class TestHttpsCompressionFeature : IHttpsCompressionFeature
    {
        public HttpsCompressionMode Mode { get; set; }
    }

    private sealed class BlockAfterFirstWriteStream : Stream
    {
        private int _writes;
        public bool BlockedWriteCancellationObserved { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1) return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                BlockedWriteCancellationObserved = true;
                throw;
            }
        }
    }
}
