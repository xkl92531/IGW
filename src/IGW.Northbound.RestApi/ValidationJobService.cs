using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

/// <summary>
/// 套用验证作业(内存态):发起后异步逐设备验证,不阻塞配置保存/其他设备采集;内存保留最近 N 份报告。
/// 可浏览驱动 → 浏览真机地址空间与展开点集匹配(matched/missing/extra);不可浏览驱动 → 对展开点集经运行循环缓存做一轮试读(readOk/readFail)。
/// </summary>
public sealed class ValidationJobService
{
    private const int MaxJobs = 20;
    private readonly object _lock = new();
    private readonly Dictionary<string, JobState> _jobs = new();
    private readonly Queue<string> _order = new();

    public string Start(IGatewayHost host, IReadOnlyList<string> devices)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        var job = new JobState { Id = id, Total = devices.Count };
        lock (_lock)
        {
            _jobs[id] = job;
            _order.Enqueue(id);
            while (_order.Count > MaxJobs && _order.TryDequeue(out var old)) _jobs.Remove(old);   // 只留最近 N 份
        }
        _ = Task.Run(() => RunAsync(host, job, devices));
        return id;
    }

    public object? Snapshot(string id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var j)) return null;
            return new
            {
                jobId = j.Id,
                status = j.Status,
                total = j.Total,
                done = j.Done,
                devices = j.Devices.Select(d => new
                {
                    device = d.Device,
                    mode = d.Mode,
                    error = d.Error,
                    summary = Summarize(d.Points),
                    points = d.Points.Select(p => new { p.Path, p.Address, result = p.Result.ToString(), p.Detail }),
                }).ToList(),
            };
        }
    }

    private async Task RunAsync(IGatewayHost host, JobState job, IReadOnlyList<string> devices)
    {
        foreach (var dev in devices)
        {
            DeviceReport rep;
            try { rep = await ValidateAsync(host, dev, CancellationToken.None); }
            catch (Exception ex) { rep = new DeviceReport(dev, "error", new(), ex.Message); }
            lock (_lock) { job.Devices.Add(rep); job.Done++; }
        }
        lock (_lock) job.Status = "done";
    }

    private static async Task<DeviceReport> ValidateAsync(IGatewayHost host, string devFull, CancellationToken ct)
    {
        var loc = Locate(host.Project, devFull);
        if (loc is null) return new DeviceReport(devFull, "error", new(), "设备不存在或未在工程中。");
        var eff = TemplateExpander.ExpandInProject(host.Project, loc.Value.dev);
        var effTags = TemplateExpander.EffectiveTags(eff);

        // 先试浏览(可浏览驱动):真机地址空间 vs 展开点集
        try
        {
            var browsed = await host.Engine.BrowseAsync(devFull, ct);
            return new DeviceReport(devFull, "browse", ValidationMatch.MatchBrowse(effTags, browsed.Tags), null);
        }
        catch (NotSupportedException) { /* 不可浏览 → 走试读 */ }
        catch (InvalidOperationException ex) { return new DeviceReport(devFull, "error", new(), ex.Message); }

        // 不可浏览驱动:激活点位 → 等设备连上并扫过一轮 → 读缓存判定 readOk/readFail(复用运行循环的真实读)
        var hub = host.Hub;
        if (hub is null) return new DeviceReport(devFull, "error", new(), "网关未授权,采集未运行,无法试读。");
        foreach (var t in effTags) hub.Touch(devFull + "." + t.Path);   // active 扫描模式下,不引用的点不轮询——先激活
        await WaitScannedAsync(host, devFull, ct);
        var points = ValidationMatch.TrialRead(effTags, path =>
        {
            string tagId = devFull + "." + path;
            if (hub.TryGet(tagId, out var v))
                return v.IsGood ? (true, "") : (false, v.Quality.ToString());
            return (false, "无缓存值(未读到)");
        });
        return new DeviceReport(devFull, "read", points, null);
    }

    // 等设备连上并给一轮扫描的余量(最长约 8s);连不上也返回,让试读如实报 readFail
    private static async Task WaitScannedAsync(IGatewayHost host, string devFull, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            if (ct.IsCancellationRequested) return;
            var st = host.Engine.DeviceStatuses().FirstOrDefault(d => d.Name == devFull);
            if (st is { State: ConnectionState.Connected }) { await Task.Delay(1500, ct); return; }
            await Task.Delay(100, ct);
        }
    }

    private static (ChannelDef ch, DeviceDef dev)? Locate(IgwProject proj, string devFull)
    {
        foreach (var ch in proj.Channels)
            foreach (var d in ch.Devices)
                if (string.Equals($"{ch.Name}.{d.Name}", devFull, StringComparison.Ordinal))
                    return (ch, d);
        return null;
    }

    private static object Summarize(List<PointResult> pts) => new
    {
        total = pts.Count,
        matched = pts.Count(p => p.Result == ValidationResultKind.Matched),
        missing = pts.Count(p => p.Result == ValidationResultKind.Missing),
        extra = pts.Count(p => p.Result == ValidationResultKind.Extra),
        readOk = pts.Count(p => p.Result == ValidationResultKind.ReadOk),
        readFail = pts.Count(p => p.Result == ValidationResultKind.ReadFail),
    };

    private sealed class JobState
    {
        public string Id = "";
        public string Status = "running";
        public int Total;
        public int Done;
        public List<DeviceReport> Devices = new();
    }

    private sealed record DeviceReport(string Device, string Mode, List<PointResult> Points, string? Error);
}
