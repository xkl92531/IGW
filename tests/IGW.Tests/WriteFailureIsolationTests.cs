using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class WriteFailureIsolationTests
{
    [Fact]
    public async Task Fresh_array_gets_a_turn_even_when_fresh_scalar_work_is_queued()
    {
        await using var hub = new DataHub();
        var array = ArrayBinding("HealthyArray");
        var scalar = Binding("Scalar");
        foreach (var binding in new[] { array, scalar }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(scalar.TagId) { BadWriteFailures = 0 };
        var loop = new DeviceLoop(driver, [array, scalar], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<ArrayWriteResult> arrayWrite = loop.EnqueueArrayWriteAsync(ArrayRequest(array));
        Task<StatusCode> scalarWrite = loop.EnqueueWriteAsync(Request(scalar, (short)7));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstStarted.Task.WaitAsync(stop.Token);
            Assert.True(arrayWrite.IsCompleted, "Fresh scalar demand must not starve a healthy array.");
            Assert.Equal(StatusCode.Good, (await arrayWrite).Status);
            driver.ReleaseFirst.TrySetResult();
            Assert.Equal(StatusCode.Good, await scalarWrite.WaitAsync(stop.Token));
        }
        finally
        {
            driver.ReleaseFirst.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Retry_exception_releases_the_tracking_entry_and_queue_slot()
    {
        await using var hub = new DataHub();
        var binding = Binding("Bad");
        hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(binding.TagId) { RetryWriteError = new FormatException("retry failed") };
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1,
            maxPendingWrites: 1);
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(binding, (short)1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        driver.ReleaseRetry.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            Assert.Equal(StatusCode.BadDecodeError, await failed.WaitAsync(stop.Token));
            var tracking = typeof(DeviceLoop).GetField("_retryingWrites",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(loop);
            Assert.Empty((System.Collections.IEnumerable)tracking!);
            Assert.NotEqual(StatusCode.BadTooManyOperations,
                await loop.EnqueueWriteAsync(Request(binding, (short)2)).WaitAsync(stop.Token));
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_array_yields_to_queued_healthy_scalar_or_array_and_preserves_retry_slices(bool healthyIsArray)
    {
        await using var hub = new DataHub();
        var bad = ArrayBinding("BadArray");
        var good = healthyIsArray ? ArrayBinding("GoodArray") : Binding("Good");
        foreach (var binding in new[] { bad, good }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(bad.TagId);
        var loop = new DeviceLoop(driver, [bad, good], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        using var caller = new CancellationTokenSource();
        Task<ArrayWriteResult> failed = loop.EnqueueArrayWriteAsync(ArrayRequest(bad), caller.Token);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstStarted.Task.WaitAsync(stop.Token);
            caller.Cancel();
            Task<ArrayWriteResult>? arraySucceeded = healthyIsArray
                ? loop.EnqueueArrayWriteAsync(ArrayRequest(good)) : null;
            Task<StatusCode>? scalarSucceeded = healthyIsArray
                ? null : loop.EnqueueWriteAsync(Request(good, (short)7));
            Task succeeded = arraySucceeded is not null ? arraySucceeded : scalarSucceeded!;
            driver.ReleaseFirst.TrySetResult();
            await driver.RetryStarted.Task.WaitAsync(stop.Token);
            Assert.True(succeeded.IsCompleted, "An unresolved array must yield before its next retry.");
            Assert.Equal(StatusCode.Good, arraySucceeded is not null
                ? (await arraySucceeded).Status : await scalarSucceeded!);
            Assert.False(failed.IsCompleted);
            driver.ReleaseRetry.TrySetResult();
            Assert.Equal(StatusCode.BadNotVerified, (await failed.WaitAsync(stop.Token)).Status);
            var writes = driver.ArrayWrites.Where(item => item.Binding.TagId == bad.TagId).ToArray();
            Assert.Equal(11, writes.Length);
            Assert.Equal(2, writes[0].Value.Count);
            Assert.All(writes.Skip(1), item => Assert.Equal(1, item.Value.Count));
            Assert.True(driver.PriorityReads >= 12);
        }
        finally
        {
            driver.ReleaseFirst.TrySetResult();
            driver.ReleaseRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Normalized_driver_target_survives_a_yielded_retry_when_later_results_omit_actual_value()
    {
        await using var hub = new DataHub();
        var binding = Binding("String", IgwDataType.String);
        hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(binding.TagId) { BadWriteFailures = 1, NormalizedValue = "1234" };
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<StatusCode> written = loop.EnqueueWriteAsync(Request(binding, "12345"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        driver.ReleaseRetry.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            Assert.Equal(StatusCode.Good, await written.WaitAsync(stop.Token));
            Assert.Equal(2, driver.Writes.Count);
            Assert.True(hub.TryGet(binding.TagId, out var cached));
            Assert.Equal("1234", cached.Value);
            Assert.Equal(2, driver.PriorityReads);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Permanent_rejection_completes_before_other_points_verification_returns()
    {
        await using var hub = new DataHub();
        var bad = Binding("Bad");
        var good = Binding("Good");
        foreach (var binding in new[] { bad, good }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(bad.TagId)
        { WriteRejection = StatusCode.BadNotWritable, BlockVerificationRead = true };
        var loop = new DeviceLoop(driver, [bad, good], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(bad, (short)1));
        Task<StatusCode> succeeded = loop.EnqueueWriteAsync(Request(good, (short)7));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            await driver.ReadStarted.Task.WaitAsync(stop.Token);
            Assert.True(failed.IsCompleted);
            Assert.Equal(StatusCode.BadNotWritable, await failed);
            Assert.False(succeeded.IsCompleted);
            driver.ReleaseRead.TrySetResult();
            Assert.Equal(StatusCode.Good, await succeeded.WaitAsync(stop.Token));
        }
        finally
        {
            driver.ReleaseRead.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Yielding_retries_preserve_prequeued_same_tag_values_and_boolean_edges(bool boolean)
    {
        await using var hub = new DataHub();
        var binding = Binding("Ordered", boolean ? IgwDataType.Bool : IgwDataType.Int16);
        hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(binding.TagId) { BadWriteFailures = 1 };
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1,
            writeOpt: boolean ? "nonbool-latest" : "all");
        object firstValue = boolean ? true : (short)1;
        object secondValue = boolean ? false : (short)2;
        Task<StatusCode> first = loop.EnqueueWriteAsync(Request(binding, firstValue));
        Task<StatusCode> second = loop.EnqueueWriteAsync(Request(binding, secondValue));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            await driver.RetryStarted.Task.WaitAsync(stop.Token);
            Assert.Equal(new[] { firstValue, firstValue }, driver.Writes.Select(item => item.Value).ToArray());
            Assert.False(second.IsCompleted);
            driver.ReleaseRetry.TrySetResult();
            Assert.Equal(StatusCode.Good, await first.WaitAsync(stop.Token));
            Assert.Equal(StatusCode.Good, await second.WaitAsync(stop.Token));
            Assert.Equal(new[] { firstValue, firstValue, secondValue }, driver.Writes.Select(item => item.Value).ToArray());
        }
        finally
        {
            driver.ReleaseRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Later_verification_partition_exception_preserves_published_success_and_counts_only_unresolved_failure()
    {
        await using var hub = new DataHub();
        var good = Binding("Good");
        var bad = Binding("Bad");
        foreach (var binding in new[] { good, bad }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(bad.TagId) { MaxRead = 1, BadReadError = new FormatException("bad value") };
        var loop = new DeviceLoop(driver, [good, bad], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<StatusCode> succeeded = loop.EnqueueWriteAsync(Request(good, (short)7));
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(bad, (short)1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            Assert.Equal(StatusCode.Good, await succeeded.WaitAsync(stop.Token));
            Assert.Equal(StatusCode.BadDecodeError, await failed.WaitAsync(stop.Token));
            Assert.True(hub.TryGet(good.TagId, out var cached));
            Assert.Equal((short)7, cached.Value);
            Assert.Equal(1, loop.Stats.WritesOk);
            Assert.Equal(1, loop.Stats.WritesErr);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Verified_peer_completes_and_publishes_before_bad_peer_retry_finishes()
    {
        await using var hub = new DataHub();
        var bad = Binding("Bad");
        var good = Binding("Good");
        foreach (var binding in new[] { bad, good }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(bad.TagId);
        var loop = new DeviceLoop(driver, [bad, good], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(bad, (short)1));
        Task<StatusCode> succeeded = loop.EnqueueWriteAsync(Request(good, (short)7));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        driver.ReleaseFirst.TrySetResult();
        loop.Start(stop.Token);
        try
        {
            await driver.RetryStarted.Task.WaitAsync(stop.Token);
            Assert.True(succeeded.IsCompleted, "Verified peer must finish before an unrelated retry finishes.");
            Assert.Equal(StatusCode.Good, await succeeded);
            Assert.True(hub.TryGet(good.TagId, out var cached));
            Assert.Equal((short)7, cached.Value);
            Assert.False(failed.IsCompleted);
            driver.ReleaseRetry.TrySetResult();
            Assert.Equal(StatusCode.BadNotVerified, await failed.WaitAsync(stop.Token));
            Assert.Equal(1, driver.Writes.Count(item => item.TagId == good.TagId));
            Assert.Equal(11, driver.Writes.Count(item => item.TagId == bad.TagId));
        }
        finally
        {
            driver.ReleaseRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Queued_unrelated_write_runs_before_bad_point_retries_and_cancellation_does_not_erase_dispatched_write()
    {
        await using var hub = new DataHub();
        var bad = Binding("Bad");
        var good = Binding("Good");
        foreach (var binding in new[] { bad, good }) hub.Register(binding.TagId, binding.Def);
        var driver = new GatedDriver(bad.TagId);
        var loop = new DeviceLoop(driver, [bad, good], hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1,
            maxPendingWrites: 2);
        using var caller = new CancellationTokenSource();
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(bad, 1), caller.Token);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstStarted.Task.WaitAsync(stop.Token);
            caller.Cancel();
            Task<StatusCode> succeeded = loop.EnqueueWriteAsync(Request(good, 7));
            driver.ReleaseFirst.TrySetResult();
            await driver.RetryStarted.Task.WaitAsync(stop.Token);
            Assert.True(succeeded.IsCompleted, "Queued healthy write must run before the bad point's next attempt.");
            Assert.Equal(StatusCode.Good, await succeeded);
            Assert.False(failed.IsCompleted);
            driver.ReleaseRetry.TrySetResult();
            Assert.Equal(StatusCode.BadNotVerified, await failed.WaitAsync(stop.Token));
            Assert.Equal(11, driver.Writes.Count(item => item.TagId == bad.TagId));
            Assert.Equal(StatusCode.Good, await loop.EnqueueWriteAsync(Request(good, 8)).WaitAsync(stop.Token));
        }
        finally
        {
            driver.ReleaseFirst.TrySetResult();
            driver.ReleaseRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding Binding(string name, IgwDataType type = IgwDataType.Int16) => new()
    {
        TagId = "Line.Plc.DB." + name,
        Def = new TagDef { Name = name, Address = name, DataType = type,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000 },
        Address = new TagAddress(0, 0, 2),
    };

    private static WriteRequest Request(TagBinding binding, object value) => new()
    {
        TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = value,
    };

    private static TagBinding ArrayBinding(string name) => new()
    {
        TagId = "Line.Plc.DB." + name,
        Def = new TagDef { Name = name, Address = name, DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
            Array = new ArrayDef { ElementType = IgwDataType.Int16, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 2 } },
        Address = new TagAddress(0, 0, 4),
    };

    private static ArrayWriteRequest ArrayRequest(TagBinding binding) => new(binding, new ArraySlice([0], [2]),
        PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), [7, 8]));

    private sealed class GatedDriver(string badTag) : IDeviceDriver, IDriverCapabilityProvider, IWriteVerificationReader, IArrayCapableDriver
    {
        private readonly ConcurrentDictionary<string, object> _values = new();
        private int _badWrites;
        private int _badArrayWrites;
        public int PriorityReads { get; private set; }
        public ConcurrentQueue<ArrayWriteRequest> ArrayWrites { get; } = new();
        public int BadWriteFailures { get; init; } = int.MaxValue;
        public int MaxRead { get; init; } = 100;
        public Exception? BadReadError { get; init; }
        public Exception? RetryWriteError { get; init; }
        public object? NormalizedValue { get; init; }
        public StatusCode? WriteRejection { get; init; }
        public bool BlockVerificationRead { get; init; }
        public ConcurrentQueue<WriteRequest> Writes { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DriverId => "GatedWriteIsolation";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 100 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        { SuggestedVerificationDelayMs = 0, MaxTagsPerRead = MaxRead };
        public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
            NativeArrays: true, StructuredArrays: false, MultiDimensional: false,
            SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
            MaxElementsPerRequest: 100, MaxBytesPerRequest: 4096);

        public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
        {
            ArrayWrites.Enqueue(request);
            if (request.Binding.TagId == badTag)
            {
                int count = ++_badArrayWrites;
                if (count == 1)
                {
                    FirstStarted.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(ct);
                }
                else if (count == 2)
                {
                    RetryStarted.TrySetResult();
                    await ReleaseRetry.Task.WaitAsync(ct);
                }
                _values[request.Binding.TagId] = PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), [-1, 8]);
            }
            else _values[request.Binding.TagId] = request.Value;
            return new ArrayWriteResult(StatusCode.Good, request.Value.Count, []);
        }

        public Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            PriorityReads++;
            return ReadAsync(tags, ct);
        }
        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            foreach (var write in writes)
            {
                Writes.Enqueue(write);
                if (write.TagId == badTag)
                {
                    int count = Interlocked.Increment(ref _badWrites);
                    if (count == 1)
                    {
                        FirstStarted.TrySetResult();
                        await ReleaseFirst.Task.WaitAsync(ct);
                    }
                    else if (count == 2)
                    {
                        RetryStarted.TrySetResult();
                        await ReleaseRetry.Task.WaitAsync(ct);
                        if (RetryWriteError is not null) throw RetryWriteError;
                    }
                    _values[write.TagId] = count <= BadWriteFailures
                        ? write.Value is bool boolean ? !boolean : (short)-1
                        : NormalizedValue ?? write.Value;
                }
                else _values[write.TagId] = write.Value;
            }
            return writes.Select(write => new WriteResult
            {
                TagId = write.TagId,
                Status = write.TagId == badTag ? WriteRejection ?? StatusCode.Good : StatusCode.Good,
                ActualValue = write.TagId == badTag && _badWrites == 1 ? NormalizedValue : null,
            }).ToArray();
        }
        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (BlockVerificationRead)
            {
                ReadStarted.TrySetResult();
                await ReleaseRead.Task.WaitAsync(ct);
            }
            if (BadReadError is not null && tags.Any(tag => tag.TagId == badTag)) throw BadReadError;
            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(tag.TagId,
                new DataValue(_values.GetValueOrDefault(tag.TagId, (short)0), StatusCode.Good, now, now))).ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new TagAddress(0, 0, 2);
            error = null;
            return true;
        }
    }
}
