#nullable enable
using System.Net;
using System.Net.Sockets;
using HslCommunication.Core.Net;
using HslCommunication.Profinet.AllenBradley;
using HslCommunication.Profinet.Omron;

namespace PlcSimulator.Protocols;

// HSL 11.2.1 的 BeginAccept 回调在停止后会抛出后台异常；沿用 S7 服务的可取消监听方式。
internal sealed class SimulationTcpListener : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket _listener;
    private readonly Task _accept;
    public SimulationTcpListener(int port, bool ipv6, Action<Socket, IPEndPoint> login)
    {
        _listener = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { _listener.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, port)); _listener.Listen(500); }
        catch { _listener.Dispose(); _stop.Dispose(); throw; }
        _accept = AcceptAsync(login);
    }
    private async Task AcceptAsync(Action<Socket, IPEndPoint> login)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                if (client.RemoteEndPoint is not IPEndPoint endpoint) { client.Dispose(); continue; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { if (_stop.IsCancellationRequested) client.Dispose(); else login(client, endpoint); }
                    catch { client.Dispose(); }
                });
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Dispose(); _accept.GetAwaiter().GetResult(); _stop.Dispose();
    }
}

internal sealed class StoppableCipServer : AllenBradleyServer
{
    private SimulationTcpListener? _listener;
    public override void ServerStart(int port)
    {
        if (IsStarted) return;
        StartInitialization(); Port = port; IsStarted = true;
        try { _listener = new SimulationTcpListener(port, EnableIPv6, (socket, endpoint) =>
        {
            if (!IsStarted || !SocketAcceptExtraCheck(socket, endpoint).IsSuccess) socket.Dispose(); else ThreadPoolLogin(socket, endpoint);
        }); }
        catch { IsStarted = false; throw; }
    }
    public override void ServerClose() { IsStarted = false; _listener?.Dispose(); _listener = null; CloseAction(); }
}

internal sealed class StoppableFinsServer : OmronFinsServer
{
    private SimulationTcpListener? _listener;
    public override void ServerStart(int port)
    {
        if (IsStarted) return;
        StartInitialization(); Port = port; IsStarted = true;
        try { _listener = new SimulationTcpListener(port, EnableIPv6, (socket, endpoint) =>
        {
            if (!IsStarted || !SocketAcceptExtraCheck(socket, endpoint).IsSuccess) socket.Dispose(); else ThreadPoolLogin(socket, endpoint);
        }); }
        catch { IsStarted = false; throw; }
    }
    public override void ServerClose() { IsStarted = false; _listener?.Dispose(); _listener = null; CloseAction(); }
}
