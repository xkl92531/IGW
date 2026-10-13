using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace IGW.Core;

public enum RelayListenRequirement
{
    Ephemeral,
    EtherNetIp
}

public enum NetworkRelayState
{
    Listening,
    Connected,
    Faulted,
    Stopped
}

public sealed record NetworkRelaySnapshot(
    NetworkRelayState State,
    string Code,
    string Message,
    string Source,
    string Target,
    IPEndPoint LoopbackEndpoint,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt);

/// <summary>One actually connected socket path, published atomically for read-only diagnostics.</summary>
public sealed record NetworkRelayConnection(long Id, IPAddress RemoteAddress, IPAddress SourceAddress);

/// <summary>
/// Local TCP relay that makes source-interface selection explicit without
/// requiring protocol drivers to expose a local-bind option.
/// </summary>
public sealed class BoundTcpRelay : IAsyncDisposable, IDisposable
{
    private const int EtherNetIpPort = 44818;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly object EtherNetIpPoolLock = new();
    private static readonly HashSet<IPAddress> EtherNetIpAddressesInUse = [];
    private static int _nextEtherNetIpAddress;

    private readonly string _name;
    private readonly DnsEndPoint _target;
    private readonly Func<NetworkAdapterResolution> _resolveSource;
    private readonly TimeSpan _connectTimeout;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly ConcurrentDictionary<long, TcpClient> _inboundSockets = new();
    private readonly ConcurrentDictionary<long, Socket> _outboundSockets = new();
    private readonly object _snapshotLock = new();
    private readonly IPAddress? _leasedEtherNetIpAddress;
    private readonly Task _acceptLoop;
    private long _connectionId;
    private NetworkRelaySnapshot _snapshot;
    private int _disposeStarted;
    private NetworkRelayConnection? _boundConnection;
    private IPAddress? _lastBoundSourceAddress;
    private readonly object _boundConnectionsGate = new();
    private readonly Dictionary<long, NetworkRelayConnection> _liveBoundConnections = [];

    private BoundTcpRelay(
        string name,
        DnsEndPoint target,
        Func<NetworkAdapterResolution> resolveSource,
        TimeSpan connectTimeout,
        TcpListener listener,
        IPAddress? leasedEtherNetIpAddress)
    {
        _name = name;
        _target = target;
        _resolveSource = resolveSource;
        _connectTimeout = connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(5);
        _listener = listener;
        _leasedEtherNetIpAddress = leasedEtherNetIpAddress;
        LoopbackEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        _snapshot = new(
            NetworkRelayState.Listening,
            "Listening",
            "等待协议驱动连接本地转发端口",
            "",
            FormatTarget(target),
            LoopbackEndpoint,
            null,
            null);
        _acceptLoop = AcceptLoopAsync();
    }

    public IPEndPoint LoopbackEndpoint { get; }
    /// <summary>Verified only with exactly one live outbound socket; overlapping paths are ambiguous.</summary>
    public NetworkRelayConnection? BoundConnection => Volatile.Read(ref _boundConnection);
    public IPAddress? LastBoundSourceAddress => Volatile.Read(ref _lastBoundSourceAddress);
    public event Action? BoundConnectionChanged;

    public static BoundTcpRelay Start(
        string name,
        DnsEndPoint target,
        Func<NetworkAdapterResolution> resolveSource,
        TimeSpan connectTimeout,
        RelayListenRequirement requirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolveSource);

        var (listener, leasedAddress) = CreateStartedListener(requirement);
        try
        {
            return new BoundTcpRelay(name, target, resolveSource, connectTimeout, listener, leasedAddress);
        }
        catch
        {
            listener.Stop();
            ReleaseEtherNetIpAddress(leasedAddress);
            throw;
        }
    }

    public NetworkRelaySnapshot Snapshot()
    {
        lock (_snapshotLock) return _snapshot;
    }

    private static (TcpListener Listener, IPAddress? LeasedAddress) CreateStartedListener(
        RelayListenRequirement requirement)
    {
        if (requirement == RelayListenRequirement.Ephemeral)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (listener, null);
        }

        // libplctag accepts only a gateway host and always connects to 44818.
        // A distinct loopback IP therefore gives every device its own fixed-port relay.
        for (var attempt = 0; attempt < 64 * 256 * 254; attempt++)
        {
            IPAddress address;
            lock (EtherNetIpPoolLock)
            {
                var ordinal = _nextEtherNetIpAddress++;
                var second = 64 + (ordinal / (256 * 254)) % 64;
                var third = (ordinal / 254) % 256;
                var fourth = ordinal % 254 + 1;
                address = IPAddress.Parse($"127.{second}.{third}.{fourth}");
                if (!EtherNetIpAddressesInUse.Add(address)) continue;
            }

            var listener = new TcpListener(address, EtherNetIpPort);
            try
            {
                listener.Start();
                return (listener, address);
            }
            catch (SocketException)
            {
                listener.Stop();
                ReleaseEtherNetIpAddress(address);
            }
        }

        throw new InvalidOperationException("无法为 EtherNet/IP 分配本地转发地址。");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var inbound = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _connectionId);
                _inboundSockets[id] = inbound;
                var task = HandleConnectionAsync(id, inbound, _stop.Token);
                _connections[id] = task;
                _ = task.ContinueWith(
                    completed =>
                    {
                        _connections.TryRemove(id, out _);
                        _inboundSockets.TryRemove(id, out _);
                        _outboundSockets.TryRemove(id, out _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetSnapshot(NetworkRelayState.Faulted, "RelayAcceptFailed",
                $"本地转发监听异常：{ex.Message}", "", DateTimeOffset.UtcNow);
        }
    }

    private async Task HandleConnectionAsync(long id, TcpClient inbound, CancellationToken stop)
    {
        using (inbound)
        {
            NetworkAdapterResolution resolution;
            try
            {
                resolution = _resolveSource();
            }
            catch (Exception ex)
            {
                SetSnapshot(NetworkRelayState.Faulted, "AdapterResolutionFailed",
                    $"解析网卡失败：{ex.Message}", "", DateTimeOffset.UtcNow);
                return;
            }

            if (resolution.Kind != NetworkAdapterResolutionKind.Bound || resolution.LocalAddress is null)
            {
                SetSnapshot(NetworkRelayState.Faulted, "AdapterUnavailable",
                    string.IsNullOrWhiteSpace(resolution.Message) ? "配置的网卡当前不可用" : resolution.Message,
                    "", DateTimeOffset.UtcNow);
                return;
            }

            Socket? outbound = null;
            NetworkRelayConnection? connection = null;
            try
            {
                var targetAddress = await ResolveTargetAsync(_target.Host, stop).ConfigureAwait(false);
                outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                _outboundSockets[id] = outbound;
                outbound.Bind(new IPEndPoint(resolution.LocalAddress, 0));

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                connectTimeout.CancelAfter(_connectTimeout);
                await outbound.ConnectAsync(
                    new IPEndPoint(targetAddress, _target.Port), connectTimeout.Token).ConfigureAwait(false);

                connection = new NetworkRelayConnection(id, ((IPEndPoint)outbound.RemoteEndPoint!).Address,
                    ((IPEndPoint)outbound.LocalEndPoint!).Address);
                UpdateBoundConnections(connection, connected: true);

                inbound.NoDelay = true;
                var source = $"{resolution.Adapter?.Label ?? resolution.LocalAddress.ToString()} ({resolution.LocalAddress})";
                SetSnapshot(NetworkRelayState.Connected, "Connected",
                    $"已通过 {source} 连接 {FormatTarget(_target)}", source, null,
                    DateTimeOffset.UtcNow);

                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var upstream = CopyAsync(inbound.GetStream(), outboundStream, relayStop.Token);
                var downstream = CopyAsync(outboundStream, inbound.GetStream(), relayStop.Token);
                await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
                relayStop.Cancel();
                // 任一方向断开就立即终止整条转发会话，避免 PLC 端和 Windows
                // 长时间保留半关闭连接(FIN_WAIT_2)，阻塞后续自动重连。
                TryAbort(inbound);
                TryAbort(outbound);
                await IgnoreRelayEndAsync(upstream).ConfigureAwait(false);
                await IgnoreRelayEndAsync(downstream).ConfigureAwait(false);

                if (!stop.IsCancellationRequested)
                {
                    SetSnapshot(NetworkRelayState.Listening, "Disconnected",
                        "协议连接已断开，等待自动重连", source, DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                SetSnapshot(NetworkRelayState.Faulted, "ConnectTimeout",
                    $"通过 {resolution.LocalAddress} 连接 {FormatTarget(_target)} 超时",
                    resolution.LocalAddress.ToString(), DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                SetSnapshot(NetworkRelayState.Faulted, "ConnectFailed",
                    $"通过 {resolution.LocalAddress} 连接 {FormatTarget(_target)} 失败：{ex.Message}",
                    resolution.LocalAddress.ToString(), DateTimeOffset.UtcNow);
            }
            finally
            {
                if (outbound is not null)
                {
                    TryAbort(outbound);
                }
                // Remove only after this socket closes; an overlap never closes either transport.
                if (connection is not null) UpdateBoundConnections(connection, connected: false);
            }
        }
    }

    private void UpdateBoundConnections(NetworkRelayConnection connection, bool connected)
    {
        bool changed;
        lock (_boundConnectionsGate)
        {
            if (connected)
            {
                _liveBoundConnections.Add(connection.Id, connection);
                Volatile.Write(ref _lastBoundSourceAddress, connection.SourceAddress);
            }
            else if (!_liveBoundConnections.Remove(connection.Id)) return;

            // A new observation also invalidates a batch captured before a brief overlap,
            // even when the same original socket is once again the sole survivor.
            var next = _liveBoundConnections.Count == 1 ? _liveBoundConnections.Values.First() with { } : null;
            changed = !ReferenceEquals(_boundConnection, next);
            Volatile.Write(ref _boundConnection, next);
        }
        if (changed) NotifyBoundConnectionChanged();
    }

    private void NotifyBoundConnectionChanged()
    {
        if (BoundConnectionChanged is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch (Exception) { /* Diagnostic observers cannot break relay transport. */ }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static async Task<IPAddress> ResolveTargetAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            return parsed;

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)
            .ConfigureAwait(false);
        return addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private void SetSnapshot(
        NetworkRelayState state,
        string code,
        string message,
        string source,
        DateTimeOffset? disconnectedAt,
        DateTimeOffset? connectedAt = null)
    {
        lock (_snapshotLock)
        {
            _snapshot = _snapshot with
            {
                State = state,
                Code = code,
                Message = $"{_name}：{message}",
                Source = source,
                LastConnectedAt = connectedAt ?? _snapshot.LastConnectedAt,
                LastDisconnectedAt = disconnectedAt ?? _snapshot.LastDisconnectedAt
            };
        }
    }

    private static string FormatTarget(DnsEndPoint endpoint) => $"{endpoint.Host}:{endpoint.Port}";

    private static async Task IgnoreRelayEndAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static void TryAbort(TcpClient client)
    {
        try { TryAbort(client.Client); }
        catch { try { client.Dispose(); } catch { } }
    }

    private static void TryAbort(Socket socket)
    {
        // 关闭超时为 0 会发送 RST，确保“硬重连”不依赖拔网线或禁用网卡
        // 才能清掉操作系统/PLC 侧残留的半关闭会话。
        try { socket.LingerState = new LingerOption(true, 0); } catch { }
        try { socket.Close(0); }
        catch { try { socket.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        BeginDispose();
    }

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            try { await Task.WhenAll(connections).ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
    }

    private void BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        foreach (var inbound in _inboundSockets.Values) TryAbort(inbound);
        foreach (var outbound in _outboundSockets.Values) TryAbort(outbound);
        ReleaseEtherNetIpAddress(_leasedEtherNetIpAddress);
        SetSnapshot(NetworkRelayState.Stopped, "Stopped", "本地转发已停止", "", DateTimeOffset.UtcNow);
    }

    private static void ReleaseEtherNetIpAddress(IPAddress? address)
    {
        if (address is null) return;
        lock (EtherNetIpPoolLock) EtherNetIpAddressesInUse.Remove(address);
    }
}
