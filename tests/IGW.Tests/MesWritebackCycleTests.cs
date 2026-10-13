using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesWritebackCycleTests
{
    [Theory]
    [InlineData("Writeback", false, false, 75)]
    [InlineData("ResponseWriteback", false, false, 200)]
    [InlineData("ResponseWriteback", true, false, 450)]
    [InlineData("ResponseWriteback", true, true, 120)]
    public async Task Cycle_waits_for_engine_terminal_write_result_then_stops_retrying_and_waits_a_full_interval(
        string nodeType, bool automatic, bool cancelDuringWrite, int interval)
    {
        const string goodId = "Line.Plc.Good", badId = "Line.Plc.Bad";
        string expectedBad = new('X', 600);
        await using var hub = new DataHub();
        foreach (string id in new[] { goodId, badId })
        {
            hub.Register(id, new TagDef { Name = id, DataType = id == badId ? IgwDataType.String : IgwDataType.Int16,
                StringLength = id == badId ? 1000 : null, Access = TagAccess.ReadWrite });
            hub.Update(id, DataValue.GoodAt(id == badId ? "" : (object)(short)0, DateTime.UtcNow));
        }
        var task = new MesTask
        {
            Id = "cycle", Name = "cycle", Enabled = true, CollectIntervalMs = interval,
            Flow = new MesFlow
            {
                Nodes = [new() { Id = "trigger", Type = "Trigger" },
                    new() { Id = "upload", Type = "Upload", Http = new() { Url = "http://offline.test/upload" } },
                    new() { Id = "write", Type = nodeType, WriteBack = new()
                    { Manual = [new() { TagId = goodId, Value = "7" }, new() { TagId = badId, Value = expectedBad }] } }],
                Edges = [new() { From = "trigger", To = "upload" }, new() { From = "upload", To = "write" }],
            },
        };
        if (automatic)
        {
            task.Flow.Nodes.Single(n => n.Id == "write").WriteBack.Manual.Clear();
            task.Flow.Nodes.Single(n => n.Id == "upload").Payload = new()
            {
                Mode = MesPayloadModes.FieldMapping,
                Fields = [new() { TagId = goodId, FieldName = "Good" }, new() { TagId = badId, FieldName = "Bad" }],
            };
        }
        var counts = new ConcurrentDictionary<string, int>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long terminalWriteAt = 0;
        using var handler = new Handler(expectedBad);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var submittedBad = new ConcurrentQueue<string>();
        var engine = new MesUploadEngine(hub, new MesUploadSettings { Enabled = true, Tasks = [task], LogDbPath = "" },
            async (id, value, ct) =>
            {
                int count = counts.AddOrUpdate(id, 1, (_, prior) => prior + 1);
                if (id == badId) submittedBad.Enqueue(Convert.ToString(value)!);
                if (id == badId && count == 1)
                {
                    // The delegate represents one engine request, including its bounded
                    // initial write plus ten retries. MES must not restart that budget.
                    writeStarted.TrySetResult();
                    await releaseWrite.Task.WaitAsync(ct);
                    Interlocked.Exchange(ref terminalWriteAt, Stopwatch.GetTimestamp());
                    if (automatic) throw new IOException("Injected write failure");
                    return false;
                }
                if (id == badId) return false;
                hub.Update(id, DataValue.GoodAt(id == badId ? value : Convert.ToInt16(value), DateTime.UtcNow));
                return true;
            }, NullLogger.Instance, httpMessageHandler: handler);
        bool disposed = false;
        try
        {
            await writeStarted.Task.WaitAsync(stop.Token);
            Assert.Single(handler.Starts);
            Assert.Equal(1, counts[goodId]);
            await Task.Delay(interval * 2, stop.Token);
            Assert.Single(handler.Starts);
            Assert.Single(submittedBad);
            Assert.All(submittedBad, value => Assert.Equal(expectedBad, value));
            if (cancelDuringWrite)
            {
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                disposed = true;
                Assert.Single(handler.Starts);
                return;
            }
            releaseWrite.TrySetResult();
            while (handler.Starts.Count < 2) await Task.Delay(10, stop.Token);
            // TickCount64 can quantize a real 200ms wait to 187ms on Windows.
            double elapsedMs = Stopwatch.GetElapsedTime(Interlocked.Read(ref terminalWriteAt), handler.Starts.ToArray()[1]).TotalMilliseconds;
            Assert.True(elapsedMs >= interval - 10, $"Elapsed={elapsedMs:F3}ms; interval={interval}ms");
            Assert.Equal("fail", Assert.Single(Assert.Single(engine.Snapshot()).Nodes, n => n.Id == "write").State);
        }
        finally
        {
            releaseWrite.TrySetResult();
            if (!disposed) await engine.DisposeAsync();
        }
    }

    private sealed class Handler(string expectedBad) : HttpMessageHandler
    {
        public ConcurrentQueue<long> Starts { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Starts.Enqueue(Stopwatch.GetTimestamp());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new
                { httpStatusCode = 200, isSuccess = true, backResult = new { Good = 7, Bad = expectedBad } })) });
        }
    }
}
