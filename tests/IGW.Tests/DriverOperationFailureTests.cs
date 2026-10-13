using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DriverOperationFailureTests
{
    [Theory]
    [InlineData(DriverFailureScope.Capacity, DriverRetryDisposition.ReduceBatchAndRetry, false)]
    [InlineData(DriverFailureScope.Point, DriverRetryDisposition.DoNotRetry, false)]
    [InlineData(DriverFailureScope.Transport, DriverRetryDisposition.RebuildConnection, true)]
    public void Explicit_driver_failure_preserves_scope_and_retry(
        DriverFailureScope scope,
        DriverRetryDisposition retry,
        bool connectionLost)
    {
        var error = new DriverOperationException(
            "test", scope, retry, StatusCode.BadTimeout);

        DriverOperationFailure actual = DriverOperationFailure.FromException(
            error, connectionLost ? ConnectionState.Faulted : ConnectionState.Connected);

        Assert.Equal(scope, actual.Scope);
        Assert.Equal(retry, actual.Retry);
        Assert.Equal(connectionLost, actual.ConnectionLost);
        Assert.Equal(StatusCode.BadTimeout, actual.Status);
    }

    [Fact]
    public void Plain_timeout_on_a_connected_driver_is_an_operation_timeout()
    {
        DriverOperationFailure actual = DriverOperationFailure.FromException(
            new TimeoutException("request timed out"), ConnectionState.Connected);

        Assert.Equal(DriverFailureScope.Block, actual.Scope);
        Assert.Equal(DriverRetryDisposition.RetrySameConnection, actual.Retry);
        Assert.False(actual.ConnectionLost);
        Assert.Equal(StatusCode.BadTimeout, actual.Status);
    }

    [Fact]
    public void Nested_socket_failure_rebuilds_the_transport()
    {
        var error = new InvalidOperationException(
            "driver wrapper",
            new IOException("socket closed"));

        DriverOperationFailure actual = DriverOperationFailure.FromException(
            error, ConnectionState.Connected);

        Assert.Equal(DriverFailureScope.Transport, actual.Scope);
        Assert.Equal(DriverRetryDisposition.RebuildConnection, actual.Retry);
        Assert.True(actual.ConnectionLost);
        Assert.Equal(StatusCode.BadCommFailure, actual.Status);
    }

    [Fact]
    public void Value_failure_does_not_rebuild_a_connected_session()
    {
        DriverOperationFailure actual = DriverOperationFailure.FromException(
            new InvalidDataException("value outside target type"), ConnectionState.Connected);

        Assert.Equal(DriverFailureScope.Point, actual.Scope);
        Assert.Equal(DriverRetryDisposition.DoNotRetry, actual.Retry);
        Assert.False(actual.ConnectionLost);
        Assert.Equal(StatusCode.BadDecodeError, actual.Status);
    }
}
