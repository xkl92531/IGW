using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceLoopHalfOpenIsolationTests
{
    [Fact]
    public async Task Half_open_read_timeout_keeps_the_recovered_transport_session()
    {
        await using var hub = new DataHub();
        const string tagId = "Line.Plc.Group.Value";
        var definition = new TagDef
        {
            Name = "Value",
            Address = "Area1",
            DataType = IgwDataType.Int16,
            ScanRateMs = 100,
        };
        var binding = new TagBinding
        {
            TagId = tagId,
            Def = definition,
            Address = new TagAddress(1, 0, 2),
        };
        hub.Register(tagId, definition);
        var driver = new ConnectOnceThenReadTimeoutDriver();
        var loop = new DeviceLoop(driver, [binding], hub, NullLogger.Instance, "Line.Plc",
            circuitFailureThreshold: 1, circuitOpenMs: 1000);
        using var demand = loop.AcquireDemand();
        using var stop = new CancellationTokenSource();

        loop.Start(stop.Token);
        try
        {
            await driver.ReadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);

            Assert.Equal(1, driver.DisconnectCalls); // only the failed initial connect was reset
            Assert.Equal(ConnectionState.Connected, driver.State);
        }
        finally
        {
            stop.Cancel();
            await loop.Completion;
        }
    }

    private sealed class ConnectOnceThenReadTimeoutDriver : IDeviceDriver
    {
        private int _connects;
        public TaskCompletionSource ReadAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisconnectCalls { get; private set; }
        public string DriverId => "half-open-timeout";
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1, MaxTagsPerWrite = 1 };
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public event Action<ConnectionState>? StateChanged;

        public Task ConnectAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _connects) == 1)
                throw new IOException("initial transport failure");
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

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            ReadAttempted.TrySetResult();
            throw new TimeoutException("read request timed out while session remains usable");
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string text, IgwDataType type, out TagAddress address, out string? error)
        {
            address = new TagAddress(1, 0, 2);
            error = null;
            return true;
        }
    }
}
