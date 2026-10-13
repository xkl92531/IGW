using System.Collections.Concurrent;
using System.Net;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 条件满足就处理：不再要求条件先复位(上升沿)；上一轮处理完后等一个完整采集周期再判断；
/// 判断前先从 PLC 现读条件点；条件节点显示的等待时间是“距上次判断”。
/// </summary>
public sealed class MesConditionCycleTests
{
    private const string Flag = "Line.Plc.DB.Request";

    [Fact]
    public async Task Condition_that_stays_satisfied_is_processed_every_cycle_after_the_previous_round_ends()
    {
        await using var hub = Hub(1);
        var handler = new Handler(TimeSpan.FromMilliseconds(250));
        var engine = Engine(hub, handler, interval: 200);
        try
        {
            await Until(() => handler.Starts.Count >= 3);
            var starts = handler.Starts.ToArray();
            // 两次上报之间 = 上一轮耗时(约 250ms) + 一个完整采集周期(200ms)
            for (int i = 1; i < starts.Length; i++) Assert.InRange(starts[i] - starts[i - 1], 430, 1500);
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task Condition_is_judged_on_the_value_read_from_the_plc_at_that_cycle()
    {
        await using var hub = Hub(0);
        var handler = new Handler(TimeSpan.Zero);
        int reads = 0;
        // 常驻轮询的缓存一直是 0；判断前现读拿到 PLC 上已经变成的 1
        var engine = Engine(hub, handler, interval: 100, readNow: (ids, _) =>
        {
            if (ids.Contains(Flag))
            {
                Interlocked.Increment(ref reads);
                hub.Update(Flag, DataValue.GoodAt((short)1, DateTime.UtcNow));
            }
            return Task.FromResult(new OnDemandReadResult(ids.Count, ids.Count, null));
        });
        try
        {
            await Until(() => !handler.Starts.IsEmpty);
            Assert.True(Volatile.Read(ref reads) > 0);
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task Waiting_condition_shows_time_since_the_last_check_not_the_total_wait()
    {
        await using var hub = Hub(0);
        var engine = Engine(hub, new Handler(TimeSpan.Zero), interval: 100);
        try
        {
            await Task.Delay(1200);
            var node = Assert.Single(engine.Snapshot()).Nodes.Single(n => n.Id == "cond");
            Assert.Equal("wait", node.State);
            Assert.InRange(node.AgeMs, 0, 600);   // 每 100ms 判断一次，不会累计到 1 秒以上
        }
        finally { await engine.DisposeAsync(); }
    }

    private static DataHub Hub(short value)
    {
        var hub = new DataHub();
        hub.Register(Flag, new TagDef { Name = "Request", DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite });
        hub.Update(Flag, DataValue.GoodAt(value, DateTime.UtcNow));
        return hub;
    }

    private static MesUploadEngine Engine(DataHub hub, Handler handler, int interval,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<OnDemandReadResult>>? readNow = null)
    {
        var task = new MesTask
        {
            Id = "t", Name = "t", Enabled = true, CollectIntervalMs = interval,
            Flow = new MesFlow
            {
                Nodes = [new() { Id = "trigger", Type = "Trigger" },
                    new() { Id = "cond", Type = "Condition", Branches = [new() { Conditions = [new() { Source = "Tag", TagId = Flag, Op = "Eq", Value = "1" }] }] },
                    new() { Id = "upload", Type = "Upload", Http = new() { Url = "http://cycle.test/upload" } }],
                Edges = [new() { From = "trigger", To = "cond" }, new() { From = "cond", FromPort = "b0", To = "upload" }],
            },
        };
        return new MesUploadEngine(hub, new MesUploadSettings { Enabled = true, Tasks = [task], LogDbPath = "" },
            (_, _, _) => Task.FromResult(true), NullLogger.Instance, httpMessageHandler: handler, readNow: readNow);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Handler(TimeSpan delay) : HttpMessageHandler
    {
        public ConcurrentQueue<long> Starts { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Starts.Enqueue(Environment.TickCount64);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}") };
        }
    }
}
