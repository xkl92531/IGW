using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// S7 Plus 变化订阅：PLC 按周期推送变化的值，符号点位不再周期轮询。
/// 这里用原生替身覆盖托管侧的契约——解析不到的符号只影响它自己、旧原生库自动回退轮询、
/// 通知值按订阅时的项序号对回点位、释放租约要删除 PLC 侧的订阅。
/// </summary>
public sealed class SiemensPlusSubscriptionTests
{
    private static TagBinding Tag(string name, int scanMs = 1000) => new()
    {
        TagId = "Line.Plc." + name,
        Def = new TagDef { Name = name, Address = "DB1." + name, DataType = IgwDataType.Bool, ScanRateMs = scanMs },
        Address = new TagAddress(0, 0, 1),
    };

    [Fact]
    public async Task Values_pushed_by_the_plc_reach_the_right_tags()
    {
        var native = new SubscribingFake([SiemensPlusResultCode.Ok, SiemensPlusResultCode.Ok]);
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        var received = new List<TagSample>();
        var tags = new[] { Tag("A"), Tag("B") };
        var subscription = await transport.SubscribeAsync(tags, 500, samples => { lock (received) received.AddRange(samples); }, default);

        Assert.NotNull(subscription);
        Assert.Equal(["Line.Plc.A", "Line.Plc.B"], subscription!.AcceptedTagIds);
        Assert.Equal(500, native.CycleTimeMs);

        native.Push([(1, true)]);                       // 第 2 项变化
        await native.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await subscription.DisposeAsync();

        var sample = Assert.Single(received);
        Assert.Equal("Line.Plc.B", sample.TagId);
        Assert.True(sample.Value.IsGood);
        Assert.Equal(true, sample.Value.Value);
        Assert.True(native.Deleted, "释放租约必须删除 PLC 侧的订阅");
    }

    [Fact]
    public async Task A_symbol_the_plc_cannot_resolve_only_drops_itself()
    {
        var native = new SubscribingFake([SiemensPlusResultCode.Ok, SiemensPlusResultCode.SymbolNotFound]);
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        var subscription = await transport.SubscribeAsync([Tag("A"), Tag("NoSuch")], 500, _ => { }, default);

        Assert.NotNull(subscription);
        Assert.Equal(["Line.Plc.A"], subscription!.AcceptedTagIds);
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task An_old_native_library_falls_back_to_polling()
    {
        // Subscribe 返回 null = 原生库低于 ABI v4，调用方必须回退轮询而不是让设备采不上数
        var native = new SubscribingFake([], supportsSubscription: false);
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        var subscription = await transport.SubscribeAsync([Tag("A")], 500, _ => { }, default);

        Assert.Null(subscription);
    }

    [Fact]
    public async Task Array_tags_stay_on_polling()
    {
        var native = new SubscribingFake([SiemensPlusResultCode.Ok]);
        await using var transport = new SiemensPlusTransport(native);
        await transport.ConnectAsync("plc", 102, TimeSpan.FromSeconds(1), false, default);

        var arrayTag = new TagBinding
        {
            TagId = "Line.Plc.Arr",
            Def = new TagDef
            {
                Name = "Arr", Address = "DB1.Arr", DataType = IgwDataType.Bool,
                Array = new ArrayDef { ElementType = IgwDataType.Bool, Dimensions = [8], LowerBounds = [0] },
            },
            Address = new TagAddress(0, 0, 1),
        };

        var subscription = await transport.SubscribeAsync([arrayTag], 500, _ => { }, default);

        Assert.Null(subscription);
        Assert.False(native.SubscribeCalled, "数组点位不该发起订阅");
    }

    private sealed class SubscribingFake(IReadOnlyList<SiemensPlusResultCode> itemCodes, bool supportsSubscription = true)
        : ISiemensPlusNativeApi
    {
        private readonly Queue<IReadOnlyList<(int Slot, object? Value)>> _pending = new();
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ushort CycleTimeMs { get; private set; }
        public bool SubscribeCalled { get; private set; }
        public bool Deleted { get; private set; }

        public void Push(IReadOnlyList<(int Slot, object? Value)> values)
        {
            lock (_pending) _pending.Enqueue(values);
        }

        public SiemensPlusNativeSubscribeResult? Subscribe(IReadOnlyList<string> symbols, ushort cycleTimeMs)
        {
            SubscribeCalled = true;
            if (!supportsSubscription) return null;
            CycleTimeMs = cycleTimeMs;
            return new SiemensPlusNativeSubscribeResult(SiemensPlusResultCode.Ok, 7, itemCodes);
        }

        public SiemensPlusNativeNotification PollNotification(uint subscriptionId, int capacity)
        {
            Assert.Equal(7u, subscriptionId);
            lock (_pending)
            {
                if (_pending.Count > 0)
                {
                    var values = _pending.Dequeue();
                    Delivered.TrySetResult();
                    return new SiemensPlusNativeNotification(SiemensPlusResultCode.Ok, values);
                }
            }
            Thread.Sleep(5);   // 模拟「这一轮没有变化」的心跳，别把测试跑成忙等
            return new SiemensPlusNativeNotification(SiemensPlusResultCode.Ok, []);
        }

        public SiemensPlusResultCode DeleteSubscription(uint subscriptionId)
        {
            Deleted = true;
            return SiemensPlusResultCode.Ok;
        }

        public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Authenticate(string username, string password) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Probe() => SiemensPlusResultCode.Ok;
        public SiemensPlusNativeBrowseResult Browse() => new(SiemensPlusResultCode.Ok, []);
        public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols) =>
            new(SiemensPlusResultCode.Ok, [], false);
        public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes) => SiemensPlusResultCode.Ok;
        public SiemensPlusResultCode Disconnect() => SiemensPlusResultCode.Ok;
        public void Dispose() { }
    }
}
