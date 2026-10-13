using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IGW.Core;

/// <summary>IPv4 diagnostic echo only. Windows binds the configured source; other platforms cannot verify routing.</summary>
public sealed class SourceBoundIcmpEchoClient : INetworkEchoClient
{
    private static readonly byte[] Payload = "IGW network diagnostic"u8.ToArray();
    private static int ReplyHeaderSize => IntPtr.Size == 8 ? 40 : 28;

    public async Task<NetworkEchoResult> SendAsync(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        if (remote.AddressFamily != AddressFamily.InterNetwork || source is not null && source.AddressFamily != AddressFamily.InterNetwork)
            return new(false, null, "unavailable", "ICMP diagnostics support IPv4 only");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(1))
            return new(false, null, "unavailable", "ICMP timeout must be greater than zero and at most one second");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No cancellation token on Task.Run: once scheduled, its native handle/buffer must be
                // owned until the synchronous echo returns (at most the configured one-second timeout).
                return await Task.Run(() => SendWindows(remote, source, timeout, cancellationToken)).ConfigureAwait(false);
            }
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(remote, timeout, Payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new(true, reply.RoundtripTime, "success", null, RouteVerified: false)
                : new(false, null, reply.Status == IPStatus.TimedOut ? "timeout" : "unavailable", reply.Status.ToString(), RouteVerified: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(); }
        catch (Exception error) { return new(false, null, "unavailable", error.Message); }
    }

    private static NetworkEchoResult SendWindows(IPAddress remote, IPAddress? source, TimeSpan timeout, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Cancelled();
        using var handle = IcmpCreateFile();
        if (handle.IsInvalid) return NativeError(Marshal.GetLastPInvokeError());
        // Echo header + request + ICMP error bytes + IO_STATUS_BLOCK, as required by IcmpSendEcho2Ex.
        var reply = new byte[ReplyHeaderSize + Payload.Length + 8 + 2 * IntPtr.Size];
        uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            source is null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(source.GetAddressBytes()),
            BinaryPrimitives.ReadUInt32LittleEndian(remote.GetAddressBytes()),
            Payload, (ushort)Payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)Math.Ceiling(timeout.TotalMilliseconds));
        int error = count == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (token.IsCancellationRequested) return Cancelled();
        var result = count == 0 ? NativeError(error) : ParseReply(reply, count);
        return result with { RouteVerified = source is not null && !source.Equals(IPAddress.Any) };
    }

    internal static NetworkEchoResult ParseReply(ReadOnlySpan<byte> reply, uint count)
    {
        // Only read the fixed scalar prefix; never dereference the native data/options pointers.
        if (count == 0 || reply.Length < ReplyHeaderSize || count > reply.Length / ReplyHeaderSize)
            return new(false, null, "unavailable", "Invalid ICMP reply count or buffer size");
        var status = BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]);
        var dataSize = BinaryPrimitives.ReadUInt16LittleEndian(reply[12..]);
        if (dataSize > reply.Length - ReplyHeaderSize)
            return new(false, null, "unavailable", "ICMP reply payload exceeds buffer size");
        return status == 0
            ? new(true, BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]), "success", null)
            : NativeError(unchecked((int)status));
    }

    private static NetworkEchoResult NativeError(int code) => new(false, null,
        code == (int)IPStatus.TimedOut ? "timeout" : "unavailable",
        $"ICMP error {code}: {(Enum.IsDefined(typeof(IPStatus), code) ? ((IPStatus)code).ToString() : new Win32Exception(code).Message)}");

    private static NetworkEchoResult Cancelled() => new(false, null, "cancelled", "Network probe cancelled");

    private sealed class IcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IcmpHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => IcmpCloseHandle(handle);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IcmpHandle IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IcmpHandle handle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize, IntPtr requestOptions,
        [Out] byte[] replyBuffer, uint replySize, uint timeout);
}
