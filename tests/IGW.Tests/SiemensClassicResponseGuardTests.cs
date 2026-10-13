using IGW.Drivers.IoTClient;
using IoTClient;
using IoTClient.Common.Constants;
using IoTClient.Common.Enums;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensClassicResponseGuardTests
{
    [Fact]
    public void Handshake_requests_configured_pdu_without_mutating_library_constants()
    {
        byte[] original1 = SiemensConstant.Command1.ToArray();
        byte[] original2 = SiemensConstant.Command2.ToArray();

        var (command1, command2) = TimedSiemensClient.CreateHandshake(
            SiemensVersion.S7_1500, rack: 0, slot: 1, requestedPdu: 960);

        Assert.Equal(1, command1[21]);
        Assert.Equal(0x03, command2[^2]);
        Assert.Equal(0xC0, command2[^1]);
        Assert.Equal(original1, SiemensConstant.Command1);
        Assert.Equal(original2, SiemensConstant.Command2);
    }

    [Fact]
    public void Negotiated_pdu_uses_endpoint_response_and_never_exceeds_request()
    {
        Assert.Equal(480, TimedSiemensClient.ParseNegotiatedPdu([0x01, 0xE0], 960));
        Assert.Equal(480, TimedSiemensClient.ParseNegotiatedPdu([0x03, 0xC0], 480));
        Assert.Equal(960, TimedSiemensClient.ParseNegotiatedPdu([], 960));
    }

    [Fact]
    public void Pdu_rejection_is_returned_as_capacity_failure_before_vendor_copy()
    {
        var result = SiemensClassicResponseGuard.ValidateSingleRead(
            SingleByteReadCommand(880),
            new Result<byte[]> { IsSucceed = true, Value = Convert.FromHexString("0300001302F080320200000001000000008500") });

        Assert.False(result.IsSucceed);
        Assert.Contains("PDU capacity", result.Err);
        Assert.DoesNotContain("sourceIndex", result.Err);
    }

    [Fact]
    public void Address_rejection_and_short_success_are_protocol_failures_not_exceptions()
    {
        var address = new byte[23];
        address[7] = 0x32; address[8] = 0x03; address[19] = 0x04; address[20] = 0x01; address[21] = 0x0A;
        var rejected = SiemensClassicResponseGuard.ValidateSingleRead(
            SingleByteReadCommand(100), new Result<byte[]> { IsSucceed = true, Value = address });
        Assert.False(rejected.IsSucceed);
        Assert.Contains("address/item", rejected.Err);

        var shortFrame = new byte[25 + 20];
        shortFrame[7] = 0x32; shortFrame[8] = 0x03; shortFrame[19] = 0x04; shortFrame[20] = 0x01; shortFrame[21] = 0xFF;
        var shortResult = SiemensClassicResponseGuard.ValidateSingleRead(
            SingleByteReadCommand(100), new Result<byte[]> { IsSucceed = true, Value = shortFrame });
        Assert.False(shortResult.IsSucceed);
        Assert.Contains("payload is short", shortResult.Err);
    }

    [Fact]
    public void Complete_success_response_is_unchanged()
    {
        var response = new byte[25 + 100];
        response[7] = 0x32; response[8] = 0x03; response[19] = 0x04; response[20] = 0x01; response[21] = 0xFF;
        var original = new Result<byte[]> { IsSucceed = true, Value = response };

        Assert.Same(original, SiemensClassicResponseGuard.ValidateSingleRead(SingleByteReadCommand(100), original));
        Assert.True(original.IsSucceed);
    }

    [Fact]
    public void Local_vendor_copy_error_does_not_become_a_transport_failure_when_socket_was_closed()
    {
        var local = new Result<byte[]>
        {
            IsSucceed = false,
            Err = "sourceIndex ('-843') must be greater than or equal to '0'.",
            Exception = new ArgumentOutOfRangeException("sourceIndex", -843, "must be non-negative"),
        };
        var socket = new Result<byte[]>
        {
            IsSucceed = false,
            Err = "connection reset",
            Exception = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset),
        };

        Assert.False(IoTPlcDeviceDriver.IsTransportFailure(local, connected: false));
        Assert.True(IoTPlcDeviceDriver.IsTransportFailure(socket, connected: false));
    }

    private static byte[] SingleByteReadCommand(int length)
    {
        var command = new byte[31];
        command[7] = 0x32;
        command[17] = 0x04;
        command[18] = 0x01;
        command[22] = 0x02;
        command[23] = (byte)(length >> 8);
        command[24] = (byte)length;
        return command;
    }
}
