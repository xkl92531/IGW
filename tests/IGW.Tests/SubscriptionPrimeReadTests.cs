using System.Reflection;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>订阅建立后对缺少首值的点做补读：不重复读已收到通知的点，不覆盖补读期间写入的新值。</summary>
public sealed class SubscriptionPrimeReadTests
{
    [Fact]
    public async Task Points_notified_during_an_earlier_batch_are_not_read_again()
    {
        await using var hub = new DataHub();
        var a = Tag("A");
        var b = Tag("B");
        foreach (var tag in new[] { a, b }) { hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId); }
        var driver = new SilentSubscriptionDriver();
        driver.DuringRead = tagIds =>
        {
            // 读 A 的这段时间里，B 的首个通知到达。
            if (tagIds.Contains(a.TagId)) driver.Notify(b.TagId, 7);
        };
        var loop = new DeviceLoop(driver, [a, b], hub, NullLogger.Instance, "L.P", scanMode: "active");
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, 3000);
            Assert.Equal([a.TagId], driver.ReadTagIds);
            Assert.True(hub.TryGet(b.TagId, out var notified));
            Assert.Equal((short)7, notified.Value);
        }
        finally { await Close(loop); }
    }

    [Fact]
    public async Task Prime_read_does_not_overwrite_a_value_written_while_it_was_in_flight()
    {
        await using var hub = new DataHub();
        var a = Tag("A");
        hub.Register(a.TagId, a.Def);
        hub.Touch(a.TagId);
        var driver = new SilentSubscriptionDriver();
        DeviceLoop? loop = null;
        driver.DuringRead = readIds =>
        {
            // 补读还没返回时 QC 写入了新值(入队即推进写入代次)。
            _ = loop!.EnqueueWriteAsync(new WriteRequest { TagId = a.TagId, Def = a.Def, Address = a.Address, Value = (short)123 });
        };
        loop = new DeviceLoop(driver, [a], hub, NullLogger.Instance, "L.P", scanMode: "active");
        await driver.ConnectAsync(default);
        try
        {
            await Reconcile(loop, 3000);
            Assert.Equal([a.TagId], driver.ReadTagIds);
            // 补读拿到的是写入之前的旧值 5，不能发布出去。
            Assert.False(hub.TryGet(a.TagId, out var value) && value.Value is short old && old == 5);
            Assert.True(driver.PriorityReads > 0, "补读应走优先通道");
        }
        finally { await Close(loop); }
    }

    private static Task Reconcile(DeviceLoop loop, long now) => (Task)typeof(DeviceLoop)
        .GetMethod("ReconcileRealtimeSubscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(loop, [now, CancellationToken.None])!;

    private static Task Close(DeviceLoop loop) => (Task)typeof(DeviceLoop)
        .GetMethod("CloseRealtimeSubscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, [])!;

    private static TagBinding Tag(string id) => new()
    {
        TagId = "L.P." + id,
        Def = new() { Name = id, Address = "ns=2;s=" + id, DataType = IgwDataType.Int16, ScanRateMs = 20 },
        Address = new(1, 0, 2),
    };

    /// <summary>订阅建立后不主动发首值的驱动，逼出补读路径；每批最多 1 点。</summary>
    private sealed class SilentSubscriptionDriver : IDeviceDriver, IDriverCapabilityProvider,
        IDriverSubscriptionProvider, IWriteVerificationReader
    {
        private Action<IReadOnlyList<TagSample>>? _publish;
        public Action<IReadOnlyList<string>>? DuringRead { get; set; }
        public List<string> ReadTagIds { get; } = [];
        public int PriorityReads;
        public string DriverId => "Silent";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new() { MaxTagsPerRead = 1, MaxTagsPerWrite = 1 };
        public DriverCapabilitySnapshot Capabilities => DriverCapabilitySnapshot.FromLimits(Limits) with
        {
            SupportsSubscriptions = true,
            SupportsBatchRead = true,
            MaxInFlight = 2,
        };

        public void Notify(string tagId, short value) =>
            _publish?.Invoke([new TagSample(tagId, DataValue.GoodAt(value, DateTime.UtcNow))]);

        public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags,
            Action<IReadOnlyList<TagSample>> onSamples, CancellationToken ct)
        {
            _publish = onSamples;
            return Task.FromResult<IDriverSubscription>(new Lease(tags.Select(tag => tag.TagId).ToArray()));
        }

        public Task<IReadOnlyList<TagSample>> ReadForWriteVerificationAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Interlocked.Increment(ref PriorityReads);
            return ReadAsync(tags, ct);
        }

        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            var ids = tags.Select(tag => tag.TagId).ToArray();
            ReadTagIds.AddRange(ids);
            DuringRead?.Invoke(ids);
            return Task.FromResult<IReadOnlyList<TagSample>>(
                ids.Select(id => new TagSample(id, DataValue.GoodAt((short)5, DateTime.UtcNow))).ToArray());
        }

        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease(IReadOnlyCollection<string> ids) : IDriverSubscription
        {
            public IReadOnlyCollection<string> TagIds => ids;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Parser : IAddressParser
        {
            public bool TryParse(string a, IgwDataType t, out TagAddress result, out string? error)
            { result = new(1, 0, 2); error = null; return true; }
        }
    }
}
