using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class PartialSubscriptionIsolationTests
{
    [Fact]
    public async Task Fully_rejected_subscription_releases_empty_lease_and_recovers_after_backoff()
    {
        await using var hub = new DataHub();
        var tag = Binding("Rejected");
        hub.Register(tag.TagId, tag.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", [tag.TagId], 20, ScanDemandPriority.Process);
        var clock = new Clock();
        var driver = new Driver { RejectedId = tag.TagId };
        var loop = new DeviceLoop(driver, [tag], hub, NullLogger.Instance, "Line.Plc", scanDemands: demands, clock: clock, idlePollMs: 10);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Reads.GetValueOrDefault(tag.TagId) >= 1, stop.Token);
            Assert.True(Assert.Single(driver.Leases).Disposed);
            clock.Advance(2_100);
            loop.WakeForDemand();
            await Until(() => driver.Reads.GetValueOrDefault(tag.TagId) >= 2, stop.Token);
            Assert.Single(driver.Batches);
            driver.RejectedId = null;
            clock.Advance(30_000);
            loop.WakeForDemand();
            await Until(() => loop.TagScanTimings(new HashSet<string> { tag.TagId }).Single().Mode == "subscription", stop.Token);
            Assert.Equal(2, driver.Leases.Count);
            Assert.False(driver.Leases.Last().Disposed);
        }
        finally { stop.Cancel(); await loop.Completion; }
        Assert.All(driver.Leases, lease => Assert.True(lease.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_point_uses_polling_and_retry_does_not_dispose_healthy_subscription(bool retryThrows)
    {
        await using var hub = new DataHub();
        var tags = new[] { Binding("Healthy"), Binding("Rejected") };
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("process", tags.Select(tag => tag.TagId), 20, ScanDemandPriority.Process);
        var clock = new Clock();
        var driver = new Driver { RejectedId = tags[1].TagId };
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "Line.Plc", scanDemands: demands, clock: clock, idlePollMs: 10);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        loop.Start(stop.Token);
        try
        {
            await Until(() => driver.Reads.GetValueOrDefault(tags[1].TagId) >= 1, stop.Token);
            var original = Assert.Single(driver.Leases);
            for (int i = 2; i <= 3; i++)
            {
                clock.Advance(2_100);
                loop.WakeForDemand();
                await Until(() => driver.Reads.GetValueOrDefault(tags[1].TagId) >= i, stop.Token);
            }
            Assert.Single(driver.Batches);
            Assert.False(original.Disposed);
            Assert.False(driver.Reads.ContainsKey(tags[0].TagId));

            driver.RejectedId = null;
            driver.ThrowOnRetry = retryThrows;
            clock.Advance(30_000);
            loop.WakeForDemand();
            await Until(() => driver.Batches.Count >= 2, stop.Token);
            Assert.Equal(new[] { tags[1].TagId }, driver.Batches.Last());
            Assert.False(original.Disposed);
            if (!retryThrows)
            {
                await Until(() => loop.TagScanTimings(tags.Select(t => t.TagId).ToHashSet())
                    .All(t => t.Mode == "subscription"), stop.Token);
                Assert.Equal(2, driver.Leases.Count);
                var retainedLeases = driver.Leases.ToArray();
                using var faster = demands.Acquire("faster", tags.Select(tag => tag.TagId), 10, ScanDemandPriority.Process);
                loop.WakeForDemand();
                await Until(() => driver.Leases.Count >= 3, stop.Token);
                Assert.All(retainedLeases, lease => Assert.True(lease.Disposed));
                Assert.Equal(new[] { 10, 10 }, driver.Periods.Last());
            }
            else
            {
                await Until(() => driver.Reads.GetValueOrDefault(tags[1].TagId) >= 4, stop.Token);
                Assert.Equal("subscription", Assert.Single(loop.TagScanTimings(new HashSet<string> { tags[0].TagId })).Mode);
            }
            demand.Dispose();
            loop.WakeForDemand();
            await Until(() => driver.Leases.All(lease => lease.Disposed), stop.Token);
        }
        finally { stop.Cancel(); await loop.Completion; }
        Assert.All(driver.Leases, lease => Assert.True(lease.Disposed));
    }

    private static TagBinding Binding(string name) => new()
    {
        TagId = $"Line.Plc.{name}", Address = new(0, 0, 2),
        Def = new() { Name = name, Address = name, DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 20 },
    };

    private static async Task Until(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(10, ct); }

    private sealed class Clock : IMonotonicClock
    {
        private long _now = 1_000;
        public long NowMilliseconds => Interlocked.Read(ref _now);
        public void Advance(long delta) => Interlocked.Add(ref _now, delta);
    }

    private sealed class Driver : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
    {
        public string? RejectedId;
        public bool ThrowOnRetry;
        public ConcurrentQueue<string[]> Batches { get; } = new();
        public ConcurrentQueue<int[]> Periods { get; } = new();
        public ConcurrentQueue<Lease> Leases { get; } = new();
        public ConcurrentDictionary<string, int> Reads { get; } = new();
        public string DriverId => "partial-subscription";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with { SupportsSubscriptions = true };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            foreach (var tag in tags) Reads.AddOrUpdate(tag.TagId, 1, (_, count) => count + 1);
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray());
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags, Action<IReadOnlyList<TagSample>> samples, CancellationToken ct)
        {
            Batches.Enqueue(tags.Select(tag => tag.TagId).ToArray());
            Periods.Enqueue(tags.Select(tag => tag.RequestedSamplingIntervalMs ?? 0).ToArray());
            if (ThrowOnRetry) throw new InvalidOperationException("retry failed");
            var accepted = tags.Where(tag => tag.TagId != RejectedId).ToArray();
            samples(accepted.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray());
            var lease = new Lease(accepted.Select(tag => tag.TagId).ToArray());
            Leases.Enqueue(lease);
            return Task.FromResult<IDriverSubscription>(lease);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(IReadOnlyCollection<string> ids) : IDriverSubscription
    {
        public bool Disposed;
        public IReadOnlyCollection<string> TagIds => ids;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new(0, 0, 2); error = null; return true; }
    }
}
