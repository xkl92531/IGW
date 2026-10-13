using System.Diagnostics;
using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace IGW.Tests;

public sealed class HighDensityWritePipelineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Two_thousand_four_hundred_seventy_seven_points_downshift_without_dropping_tags_or_writes()
    {
        const int count = 2_477;
        await using var hub = new DataHub();
        var bindings = new TagBinding[count];
        for (int index = 0; index < count; index++)
        {
            string id = $"Line.Plc.DB.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}", Address = $"DB1.DBW{index * 2}", DataType = IgwDataType.Int16,
                Access = TagAccess.ReadWrite, ScanRateMs = 20,
            };
            hub.Register(id, definition);
            bindings[index] = new TagBinding
            {
                TagId = id, Def = definition, Address = new TagAddress(1, index * 2, 2),
            };
        }

        var driver = new AdaptiveHighDensityDriver(count);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("stress", bindings.Select(binding => binding.TagId), 20);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc",
            idlePollMs: 1, scanDemands: demands);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstOversizedReadStarted.Task.WaitAsync(stop.Token);
            Task<StatusCode> write = loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = bindings[0].TagId,
                Def = bindings[0].Def,
                Address = bindings[0].Address,
                Value = (short)77,
            });
            driver.ReleaseOversizedRead.TrySetResult();

            Assert.Equal(StatusCode.Good, await write.WaitAsync(stop.Token));
            await WaitUntilAsync(() => driver.DistinctReadTags.Count == count, stop.Token);

            Assert.Equal(0, driver.DisconnectCalls);
            Assert.Equal(count, driver.DistinctReadTags.Count);
            Assert.All(driver.BatchSizesAfterDownshift, size => Assert.InRange(size, 1, 256));
            Assert.Equal((short)77, driver.Values[0]);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task One_hundred_thousand_write_intents_are_batched_and_read_back_once()
    {
        const int count = 100_000;
        var hub = new DataHub();
        var bindings = new TagBinding[count];
        for (int index = 0; index < count; index++)
        {
            string id = $"Line.Plc.DB.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}", Address = $"Value{index}", DataType = IgwDataType.Int32,
                Access = TagAccess.ReadWrite, ScanRateMs = 30_000,
            };
            hub.Register(id, definition);
            bindings[index] = new TagBinding
            {
                TagId = id, Def = definition, Address = new TagAddress(0, index * 4, 4),
            };
        }

        var driver = new HighDensityDriver(count);
        var loop = new DeviceLoop(driver, bindings, hub, NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        var completions = new Task<StatusCode>[count];
        for (int index = 0; index < count; index++)
        {
            var binding = bindings[index];
            completions[index] = loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = index,
            });
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        loop.Start(stop.Token);
        var statuses = await Task.WhenAll(completions).WaitAsync(stop.Token);
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;

        Assert.All(statuses, status => Assert.Equal(StatusCode.Good, status));
        Assert.Single(driver.WriteBatchSizes);
        Assert.Equal(count, driver.WriteBatchSizes[0]);
        Assert.Single(driver.ReadBatchSizes);
        Assert.Equal(count, driver.ReadBatchSizes[0]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"耗时 {watch.Elapsed}");
        output.WriteLine($"writes={count:N0}; elapsedMs={watch.Elapsed.TotalMilliseconds:F1}; allocatedMB={allocated / 1024d / 1024d:F1}");

        stop.Cancel();
        await loop.Completion;
        await hub.DisposeAsync();
    }

    private sealed class HighDensityDriver(int capacity) : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly int[] _values = new int[capacity];
        public List<int> WriteBatchSizes { get; } = [];
        public List<int> ReadBatchSizes { get; } = [];
        public string DriverId => "HighDensity";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new()
        {
            MaxReadUnits = int.MaxValue, MaxGap = 0,
            MaxTagsPerRead = 100_000, MaxTagsPerWrite = 100_000,
        };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SuggestedVerificationDelayMs = 0,
        };

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

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            ReadBatchSizes.Add(tags.Count);
            var now = DateTime.UtcNow;
            var samples = new TagSample[tags.Count];
            for (int index = 0; index < tags.Count; index++)
            {
                var tag = tags[index];
                int address = tag.Address.Offset / 4;
                samples[index] = new TagSample(tag.TagId,
                    new DataValue(_values[address], StatusCode.Good, now, now));
            }
            return Task.FromResult<IReadOnlyList<TagSample>>(samples);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            WriteBatchSizes.Add(writes.Count);
            var results = new WriteResult[writes.Count];
            for (int index = 0; index < writes.Count; index++)
            {
                var write = writes[index];
                _values[write.Address.Offset / 4] = Convert.ToInt32(write.Value);
                results[index] = new WriteResult { TagId = write.TagId, Status = StatusCode.Good };
            }
            return Task.FromResult<IReadOnlyList<WriteResult>>(results);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(0, 0, 4);
                error = null;
                return true;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private sealed class AdaptiveHighDensityDriver(int capacity) : IDeviceDriver, IDriverCapabilityProvider
    {
        private int _maxRead = 512;
        private long _version = 1;
        public short[] Values { get; } = new short[capacity];
        public ConcurrentDictionary<string, byte> DistinctReadTags { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<int> BatchSizesAfterDownshift { get; } = new();
        public TaskCompletionSource FirstOversizedReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOversizedRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisconnectCalls { get; private set; }
        public string DriverId => "AdaptiveHighDensity";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new()
        {
            MaxReadUnits = int.MaxValue, MaxGap = 0,
            MaxTagsPerRead = 10_000, MaxTagsPerWrite = 10_000,
        };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            MaxTagsPerRead = Volatile.Read(ref _maxRead),
            MaxTagsPerWrite = 10_000,
            SuggestedVerificationDelayMs = 0,
            Version = Volatile.Read(ref _version),
        };

        public Task ConnectAsync(CancellationToken ct)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            if (tags.Count > 256)
            {
                FirstOversizedReadStarted.TrySetResult();
                await ReleaseOversizedRead.Task.WaitAsync(ct);
                Volatile.Write(ref _maxRead, 256);
                Interlocked.Increment(ref _version);
                throw new DriverOperationException(
                    "server request window exceeded",
                    DriverFailureScope.Capacity,
                    DriverRetryDisposition.ReduceBatchAndRetry,
                    StatusCode.BadTimeout);
            }

            BatchSizesAfterDownshift.Enqueue(tags.Count);
            var now = DateTime.UtcNow;
            var samples = new TagSample[tags.Count];
            for (int index = 0; index < tags.Count; index++)
            {
                var tag = tags[index];
                DistinctReadTags.TryAdd(tag.TagId, 0);
                samples[index] = new TagSample(tag.TagId,
                    new DataValue(Values[tag.Address.Offset / 2], StatusCode.Good, now, now));
            }
            return samples;
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            var results = new WriteResult[writes.Count];
            for (int index = 0; index < writes.Count; index++)
            {
                var write = writes[index];
                Values[write.Address.Offset / 2] = Convert.ToInt16(write.Value);
                results[index] = new WriteResult { TagId = write.TagId, Status = StatusCode.Good };
            }
            return Task.FromResult<IReadOnlyList<WriteResult>>(results);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
            {
                result = new TagAddress(1, 0, 2);
                error = null;
                return true;
            }
        }
    }
}
