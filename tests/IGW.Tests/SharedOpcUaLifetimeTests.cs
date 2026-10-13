using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class SharedOpcUaLifetimeTests
{
    [Fact]
    public async Task An_unused_intermediate_generation_cannot_let_a_new_connection_overtake_the_old_close()
    {
        var releaseOld = Signal();
        var oldClosing = Signal();
        var old = new Driver { Closing = async () => { oldClosing.TrySetResult(); await releaseOld.Task; } };
        await using var pool = new OpcUaConnectionPool();
        var a = pool.Acquire("plc", () => new(old, null));
        await a.ConnectAsync(default);
        var closingA = a.DisposeAsync().AsTask();
        await oldClosing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = pool.Acquire("plc", () => new(new Driver(), null));
        var closingB = b.DisposeAsync().AsTask();
        var newest = new Driver();
        var c = pool.Acquire("plc", () => new(newest, null));
        var connectingC = c.ConnectAsync(default);
        try
        {
            Assert.False(connectingC.IsCompleted, "The new connection bypassed an older generation still disposing.");
            Assert.Equal(0, newest.Connects);
        }
        finally
        {
            releaseOld.TrySetResult();
            await Task.WhenAll(closingA, closingB, connectingC).WaitAsync(TimeSpan.FromSeconds(5));
            await c.DisposeAsync();
        }
        Assert.Equal(1, newest.Connects);
    }

    [Fact]
    public async Task Throwing_subscription_cleanup_still_releases_the_last_session_owner()
    {
        var driver = new Driver { ThrowSubscriptionDispose = true };
        await using var pool = new OpcUaConnectionPool();
        var lease = pool.Acquire("plc", () => new(driver, null));
        await lease.ConnectAsync(default);
        await lease.SubscribeAsync([Binding("value")], _ => { }, default);
        // The caller may receive the cleanup error; resource release must happen either way.
        await Record.ExceptionAsync(() => lease.DisposeAsync().AsTask());
        Assert.Equal(1, driver.Disposals);
        var replacement = new Driver();
        await using var next = pool.Acquire("plc", () => new(replacement, null));
        await next.ConnectAsync(default);
        Assert.Equal(1, replacement.Connects);
    }

    [Fact]
    public async Task Success_from_an_already_running_request_cannot_erase_a_peer_failure_backoff()
    {
        var goodStarted = Signal();
        var releaseGood = Signal();
        var driver = new Driver
        {
            Reading = async (tags, ct) =>
            {
                if (tags[0].TagId == "fail") throw new IOException("transport lost");
                goodStarted.TrySetResult();
                await releaseGood.Task.WaitAsync(ct);
                return Samples(tags);
            }
        };
        await using var pool = new OpcUaConnectionPool(retryBaseMs: 30_000);
        await using var a = pool.Acquire("plc", () => new(driver, null));
        await using var b = pool.Acquire("plc", () => throw new InvalidOperationException("duplicate session"));
        await Task.WhenAll(a.ConnectAsync(default), b.ConnectAsync(default));
        var olderSuccess = b.ReadAsync([Binding("good")], default);
        await goodStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => a.ReadAsync([Binding("fail")], default));
            releaseGood.TrySetResult();
            await olderSuccess.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            var retry = a.ConnectAsync(cancelled.Token);
            try
            {
                Assert.False(retry.IsCompleted, "A stale success cleared the failure backoff.");
                Assert.Equal(1, driver.Connects);
            }
            finally { cancelled.Cancel(); await Record.ExceptionAsync(() => retry); }
        }
        finally { releaseGood.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_subscription_callbacks_are_rejected_after_disconnect_or_generation_replacement(bool disconnectOnly)
    {
        var driver = new Driver();
        await using var pool = new OpcUaConnectionPool(retryBaseMs: 1);
        await using var a = pool.Acquire("plc", () => new(driver, null));
        await using var b = pool.Acquire("plc", () => throw new InvalidOperationException("duplicate session"));
        await Task.WhenAll(a.ConnectAsync(default), b.ConnectAsync(default));
        int published = 0;
        await b.SubscribeAsync([Binding("value")], _ => published++, default);
        var obsolete = driver.Callbacks.Single();
        obsolete(Samples([Binding("value")]));
        Assert.Equal(1, published);
        if (disconnectOnly) await b.DisconnectAsync();
        else
        {
            driver.Reading = (_, _) => throw new IOException("transport lost");
            await Assert.ThrowsAsync<IOException>(() => a.ReadAsync([Binding("fail")], default));
            driver.Reading = null;
            await a.ConnectAsync(default);
        }
        obsolete(Samples([Binding("value")]));
        Assert.Equal(1, published);
        await b.ConnectAsync(default);
        await b.SubscribeAsync([Binding("value")], _ => published++, default);
        obsolete(Samples([Binding("value")]));
        driver.Callbacks.Last()(Samples([Binding("value")]));
        Assert.Equal(2, published);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TagBinding Binding(string id) => new()
    {
        TagId = id,
        Def = new TagDef { Name = id, Address = "ns=2;s=" + id, DataType = IgwDataType.Int16 },
        Address = new(0, 0, 2),
    };
    private static IReadOnlyList<TagSample> Samples(IReadOnlyList<TagBinding> tags) => tags.Select(tag =>
        new TagSample(tag.TagId, DataValue.GoodAt((short)42, DateTime.UtcNow))).ToArray();

    private sealed class Driver : IDeviceDriver, IDriverSubscriptionProvider
    {
        public int Connects;
        public int Disposals;
        public bool ThrowSubscriptionDispose;
        public Func<Task>? Closing;
        public Func<IReadOnlyList<TagBinding>, CancellationToken, Task<IReadOnlyList<TagSample>>>? Reading;
        public List<Action<IReadOnlyList<TagSample>>> Callbacks { get; } = [];
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser => throw new NotSupportedException();
        public DriverLimits Limits { get; } = new();
        public Task ConnectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Connects++; State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => Reading?.Invoke(tags, ct) ?? Task.FromResult(Samples(tags));
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags, Action<IReadOnlyList<TagSample>> callback, CancellationToken ct)
        {
            // Retaining the delegate models a notification already queued by the SDK before unsubscribe.
            Callbacks.Add(callback);
            return Task.FromResult<IDriverSubscription>(new Subscription(tags.Select(tag => tag.TagId).ToArray(), ThrowSubscriptionDispose));
        }
        public async ValueTask DisposeAsync() { Disposals++; if (Closing is not null) await Closing(); State = ConnectionState.Disconnected; }
    }

    private sealed class Subscription(IReadOnlyCollection<string> ids, bool throwOnDispose) : IDriverSubscription
    {
        public IReadOnlyCollection<string> TagIds => ids;
        public ValueTask DisposeAsync() => throwOnDispose ? ValueTask.FromException(new IOException("subscription cleanup failed")) : ValueTask.CompletedTask;
    }
}
