using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IGW.OpcUaProbe;

public sealed record ProbeNetworkAdapter(string Name, string Id, string Ipv4, int PrefixLength)
{
    public string Label => $"{Name} — {Ipv4}/{PrefixLength}";
}

public static class ProbeNetworkAdapterCatalog
{
    public static IReadOnlyList<ProbeNetworkAdapter> Available()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ToRows)
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Ipv4, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<ProbeNetworkAdapter> ToRows(NetworkInterface adapter)
    {
        IPInterfaceProperties properties;
        try { properties = adapter.GetIPProperties(); }
        catch { yield break; }

        foreach (var address in properties.UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            yield return new ProbeNetworkAdapter(
                adapter.Name, adapter.Id, address.Address.ToString(), address.PrefixLength);
        }
    }
}

/// <summary>
/// Keeps OPC UA on the user-selected source interface even when the SDK does not
/// expose a local-bind option. It only relays bytes; it cannot issue OPC UA writes.
/// </summary>
public sealed class SourceBoundTcpRelay : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly Uri _target;
    private readonly IPAddress _localAddress;
    private readonly TimeSpan _connectTimeout;
    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly object _connectionsLock = new();
    private readonly HashSet<Task> _connections = [];
    private int _disposed;

    private SourceBoundTcpRelay(
        Uri target,
        IPAddress localAddress,
        TimeSpan connectTimeout,
        Action<string>? log)
    {
        _target = target;
        _localAddress = localAddress;
        _connectTimeout = connectTimeout;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LoopbackEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync();
    }

    public IPEndPoint LoopbackEndpoint { get; }
    public string LocalIp => _localAddress.ToString();
    public string Target => $"{_target.Host}:{_target.Port}";

    public static SourceBoundTcpRelay Start(
        string opcUaUrl,
        string localIp,
        TimeSpan connectTimeout,
        Action<string>? log = null)
    {
        if (!Uri.TryCreate(opcUaUrl, UriKind.Absolute, out var target)
            || !string.Equals(target.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(target.Host) || target.Port <= 0)
            throw new ArgumentException("必须提供有效的 opc.tcp:// 地址。", nameof(opcUaUrl));
        if (!IPAddress.TryParse(localIp, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("本机绑定地址必须是有效的 IPv4 地址。", nameof(localIp));
        if (!ProbeNetworkAdapterCatalog.Available().Any(row => row.Ipv4 == address.ToString()))
            throw new InvalidOperationException($"本机当前不存在已启用的 IPv4 地址 {address}，请重新选择网卡。");
        return new SourceBoundTcpRelay(target, address,
            connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(15), log);
    }

    public static string BuildLoopbackUrl(string opcUaUrl, int loopbackPort)
    {
        if (!Uri.TryCreate(opcUaUrl, UriKind.Absolute, out var target))
            throw new ArgumentException("OPC UA 地址无效。", nameof(opcUaUrl));
        return new UriBuilder("opc.tcp", IPAddress.Loopback.ToString(), loopbackPort,
            target.AbsolutePath == "/" ? string.Empty : target.AbsolutePath).Uri.ToString();
    }

    public string ConnectionUrl() => BuildLoopbackUrl(_target.ToString(), LoopbackEndpoint.Port);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var inbound = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var connection = RelayAsync(inbound, _stop.Token);
                lock (_connectionsLock) _connections.Add(connection);
                _ = connection.ContinueWith(completed =>
                {
                    lock (_connectionsLock) _connections.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task RelayAsync(TcpClient inbound, CancellationToken cancellationToken)
    {
        try
        {
            using (inbound)
            using (var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            })
            {
                outbound.Bind(new IPEndPoint(_localAddress, 0));
                IPAddress target = await ResolveTargetAsync(_target.Host, cancellationToken).ConfigureAwait(false);
                _log?.Invoke($"源地址绑定转发准备连接；Source={_localAddress}；Target={target}:{_target.Port}");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_connectTimeout);
                await outbound.ConnectAsync(new IPEndPoint(target, _target.Port), timeout.Token).ConfigureAwait(false);
                _log?.Invoke($"源地址绑定转发已连接；Local={outbound.LocalEndPoint}；Remote={outbound.RemoteEndPoint}");
                inbound.NoDelay = true;
                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var upstream = CopyAsync(inbound.GetStream(), outboundStream, relayStop.Token);
                var downstream = CopyAsync(outboundStream, inbound.GetStream(), relayStop.Token);
                Task ended = await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
                string direction = ReferenceEquals(ended, upstream) ? "客户端到服务器" : "服务器到客户端";
                _log?.Invoke($"源地址绑定转发方向结束；Direction={direction}；Target={target}:{_target.Port}");
                relayStop.Cancel();
                Abort(inbound.Client);
                Abort(outbound);
                await IgnoreEndAsync(upstream).ConfigureAwait(false);
                await IgnoreEndAsync(downstream).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log?.Invoke($"源地址绑定转发失败；Source={_localAddress}；Target={Target}；Error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<IPAddress> ResolveTargetAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            return parsed;
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)
            .ConfigureAwait(false);
        return addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task CopyAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreEndAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static void Abort(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); } catch { }
        try { socket.Close(0); } catch { try { socket.Dispose(); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        Task[] connections;
        lock (_connectionsLock) connections = _connections.ToArray();
        if (connections.Length > 0)
        {
            try { await Task.WhenAll(connections).ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
    }
}
