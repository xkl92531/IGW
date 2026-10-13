using System.Net;
using System.Net.Sockets;
using System.Text;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class BoundTcpRelayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bound_path_is_unavailable_during_overlap_and_restores_either_single_survivor(bool closeFirst)
    {
        await using var target = await EchoServer.StartAsync();
        string selected = "127.0.0.1";
        await using var relay = BoundTcpRelay.Start("source-change", new DnsEndPoint("127.0.0.1", target.Endpoint.Port),
            () => new(NetworkAdapterResolutionKind.Bound, new("loop", "Loop Test", selected, 8), IPAddress.Parse(selected), ""),
            TimeSpan.FromSeconds(2), RelayListenRequirement.Ephemeral);
        Assert.Null(relay.BoundConnection);
        Assert.Null(relay.LastBoundSourceAddress);
        relay.BoundConnectionChanged += () => throw new InvalidOperationException("diagnostic observer failed");
        using var first = new TcpClient(AddressFamily.InterNetwork);
        await first.ConnectAsync(relay.LoopbackEndpoint);
        await Handshake(first);
        var firstPath = relay.BoundConnection!;
        Assert.Equal("127.0.0.1", firstPath.SourceAddress.ToString());
        selected = "127.0.0.2";
        using var second = new TcpClient(AddressFamily.InterNetwork);
        await second.ConnectAsync(relay.LoopbackEndpoint);
        await Handshake(second);
        Assert.Equal(2, target.ActiveConnections);
        Assert.Null(relay.BoundConnection);
        Assert.Equal("127.0.0.2", relay.LastBoundSourceAddress!.ToString());
        // Ambiguity only suppresses diagnostic identity: both sockets still forward normally.
        await Handshake(first);
        await Handshake(second);
        var closed = closeFirst ? first : second;
        var survivor = closeFirst ? second : first;
        closed.Dispose();
        await WaitUntilAsync(() => target.ActiveConnections == 1 && relay.BoundConnection is not null, TimeSpan.FromSeconds(2));
        var survivingPath = relay.BoundConnection!;
        Assert.Equal(closeFirst ? "127.0.0.2" : "127.0.0.1", survivingPath.SourceAddress.ToString());
        Assert.Equal(IPAddress.Loopback, survivingPath.RemoteAddress);
        if (closeFirst) Assert.NotEqual(firstPath.Id, survivingPath.Id);
        else
        {
            Assert.Equal(firstPath.Id, survivingPath.Id);
            // Republish a fresh observation so a batch spanning an overlap cannot reuse its old capture.
            Assert.NotSame(firstPath, survivingPath);
        }
        await Handshake(survivor);
        survivor.Dispose();
        await WaitUntilAsync(() => relay.BoundConnection is null, TimeSpan.FromSeconds(2));
        Assert.Equal("127.0.0.2", relay.LastBoundSourceAddress!.ToString());

        static async Task Handshake(TcpClient client)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(new byte[] { 7 }, timeout.Token);
            var response = new byte[1];
            await client.GetStream().ReadExactlyAsync(response, timeout.Token);
            Assert.Equal(7, response[0]);
        }
    }

    [Fact]
    public async Task Relay_binds_outbound_socket_to_selected_ipv4_and_forwards_both_directions()
    {
        await using var target = await EchoServer.StartAsync();
        await using var relay = BoundTcpRelay.Start(
            "device-1",
            new DnsEndPoint("127.0.0.1", target.Endpoint.Port),
            BoundLoopback,
            TimeSpan.FromSeconds(2),
            RelayListenRequirement.Ephemeral);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(relay.LoopbackEndpoint);
        await client.GetStream().WriteAsync("IGW"u8.ToArray());
        var response = new byte[3];
        await client.GetStream().ReadExactlyAsync(response);

        Assert.Equal("IGW", Encoding.ASCII.GetString(response));
        Assert.Equal(IPAddress.Loopback, target.LastRemoteAddress);
        Assert.Equal(NetworkRelayState.Connected, relay.Snapshot().State);
    }

    [Fact]
    public async Task Unavailable_adapter_never_reaches_target_and_reports_source_failure()
    {
        await using var target = await EchoServer.StartAsync();
        await using var relay = BoundTcpRelay.Start(
            "device-1",
            new DnsEndPoint("127.0.0.1", target.Endpoint.Port),
            () => new(NetworkAdapterResolutionKind.Unavailable, null, null, "配置的网卡不可用"),
            TimeSpan.FromMilliseconds(300),
            RelayListenRequirement.Ephemeral);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(relay.LoopbackEndpoint);
        var buffer = new byte[1];
        int read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, read);
        Assert.Equal(0, target.AcceptedConnections);
        Assert.Equal("AdapterUnavailable", relay.Snapshot().Code);
    }

    [Fact]
    public async Task EtherNetIp_relays_use_distinct_loopback_addresses_on_fixed_port()
    {
        await using var firstTarget = await EchoServer.StartAsync();
        await using var secondTarget = await EchoServer.StartAsync();
        await using var first = BoundTcpRelay.Start("eip-1",
            new DnsEndPoint("127.0.0.1", firstTarget.Endpoint.Port), BoundLoopback,
            TimeSpan.FromSeconds(2), RelayListenRequirement.EtherNetIp);
        await using var second = BoundTcpRelay.Start("eip-2",
            new DnsEndPoint("127.0.0.1", secondTarget.Endpoint.Port), BoundLoopback,
            TimeSpan.FromSeconds(2), RelayListenRequirement.EtherNetIp);

        Assert.Equal(44818, first.LoopbackEndpoint.Port);
        Assert.Equal(44818, second.LoopbackEndpoint.Port);
        Assert.NotEqual(first.LoopbackEndpoint.Address, second.LoopbackEndpoint.Address);

        await RoundTrip(first.LoopbackEndpoint, 11);
        await RoundTrip(second.LoopbackEndpoint, 22);
        Assert.Equal(1, firstTarget.AcceptedConnections);
        Assert.Equal(1, secondTarget.AcceptedConnections);
    }

    [Fact]
    public async Task Disposing_relay_aborts_active_session_and_replacement_can_reconnect()
    {
        await using var target = await EchoServer.StartAsync();
        var relay = BoundTcpRelay.Start(
            "device-1",
            new DnsEndPoint("127.0.0.1", target.Endpoint.Port),
            BoundLoopback,
            TimeSpan.FromSeconds(2),
            RelayListenRequirement.Ephemeral);

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(relay.LoopbackEndpoint);
        await client.GetStream().WriteAsync(new byte[] { 7 });
        var response = new byte[1];
        await client.GetStream().ReadExactlyAsync(response);
        Assert.Equal(1, target.ActiveConnections);

        await relay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => target.ActiveConnections == 0, TimeSpan.FromSeconds(2));

        await using var replacement = BoundTcpRelay.Start(
            "device-1-replacement",
            new DnsEndPoint("127.0.0.1", target.Endpoint.Port),
            BoundLoopback,
            TimeSpan.FromSeconds(2),
            RelayListenRequirement.Ephemeral);
        await RoundTrip(replacement.LoopbackEndpoint, 9);

        Assert.True(target.AcceptedConnections >= 2);
    }

    private static NetworkAdapterResolution BoundLoopback() => new(
        NetworkAdapterResolutionKind.Bound,
        new NetworkAdapterInfo("loop", "Loop Test", "127.0.0.1", 8),
        IPAddress.Loopback,
        "");

    private static async Task RoundTrip(IPEndPoint endpoint, byte value)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(endpoint);
        await client.GetStream().WriteAsync(new[] { value });
        var response = new byte[1];
        await client.GetStream().ReadExactlyAsync(response);
        Assert.Equal(value, response[0]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(predicate());
    }

    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _acceptedConnections;
        private int _activeConnections;

        private EchoServer(TcpListener listener)
        {
            _listener = listener;
            _acceptLoop = AcceptLoopAsync();
        }

        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
        public IPAddress? LastRemoteAddress { get; private set; }
        public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
        public int ActiveConnections => Volatile.Read(ref _activeConnections);

        public static Task<EchoServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new EchoServer(listener));
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _acceptedConnections);
                    Interlocked.Increment(ref _activeConnections);
                    LastRemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    _ = EchoAsync(client, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task EchoAsync(TcpClient client, CancellationToken stop)
        {
            try
            {
                using (client)
                {
                    var buffer = new byte[4096];
                    try
                    {
                        while (true)
                        {
                            int read = await client.GetStream().ReadAsync(buffer, stop);
                            if (read == 0) return;
                            await client.GetStream().WriteAsync(buffer.AsMemory(0, read), stop);
                        }
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
            finally { Interlocked.Decrement(ref _activeConnections); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _acceptLoop; } catch { }
            _stop.Dispose();
        }
    }
}
