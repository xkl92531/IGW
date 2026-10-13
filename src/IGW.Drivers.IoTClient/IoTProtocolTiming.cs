using System.Buffers.Binary;
using System.Net.Sockets;
using IGW.Abstractions;
using IoTClient;
using IoTClient.Clients.Modbus;
using IoTClient.Clients.PLC;
using IoTClient.Common.Constants;
using IoTClient.Common.Enums;
using IoTClient.Common.Helpers;
using IoTClient.Enums;

namespace IGW.Drivers.IoTClient;

// IoTClient 1.0.48 routes retries and native BatchRead partitions through virtual
// SendPackageSingle. Instrument that exchange, never the outer typed/BatchRead calls.
internal interface IPhysicalRequestClient { }

// The pinned library catches raw exceptions and reconnects. Return an explicit abort result
// inside it, suppress its reconnect attempt, then rethrow the original control signal on exit.
internal sealed class IoTReadBoundary : IDisposable
{
    private static readonly AsyncLocal<IoTReadBoundary?> Current = new();
    private readonly IoTReadBoundary? _previous;
    private readonly CancellationToken _cancellation;
    private readonly Func<bool> _priorityPending;
    private Exception? _interruption;

    internal IoTReadBoundary(CancellationToken cancellation, Func<bool> priorityPending)
    {
        _previous = Current.Value;
        _cancellation = cancellation;
        _priorityPending = priorityPending;
        Current.Value = this;
    }

    internal static Result<byte[]>? BeforeRequest()
    {
        var boundary = Current.Value;
        if (boundary is null) return null;
        if (boundary._interruption is null)
        {
            if (boundary._cancellation.IsCancellationRequested)
                boundary._interruption = new OperationCanceledException(boundary._cancellation);
            else if (boundary._priorityPending()) boundary._interruption = new DriverReadYieldException();
        }
        return boundary._interruption is null ? null : new(AbortResult(boundary._interruption));
    }

    internal static Result? AbortedConnection => Current.Value?._interruption is { } interruption
        ? AbortResult(interruption) : null;

    private static Result AbortResult(Exception interruption) => new()
        { IsSucceed = false, Err = "IGW read interrupted", Exception = interruption };

    internal static void ThrowIfInterrupted()
    {
        if (Current.Value?._interruption is { } interruption)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(interruption).Throw();
    }

    internal static T Invoke<T>(Func<T> action)
    {
        try { return action(); }
        finally { ThrowIfInterrupted(); }
    }

    public void Dispose() => Current.Value = _previous;
}

internal sealed class IoTProtocolTiming : IDisposable
{
    private static readonly AsyncLocal<ProtocolRequestDescriptor?> Description = new();
    private readonly ProtocolRequestDescriptor? _previous;
    internal IoTProtocolTiming(ProtocolRequestDescriptor descriptor)
    { _previous = Description.Value; Description.Value = descriptor; }
    public void Dispose() => Description.Value = _previous;

    internal static Result<byte[]> Exchange(Func<Result<byte[]>> action, string protocol, byte[] frame)
    {
        if (IoTReadBoundary.BeforeRequest() is { } interrupted) return interrupted;
        return ProtocolOperationTiming.MeasureResult(action, r => IsFailure(protocol, r),
            Description.Value ?? DescribeFrame(protocol, frame));
    }

    internal static T ClientIo<T>(object client, Func<T> action)
    {
        if (client is not IPhysicalRequestClient) ProtocolOperationTiming.MarkOpaqueOperation();
        return action();
    }

    private static bool IsFailure(string protocol, Result<byte[]> result)
    {
        if (!result.IsSucceed) return true;
        var response = result.Value;
        return protocol switch
        {
            "Modbus" when response is { Length: >= 9 } => (response[7] & 0x80) != 0,
            "IoT MC" when response is { Length: >= 11 } && response[0] == 0xD0 =>
                BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(9)) != 0,
            "FINS" when response is { Length: >= 30 } =>
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(28)) != 0,
            _ => false
        };
    }

    // Unknown configured tag coverage remains -1. No retained frames, payloads, values or credentials.
    private static ProtocolRequestDescriptor DescribeFrame(string protocol, byte[] frame)
    {
        if (protocol == "Modbus" && frame.Length >= 12)
        {
            byte fc = frame[7];
            int start = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8));
            int count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(10));
            int bytes = fc is 1 or 2 or 15 ? (count + 7) / 8 : fc is 3 or 4 or 16 ? count * 2 : -1;
            return new(fc < 5 ? "Read" : "Write", $"Modbus unit={frame[6]} FC{fc} offset={start}", -1, bytes, -1);
        }
        if (protocol == "FINS" && frame.Length >= 34)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(32));
            int offset = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(29));
            bool word = (frame[28] & 0x80) != 0;
            return new(frame[27] == 1 ? "Read" : "Write",
                $"FINS area=0x{frame[28]:X2} offset={offset}.{frame[31]} count={count}", -1,
                word ? count * 2 : count, -1);
        }
        if (protocol == "IoT MC" && frame.Length >= 21 && frame[0] == 0x50)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(19));
            int offset = frame[15] | frame[16] << 8 | frame[17] << 16;
            bool bit = frame[13] == 1;
            return new(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) == 0x0401 ? "Read" : "Write",
                $"IoT MC area=0x{frame[18]:X2} offset={offset} count={count}", -1,
                bit ? (count + 1) / 2 : count * 2, -1);
        }
        if (protocol == "S7 Classic" && frame.Length >= 19 && frame[17] is 4 or 5)
            return new(frame[17] == 4 ? "Read" : "Write", $"S7 Classic items={frame[18]}", -1, -1, -1);
        return new("Exchange", protocol, -1, -1, -1);
    }
}

internal sealed class TimedModbusClient(string host, int port, int timeout, EndianFormat endian)
    : ModbusTcpClient(host, port, timeout, endian, plcAddresses: false), IPhysicalRequestClient
{
    public override Result<byte[]> SendPackageSingle(byte[] command) =>
        IoTProtocolTiming.Exchange(() => base.SendPackageSingle(command), "Modbus", command);
}

internal sealed class TimedSiemensClient : SiemensClient, IPhysicalRequestClient
{
    private readonly SiemensVersion _version;
    private readonly int _timeout;
    private readonly ushort _requestedPdu;

    internal TimedSiemensClient(SiemensVersion version, string host, int port, byte slot, byte rack,
        int timeout, int requestedPdu)
        : base(version, host, port, slot, rack, timeout)
    {
        _version = version;
        _timeout = timeout;
        _requestedPdu = checked((ushort)Math.Clamp(requestedPdu, 240, 960));
    }

    internal int RequestedPdu => _requestedPdu;
    internal int NegotiatedPdu { get; private set; }

    protected override Result Connect()
    {
        if (IoTReadBoundary.AbortedConnection is { } aborted) return aborted;

        var result = new Result();
        socket?.SafeClose();
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveTimeout = _timeout,
            SendTimeout = _timeout,
        };
        NegotiatedPdu = 0;
        try
        {
            IAsyncResult pending = socket.BeginConnect(IpEndPoint, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(_timeout)) throw new TimeoutException("连接超时");
            socket.EndConnect(pending);

            (byte[] command1, byte[] command2) = CreateHandshake(_version, Rack, Slot, _requestedPdu);
            result.Requst = Hex(command1);
            socket.Send(command1);
            var first = ReadFrame();
            if (!first.IsSucceed) return first;
            result.Response = Hex(first.Value!);

            result.Requst2 = Hex(command2);
            socket.Send(command2);
            var second = ReadFrame();
            if (!second.IsSucceed) return second;
            byte[] response = second.Value!;
            result.Response2 = Hex(response);
            NegotiatedPdu = ParseNegotiatedPdu(response, _requestedPdu);
            return result;
        }
        catch (Exception ex)
        {
            socket?.SafeClose();
            result.IsSucceed = false;
            result.Err = ex.Message;
            result.ErrCode = 408;
            result.Exception = ex;
            return result;
        }
    }

    private Result<byte[]> ReadFrame()
    {
        var head = SocketRead(socket, SiemensConstant.InitHeadLength);
        if (!head.IsSucceed) return head;
        var content = SocketRead(socket, GetContentLength(head.Value!));
        if (!content.IsSucceed) return content;
        content.Value = head.Value!.Concat(content.Value!).ToArray();
        return content;
    }

    internal static (byte[] Command1, byte[] Command2) CreateHandshake(
        SiemensVersion version, byte rack, byte slot, ushort requestedPdu)
    {
        byte[] command1 = (version switch
        {
            SiemensVersion.S7_200 => SiemensConstant.Command1_200,
            SiemensVersion.S7_200Smart => SiemensConstant.Command1_200Smart,
            _ => SiemensConstant.Command1,
        }).ToArray();
        byte[] command2 = (version switch
        {
            SiemensVersion.S7_200 => SiemensConstant.Command2_200,
            SiemensVersion.S7_200Smart => SiemensConstant.Command2_200Smart,
            _ => SiemensConstant.Command2,
        }).ToArray();

        switch (version)
        {
            case SiemensVersion.S7_300:
            case SiemensVersion.S7_1200:
            case SiemensVersion.S7_1500:
                command1[21] = (byte)(rack * 0x20 + slot);
                break;
            case SiemensVersion.S7_400:
                command1[17] = 0;
                command1[21] = (byte)(rack * 0x20 + slot);
                break;
        }
        command2[^2] = (byte)(requestedPdu >> 8);
        command2[^1] = (byte)requestedPdu;
        return (command1, command2);
    }

    internal static int ParseNegotiatedPdu(byte[] response, int fallback) =>
        response.Length >= 2 && ((response[^2] << 8) | response[^1]) >= 19
            ? Math.Min(fallback, (response[^2] << 8) | response[^1])
            : fallback;

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    public override Result<byte[]> SendPackageSingle(byte[] command) =>
        IoTProtocolTiming.Exchange(
            () => SiemensClassicResponseGuard.ValidateSingleRead(command, base.SendPackageSingle(command)),
            "S7 Classic", command);
}

/// <summary>
/// IoTClient 1.0.48 copies the requested byte count out of a response before it checks the S7
/// acknowledgement. A short S7 error frame therefore becomes an ArgumentOutOfRangeException and
/// the library closes a healthy socket. Validate the acknowledgement at the physical exchange
/// boundary so capacity/address errors remain protocol failures and can be recovered in-session.
/// </summary>
internal static class SiemensClassicResponseGuard
{
    internal static Result<byte[]> ValidateSingleRead(byte[] command, Result<byte[]> result)
    {
        if (!result.IsSucceed || !IsSingleRead(command)) return result;
        byte[]? response = result.Value;
        if (response is null || response.Length < 19)
            return Fail(result, $"S7 protocol response is incomplete ({response?.Length ?? 0} bytes).");

        // S7 Ack without data. 0x85/0x00 is returned by S7 endpoints when the requested payload
        // does not fit the negotiated PDU. Treat it as capacity, not a lost TCP connection.
        if (response[7] == 0x32 && response[8] == 0x02 && (response[17] != 0 || response[18] != 0))
        {
            string code = $"class=0x{response[17]:X2}, code=0x{response[18]:X2}";
            return response[17] == 0x85
                ? Fail(result, $"S7 PDU capacity rejected ({code}).")
                : Fail(result, $"S7 protocol request rejected ({code}).");
        }

        if (response[7] != 0x32 || response[8] != 0x03 || response.Length < 23 ||
            response[19] != 0x04 || response[20] != 0x01)
            return Fail(result, $"S7 protocol response shape is invalid ({response.Length} bytes).");

        byte returnCode = response[21];
        if (returnCode != 0xFF)
        {
            string message = returnCode is 0x05 or 0x0A
                ? $"S7 address/item rejected (return=0x{returnCode:X2})."
                : $"S7 read rejected (return=0x{returnCode:X2}).";
            return Fail(result, message);
        }

        int quantity = (command[23] << 8) | command[24];
        int expectedDataBytes = command[22] == 0x01 ? (quantity + 7) / 8 : quantity;
        if (response.Length < 25 + expectedDataBytes)
            return Fail(result,
                $"S7 protocol response payload is short (expected={expectedDataBytes}, actual={Math.Max(0, response.Length - 25)} bytes).");
        return result;
    }

    private static bool IsSingleRead(byte[] command) => command.Length >= 31 &&
        command[7] == 0x32 && command[17] == 0x04 && command[18] == 0x01;

    private static Result<byte[]> Fail(Result<byte[]> result, string error)
    {
        result.IsSucceed = false;
        result.Err = error;
        return result;
    }
}

internal sealed class TimedFinsClient(string host, int port, int timeout, EndianFormat endian)
    : OmronFinsClient(host, port, timeout, endian), IPhysicalRequestClient
{
    protected override Result Connect() => IoTReadBoundary.AbortedConnection ?? base.Connect();
    public override Result<byte[]> SendPackageSingle(byte[] command) =>
        IoTProtocolTiming.Exchange(() => base.SendPackageSingle(command), "FINS", command);
}

internal sealed class TimedMitsubishiClient(MitsubishiVersion version, string host, int port, int timeout)
    : MitsubishiClient(version, host, port, timeout), IPhysicalRequestClient
{
    protected override Result Connect() => IoTReadBoundary.AbortedConnection ?? base.Connect();
    public override Result<byte[]> SendPackageSingle(byte[] command) =>
        IoTProtocolTiming.Exchange(() => base.SendPackageSingle(command), "IoT MC", command);
}

internal sealed class TimedAbClient(string host, int port, byte slot, int timeout)
    : AllenBradleyClient(host, port, slot, timeout), IPhysicalRequestClient
{
    protected override Result Connect() => IoTReadBoundary.AbortedConnection ?? base.Connect();
    public override Result<byte[]> SendPackageSingle(byte[] command) =>
        IoTProtocolTiming.Exchange(() => base.SendPackageSingle(command), "IoT CIP", command);
}
