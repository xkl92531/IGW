using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;
using IgwStatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Tests;

public sealed class OpcUaAdaptiveBatchTests
{
    [Fact]
    public void Undeclared_limit_starts_conservatively_without_limiting_total_points()
    {
        var window = AdaptiveRequestWindow.Undeclared(initial: 512, ceiling: 100_000);

        Assert.Equal(512, window.Current);
        Assert.Equal(100_000, window.Ceiling);
        Assert.False(window.Declared);
    }

    [Fact]
    public void Capacity_failure_halves_only_the_current_request_window()
    {
        var window = AdaptiveRequestWindow.Undeclared(initial: 512, ceiling: 100_000);

        int reduced = window.OnCapacityFailure(attempted: 512);

        Assert.Equal(256, reduced);
        Assert.Equal(256, window.Current);
        Assert.Equal(100_000, window.Ceiling);
        Assert.Equal(1, window.Version);
    }

    [Fact]
    public void Successful_batches_grow_slowly_up_to_the_exploration_ceiling()
    {
        var window = AdaptiveRequestWindow.Undeclared(initial: 512, ceiling: 100_000);

        for (int i = 0; i < 19; i++) Assert.False(window.OnSuccess(512, 512));
        Assert.True(window.OnSuccess(512, 512));

        Assert.Equal(640, window.Current);
        Assert.Equal(1, window.Version);
    }

    [Fact]
    public void Declared_server_limit_is_never_exceeded()
    {
        var window = AdaptiveRequestWindow.ForDeclaredLimit(limit: 256);

        for (int i = 0; i < 64; i++) window.OnSuccess(512, 512);

        Assert.Equal(256, window.Current);
        Assert.Equal(256, window.Ceiling);
    }

    [Fact]
    public void Capacity_cooldown_and_cost_regression_bound_growth()
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(100, 200, () => clock);
        Assert.Equal(50, window.OnCapacityFailure(100));
        for (int i = 0; i < 40; i++) window.OnSuccess(50, 50);
        Assert.Equal(50, window.Current);
        clock = 60_000;
        Assert.True(window.OnSuccess(50, 50));
        Assert.Equal(62, window.Current);
        for (int i = 0; i < 4; i++) Assert.False(window.OnSuccess(62, 80));
        Assert.True(window.OnSuccess(62, 80));
        Assert.Equal(50, window.Current);
        for (int i = 0; i < 40; i++) window.OnSuccess(50, 50);
        Assert.Equal(50, window.Current);
        clock = 120_000;
        Assert.True(window.OnSuccess(50, 50));
    }

    [Fact]
    public void Invalid_samples_do_not_train_the_window()
    {
        var window = new AdaptiveRequestWindow(100, 200);
        for (int i = 0; i < 100; i++)
        {
            window.OnSuccess(0, 10);
            window.OnSuccess(100, double.NaN);
            window.OnSuccess(100, -1);
        }
        Assert.Equal(100, window.Current);
    }

    [Fact]
    public void Multi_node_request_timeout_retries_same_connection_without_capacity_subdivision()
    {
        DriverOperationException actual = OpcUaClientDeviceDriver.ClassifyReadFailure(
            new ServiceResultException(StatusCodes.BadRequestTimeout),
            requestSize: 512);

        Assert.Equal(DriverFailureScope.Block, actual.Scope);
        Assert.Equal(DriverRetryDisposition.RetrySameConnection, actual.Retry);
        Assert.Equal(new IgwStatusCode(StatusCodes.BadRequestTimeout), actual.Status);
        Assert.False(DriverOperationFailure.FromException(actual, ConnectionState.Connected).ConnectionLost);
    }

    [Fact]
    public void Interrupted_request_keeps_the_session_when_the_loop_is_not_stopping()
    {
        DriverOperationException actual = OpcUaClientDeviceDriver.ClassifyReadFailure(
            new ServiceResultException(StatusCodes.BadRequestInterrupted),
            requestSize: 256);

        Assert.Equal(DriverFailureScope.Block, actual.Scope);
        Assert.Equal(DriverRetryDisposition.RetrySameConnection, actual.Retry);
        Assert.False(DriverOperationFailure.FromException(actual, ConnectionState.Connected).ConnectionLost);
    }

    [Fact]
    public void Closed_connection_is_a_transport_failure()
    {
        DriverOperationException actual = OpcUaClientDeviceDriver.ClassifyReadFailure(
            new ServiceResultException(StatusCodes.BadConnectionClosed),
            requestSize: 256);

        Assert.Equal(DriverFailureScope.Transport, actual.Scope);
        Assert.Equal(DriverRetryDisposition.RebuildConnection, actual.Retry);
        Assert.True(DriverOperationFailure.FromException(actual, ConnectionState.Connected).ConnectionLost);
    }
}
