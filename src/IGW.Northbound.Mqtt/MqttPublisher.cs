using System.Globalization;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace IGW.Northbound.Mqtt;

/// <summary>
/// 北向 IoT 发布(对标 KEP「IoT Gateway / MQTT」)。订阅 DataHub 变化 → 发布到 MQTT broker;
/// 主题 = 前缀/通道/设备/.../点,负载 JSON {v,q,ts}。支持变化即发 或 周期全量;断线后台自动重连。
/// </summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    private readonly IDataHub _hub;
    private readonly MqttPublishSettings _cfg;
    private readonly ILogger _log;
    private readonly IDisposable? _sub;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _opts;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _conn;
    private readonly Task? _interval;
    private long _published;

    public long Published => Interlocked.Read(ref _published);
    public bool Connected => _client.IsConnected;

    public MqttPublisher(IDataHub hub, MqttPublishSettings cfg, ILogger log)
    {
        _hub = hub; _cfg = cfg; _log = log;
        _client = new MqttFactory().CreateMqttClient();
        string cid = string.IsNullOrWhiteSpace(cfg.ClientId) ? "IGW-Pub-" + Guid.NewGuid().ToString("N")[..8] : cfg.ClientId;
        var ob = new MqttClientOptionsBuilder().WithTcpServer(cfg.BrokerHost, cfg.BrokerPort).WithClientId(cid).WithCleanSession();
        if (!string.IsNullOrEmpty(cfg.Username)) ob = ob.WithCredentials(cfg.Username, cfg.Password);
        _opts = ob.Build();
        _sub = hub.SubscribeAll(OnChange, "mqtt");   // 真全量:变化即发模式发布所有点
        _conn = Task.Run(ConnectLoopAsync);
        if (cfg.PublishIntervalMs > 0) _interval = Task.Run(IntervalLoopAsync);
        _log.LogInformation("IoT 发布已启用 → {h}:{p}(前缀 {pre},{mode})", cfg.BrokerHost, cfg.BrokerPort, cfg.TopicPrefix,
            cfg.PublishIntervalMs > 0 ? $"周期 {cfg.PublishIntervalMs}ms" : "变化即发");
    }

    private async Task ConnectLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try { await _client.ConnectAsync(_opts, _cts.Token); _log.LogInformation("IoT 发布已连接 broker"); }
                catch { /* broker 未就绪,下轮重试 */ }
            }
            try { await Task.Delay(3000, _cts.Token); } catch { break; }
        }
    }

    private async Task IntervalLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(_cfg.PublishIntervalMs, _cts.Token); } catch { break; }
            if (!_client.IsConnected) continue;
            var batch = new List<Task>(200);   // 分批并行(200 在飞):原来逐条串行 await,大点表 QoS1 一轮要发几十秒
            foreach (var id in _hub.ListTagIds())
            {
                if (_cts.IsCancellationRequested || !_client.IsConnected) break;
                if (_hub.TryGet(id, out var dv)) batch.Add(PublishAsync(id, dv));
                if (batch.Count >= 200) { await Task.WhenAll(batch); batch.Clear(); }
            }
            if (batch.Count > 0) await Task.WhenAll(batch);
        }
    }

    private ValueTask OnChange(TagChange c)
    {
        // 变化即发:await 回订阅 pump(每订阅自带 1 万深度 DropOldest 队列)→ 天然背压+丢旧;
        // 原来 fire-and-forget,慢 broker 时在飞任务无上限,内存随积压失控
        if (_cfg.PublishIntervalMs <= 0 && _client.IsConnected) return new ValueTask(PublishAsync(c.TagId, c.Value));
        return ValueTask.CompletedTask;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _topics = new();   // tagId→topic 缓存(不再每条变化拼串)

    private async Task PublishAsync(string tagId, DataValue dv)
    {
        try
        {
            string topic = _topics.GetOrAdd(tagId, static (id, prefix) => prefix + "/" + id.Replace('.', '/'), _cfg.TopicPrefix.TrimEnd('/'));
            string payload = $"{{\"v\":{ToJson(dv.Value)},\"q\":\"{dv.Quality.Class}\",\"ts\":\"{dv.SourceTimestamp:o}\"}}";
            await _client.PublishStringAsync(topic, payload,
                (MqttQualityOfServiceLevel)Math.Clamp(_cfg.Qos, 0, 2), _cfg.Retain, _cts.Token);
            Interlocked.Increment(ref _published);
        }
        catch { /* 单条失败忽略,断线由重连处理 */ }
    }

    private static string ToJson(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null",   // NaN/Infinity 不是合法 JSON → null
        float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "null",
        string s => Quote(s),
        DateTime dt => Quote(dt.ToString("o", CultureInfo.InvariantCulture)),                        // 时间要带引号,否则破坏 JSON
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        _ => Quote(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),                         // 其它未知类型:当字符串带引号,保证合法 JSON
    };
    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public async ValueTask DisposeAsync()
    {
        try { _sub?.Dispose(); } catch { }
        _cts.Cancel();
        try { await _conn; } catch { }
        if (_interval != null) { try { await _interval; } catch { } }
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        _client.Dispose(); _cts.Dispose();
    }
}
