using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IGW.Observability;

if (args.Length < 2) throw new ArgumentException("Usage: QuestDbBench runtime-directory new-empty-data-directory [rows=100000] [rows-per-second=5000]");
string data = Path.GetFullPath(args[1]);
if (Directory.Exists(data)) throw new ArgumentException("Use a new empty benchmark directory; benchmark never overwrites user data.");
int count = args.Length > 2 ? int.Parse(args[2]) : 100_000;
int rate = args.Length > 3 ? int.Parse(args[3]) : 5000;
if (count <= 0 || rate <= 0) throw new ArgumentOutOfRangeException(nameof(args));
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var settings = ObservabilitySettings.Initialize(data, Path.Combine(data, "logs"), port);
await using var process = new QuestDbProcess(settings, Path.GetFullPath(args[0]));
using var client = new QuestDbClient(new Uri($"http://127.0.0.1:{port}"), settings.Username, settings.Password);
process.Start();
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (!process.Ready)
{
    try { await process.EnsureReadyAsync(client, 30, startupTimeout.Token); }
    catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !startupTimeout.IsCancellationRequested)
    { await Task.Delay(100, startupTimeout.Token); }
}
await using var store = new QuestDbLogStore(client, Path.Combine(data, "spool"));
var query = new QuestDbLogQuery(client);
var queryTimes = new List<long>();
using var stopQueries = new CancellationTokenSource();
DateTimeOffset start = DateTimeOffset.UtcNow;
Task reader = Task.Run(async () =>
{
    while (!stopQueries.IsCancellationRequested)
    {
        var result = await query.QueryAsync(new LogCenterRequest { From = start.AddSeconds(-1), To = DateTimeOffset.UtcNow.AddSeconds(1), Device = "产线1.PLC1" }, stopQueries.Token);
        if (result.Partial) throw new IOException("Concurrent query timed out or failed.");
        queryTimes.Add(result.TookMs);
        await Task.Delay(200, stopQueries.Token);
    }
});
long appendMaxTicks = 0;
var timer = Stopwatch.StartNew();
for (int row = 0; row < count; row++)
{
    var fields = new Dictionary<string, object?>
    {
        ["igw_device"] = $"产线1.PLC{row % 10}", ["igw_channel"] = "产线1", ["igw_protocol"] = "Siemens.S7",
        ["igw_module"] = "acquisition", ["igw_feature"] = "scan", ["igw_task_id"] = $"OP{row % 100}",
        ["igw_trace_id"] = Guid.NewGuid().ToString("N"), ["igw_tag_values"] = "{\"DataReady\":true,\"Torque\":12.34,\"Result\":1}",
        ["duration_ms"] = 12.34
    };
    var record = new LogRecord(LogRecordCodec.ToMicros(DateTimeOffset.UtcNow), row % 100 == 0 ? 4 : 2, "collection", "点位扫描成功，数据已推送；PLC读取与写入正常。", fields);
    long before = Stopwatch.GetTimestamp(); store.Append(record);
    appendMaxTicks = Math.Max(appendMaxTicks, Stopwatch.GetTimestamp() - before);
    if (row % 250 == 249)
    {
        double target = (row + 1) * 1000.0 / rate;
        if (timer.Elapsed.TotalMilliseconds < target) await Task.Delay((int)(target - timer.Elapsed.TotalMilliseconds));
    }
}
await store.FlushAsync(default);
using var drain = new CancellationTokenSource(TimeSpan.FromMinutes(2));
while (store.Pending > 0) await Task.Delay(100, drain.Token);
LogCenterResponse complete;
do
{
    complete = await query.QueryAsync(new LogCenterRequest { From = start.AddSeconds(-1), To = DateTimeOffset.UtcNow.AddSeconds(1) }, drain.Token);
    if (complete.Total != count) await Task.Delay(100, drain.Token);
} while (complete.Total != count);
stopQueries.Cancel();
try { await reader; } catch (OperationCanceledException) { }
long bytes = Directory.EnumerateFiles(process.Root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
queryTimes.Sort();
var result = new
{
    Rows = count, TargetRowsPerSecond = rate, ElapsedSeconds = timer.Elapsed.TotalSeconds,
    DeliveredRowsPerSecond = count / timer.Elapsed.TotalSeconds, store.Dropped, store.WriteFailures,
    AppendMaxMilliseconds = appendMaxTicks * 1000.0 / Stopwatch.Frequency,
    ConcurrentQueries = queryTimes.Count,
    QueryP50Milliseconds = queryTimes.Count == 0 ? 0 : queryTimes[queryTimes.Count / 2],
    QueryP95Milliseconds = queryTimes.Count == 0 ? 0 : queryTimes[(int)((queryTimes.Count - 1) * 0.95)],
    UnfilteredQueryMilliseconds = complete.TookMs, LogicalDatabaseBytes = bytes,
    Runtime = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Note = "Synthetic single-machine benchmark. Not a 900-million-row capacity or power-loss durability test."
};
string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(data, "benchmark.json"), json);
Console.WriteLine(json);
if (store.Dropped > 0 || store.WriteFailures > 0) Environment.ExitCode = 1;
