using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

public sealed class SiemensHybridSessionLifecycleTests
{
    [Fact]
    public async Task Reset_blocks_connect_and_read_until_replacement_without_reopening_old_transport()
    {
        var sessions = new SessionCount();
        var oldClassic = new CountingClassic(sessions);
        var nextClassic = new CountingClassic(sessions);
        var oldPlus = new BlockingDisposePlus();
        var driver = new SiemensHybridDeviceDriver("plc", 102, TimeSpan.FromSeconds(1), false,
            oldClassic, oldPlus, classicFactory: () => nextClassic,
            plusFactory: () => new BlockingDisposePlus(block: false));
        await driver.ConnectAsync(default);
        Assert.Equal(1, sessions.Live);
        var resetting = driver.ResetConnectionAsync(default);
        await oldPlus.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? connecting = null;
        Task<IReadOnlyList<TagSample>>? reading = null;
        try
        {
            connecting = driver.ConnectAsync(default);
            reading = driver.ReadAsync([new TagBinding { TagId = "Line.Plc.Value",
                Def = new TagDef { Address = "DB1.DBW0", DataType = IgwDataType.UInt16 }, Address = new(1, 0, 2) }], default);
            Assert.False(connecting.IsCompleted);
            Assert.False(reading.IsCompleted);
            Assert.Equal(1, oldClassic.Connects);
            Assert.Equal(0, sessions.Live);
        }
        finally
        {
            oldPlus.AllowDisposal.TrySetResult();
            await resetting.WaitAsync(TimeSpan.FromSeconds(2));
            if (connecting is not null) await connecting.WaitAsync(TimeSpan.FromSeconds(2));
            if (reading is not null) await reading.WaitAsync(TimeSpan.FromSeconds(2));
            await driver.DisposeAsync();
        }
        Assert.Equal(1, oldClassic.Connects);
        Assert.False(oldClassic.ReopenedAfterDisposal);
        Assert.Equal(1, nextClassic.Connects);
        Assert.Equal(1, nextClassic.Reads);
        Assert.Equal(1, sessions.Maximum);
        Assert.Equal(0, sessions.Live);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => driver.ConnectAsync(default));
    }

    private sealed class SessionCount
    {
        public int Live, Maximum;
        public void Open() { Live++; Maximum = Math.Max(Maximum, Live); }
        public void Close() => Live--;
    }

    // Intentionally allows reconnect after disposal, like the concrete legacy IoT adapter.
    // The owning driver's gate must prevent that old generation from ever being used again.
    private sealed class CountingClassic(SessionCount sessions) : ISiemensClassicTransport
    {
        public bool IsConnected { get; private set; }
        public int Connects, Reads;
        public bool Disposed, ReopenedAfterDisposal;
        public Task ConnectAsync(CancellationToken ct)
        {
            Connects++;
            ReopenedAfterDisposal |= Disposed;
            if (!IsConnected) { IsConnected = true; sessions.Open(); }
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            if (IsConnected) { IsConnected = false; sessions.Close(); }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(t =>
                new TagSample(t.TagId, DataValue.GoodAt((ushort)7, DateTime.UtcNow))).ToArray());
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            throw new NotSupportedException();
        public async ValueTask DisposeAsync() { await DisconnectAsync(); Disposed = true; }
    }

    private sealed class BlockingDisposePlus(bool block = true) : ISiemensPlusTransport
    {
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => false;
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct) => throw new NotSupportedException();
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            DisposalEntered.TrySetResult();
            if (block) await AllowDisposal.Task;
        }
    }
}
