using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 心跳这类不调接口、只把固定值回写 PLC 的流程每半秒跑一次，逐条记「MES 回写节点完成」时一天上百万条，淹没采集和状态记录。
/// 这种回写成功时只记第一次、之后按分钟汇总；调过 MES 接口的流程(真正的业务上报)照常逐条记，便于追溯每一次。
/// </summary>
public sealed class MesWritebackLogSummaryTests
{
    private const string Heart = "Line.Plc.DB.Heart";

    [Fact]
    public async Task Heartbeat_writebacks_are_logged_once_then_summarized()
    {
        await using var hub = Hub();
        var log = new MessageLogger();
        int writes = 0;
        var engine = Engine(hub, log, new Handler(), (_, _, _) => { Interlocked.Increment(ref writes); return Task.FromResult(true); },
            [new MesNode { Id = "fixed", Type = "FixedData", FixedValues = [new() { Name = "Heart", DataType = MesFixedValueTypes.Bool, Value = "true", TagId = Heart }] },
             new MesNode { Id = "write", Type = "ResponseWriteback" }]);
        try { await Until(() => Volatile.Read(ref writes) >= 5); }
        finally { await engine.DisposeAsync(); }   // 停止时补记攒着的汇总

        Assert.Single(log.Messages, message => message.StartsWith("MES 回写节点完成", StringComparison.Ordinal));
        string summary = Assert.Single(log.Messages, message => message.StartsWith("MES 回写节点汇总", StringComparison.Ordinal));
        Assert.Contains("任务=t", summary);
        Assert.Contains("次成功", summary);
    }

    [Fact]
    public async Task Writebacks_after_an_mes_interface_call_stay_individual()
    {
        await using var hub = Hub();
        var log = new MessageLogger();
        var handler = new Handler();
        var engine = Engine(hub, log, handler, (_, _, _) => Task.FromResult(true),
            [new MesNode { Id = "upload", Type = "Upload", Http = new MesHttp { Url = "http://mes.local/api" } },
             new MesNode { Id = "write", Type = "ResponseWriteback" }]);
        try { await Until(() => handler.Calls >= 3 && log.Messages.Count(m => m.StartsWith("MES 回写节点完成", StringComparison.Ordinal)) >= 3); }
        finally { await engine.DisposeAsync(); }

        Assert.DoesNotContain(log.Messages, message => message.StartsWith("MES 回写节点汇总", StringComparison.Ordinal));
    }

    private static MesUploadEngine Engine(DataHub hub, ILogger log, Handler handler,
        Func<string, object, CancellationToken, Task<bool>> write, MesNode[] nodes)
    {
        var all = new List<MesNode> { new() { Id = "trigger", Type = "Trigger" } };
        all.AddRange(nodes);
        var edges = new List<MesEdge>();
        for (int i = 0; i < all.Count - 1; i++) edges.Add(new MesEdge { From = all[i].Id, To = all[i + 1].Id });
        return new MesUploadEngine(hub, new MesUploadSettings
        {
            Enabled = true, LogDbPath = "",
            Tasks = [new MesTask { Id = "t", Name = "t", Enabled = true, CollectIntervalMs = 50, Flow = new MesFlow { Nodes = all, Edges = edges } }],
        }, write, log, httpMessageHandler: handler);
    }

    private static DataHub Hub()
    {
        var hub = new DataHub();
        hub.Register(Heart, new TagDef { Name = "Heart", DataType = IgwDataType.Bool, Access = TagAccess.ReadWrite });
        hub.Update(Heart, DataValue.GoodAt(false, DateTime.UtcNow));
        return hub;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class MessageLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }

    private sealed class Handler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"httpStatusCode\":200}", Encoding.UTF8, "application/json") });
        }
    }
}
