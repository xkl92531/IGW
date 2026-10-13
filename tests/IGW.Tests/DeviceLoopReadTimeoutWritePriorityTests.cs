using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopReadTimeoutWritePriorityTests
{
    [Fact]
    public async Task Capacity_read_timeout_keeps_session_and_allows_queued_write_to_verify()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.Group.Value";
        var definition = new TagDef
        {
            Name = "Value",
            Address = "Area1",
            DataType = IgwDataType.Int16,
            ScanRateMs = 1,
            Access = TagAccess.ReadWrite,
        };
        var binding = new TagBinding
        {
            TagId = tagId,
            Def = definition,
            Address = new TagAddress(1, 0, 2),
        };
        hub.Register(tagId, definition);
        var demands = new ScanDemandRegistry(hub);
        using var demand = demands.Acquire("test", [tagId], 1);
        var driver = new CapacityTimeoutDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            scanDemands: demands);
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        try
        {
            await driver.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<StatusCode> write = loop.EnqueueWriteAsync(new WriteRequest
            {
                TagId = tagId,
                Def = definition,
                Address = binding.Address,
                Value = (short)7,
            });
            driver.ReleaseFirstReadFailure.TrySetResult();

            StatusCode status = await write.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(status.IsGood, status.ToString());
            Assert.Equal(0, driver.DisconnectCalls);
            Assert.True(driver.WriteCalls > 0);
            Assert.Equal(ConnectionState.Connected, driver.State);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    private sealed class CapacityTimeoutDriver : IDeviceDriver
    {
        private int _reads;
        private short _value;

        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstReadFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisconnectCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public string DriverId => "CapacityTimeout";
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new PassthroughParser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1, MaxTagsPerWrite = 1 };

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

        public async Task<IReadOnlyList<TagSample>> ReadAsync(
            IReadOnlyList<TagBinding> tags,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstReadFailure.Task.WaitAsync(ct);
                throw new DriverOperationException(
                    "large request exceeded the current service window",
                    DriverFailureScope.Capacity,
                    DriverRetryDisposition.ReduceBatchAndRetry,
                    StatusCode.BadTimeout);
            }

            var now = DateTime.UtcNow;
            return tags.Select(tag => new TagSample(
                tag.TagId,
                new DataValue(_value, StatusCode.Good, now, now))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(
            IReadOnlyList<WriteRequest> writes,
            CancellationToken ct)
        {
            WriteCalls++;
            _value = Convert.ToInt16(writes[^1].Value);
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(write => new WriteResult
            {
                TagId = write.TagId,
                Status = StatusCode.Good,
            }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassthroughParser : IAddressParser
    {
        public bool TryParse(string text, IgwDataType type, out TagAddress address, out string? error)
        {
            address = new TagAddress(1, 0, 2);
            error = null;
            return true;
        }
    }
}
