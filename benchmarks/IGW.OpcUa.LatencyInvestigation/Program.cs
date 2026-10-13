using System.Collections.Concurrent;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;

// Offline characterization only: no network, real PLC, production settings or services.
// Observations describe current behavior; they are not assertions of desired behavior.
var observations = new Dictionary<string, object>();
await PartialSubscriptionPollingAsync();
await BatchCompletionAsync();
await SharedReadPriorityAsync();
await PersistentReadTimeoutAsync();
Console.WriteLine(JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));

async Task PersistentReadTimeoutAsync()
{
    await using var driver = new OpcUaClientDeviceDriver("opc.tcp://offline.invalid:4840", 10_000, true, "", "", "None");
    var sizes = new List<int>();
    var tags = Enumerable.Range(0, 512).Select(i => Binding($"T{i}")).ToArray();
    foreach (var tag in tags) tag.Def.Address = "ns=2;s=" + tag.TagId;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var samples = await driver.ReadCoreAsync(tags, (requests, ct) => {
        sizes.Add(requests.Count);
        return Task.FromException<Opc.Ua.DataValueCollection>(new TimeoutException("offline injected service timeout"));
    }, () => true, stop.Token);
    observations["persistentReadTimeout"] = new {
        requestedTags = tags.Length, physicalReadAttempts = sizes.Count,
        initialRequestSizes = sizes.Take(12).ToArray(),
        badSamples = samples.Count(sample => !sample.Value.IsGood),
        theoreticalSecondsIfEveryAttemptTakesConfiguredTenSecondTimeout = sizes.Count * 10,
        note = "Timeouts are injected instantly; this is an attempt count, not measured PLC duration.",
    };
}

async Task PartialSubscriptionPollingAsync()
{
    await using var hub = new DataHub();
    var tags = new[] { Binding("Accepted"), Binding("Rejected") };
    foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
    var demands = new ScanDemandRegistry(hub);
    using var demand = demands.Acquire("probe", tags.Select(tag => tag.TagId), 100, ScanDemandPriority.Process);
    var clock = new ProbeClock();
    var driver = new ProbeDriver { RejectSubscriptionTag = tags[1].TagId };
    var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "Line.Plc",
        scanDemands: demands, clock: clock, idlePollMs: 10);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    loop.Start(stop.Token);
    try
    {
        var rejected = new HashSet<string> { tags[1].TagId };
        await UntilAsync(() => driver.SubscriptionCreates >= 1 &&
            driver.Reads.GetValueOrDefault(tags[1].TagId) >= 1 &&
            loop.TagScanTimings(rejected).Single().Batch is not null, stop.Token);
        for (int round = 2; round <= 3; round++)
        {
            long completedSequence = loop.TagScanTimings(rejected).Single().Batch!.Sequence;
            clock.Advance(2_100);
            loop.WakeForDemand();
            await UntilAsync(() => driver.Reads.GetValueOrDefault(tags[1].TagId) >= round &&
                loop.TagScanTimings(rejected).Single().Batch is { Sequence: > 0 } completed &&
                completed.Sequence > completedSequence, stop.Token);
        }
        observations["partialSubscription"] = new {
            requestedTags = 2, acceptedTags = 1, unchangedDemand = true,
            simulatedClockElapsedMs = 4_200,
            rejectedTagPollingRounds = driver.Reads.GetValueOrDefault(tags[1].TagId),
            acceptedTagPollingRounds = driver.Reads.GetValueOrDefault(tags[0].TagId),
            createsBeforeStop = driver.SubscriptionCreates, disposalsBeforeStop = driver.SubscriptionDisposals,
            healthySubscriptionRetained = driver.SubscriptionCreates == 1 && driver.SubscriptionDisposals == 0,
        };
    }
    finally { stop.Cancel(); await loop.Completion; }
}

async Task BatchCompletionAsync()
{
    await using var hub = new DataHub();
    var tags = new[] { Binding("Good"), Binding("Mismatch"), Binding("Later") };
    foreach (var tag in tags) hub.Register(tag.TagId, tag.Def);
    var driver = new ProbeDriver {
        MismatchedTag = tags[1].TagId, BlockSecondWrite = true, BlockThirdMismatchWrite = true,
    };
    var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 10);
    var first = loop.EnqueueWriteAsync(Request(tags[0]));
    var second = loop.EnqueueWriteAsync(Request(tags[1]));
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    loop.Start(stop.Token);
    try
    {
        await driver.SecondWriteEntered.Task.WaitAsync(stop.Token);
        bool goodRequestCompletedDuringRetry = first.IsCompleted;
        bool goodSamplePublished = hub.TryGet(tags[0].TagId, out var value) && value.IsGood && Equals(value.Value, (short)42);
        var later = loop.EnqueueWriteAsync(Request(tags[2]));
        bool laterDispatchedWhileRetryInFlight = driver.Writes.ContainsKey(tags[2].TagId);
        driver.ContinueSecondWrite.TrySetResult();
        StatusCode laterResult = await later.WaitAsync(stop.Token);
        await driver.ThirdMismatchWriteEntered.Task.WaitAsync(stop.Token);
        int failedPointAttemptsAtObservation = driver.Writes.GetValueOrDefault(tags[1].TagId);
        bool failedPointStillRetryingWhenLaterCompleted = !second.IsCompleted;
        driver.ContinueThirdMismatchWrite.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(stop.Token);
        observations["batchCompletion"] = new {
            goodSamplePublished, goodRequestCompletedDuringRetry,
            laterDispatchedWhileRetryInFlight,
            inFlightRetryWasNotPreempted = !laterDispatchedWhileRetryInFlight,
            failedPointStillRetryingWhenLaterCompleted, failedPointAttemptsAtObservation,
            goodResult = first.Result.SymbolicName, mismatchResult = second.Result.SymbolicName,
            laterResult = laterResult.SymbolicName, writeCounts = driver.Writes,
        };
    }
    finally {
        driver.ContinueSecondWrite.TrySetResult();
        driver.ContinueThirdMismatchWrite.TrySetResult();
        stop.Cancel();
        await loop.Completion;
    }
}

async Task SharedReadPriorityAsync()
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var driver = new ProbeDriver { BlockAllReads = true };
    await using var pool = new OpcUaConnectionPool();
    await using var lease = pool.Acquire("offline-probe", () => new(driver, null));
    await lease.ConnectAsync(stop.Token);
    await lease.WriteAsync([Request(Binding("CompletedWrite"))], stop.Token);
    var first = lease.ReadAsync([Binding("Busy1")], stop.Token);
    var second = lease.ReadAsync([Binding("Busy2")], stop.Token);
    await UntilAsync(() => driver.OperationOrder.Contains("Line.Plc.Busy1") &&
        driver.OperationOrder.Contains("Line.Plc.Busy2"), stop.Token);
    var reads = Enumerable.Range(0, 6).Select(i => lease.ReadAsync([Binding($"Background{i}")], stop.Token)).ToArray();
    var verification = ((IWriteVerificationReader)lease).ReadForWriteVerificationAsync(
        [Binding("WriteReadback")], stop.Token);
    try
    {
        bool readbackWaitedForInFlightPermit = !driver.OperationOrder.Contains("Line.Plc.WriteReadback");
        driver.ReleaseReads(1);
        await UntilAsync(() => driver.OperationOrder.Contains("Line.Plc.WriteReadback"), stop.Token);
        bool anInFlightReadRemainedBlocked = !first.IsCompleted || !second.IsCompleted;
        driver.ReleaseReads(16);
        await Task.WhenAll(reads.Concat([first, second, verification])).WaitAsync(stop.Token);
        var order = driver.OperationOrder.ToArray();
        observations["sharedReadPriority"] = new {
            operations = order,
            readbackWaitedForInFlightPermit,
            anInFlightReadRemainedBlocked,
            backgroundReadsDispatchedBeforeReadback = order.TakeWhile(id => id != "Line.Plc.WriteReadback")
                .Count(id => id.StartsWith("Line.Plc.Background", StringComparison.Ordinal)),
        };
    }
    finally { driver.ReleaseReads(16); stop.Cancel(); }
}

static TagBinding Binding(string name) => new() {
    TagId = $"Line.Plc.{name}", Address = new TagAddress(0, 0, 2),
    Def = new TagDef { Name = name, Address = name, DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite, ScanRateMs = 100 },
};
static WriteRequest Request(TagBinding binding) => new() {
    TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = (short)42,
};
static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
{
    while (!condition()) await Task.Delay(10, ct);
}

sealed class ProbeClock : IMonotonicClock
{
    private long _now = 1_000;
    public long NowMilliseconds => Interlocked.Read(ref _now);
    public void Advance(long delta) => Interlocked.Add(ref _now, delta);
}

sealed class ProbeDriver : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
{
    public string? RejectSubscriptionTag { get; init; }
    public string? MismatchedTag { get; init; }
    public bool BlockSecondWrite { get; init; }
    public bool BlockThirdMismatchWrite { get; init; }
    public bool BlockAllReads { get; init; }
    public readonly TaskCompletionSource SecondWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ContinueSecondWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ThirdMismatchWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ContinueThirdMismatchWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _readPermits = new(0);
    public readonly ConcurrentDictionary<string, int> Reads = new();
    public readonly ConcurrentDictionary<string, int> Writes = new();
    public readonly ConcurrentQueue<string> OperationOrder = new();
    private int _writes, _creates, _disposals;
    public int SubscriptionCreates => Volatile.Read(ref _creates);
    public int SubscriptionDisposals => Volatile.Read(ref _disposals);
    public void ReleaseReads(int count) { if (BlockAllReads) _readPermits.Release(count); }
    public string DriverId => "offline-opcua-latency-probe";
    public ConnectionState State { get; private set; }
    public event Action<ConnectionState>? StateChanged;
    public IAddressParser AddressParser { get; } = new Parser();
    public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
    public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with {
        SupportsSubscriptions = RejectSubscriptionTag is not null,
        SupportsBatchRead = true, SupportsBatchWrite = true, MaxInFlight = 2, SuggestedVerificationDelayMs = 0,
    };
    public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
    public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
    {
        foreach (var tag in tags)
        {
            OperationOrder.Enqueue(tag.TagId);
            Reads.AddOrUpdate(tag.TagId, 1, (_, count) => count + 1);
        }
        if (BlockAllReads) await _readPermits.WaitAsync(ct);
        return tags.Select(tag => new TagSample(tag.TagId,
            DataValue.GoodAt(tag.TagId == MismatchedTag ? (short)0 : (short)42, DateTime.UtcNow))).ToArray();
    }
    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
    {
        foreach (var write in writes) { Writes.AddOrUpdate(write.TagId, 1, (_, count) => count + 1); OperationOrder.Enqueue(write.TagId); }
        if (Interlocked.Increment(ref _writes) == 2 && BlockSecondWrite)
        {
            SecondWriteEntered.TrySetResult();
            await ContinueSecondWrite.Task.WaitAsync(ct);
        }
        if (BlockThirdMismatchWrite && MismatchedTag is not null &&
            writes.Any(write => write.TagId == MismatchedTag) &&
            Writes.GetValueOrDefault(MismatchedTag) == 3)
        {
            ThirdMismatchWriteEntered.TrySetResult();
            await ContinueThirdMismatchWrite.Task.WaitAsync(ct);
        }
        return writes.Select(write => new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray();
    }
    public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags, Action<IReadOnlyList<TagSample>> onSamples, CancellationToken ct)
    {
        var accepted = tags.Where(tag => tag.TagId != RejectSubscriptionTag).ToArray();
        onSamples(accepted.Select(tag => new TagSample(tag.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray());
        Interlocked.Increment(ref _creates);
        return Task.FromResult<IDriverSubscription>(new Lease(accepted.Select(tag => tag.TagId).ToArray(), () => Interlocked.Increment(ref _disposals)));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    sealed class Lease(IReadOnlyCollection<string> tags, Action dispose) : IDriverSubscription
    {
        public IReadOnlyCollection<string> TagIds => tags;
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
    sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        { result = new TagAddress(0, 0, 2); error = null; return true; }
    }
}
