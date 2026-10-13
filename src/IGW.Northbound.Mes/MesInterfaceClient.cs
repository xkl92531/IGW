using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

public sealed record MesInterfaceMessage(string Data, string? Id, string? Event);

/// <summary>Runs one bounded, cancellable backend connection. Reconnect and durable delivery belong to the caller.</summary>
public sealed partial class MesInterfaceClient(HttpClient http) : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _state = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket? _socket;
    private bool _running;
    private bool _disposed;
    private int _maximum;
    private TimeSpan _idleTimeout;
    private CancellationToken _connectionToken;

    public async Task RunAsync(MesInterfaceListener settings, string? lastEventId, string subscriptionJson,
        Func<MesInterfaceMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onMessage);
        var isWebSocket = string.Equals(settings.Protocol, "WebSocket", StringComparison.OrdinalIgnoreCase);
        var isSignalR = string.Equals(settings.Protocol, "SignalR", StringComparison.OrdinalIgnoreCase);
        if (!isWebSocket && !isSignalR && !string.Equals(settings.Protocol, "SSE", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Listener protocol must be SignalR, WebSocket or SSE.", nameof(settings));
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri)
            || (isWebSocket ? uri.Scheme is not ("ws" or "wss") : uri.Scheme is not ("http" or "https"))
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Listener URL does not match its protocol or contains a fragment.", nameof(settings));
        if (settings.MaxMessageBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(settings), "Message limit must be between 1 byte and 16 MiB.");
        if (settings.IdleTimeoutSec is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(settings), "Idle timeout must be between 1 and 86400 seconds.");

        CancellationTokenSource connection;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) throw new InvalidOperationException("A listener connection is already running.");
            connection = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            _connectionToken = connection.Token;
            _running = true;
            _maximum = settings.MaxMessageBytes;
            _idleTimeout = TimeSpan.FromSeconds(settings.IdleTimeoutSec);
        }
        using (connection)
        {
            try
            {
                if (isSignalR)
                    await RunSignalRAsync(uri, settings, subscriptionJson, onMessage, connection.Token).ConfigureAwait(false);
                else if (isWebSocket)
                    await RunWebSocketAsync(uri, settings, subscriptionJson, onMessage, connection.Token).ConfigureAwait(false);
                else
                    await RunSseAsync(uri, settings, lastEventId, onMessage, connection.Token).ConfigureAwait(false);
            }
            // 停止/释放时 Abort 会让挂起的接收抛出 ObjectDisposedException 或 WebSocketException，时机不定；
            // 连接已被取消时统一按取消上报，调用方不会把正常停机当成连接故障。
            catch (Exception error) when (error is not OperationCanceledException && connection.IsCancellationRequested)
            {
                throw new OperationCanceledException("Interface connection was stopped.", error, connection.Token);
            }
            finally
            {
                await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    lock (_state)
                    {
                        _socket?.Dispose();
                        _socket = null;
                        _running = false;
                    }
                }
                finally { _sendGate.Release(); }
            }
        }
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        CancellationTokenSource send;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            send = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token, _connectionToken);
        }
        using (send)
        {
            await _sendGate.WaitAsync(send.Token).ConfigureAwait(false);
            try
            {
                if (_hub is not null)
                {
                    await SendSignalRAsync(json, send.Token).ConfigureAwait(false);
                    return;
                }
                var socket = _socket;
                if (socket?.State != WebSocketState.Open)
                    throw new InvalidOperationException("No open WebSocket connection is available for sending.");
                if (StrictUtf8.GetByteCount(json) > _maximum)
                    throw new InvalidDataException("Outgoing WebSocket message exceeds the configured byte limit.");
                var bytes = StrictUtf8.GetBytes(json);
                await WithTimeout(async token =>
                {
                    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                    return true;
                }, send.Token).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }
    }

    private async Task RunWebSocketAsync(Uri uri, MesInterfaceListener settings, string subscriptionJson,
        Func<MesInterfaceMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        lock (_state)
        {
            if (_disposed) { socket.Dispose(); ct.ThrowIfCancellationRequested(); }
            _socket = socket;
        }
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, settings.IdleTimeoutSec / 3)));
        foreach (var header in settings.Headers) socket.Options.SetRequestHeader(header.Key, header.Value);
        await WithTimeout(async token => { await socket.ConnectAsync(uri, token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subscriptionJson)) await SendAsync(subscriptionJson, ct).ConfigureAwait(false);

        var buffer = new byte[Math.Min(8192, _maximum)];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await WithTimeout(token => socket.ReceiveAsync(buffer.AsMemory(), token).AsTask(), ct).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await WithTimeout(async token =>
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait(false);
                            return true;
                        }, ct).ConfigureAwait(false);
                }
                finally { _sendGate.Release(); }
                return;
            }
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Interface WebSocket only accepts UTF-8 text messages.");
            if (message.Length + received.Count > _maximum)
                throw new InvalidDataException("WebSocket message exceeds the configured byte limit.");
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;
            var data = StrictUtf8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            await onMessage(new MesInterfaceMessage(data, null, null), ct).ConfigureAwait(false);
        }
    }

    private async Task RunSseAsync(Uri uri, MesInterfaceListener settings, string? lastEventId,
        Func<MesInterfaceMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in settings.Headers) request.Headers.Add(header.Key, header.Value);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Remove("Last-Event-ID");
        if (!string.IsNullOrEmpty(lastEventId)) request.Headers.Add("Last-Event-ID", lastEventId);
        using var response = await WithTimeout(token => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SSE endpoint must return Content-Type: text/event-stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Min(8192, _maximum)];
        using var line = new MemoryStream();
        var data = new StringBuilder();
        var id = lastEventId;
        string? eventName = null;
        var hasData = false;
        var firstLine = true;
        var skipLf = false;
        var eventBytes = 0;
        while (true)
        {
            var count = await WithTimeout(token => stream.ReadAsync(buffer.AsMemory(), token).AsTask(), ct).ConfigureAwait(false);
            if (count == 0) return; // An event without its terminating blank line is incomplete.
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var value = buffer[i];
                if (skipLf && value == '\n') { skipLf = false; continue; }
                skipLf = value == '\r';
                if (++eventBytes > _maximum)
                    throw new InvalidDataException("SSE event exceeds the configured byte limit.");
                if (value is not (byte)'\r' and not (byte)'\n') { line.WriteByte(value); continue; }
                var text = StrictUtf8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
                line.SetLength(0);
                if (firstLine) { text = text.TrimStart('\uFEFF'); firstLine = false; }
                if (text.Length == 0)
                {
                    if (hasData)
                        await onMessage(new MesInterfaceMessage(data.ToString(0, data.Length - 1), id, eventName), ct).ConfigureAwait(false);
                    data.Clear();
                    hasData = false;
                    eventName = null;
                    eventBytes = 0;
                    continue;
                }
                if (text[0] == ':') continue;
                var colon = text.IndexOf(':');
                var field = colon < 0 ? text : text[..colon];
                var fieldValue = colon < 0 ? "" : text[(colon + 1)..];
                if (fieldValue.StartsWith(' ')) fieldValue = fieldValue[1..];
                switch (field)
                {
                    case "data": data.Append(fieldValue).Append('\n'); hasData = true; break;
                    case "event": eventName = fieldValue.Length == 0 ? null : fieldValue; break;
                    case "id" when !fieldValue.Contains('\0'): id = fieldValue; break;
                }
            }
        }
    }

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_idleTimeout);
        try { return await action(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("Interface connection exceeded its idle timeout.", error); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _lifetime.Cancel();
            _socket?.Abort();
            _lifetime.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
