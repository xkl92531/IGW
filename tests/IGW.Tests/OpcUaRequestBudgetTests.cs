using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.OpcUaClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 单请求耗时预算：串行处理请求的 PLC(S7-1515 现场约 15 ms + 1.6 ms/点)上，大请求会让同一 PLC 的写入长时间排队。
/// </summary>
public sealed class OpcUaRequestBudgetTests
{
    [Theory]
    [InlineData(null, RequestBudgetMode.Auto, 0)]
    [InlineData("", RequestBudgetMode.Auto, 0)]
    [InlineData("auto", RequestBudgetMode.Auto, 0)]
    [InlineData("AUTO", RequestBudgetMode.Auto, 0)]
    [InlineData("自动", RequestBudgetMode.Auto, 0)]
    [InlineData("0", RequestBudgetMode.Off, 0)]
    [InlineData("250", RequestBudgetMode.Fixed, 250)]
    [InlineData(" 80.5 ", RequestBudgetMode.Fixed, 80.5)]
    [InlineData("-5", RequestBudgetMode.Auto, 0)]
    [InlineData("abc", RequestBudgetMode.Auto, 0)]
    public void Channel_setting_parses_to_a_budget(string? text, RequestBudgetMode mode, double ms)
    {
        var budget = RequestBudget.Parse(text);
        Assert.Equal(mode, budget.Mode);
        Assert.Equal(ms, budget.FixedMs);
    }

    [Fact]
    public void Auto_budget_is_derived_from_measured_plc_costs_and_shrinks_large_requests()
    {
        var window = AdaptiveRequestWindow.Undeclared(512, 100_000, RequestBudget.Auto);
        // 预算尚未学到前不限制。
        Assert.Equal(0, window.EffectiveBudgetMs);

        Train(window, S71515Ms);
        var (fixedMs, unitMs) = window.Costs;
        Assert.InRange(fixedMs, 14, 16);
        Assert.InRange(unitMs, 1.55, 1.65);
        // 固定开销占 10%：15 ms × 10 = 150 ms；缩窗下限为固定开销占 25% 的点数(约 29 点)。
        Assert.InRange(window.EffectiveBudgetMs, 140, 160);
        Assert.InRange(window.MinimumWindow, 25, 32);

        ShrinkUntilStable(window, S71515Ms);
        Assert.InRange(window.Current, window.MinimumWindow, 200);
        Assert.True(S71515Ms(window.Current) <= window.EffectiveBudgetMs * 1.2);
    }

    [Fact]
    public void Budgeted_window_does_not_grow_past_the_budget()
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(512, 100_000, () => clock) { Budget = RequestBudget.Auto };
        Train(window, S71515Ms);
        ShrinkUntilStable(window, S71515Ms);
        int settled = window.Current;
        clock = 10 * 60_000;
        for (int i = 0; i < 200; i++) window.OnSuccess(settled, S71515Ms(settled));
        Assert.Equal(settled, window.Current);
    }

    [Fact]
    public void Fast_server_keeps_growing_for_throughput()
    {
        long clock = 0;
        var window = new AdaptiveRequestWindow(512, 100_000, () => clock) { Budget = RequestBudget.Auto };
        static double Fast(int units) => 1 + units * 0.002;
        Train(window, Fast);
        Assert.Equal(100, window.EffectiveBudgetMs);   // 自动预算不低于 100 ms
        for (int round = 0; round < 10; round++)
        {
            clock += 61_000;
            for (int i = 0; i < 30; i++) window.OnSuccess(window.Current, Fast(window.Current));
        }
        Assert.True(window.Current > 512, $"window={window.Current}");
    }

    [Fact]
    public void Off_budget_never_shrinks_for_latency()
    {
        var window = AdaptiveRequestWindow.Undeclared(512, 100_000, RequestBudget.Off);
        Train(window, S71515Ms);
        ShrinkUntilStable(window, S71515Ms);
        // 不限预算时只按吞吐学习：每点耗时不变，窗口照常增长。
        Assert.True(window.Current >= 512, $"window={window.Current}");
    }

    [Fact]
    public void Fixed_budget_uses_the_configured_milliseconds()
    {
        var window = AdaptiveRequestWindow.ForDeclaredLimit(1000, new RequestBudget(RequestBudgetMode.Fixed, 150));
        Assert.Equal(150, window.EffectiveBudgetMs);
        Train(window, S71515Ms);
        ShrinkUntilStable(window, S71515Ms);
        Assert.True(S71515Ms(window.Current) <= 150 * 1.2 || window.Current == window.MinimumWindow, $"window={window.Current}");
        Assert.Equal(1000, window.Ceiling);
    }

    [Fact]
    public void Queued_samples_do_not_inflate_the_learned_costs()
    {
        var window = AdaptiveRequestWindow.Undeclared(512, 100_000, RequestBudget.Auto);
        var random = new Random(3);
        // 一半样本额外排队 0–600 ms：取每个大小区间的最小耗时，估算仍接近真实开销。
        for (int i = 0; i < 128; i++)
        {
            int units = new[] { 4, 20, 64, 200, 512 }[i % 5];
            double queued = i % 2 == 0 ? random.NextDouble() * 600 : 0;
            window.OnSuccess(units, S71515Ms(units) + queued);
        }
        var (fixedMs, unitMs) = window.Costs;
        Assert.InRange(fixedMs, 10, 20);
        Assert.InRange(unitMs, 1.4, 1.8);
    }

    [Fact]
    public void Tiny_requests_alone_never_limit_the_window()
    {
        var window = AdaptiveRequestWindow.Undeclared(1000, 100_000, RequestBudget.Auto);
        var random = new Random(5);
        // 握手写入几乎都是 1–4 点，排队让耗时忽高忽低；这类样本不能拟合出每点耗时。
        for (int i = 0; i < 128; i++) window.OnSuccess(1 + i % 4, 20 + random.NextDouble() * 300);
        Assert.True(double.IsNaN(window.Costs.PerUnitMs));
        Assert.Equal(0, window.EffectiveBudgetMs);
        window.OnSuccess(1000, 400);
        Assert.True(window.Current >= 1000, $"window={window.Current}");
    }

    [Fact]
    public void Opc_ua_factory_passes_the_channel_budget_to_the_driver()
    {
        var factory = new OpcUaClientDriverFactory();
        Assert.Contains(factory.Describe().ChannelSettings, setting =>
            setting.Key == OpcUaClientDriverFactory.RequestBudgetKey && setting.Default == "auto");
    }

    [Fact]
    public async Task Shared_session_admits_a_large_read_chunk_by_chunk_so_writes_can_cut_in()
    {
        var driver = new ChunkedDriver { ChunkSize = 2 };
        await using var pool = new OpcUaConnectionPool();
        await using var lease = pool.Acquire("plc", () => new(driver, null));
        await lease.ConnectAsync(default);
        var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.Gate = firstChunk.Task;
        var big = lease.ReadAsync(Enumerable.Range(0, 5).Select(i => Binding("t" + i)).ToArray(), default);
        await driver.Started("t0").WaitAsync(TimeSpan.FromSeconds(2));
        var target = Binding("w");
        var write = lease.WriteAsync([new WriteRequest { TagId = "w", Value = (short)1, Def = target.Def, Address = target.Address }], default);
        // 写入用保留的执行槽，不必等第一块读取完成。
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        firstChunk.SetResult();

        var samples = await big.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["t0", "t1", "t2", "t3", "t4"], samples.Select(s => s.TagId));
        Assert.Equal(["t0,t1", "write", "t2,t3", "t4"], driver.Calls.ToArray());
    }

    [Fact]
    public async Task Priority_on_demand_pull_is_also_admitted_chunk_by_chunk()
    {
        var driver = new ChunkedDriver { ChunkSize = 2 };
        await using var pool = new OpcUaConnectionPool();
        await using var lease = pool.Acquire("plc", () => new(driver, null));
        await lease.ConnectAsync(default);
        var reader = Assert.IsAssignableFrom<IWriteVerificationReader>(lease);

        var samples = await reader.ReadForWriteVerificationAsync(
            Enumerable.Range(0, 5).Select(i => Binding("p" + i)).ToArray(), default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["p0", "p1", "p2", "p3", "p4"], samples.Select(s => s.TagId));
        // 分层采集的按需拉取走优先通道：同样每块单独准入，不能一次占住执行槽读完上千点。
        Assert.Equal(["p0,p1", "p2,p3", "p4"], driver.Calls.ToArray());
    }

    private static double S71515Ms(int units) => 15 + 1.6 * units;

    private static void Train(AdaptiveRequestWindow window, Func<int, double> cost)
    {
        foreach (int units in new[] { 2, 4, 16, 24, 64, 128, 300, 512 })
            for (int i = 0; i < 2; i++) window.OnSuccess(units, cost(units));
    }

    private static void ShrinkUntilStable(AdaptiveRequestWindow window, Func<int, double> cost)
    {
        for (int i = 0; i < 20; i++)
        {
            var plan = window.Capture();
            window.OnSuccess(plan.Current, cost(plan.Current), plan);
        }
    }

    private static TagBinding Binding(string name) => new()
    {
        TagId = name,
        Def = new TagDef { Name = name, Address = "ns=2;s=" + name, DataType = IgwDataType.Int16 },
        Address = new(0, 0, 2),
    };

    private sealed class ChunkedDriver : IDeviceDriver, IReadChunkHint
    {
        private readonly Dictionary<string, TaskCompletionSource> _started = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();
        public int ChunkSize { get; set; }
        public Task Gate = Task.CompletedTask;
        public int ReadChunkSize => ChunkSize;
        public string DriverId => "OpcUa.Client";
        public ConnectionState State { get; private set; }
        public event Action<ConnectionState>? StateChanged;
        public IAddressParser AddressParser { get; } = new Parser();
        public DriverLimits Limits { get; } = new();

        public Task Started(string tagId)
        {
            lock (_started)
            {
                if (!_started.TryGetValue(tagId, out var started))
                    _started[tagId] = started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return started.Task;
            }
        }

        public Task ConnectAsync(CancellationToken ct) { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task DisconnectAsync() { State = ConnectionState.Disconnected; return Task.CompletedTask; }

        public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Calls.Enqueue(string.Join(",", tags.Select(t => t.TagId)));
            foreach (var tag in tags) { _ = Started(tag.TagId); lock (_started) _started[tag.TagId].TrySetResult(); }
            if (tags[0].TagId == "t0") await Gate.WaitAsync(ct);
            return tags.Select(t => new TagSample(t.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray();
        }

        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct)
        {
            Calls.Enqueue("write");
            return Task.FromResult<IReadOnlyList<WriteResult>>(writes.Select(w => new WriteResult { TagId = w.TagId, Status = StatusCode.Good }).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Parser : IAddressParser
    {
        public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        {
            result = new(0, 0, 2);
            error = null;
            return true;
        }
    }
}
