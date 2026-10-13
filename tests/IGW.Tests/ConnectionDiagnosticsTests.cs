using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ConnectionDiagnosticsTests
{
    public static TheoryData<Exception, ConnectionDiagnosticCategory> TransportCases => new()
    {
        { new SocketException((int)SocketError.ConnectionRefused), ConnectionDiagnosticCategory.ConnectionRefused },
        { new SocketException((int)SocketError.TimedOut), ConnectionDiagnosticCategory.ConnectionTimeout },
        { new SocketException((int)SocketError.NetworkUnreachable), ConnectionDiagnosticCategory.NetworkUnreachable },
        { new SocketException((int)SocketError.HostNotFound), ConnectionDiagnosticCategory.NameResolutionFailure },
        { new SocketException((int)SocketError.ConnectionReset), ConnectionDiagnosticCategory.RemoteClosed },
        { new TimeoutException("handshake timed out"), ConnectionDiagnosticCategory.ConnectionTimeout },
    };

    [Theory]
    [MemberData(nameof(TransportCases))]
    public void Classifier_uses_exception_types_and_socket_codes_before_message_guessing(
        Exception error,
        ConnectionDiagnosticCategory expected)
    {
        var result = ConnectionDiagnosticClassifier.Classify(error, null);

        Assert.Equal(expected, result.Category);
        Assert.False(string.IsNullOrWhiteSpace(result.Cause));
        Assert.False(string.IsNullOrWhiteSpace(result.Suggestion));
        Assert.False(string.IsNullOrWhiteSpace(result.RawError));
    }

    public static TheoryData<string, ConnectionDiagnosticCategory, ConnectionDiagnosticScope> MessageCases => new()
    {
        { "BadSecurityChecksFailed: certificate is untrusted", ConnectionDiagnosticCategory.SecurityPolicyMismatch, ConnectionDiagnosticScope.Session },
        { "BadIdentityTokenRejected: username or password invalid", ConnectionDiagnosticCategory.AuthenticationFailed, ConnectionDiagnosticScope.Session },
        { "anonymous access rejected; authentication required", ConnectionDiagnosticCategory.AuthenticationRequired, ConnectionDiagnosticScope.Session },
        { "BadUserAccessDenied: write permission denied", ConnectionDiagnosticCategory.PermissionDenied, ConnectionDiagnosticScope.Session },
        { "S7 protocol handshake failed: rack or slot mismatch", ConnectionDiagnosticCategory.ProtocolMismatch, ConnectionDiagnosticScope.Session },
        { "DB2100 item not available / address out of range", ConnectionDiagnosticCategory.DataAddressMismatch, ConnectionDiagnosticScope.DataAccess },
        { "An existing connection was forcibly closed by the remote host", ConnectionDiagnosticCategory.RemoteClosed, ConnectionDiagnosticScope.Transport },
        { "vendor specific failure 0x7abc", ConnectionDiagnosticCategory.UnknownConnectionFailure, ConnectionDiagnosticScope.Transport },
    };

    [Theory]
    [MemberData(nameof(MessageCases))]
    public void Classifier_preserves_unknown_vendor_text_and_maps_known_session_or_data_failures(
        string message,
        ConnectionDiagnosticCategory expected,
        ConnectionDiagnosticScope scope)
    {
        var result = ConnectionDiagnosticClassifier.Classify(new IOException(message), null);

        Assert.Equal(expected, result.Category);
        Assert.Equal(scope, result.Scope);
        Assert.Contains(message[..Math.Min(12, message.Length)], result.RawError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AuthenticationRequired", ConnectionDiagnosticCategory.AuthenticationRequired)]
    [InlineData("AuthenticationFailed", ConnectionDiagnosticCategory.AuthenticationFailed)]
    [InlineData("PermissionDenied", ConnectionDiagnosticCategory.PermissionDenied)]
    [InlineData("UnsupportedFirmware", ConnectionDiagnosticCategory.ProtocolMismatch)]
    [InlineData("SymbolMismatch", ConnectionDiagnosticCategory.ProtocolMismatch)]
    [InlineData("DbAddressMismatch", ConnectionDiagnosticCategory.DataAddressMismatch)]
    [InlineData("PartialOnline", ConnectionDiagnosticCategory.PartialDataFailure)]
    [InlineData("NetworkAdapterUnavailable", ConnectionDiagnosticCategory.NetworkAdapterUnavailable)]
    public void Explicit_driver_detail_has_priority_over_generic_exception(
        string code,
        ConnectionDiagnosticCategory expected)
    {
        var result = ConnectionDiagnosticClassifier.Classify(
            new TimeoutException("generic timeout"),
            new DeviceConnectionDetail(code, "driver detail"));

        Assert.Equal(expected, result.Category);
        Assert.Contains("driver detail", result.RawError);
    }

    [Fact]
    public void Transport_failure_detail_still_classifies_timeout_from_raw_error()
    {
        var result = ConnectionDiagnosticClassifier.Classify(
            new IOException("连接断开:连接超时"),
            new DeviceConnectionDetail("TransportFailure", "S7 Classic 通道离线：连接超时"));

        Assert.Equal(ConnectionDiagnosticCategory.ConnectionTimeout, result.Category);
    }

    [Fact]
    public void Raw_error_redacts_credentials_flattens_lines_and_caps_payload()
    {
        string message = "opc.tcp://admin:secret@plc:4840\r\npassword=my-password Authorization: Basic Zm9vOmJhcg== "
            + new string('x', 4_000);

        var result = ConnectionDiagnosticClassifier.Classify(new IOException(message), null);

        Assert.DoesNotContain("secret", result.RawError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my-password", result.RawError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Zm9vOmJhcg", result.RawError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\r', result.RawError);
        Assert.DoesNotContain('\n', result.RawError);
        Assert.True(result.RawError.Length <= 2_000);
    }

    [Fact]
    public void Registry_aggregates_repeats_transitions_categories_and_recovers()
    {
        var registry = new ConnectionIncidentRegistry();
        var start = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var refused = ConnectionDiagnosticClassifier.Classify(
            new SocketException((int)SocketError.ConnectionRefused), null);
        var timeout = ConnectionDiagnosticClassifier.Classify(new TimeoutException("connect timeout"), null);

        registry.Report("Line.PLC1", "Line", "Siemens.S7", refused, ConnectionState.Faulted, true, 1_000, start);
        registry.Report("Line.PLC1", "Line", "Siemens.S7", refused, ConnectionState.Faulted, true, 2_000, start.AddSeconds(2));

        var repeated = Assert.Single(registry.Snapshot().Active);
        Assert.Equal(2, repeated.OccurrenceCount);
        Assert.Equal(start, repeated.FirstOccurredAt);
        Assert.Equal(start.AddSeconds(2), repeated.LastOccurredAt);
        Assert.Equal(2_000, repeated.NextRetryMs);

        registry.Report("Line.PLC1", "Line", "Siemens.S7", timeout, ConnectionState.Faulted, true, 4_000, start.AddSeconds(5));

        var changed = registry.Snapshot();
        Assert.Equal(ConnectionDiagnosticCategory.ConnectionTimeout, Assert.Single(changed.Active).Category);
        var prior = Assert.Single(changed.Recovered);
        Assert.Equal(ConnectionDiagnosticCategory.ConnectionRefused, prior.Category);
        Assert.Equal(start.AddSeconds(5), prior.RecoveredAt);

        registry.Recover("Line.PLC1", start.AddSeconds(8));

        var recovered = registry.Snapshot();
        Assert.Empty(recovered.Active);
        Assert.Equal(2, recovered.Recovered.Count);
        Assert.Equal(start.AddSeconds(8), recovered.Recovered[0].RecoveredAt);
        Assert.Null(registry.Current("Line.PLC1"));
    }

    [Fact]
    public void Registry_bounds_recovered_history_and_returns_snapshot_copies()
    {
        var registry = new ConnectionIncidentRegistry(recoveredCapacity: 200);
        var descriptor = ConnectionDiagnosticClassifier.Classify(new TimeoutException("timeout"), null);
        var start = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 205; i++)
        {
            string device = $"Line.PLC{i}";
            registry.Report(device, "Line", "Modbus.Tcp", descriptor, ConnectionState.Faulted, true, 1_000, start.AddSeconds(i));
            registry.Recover(device, start.AddSeconds(i + 1));
        }

        var first = registry.Snapshot();
        Assert.Equal(200, first.Recovered.Count);
        Assert.DoesNotContain(first.Recovered, item => item.Device == "Line.PLC0");

        var second = registry.Snapshot();
        Assert.NotSame(first.Recovered, second.Recovered);
        Assert.Equal(first.Recovered, second.Recovered);
    }
}
