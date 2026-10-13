using IGW.Abstractions;
using IGW.Drivers.EtherNetIP;
using libplctag;
using Xunit;

namespace IGW.Tests;

public sealed class EtherNetIpFailureIsolationTests
{
    [Fact]
    public async Task Failed_native_read_is_counted_without_rebuilding_a_reachable_session()
    {
        await using var driver = DriverProtocolTimingTests.Eip(() => new DriverProtocolTimingTests.FakeNativeTag
        { Read = _ => Task.FromException(new IOException("bad tag")) });
        using var timing = ProtocolOperationTiming.Begin();
        var samples = await driver.ReadAsync([DriverProtocolTimingTests.Binding("Missing")], default);
        Assert.False(Assert.Single(samples).Value.IsGood);
        Assert.Equal(2, timing.Snapshot().Requests);
        Assert.Equal(1, timing.Snapshot().FailedRequests);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task Native_read_cancellation_is_not_swallowed_as_a_bad_point_or_transport_failure()
    {
        await using var driver = DriverProtocolTimingTests.Eip(() => new DriverProtocolTimingTests.FakeNativeTag
        { Read = _ => Task.FromCanceled(new CancellationToken(true)) });
        using var timing = ProtocolOperationTiming.Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            driver.ReadAsync([DriverProtocolTimingTests.Binding("Value")], default));
        Assert.Equal(1, timing.Snapshot().CancelledRequests);
        Assert.Equal(0, timing.Snapshot().FailedRequests);
    }

    [Fact]
    public async Task All_bad_points_do_not_fault_a_reachable_transport()
    {
        var driver = new EtherNetIpDeviceDriver(
            "Omron.Cip",
            "127.0.0.1",
            null,
            PlcType.Omron,
            100,
            128,
            transportProbe: _ => Task.FromResult(true));

        bool transportLost = await driver.ConfirmTransportLostAfterAllBadAsync(12, CancellationToken.None);

        Assert.False(transportLost);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task All_bad_points_fault_only_after_transport_probe_fails()
    {
        var driver = new EtherNetIpDeviceDriver(
            "Omron.Cip",
            "127.0.0.1",
            null,
            PlcType.Omron,
            100,
            128,
            transportProbe: _ => Task.FromResult(false));

        bool transportLost = await driver.ConfirmTransportLostAfterAllBadAsync(12, CancellationToken.None);

        Assert.True(transportLost);
        Assert.Equal(ConnectionState.Faulted, driver.State);
    }
}
