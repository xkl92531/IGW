using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaScanPublicationTests
{
    [Theory]
    [InlineData("all")]
    [InlineData("active")]
    public async Task Completed_physical_batch_reaches_cache_and_live_consumers_before_later_batch_finishes(string scanMode)
    {
        await using var hub = new DataHub();
        await using var driver = new GatedUaDriver();
        var tags = Enumerable.Range(0, 513).Select(i => new TagBinding
        {
            TagId = $"Line.Plc.T{i}", Address = new(0, 0, 2),
            Def = new() { Name = $"T{i}", Address = $"ns=2;s=T{i}", DataType = IgwDataType.Int16, ScanRateMs = 200 },
        }).ToArray();
        foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("mes-test", tags.Select(tag => tag.TagId), 200, ScanDemandPriority.Process);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var live = hub.SubscribePoints(change =>
        {
            if (change.Value.IsGood) published.TrySetResult();
            return ValueTask.CompletedTask;
        }, "qc-test");
        live.Add(tags[0].TagId);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "Line.Plc", scanMode: scanMode, scanDemands: demands);
        loop.Start(stop.Token);
        try
        {
            await driver.SecondBatchStarted.Task.WaitAsync(stop.Token);
            Assert.True(hub.TryGet(tags[0].TagId, out var current) && current.IsGood,
                "A completed first physical read must not wait for the blocked second read to publish.");
            Assert.Equal((short)42, current.Value);
            await published.Task.WaitAsync(stop.Token);
            Assert.False(driver.ReleaseSecondBatch.Task.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            driver.ReleaseSecondBatch.TrySetResult();
            await loop.Completion;
        }
    }

    private sealed class GatedUaDriver : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly OpcUaClientDeviceDriver _inner = new("opc.tcp://offline.invalid:4840", 10_000, true, "", "", "None");
        private int _calls;
        public TaskCompletionSource SecondBatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => _inner.DriverId;
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser => _inner.AddressParser;
        public DriverLimits Limits => _inner.Limits;
        public DriverCapabilitySnapshot Capabilities => _inner.Capabilities with { SupportsSubscriptions = false };
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            _inner.ReadCoreAsync(tags, async (requests, token) =>
            {
                if (Interlocked.Increment(ref _calls) == 2)
                {
                    SecondBatchStarted.TrySetResult();
                    await ReleaseSecondBatch.Task.WaitAsync(token);
                }
                return new Opc.Ua.DataValueCollection(requests.Select(_ => new Opc.Ua.DataValue((short)42)));
            }, () => true, ct);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
