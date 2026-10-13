using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ArrayRetryFairnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Array_retry_gets_a_turn_after_a_bounded_burst_of_fresh_scalar_or_array_writes(
        bool freshWritesAreArrays)
    {
        await using var hub = new DataHub();
        var bad = ArrayBinding("BadArray");
        TagBinding[] healthy = Enumerable.Range(0, 20)
            .Select(index => freshWritesAreArrays
                ? ArrayBinding($"GoodArray{index}")
                : ScalarBinding($"Good{index}"))
            .ToArray();
        foreach (var binding in healthy.Prepend(bad)) hub.Register(binding.TagId, binding.Def);

        var driver = new FairnessDriver(bad.TagId);
        var loop = new DeviceLoop(driver, healthy.Prepend(bad).ToArray(), hub,
            NullLogger.Instance, "Line.Plc", idlePollMs: 1);
        Task<ArrayWriteResult> failed = loop.EnqueueArrayWriteAsync(ArrayRequest(bad));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);
        try
        {
            await driver.FirstBadWriteStarted.Task.WaitAsync(stop.Token);
            Task[] healthyWrites = freshWritesAreArrays
                ? healthy.Select(binding => (Task)loop.EnqueueArrayWriteAsync(ArrayRequest(binding))).ToArray()
                : healthy.Select((binding, index) =>
                    (Task)loop.EnqueueWriteAsync(ScalarRequest(binding, (short)(index + 1)))).ToArray();

            driver.ReleaseFirstBadWrite.TrySetResult();
            await driver.BadRetryStarted.Task.WaitAsync(stop.Token);

            Assert.InRange(driver.HealthyWritesAtBadRetry, 1, 8);
            driver.ReleaseBadRetry.TrySetResult();
            await Task.WhenAll(healthyWrites).WaitAsync(stop.Token);
            Assert.Equal(StatusCode.BadNotVerified, (await failed.WaitAsync(stop.Token)).Status);
        }
        finally
        {
            driver.ReleaseFirstBadWrite.TrySetResult();
            driver.ReleaseBadRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
        }
    }

    private static TagBinding ScalarBinding(string name) => new()
    {
        TagId = "Line.Plc.DB." + name,
        Def = new TagDef
        {
            Name = name,
            Address = name,
            DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 30_000,
        },
        Address = new TagAddress(0, 0, 2),
    };

    private static TagBinding ArrayBinding(string name) => new()
    {
        TagId = "Line.Plc.DB." + name,
        Def = new TagDef
        {
            Name = name,
            Address = name,
            DataType = IgwDataType.Int16,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 30_000,
            Array = new ArrayDef
            {
                ElementType = IgwDataType.Int16,
                Dimensions = [2],
                LowerBounds = [0],
                ElementStrideBytes = 2,
            },
        },
        Address = new TagAddress(0, 0, 4),
    };

    private static WriteRequest ScalarRequest(TagBinding binding, short value) => new()
    {
        TagId = binding.TagId,
        Def = binding.Def,
        Address = binding.Address,
        Value = value,
    };

    private static ArrayWriteRequest ArrayRequest(TagBinding binding) => new(
        binding,
        new ArraySlice([0], [2]),
        PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), [7, 8]));

    private sealed class FairnessDriver(string badArrayTag) :
        IDeviceDriver, IDriverCapabilityProvider, IArrayCapableDriver, IWriteVerificationReader
    {
        private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
        private int _badWrites;
        private int _healthyWrites;

        public TaskCompletionSource FirstBadWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstBadWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BadRetryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBadRetry { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HealthyWritesAtBadRetry { get; private set; }
        public string DriverId => "array-retry-fairness";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 100, MaxTagsPerWrite = 1 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            MaxTagsPerRead = 100,
            MaxTagsPerWrite = 1,
            SuggestedVerificationDelayMs = 0,
        };
        public ArrayDriverCapabilities ArrayCapabilities { get; } = new(
            NativeArrays: true, StructuredArrays: false, MultiDimensional: false,
            SliceRead: true, SliceWrite: true, AtomicWholeWrite: false,
            MaxElementsPerRequest: 100, MaxBytesPerRequest: 4096);

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

        public async Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct)
        {
            if (request.Binding.TagId == badArrayTag)
            {
                int attempt = Interlocked.Increment(ref _badWrites);
                if (attempt == 1)
                {
                    FirstBadWriteStarted.TrySetResult();
                    await ReleaseFirstBadWrite.Task.WaitAsync(ct);
                }
                else if (attempt == 2)
                {
                    HealthyWritesAtBadRetry = Volatile.Read(ref _healthyWrites);
                    BadRetryStarted.TrySetResult();
                    await ReleaseBadRetry.Task.WaitAsync(ct);
                }
                _values[request.Binding.TagId] = PrimitiveArrayValue<short>.Create(
                    ArrayShape.Create([2], [0]), [-1, 8]);
            }
            else
            {
                _values[request.Binding.TagId] = request.Value;
                Interlocked.Increment(ref _healthyWrites);
            }
            return new ArrayWriteResult(StatusCode.Good, request.Value.Count, []);
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(
            IReadOnlyList<WriteRequest> writes,
            CancellationToken ct)
        {
            foreach (var write in writes)
            {
                _values[write.TagId] = write.Value;
                Interlocked.Increment(ref _healthyWrites);
            }
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
            {
                TagId = write.TagId,
                Status = StatusCode.Good,
            }).ToArray());
        }

        public Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct) => ReadAsync(tags, ct);

        public Task<IReadOnlyList<TagSample>> ReadAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag => new TagSample(
                tag.TagId,
                new DataValue(_values.GetValueOrDefault(tag.TagId,
                    tag.Def.Array is null
                        ? (object)(short)0
                        : PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), [0, 0])),
                    StatusCode.Good, now, now))).ToArray());
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
