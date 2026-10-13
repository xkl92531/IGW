using System.Text;
using System.Text.Json;
using IGW.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace IGW.Northbound.RestApi;

internal class TagValueFrameWriter
{
    public const int MaxSnapshotValuesPerChunk = 256;
    public const int MaxDeltaValuesPerFrame = 256;
    public const int MaxEventBytes = 1024 * 1024;
    private const int MaxPayloadBytes = MaxEventBytes - 512;
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Func<RealtimeFrame, CancellationToken, Task<int>> _send;
    private readonly string _streamId;
    private readonly TimeSpan _writeTimeout;
    private long _sequence;
    private int _lastFrameBytes;
    private long _totalBytes;
    private bool _helloWritten;

    protected virtual string TimeoutErrorCode => "realtime_write_timeout";

    public long LastSequence => Volatile.Read(ref _sequence);
    public int LastFrameBytes => Volatile.Read(ref _lastFrameBytes);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public TagValueFrameWriter(Func<RealtimeFrame, CancellationToken, Task<int>> send, string streamId, TimeSpan? writeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        _send = send;
        _streamId = streamId;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
        if (_writeTimeout <= TimeSpan.Zero && _writeTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(writeTimeout), "write_timeout_must_be_positive");
    }

    public async Task<long> WriteHelloAsync(TagStreamHello hello, CancellationToken cancellationToken)
    {
        if (_helloWritten) throw new InvalidOperationException("hello_already_written");
        long sequence = await WriteFrameAsync("hello", hello, cancellationToken);
        _helloWritten = true;
        return sequence;
    }

    public Task<long> WriteSnapshotAsync(TagStreamSnapshot snapshot, CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Values.Count > MaxSnapshotValuesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "snapshot_chunk_too_large");
        if (snapshot.ChunkCount < 1
            || snapshot.ChunkIndex < 0
            || snapshot.ChunkIndex >= snapshot.ChunkCount
            || snapshot.Complete != (snapshot.ChunkIndex == snapshot.ChunkCount - 1))
            throw new ArgumentException("invalid_snapshot_chunk", nameof(snapshot));

        return WriteFrameAsync("snapshot", snapshot, cancellationToken);
    }

    public Task<long> WriteSnapshotBeginAsync(
        TagStreamSnapshotBegin snapshot,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalValues < 0 || snapshot.ChunkCount < 0)
            throw new ArgumentException("invalid_snapshot_begin", nameof(snapshot));
        return WriteFrameAsync("snapshot-begin", snapshot, cancellationToken);
    }

    public Task<long> WriteSnapshotChunkAsync(
        TagStreamSnapshotChunk snapshot,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ChunkIndex < 0)
            throw new ArgumentException("invalid_snapshot_chunk", nameof(snapshot));
        if (snapshot.Values.Count > MaxSnapshotValuesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "snapshot_chunk_too_large");
        return WriteFrameAsync("snapshot-chunk", snapshot, cancellationToken);
    }

    public Task<long> WriteSnapshotCompleteAsync(
        TagStreamSnapshotComplete snapshot,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TotalValues < 0 || snapshot.ChunkCount < 0)
            throw new ArgumentException("invalid_snapshot_complete", nameof(snapshot));
        return WriteFrameAsync("snapshot-complete", snapshot, cancellationToken);
    }

    public Task<long> WriteDeltaAsync(TagStreamDelta delta, CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("delta", delta, cancellationToken);
    }

    public Task<long> WriteTagDeltaAsync(TagStreamDelta delta, CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("tag-delta", delta, cancellationToken);
    }

    public async Task<long> WriteTagDeltaBatchesAsync(
        TagStreamDelta delta,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Values.Count == 0)
            return await WriteFrameAsync("tag-delta", delta, cancellationToken).ConfigureAwait(false);

        long lastSequence = LastSequence;
        ValueChunkPlan plan = PlanValueChunks(
            delta.Values,
            values => new TagStreamDelta(delta.EmittedAtUtc, values));
        foreach (TagStreamValue oversized in plan.Oversized)
        {
            lastSequence = await WriteValueTooLargeAsync(
                oversized, "tag-delta", cancellationToken).ConfigureAwait(false);
        }
        foreach (IReadOnlyList<TagStreamValue> values in plan.Chunks)
            lastSequence = await WriteFrameAsync(
                "tag-delta",
                new TagStreamDelta(delta.EmittedAtUtc, values),
                cancellationToken).ConfigureAwait(false);
        return lastSequence;
    }

    public async Task<long> WriteSnapshotValuesAsync(
        string snapshotId,
        DateTime capturedAtUtc,
        IReadOnlyList<TagStreamValue> values,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(values);
        ValueChunkPlan plan = PlanValueChunks(
            values,
            chunk => new TagStreamSnapshotChunk(snapshotId, 99_999, chunk));
        int deliveredValues = plan.Chunks.Sum(static chunk => chunk.Count);
        await WriteSnapshotBeginAsync(new TagStreamSnapshotBegin(
            snapshotId, deliveredValues, plan.Chunks.Count, capturedAtUtc), cancellationToken).ConfigureAwait(false);
        long lastSequence = LastSequence;
        foreach (TagStreamValue oversized in plan.Oversized)
            lastSequence = await WriteValueTooLargeAsync(
                oversized, "snapshot", cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < plan.Chunks.Count; index++)
        {
            lastSequence = await WriteSnapshotChunkAsync(new TagStreamSnapshotChunk(
                snapshotId, index, plan.Chunks[index]), cancellationToken).ConfigureAwait(false);
        }
        lastSequence = await WriteSnapshotCompleteAsync(new TagStreamSnapshotComplete(
            snapshotId, deliveredValues, plan.Chunks.Count, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        return lastSequence;
    }

    public Task<long> WriteTerminalErrorAsync(
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        if (code.Length > 128) code = code[..128];
        if (message.Length > 2_048) message = message[..2_048];
        return WriteFrameAsync("stream-error",
            new TagStreamTerminalError(code, message, DateTime.UtcNow), cancellationToken);
    }

    private Task<long> WriteValueTooLargeAsync(
        TagStreamValue value,
        string context,
        CancellationToken cancellationToken)
    {
        int bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, TagValueStreamJson.Options).Length;
        }
        catch { bytes = int.MaxValue; }
        return WriteFrameAsync("value-too-large",
            new TagStreamValueTooLarge(value.Id, bytes, context, DateTime.UtcNow), cancellationToken);
    }

    private static ValueChunkPlan PlanValueChunks<TPayload>(
        IReadOnlyList<TagStreamValue> values,
        Func<IReadOnlyList<TagStreamValue>, TPayload> payloadFactory)
    {
        var chunks = new List<IReadOnlyList<TagStreamValue>>();
        var oversized = new List<TagStreamValue>();
        for (int offset = 0; offset < values.Count; offset += MaxDeltaValuesPerFrame)
        {
            int count = Math.Min(MaxDeltaValuesPerFrame, values.Count - offset);
            var initial = new TagStreamValue[count];
            for (int index = 0; index < count; index++) initial[index] = values[offset + index];
            Split(initial);
        }
        return new ValueChunkPlan(chunks, oversized);

        void Split(IReadOnlyList<TagStreamValue> candidate)
        {
            int payloadBytes;
            try
            {
                payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
                    payloadFactory(candidate), TagValueStreamJson.Options).Length;
            }
            catch
            {
                payloadBytes = int.MaxValue;
            }
            if (payloadBytes <= MaxPayloadBytes)
            {
                chunks.Add(candidate);
                return;
            }
            if (candidate.Count == 1)
            {
                oversized.Add(candidate[0]);
                return;
            }
            int middle = candidate.Count / 2;
            Split(candidate.Take(middle).ToArray());
            Split(candidate.Skip(middle).ToArray());
        }
    }

    private sealed record ValueChunkPlan(
        IReadOnlyList<IReadOnlyList<TagStreamValue>> Chunks,
        IReadOnlyList<TagStreamValue> Oversized);

    public Task<long> WriteRuntimeEventAsync(RealtimeRuntimeEvent value, CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("runtime-event", value, cancellationToken);
    }

    public Task<long> WriteProjectChangedAsync(TagStreamProjectChanged value, CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("project-changed", value, cancellationToken);
    }

    public Task<long> WriteHeartbeatAsync(TagStreamHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("heartbeat", heartbeat, cancellationToken);
    }

    public Task<long> WriteResetRequiredAsync(
        TagStreamResetRequired resetRequired,
        CancellationToken cancellationToken)
    {
        EnsureHello();
        return WriteFrameAsync("reset-required", resetRequired, cancellationToken);
    }

    private void EnsureHello()
    {
        if (!_helloWritten) throw new InvalidOperationException("hello_must_be_first");
    }

    private async Task<long> WriteFrameAsync<T>(
        string eventName,
        T payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        long nextSequence = checked(_sequence + 1);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, TagValueStreamJson.Options);
        if (json.Length > MaxPayloadBytes) throw new InvalidOperationException("event_too_large");
        var frame = new RealtimeFrame(eventName, $"{_streamId}:{nextSequence}", Utf8.GetString(json));
        int frameLength;

        using var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_writeTimeout != Timeout.InfiniteTimeSpan) writeBudget.CancelAfter(_writeTimeout);
        try
        {
            frameLength = await _send(frame, writeBudget.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                                                    && writeBudget.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutErrorCode, ex);
        }
        Volatile.Write(ref _lastFrameBytes, frameLength);
        Interlocked.Add(ref _totalBytes, frameLength);
        _sequence = nextSequence;
        return nextSequence;
    }
}
