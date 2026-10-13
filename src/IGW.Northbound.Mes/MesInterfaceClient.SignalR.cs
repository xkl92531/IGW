using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using IGW.Abstractions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace IGW.Northbound.Mes;

public sealed partial class MesInterfaceClient
{
    private HubConnection? _hub;
    private string _replyMethod = "";

    private async Task RunSignalRAsync(Uri uri, MesInterfaceListener settings, string subscriptionJson,
        Func<MesInterfaceMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ReceiveMethod) || settings.ReceiveMethod.Length > 256)
            throw new ArgumentException("SignalR receive method must contain 1 to 256 characters.", nameof(settings));
        if (settings.SubscribeMethod.Length > 256 || settings.ReplyMethod.Length > 256)
            throw new ArgumentException("SignalR method names cannot exceed 256 characters.", nameof(settings));
        var arguments = string.IsNullOrWhiteSpace(settings.SubscribeMethod) ? [] : ParseHubArguments(subscriptionJson);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sessionToken = session.Token;
        var receiveGate = new SemaphoreSlim(1, 1);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Keep the official JSON protocol, with a frame limit before deserialization.
        var frameLimit = checked(_maximum * 6 + 4096); // JSON string escaping can use six bytes per payload byte.
        var builder = new HubConnectionBuilder().WithUrl(uri, options =>
        {
            foreach (var header in settings.Headers) options.Headers.Add(header.Key, header.Value);
            options.ApplicationMaxBufferSize = frameLimit + 1L;
            options.TransportMaxBufferSize = frameLimit + 1L;
        });
        var protocol = new BoundedJsonHubProtocol(frameLimit, settings.ReceiveMethod);
        builder.Services.AddSingleton<IHubProtocol>(protocol);
        await using var hub = builder.Build();
        hub.ServerTimeout = _idleTimeout;
        hub.HandshakeTimeout = _idleTimeout;
        hub.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(0.25, Math.Min(15, settings.IdleTimeoutSec / 3.0)));
        lock (_state) { _hub = hub; _replyMethod = settings.ReplyMethod; }
        hub.Closed += async error =>
        {
            if (error is null)
            {
                try
                {
                    await protocol.Drained.WaitAsync(sessionToken).ConfigureAwait(false);
                    finished.TrySetResult();
                }
                catch (OperationCanceledException) { finished.TrySetCanceled(sessionToken); }
            }
            else finished.TrySetException(error);
        };
        using var registration = hub.On<JsonElement>(settings.ReceiveMethod, async argument =>
        {
            var acquired = false;
            var pendingBytes = protocol.BeginDispatch();
            try
            {
                if (finished.Task.IsCompleted || sessionToken.IsCancellationRequested) return;
                await receiveGate.WaitAsync(sessionToken).ConfigureAwait(false);
                acquired = true;
                var data = argument.ValueKind == JsonValueKind.String ? argument.GetString()! : argument.GetRawText();
                if (StrictUtf8.GetByteCount(data) > _maximum)
                    throw new InvalidDataException("SignalR argument exceeds the configured byte limit.");
                await onMessage(new MesInterfaceMessage(data, null, null), sessionToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // SignalR normally logs handler exceptions. Propagate them to the owner so inbox failures reconnect.
                finished.TrySetException(error);
            }
            finally
            {
                if (acquired) receiveGate.Release();
                protocol.CompleteDispatch(pendingBytes);
            }
        });
        try
        {
            await WithTimeout(async token => { await hub.StartAsync(token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
            if (finished.Task.IsCompleted) { await finished.Task.ConfigureAwait(false); return; }
            if (!string.IsNullOrWhiteSpace(settings.SubscribeMethod))
            {
                var subscribe = WithTimeout(async token =>
                {
                    await hub.InvokeCoreAsync(settings.SubscribeMethod, arguments, token).ConfigureAwait(false);
                    return true;
                }, session.Token);
                var completed = await Task.WhenAny(subscribe, finished.Task).ConfigureAwait(false);
                if (completed == finished.Task)
                {
                    session.Cancel();
                    try { await subscribe.ConfigureAwait(false); } catch (Exception) { }
                    await finished.Task.ConfigureAwait(false);
                    return;
                }
                await subscribe.ConfigureAwait(false);
            }
            await finished.Task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            session.Cancel();
            lock (_state) { _hub = null; _replyMethod = ""; }
        }
    }

    private async Task SendSignalRAsync(string json, CancellationToken ct)
    {
        var hub = _hub;
        var method = _replyMethod;
        if (hub?.State != HubConnectionState.Connected || string.IsNullOrWhiteSpace(method))
            throw new InvalidOperationException("An open SignalR connection and reply method are required for sending.");
        var arguments = ParseHubArguments(json);
        await WithTimeout(async token => { await hub.SendCoreAsync(method, arguments, token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    private object?[] ParseHubArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        if (StrictUtf8.GetByteCount(json) > _maximum)
            throw new InvalidDataException("SignalR arguments exceed the configured byte limit.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SignalR subscription and reply JSON must be an argument array.");
        return document.RootElement.EnumerateArray().Select(value => (object?)value.Clone()).ToArray();
    }

    private sealed class BoundedJsonHubProtocol(int maximum, string receiveMethod) : IHubProtocol
    {
        private readonly JsonHubProtocol _inner = new();
        private readonly object _pendingLock = new();
        private readonly Queue<long> _pendingSizes = new();
        private long _pendingBytes;
        private int _pendingCount;
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Drained { get { lock (_pendingLock) return _pendingCount == 0 ? Task.CompletedTask : _drained.Task; } }

        public long BeginDispatch()
        {
            lock (_pendingLock) return _pendingSizes.Dequeue();
        }

        public void CompleteDispatch(long bytes)
        {
            lock (_pendingLock)
            {
                _pendingBytes -= bytes;
                if (--_pendingCount == 0) _drained.TrySetResult();
            }
        }
        public string Name => _inner.Name;
        public int Version => _inner.Version;
        public TransferFormat TransferFormat => _inner.TransferFormat;
        public bool IsVersionSupported(int version) => _inner.IsVersionSupported(version);
        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) => _inner.GetMessageBytes(message);
        public void WriteMessage(HubMessage message, IBufferWriter<byte> output) => _inner.WriteMessage(message, output);

        public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
        {
            var end = input.PositionOf((byte)0x1e);
            var length = end.HasValue ? input.Slice(0, end.Value).Length : input.Length;
            if (length > maximum) throw new InvalidDataException("SignalR frame exceeds the configured byte limit.");
            var parsed = _inner.TryParseMessage(ref input, binder, out message);
            if (parsed && message is InvocationBindingFailureMessage)
                throw new InvalidDataException("SignalR invocation does not match the configured one-argument receive method.");
            if (parsed && message is InvocationMessage invocation && invocation.Target == receiveMethod)
            {
                lock (_pendingLock)
                {
                    // The official client uses an unbounded invocation channel. Bound admission before it reaches that channel.
                    if (_pendingCount >= 64 || _pendingBytes + length > Math.Max(65536L, maximum * 2L))
                        throw new InvalidDataException("SignalR receive backlog is full; reconnect and replay unacknowledged messages.");
                    if (_pendingCount++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingBytes += length;
                    _pendingSizes.Enqueue(length);
                }
            }
            return parsed;
        }
    }
}
