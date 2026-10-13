using System.Collections.Concurrent;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

public sealed class WriteSchedulingReviewTests
{
    [Fact]
    public async Task Throwing_write_diagnostic_logger_does_not_change_a_verified_good_result()
    {
        await using var hub = new DataHub();
        var binding = Binding("Good");
        hub.Register(binding.TagId, binding.Def);
        var driver = new ReviewDriver();
        var loop = new DeviceLoop(driver, [binding], hub, new ThrowingWriteCompletionLogger(),
            "Line.Plc", idlePollMs: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            StatusCode result = await loop.EnqueueWriteAsync(Request(binding, 7)).WaitAsync(stop.Token);

            Assert.Equal(StatusCode.Good, result);
            Assert.True(hub.TryGet(binding.TagId, out var cached));
            Assert.Equal((short)7, cached.Value);
            Assert.Equal(1, loop.Stats.WritesOk);
            Assert.Equal(0, loop.Stats.WritesErr);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    [Fact]
    public async Task Continuous_fresh_writes_allow_a_finite_burst_before_a_due_retry_runs()
    {
        await using var hub = new DataHub();
        var bad = Binding("Bad");
        TagBinding[] fresh = Enumerable.Range(0, 32).Select(i => Binding($"Fresh{i}")).ToArray();
        foreach (var binding in fresh.Prepend(bad)) hub.Register(binding.TagId, binding.Def);
        var driver = new ReviewDriver { BadTagId = bad.TagId };
        var loop = new DeviceLoop(driver, fresh.Prepend(bad).ToArray(), hub,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            "Line.Plc", idlePollMs: 1, maxPendingWrites: 64);
        var freshResults = new ConcurrentQueue<Task<StatusCode>>();
        int nextFresh = 1;
        driver.OnFreshWrite = () =>
        {
            int index = Interlocked.Increment(ref nextFresh) - 1;
            if (index < fresh.Length)
                freshResults.Enqueue(loop.EnqueueWriteAsync(Request(fresh[index], index)));
        };
        Task<StatusCode> failed = loop.EnqueueWriteAsync(Request(bad, 1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        loop.Start(stop.Token);

        try
        {
            await driver.FirstBadWriteStarted.Task.WaitAsync(stop.Token);
            freshResults.Enqueue(loop.EnqueueWriteAsync(Request(fresh[0], 0)));
            driver.ReleaseFirstBadWrite.TrySetResult();

            await driver.BadRetryStarted.Task.WaitAsync(stop.Token);

            Assert.InRange(driver.FreshWritesBeforeRetry, 1, 8);
            Assert.False(failed.IsCompleted);
        }
        finally
        {
            driver.ReleaseFirstBadWrite.TrySetResult();
            driver.ReleaseBadRetry.TrySetResult();
            stop.Cancel();
            await loop.Completion;
            await failed;
        }
    }

    private static TagBinding Binding(string name) => new()
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

    private static WriteRequest Request(TagBinding binding, int value) => new()
    {
        TagId = binding.TagId,
        Def = binding.Def,
        Address = binding.Address,
        Value = (short)value,
    };

    private sealed class ReviewDriver : IDeviceDriver, IDriverCapabilityProvider
    {
        private readonly ConcurrentDictionary<string, short> _values = new(StringComparer.Ordinal);
        private int _badWrites;
        private int _freshWrites;

        public string? BadTagId { get; init; }
        public Action? OnFreshWrite { get; set; }
        public TaskCompletionSource FirstBadWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstBadWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BadRetryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBadRetry { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FreshWritesBeforeRetry { get; private set; }
        public string DriverId => "WriteSchedulingReview";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1, MaxTagsPerWrite = 1 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            MaxTagsPerRead = 1,
            MaxTagsPerWrite = 1,
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

        public async Task<IReadOnlyList<WriteResult>> WriteAsync(
            IReadOnlyList<WriteRequest> writes,
            CancellationToken ct)
        {
            foreach (var write in writes)
            {
                if (write.TagId == BadTagId)
                {
                    int attempt = Interlocked.Increment(ref _badWrites);
                    if (attempt == 1)
                    {
                        FirstBadWriteStarted.TrySetResult();
                        await ReleaseFirstBadWrite.Task.WaitAsync(ct);
                    }
                    else if (attempt == 2)
                    {
                        FreshWritesBeforeRetry = Volatile.Read(ref _freshWrites);
                        BadRetryStarted.TrySetResult();
                        await ReleaseBadRetry.Task.WaitAsync(ct);
                    }
                    _values[write.TagId] = -1;
                }
                else
                {
                    _values[write.TagId] = Convert.ToInt16(write.Value);
                    Interlocked.Increment(ref _freshWrites);
                    OnFreshWrite?.Invoke();
                }
            }
            return writes.Select(write =>
                new WriteResult { TagId = write.TagId, Status = StatusCode.Good }).ToArray();
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId,
                    new DataValue(_values.GetValueOrDefault(tag.TagId), StatusCode.Good, now, now))).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingWriteCompletionLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("写入批次完成", StringComparison.Ordinal))
                throw new InvalidOperationException("injected write diagnostic failure");
        }
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
