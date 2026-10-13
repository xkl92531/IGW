using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using IGW.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaPreviewReuseTests
{
    [Fact]
    public async Task Different_credentials_use_different_sessions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-browse-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new CountingFactory();
        var registry = new DriverRegistry(); registry.Register(factory);
        try
        {
            var storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
                new ProjectStoragePaths(Path.Combine(dir, "project.db"), Path.Combine(dir, "project.igwproj"), Path.Combine(dir, "igw.masterkey")), default);
            await using var host = new GatewayHost(await storage.Repository.LoadRuntimeProjectAsync(default), storage.Repository,
                await storage.Repository.GetRevisionAsync(default), registry, NullLoggerFactory.Instance, dir);
            var connection = new BrowsePreviewRequest("opc.tcp://127.0.0.1:4840", Anonymous: false, Username: "operator", Password: "first");
            await host.BrowseOpcUaChildrenAsync(connection, "i=85", [], default);
            await host.BrowseOpcUaChildrenAsync(connection with { Password = "changed" }, "i=85", [], default);
            await host.BrowseOpcUaChildrenAsync(connection, "i=85", [], default);
            Assert.Equal(2, factory.Created);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task One_plc_is_serialized_while_other_plcs_can_browse_independently()
    {
        var factory = new CountingFactory();
        await using var pool = new OpcUaPreviewSessionPool();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = pool.UseAsync("A", () => new Driver(factory), async (_, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct); return 1;
        }, default);
        await entered.Task;
        var second = pool.UseAsync("A", () => throw new Exception("must reuse"), (_, _) => Task.FromResult(2), default);
        var other = await pool.UseAsync("B", () => new Driver(factory), (_, _) => Task.FromResult(3), default);
        Assert.Equal(3, other);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
    }

    [Fact]
    public async Task Failed_operations_discard_sessions_and_idle_sessions_are_released()
    {
        var time = new BrowseTime();
        var factory = new CountingFactory();
        await using var pool = new OpcUaPreviewSessionPool(time, TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<IOException>(() => pool.UseAsync<int>("A", () => new Driver(factory),
            (_, _) => throw new IOException("browse failed"), default));
        Assert.Equal(1, factory.Disposed);
        await pool.UseAsync("A", () => new Driver(factory), (_, _) => Task.FromResult(1), default);
        time.Now = time.Now.AddMinutes(3);
        await pool.SweepAsync();
        Assert.Equal(2, factory.Disposed);
    }

    [Fact]
    public async Task Shutdown_cancels_active_browse_and_does_not_create_a_queued_session()
    {
        var factory = new CountingFactory();
        var pool = new OpcUaPreviewSessionPool(capacity: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = pool.UseAsync("A", () => new Driver(factory), async (_, ct) =>
        {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 1;
        }, default);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.UseAsync("B", () => new Driver(factory), (_, _) => Task.FromResult(2), default));
        var queued = pool.UseAsync("A", () => throw new Exception("cancelled waiter must not create a session"), (_, _) => Task.FromResult(2), default);
        await pool.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, factory.Disposed);
    }

    private sealed class BrowseTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Expanding_multiple_nodes_reuses_one_session_and_host_shutdown_releases_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-browse-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new CountingFactory();
        var registry = new DriverRegistry(); registry.Register(factory);
        try
        {
            var storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
                new ProjectStoragePaths(Path.Combine(dir, "project.db"), Path.Combine(dir, "project.igwproj"), Path.Combine(dir, "igw.masterkey")), default);
            await using (var host = new GatewayHost(await storage.Repository.LoadRuntimeProjectAsync(default), storage.Repository,
                await storage.Repository.GetRevisionAsync(default), registry, NullLoggerFactory.Instance, dir))
            {
                var connection = new BrowsePreviewRequest("opc.tcp://127.0.0.1:4840");
                await host.BrowseOpcUaChildrenAsync(connection, "i=85", [], default);
                await host.BrowseOpcUaChildrenAsync(connection, "ns=3;s=DB", [], default);
                Assert.Equal(1, factory.Created);
                Assert.Equal(0, factory.Disposed);
            }
            Assert.Equal(1, factory.Disposed);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class CountingFactory : IDriverFactory
    {
        public int Created, Disposed;
        public string DriverId => "OpcUa.Client";
        public DriverDescriptor Describe() => new() { DriverId = DriverId, DisplayName = DriverId };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) { Created++; return new Driver(this); }
    }

    private sealed class Driver(CountingFactory factory) : IDeviceDriver, IHierarchicalBrowsableDriver
    {
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged { add { } remove { } }
        public IAddressParser AddressParser => throw new NotSupportedException();
        public DriverLimits Limits => new();
        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<OpcUaBrowseChildrenResult> BrowseChildrenAsync(string id, IReadOnlyList<string> path, CancellationToken ct) => Task.FromResult(new OpcUaBrowseChildrenResult([]));
        public Task<BrowseResult> BrowseBranchAsync(OpcUaBrowseNode root, int limit, CancellationToken ct) => Task.FromResult(new BrowseResult([], false));
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { factory.Disposed++; return ValueTask.CompletedTask; }
    }
}
