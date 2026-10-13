using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

/// <summary>
/// 异常中心聚合(纯读、每次查询现算,不建常驻告警状态机):
/// 设备离线 = 根因,能归属到它的任务/节点异常折叠为子项(归属不明不折叠,宁不折不错折);
/// 排序 = 严重度类别(offline &gt; fail &gt; stuck &gt; slow)→ 波及数 → 持续时长,不按时间流水。
/// </summary>
public static class AnomalyService
{
    private static int Rank(string sev) => sev switch { "offline" => 0, "fail" => 1, "stuck" => 2, _ => 3 };

    private sealed record NodeEntry(string Severity, MesTaskStatus Task, MesNodeStatus Node);

    public static object Build(IGatewayHost host, string? q)
    {
        var devices = host.Reloading || host.Hub is null
            ? new List<DeviceStatus>()
            : host.Engine.DeviceStatuses().ToList();
        var tasks = host.MesTaskStatuses();

        // 节点级异常:fail > stuck > slow(慢于基线=黄色预警:超过 P95/手工基线但未到卡住阈值)
        var nodeEntries = new List<NodeEntry>();
        foreach (var t in tasks)
            foreach (var n in t.Nodes)
            {
                string sev = n.State == "fail" || (n.FailStreak > 0 && n.State is "run" or "wait") ? "fail"   // 重试中=仍在失败(防止条目在重试窗口内闪烁)
                    : n.Stuck ? "stuck"
                    : n.State is "wait" or "run" && n.BaselineMs > 0 && n.AgeMs > n.BaselineMs ? "slow"
                    : "";
                if (sev.Length == 0) continue;
                nodeEntries.Add(new NodeEntry(sev, t, n));
            }

        // 根因折叠两遍:先收离线设备,再把关联任务的节点异常挂为其子项
        var offline = devices.Where(d => d.DemandActive && d.State != ConnectionState.Connected).ToList();
        var folded = new HashSet<NodeEntry>();
        var roots = new List<Dictionary<string, object?>>();
        foreach (var d in offline)
        {
            var kids = nodeEntries.Where(e => e.Task.Devices.Contains(d.Name, StringComparer.Ordinal)).ToList();
            foreach (var k in kids) folded.Add(k);
            roots.Add(RootDto(d, kids));
        }
        foreach (var d in devices.Where(device =>
                     device.State == ConnectionState.Connected &&
                     device.Diagnostic is { Active: true, Scope: ConnectionDiagnosticScope.DataAccess }))
            roots.Add(RootDto(d, []));
        foreach (var e in nodeEntries)
            if (!folded.Contains(e)) roots.Add(NodeDto(e, standalone: true));
        var periodSlow = devices.Where(d => d.State == ConnectionState.Connected && !d.Stats.PeriodHealthy).ToList();
        foreach (var device in periodSlow) roots.Add(PeriodDto(device));

        // 搜索:设备/任务/节点标题/信息(含点位路径)子串;根因自身命中保留全部子项,否则只留命中的子项
        if (!string.IsNullOrWhiteSpace(q))
        {
            string s = q.Trim();
            bool Hit(Dictionary<string, object?> x) =>
                (x.GetValueOrDefault("device") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("taskName") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("nodeTitle") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("info") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("category") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("suggestion") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("rawError") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("protocol") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true ||
                (x.GetValueOrDefault("title") as string)?.Contains(s, StringComparison.OrdinalIgnoreCase) == true;
            var filtered = new List<Dictionary<string, object?>>();
            foreach (var r in roots)
            {
                var kids = r.GetValueOrDefault("impacted") as List<Dictionary<string, object?>>;
                if (Hit(r)) { filtered.Add(r); continue; }
                if (kids is { Count: > 0 })
                {
                    var hitKids = kids.Where(Hit).ToList();
                    if (hitKids.Count > 0)
                    {
                        var clone = new Dictionary<string, object?>(r) { ["impacted"] = hitKids, ["impactedCount"] = hitKids.Count };
                        filtered.Add(clone);
                    }
                }
            }
            roots = filtered;
        }

        roots.Sort((a, b) =>
        {
            int r = Rank((string)a["severity"]!).CompareTo(Rank((string)b["severity"]!));
            if (r != 0) return r;
            r = ((int)b["impactedCount"]!).CompareTo((int)a["impactedCount"]!);
            if (r != 0) return r;
            return ((long)b["ageMs"]!).CompareTo((long)a["ageMs"]!);
        });

        return new
        {
            total = roots.Count,
            entries = roots,
            channels = ChannelTiles(host, devices, offline, nodeEntries),
        };
    }

    private static Dictionary<string, object?> RootDto(DeviceStatus d, List<NodeEntry> kids)
    {
        var diagnostic = d.Diagnostic;
        bool dataAccess = diagnostic?.Scope == ConnectionDiagnosticScope.DataAccess && d.State == ConnectionState.Connected;
        long ageMs = diagnostic is null
            ? 0L
            : Math.Max(0L, (long)(DateTimeOffset.UtcNow - diagnostic.FirstOccurredAt).TotalMilliseconds);
        return new Dictionary<string, object?>
        {
            ["severity"] = dataAccess ? "fail" : "offline",
            ["title"] = diagnostic is null
                ? $"设备 {d.Name} 离线"
                : $"设备 {d.Name}：{diagnostic.CategoryLabel}",
            ["device"] = d.Name,
            ["channel"] = diagnostic?.Channel ?? ChannelOf(d.Name),
            ["protocol"] = diagnostic?.Protocol ?? "",
            ["categoryCode"] = diagnostic?.Code ?? "",
            ["category"] = diagnostic?.CategoryLabel ?? "",
            ["scope"] = diagnostic?.Scope.ToString() ?? "",
            ["info"] = diagnostic?.Cause ?? (!string.IsNullOrWhiteSpace(d.Detail?.Message)
                ? d.Detail.Message
                : string.IsNullOrEmpty(d.Stats.LastError) ? "连接中断" : d.Stats.LastError),
            ["suggestion"] = diagnostic?.Suggestion ?? "检查网络、端口、协议和 PLC 配置。",
            ["rawError"] = diagnostic?.RawError ?? d.Stats.LastError,
            ["firstOccurredAt"] = diagnostic?.FirstOccurredAt,
            ["lastOccurredAt"] = diagnostic?.LastOccurredAt,
            ["occurrenceCount"] = diagnostic?.OccurrenceCount ?? 1,
            ["retrying"] = diagnostic?.Retrying ?? d.DemandActive,
            ["nextRetryMs"] = diagnostic?.NextRetryMs ?? 0,
            ["ageMs"] = ageMs,
            ["impactedCount"] = kids.Count,
            ["impacted"] = kids.Select(k => NodeDto(k, standalone: false)).ToList(),
        };
    }

    private static Dictionary<string, object?> NodeDto(NodeEntry e, bool standalone) => new()
    {
        ["severity"] = e.Severity,
        ["title"] = $"{e.Task.Name} · {e.Node.Title}",
        ["device"] = e.Task.Devices.FirstOrDefault() ?? "",
        ["channel"] = ChannelOf(e.Task.Devices.FirstOrDefault() ?? ""),
        ["taskId"] = e.Task.Id,
        ["taskName"] = e.Task.Name,
        ["nodeId"] = e.Node.Id,
        ["nodeTitle"] = e.Node.Title,
        ["state"] = e.Node.State,
        ["info"] = e.Node.Info,
        ["ageMs"] = e.Node.AgeMs,
        ["baselineMs"] = e.Node.BaselineMs,
        ["impactedCount"] = standalone ? 0 : 0,
        ["impacted"] = (List<Dictionary<string, object?>>?)null,
    };

    private static Dictionary<string, object?> PeriodDto(DeviceStatus d) => new()
    {
        ["severity"] = "slow",
        ["title"] = $"设备 {d.Name} 采集周期超时",
        ["device"] = d.Name,
        ["channel"] = ChannelOf(d.Name),
        ["info"] = $"目标最短周期 {d.Stats.ConfiguredMinPeriodMs}ms，实际 P95 {d.Stats.ActualP95PeriodMs}ms，累计跳过 {d.Stats.OverduePeriods} 个过期周期；系统已合并过期轮次，不会堆积补跑。",
        ["ageMs"] = 0L,
        ["impactedCount"] = 0,
        ["impacted"] = (List<Dictionary<string, object?>>?)null,
    };

    private static string ChannelOf(string deviceFullName)
    {
        int i = deviceFullName.IndexOf('.');
        return i > 0 ? deviceFullName[..i] : deviceFullName;
    }

    // 通道健康色块:块内最严重级别 + 异常计数(全通道都给,绿色也在——总览要能"扫一眼全场")
    private static List<object> ChannelTiles(IGatewayHost host, List<DeviceStatus> devices, List<DeviceStatus> offline, List<NodeEntry> nodeEntries)
    {
        var tiles = new List<object>();
        foreach (var ch in host.Project.Channels)
        {
            string worst = "ok"; int count = 0; int worstRank = int.MaxValue;
            void Bump(string sev) { count++; int r = Rank(sev); if (r < worstRank) { worstRank = r; worst = sev; } }
            foreach (var d in offline)
                if (ChannelOf(d.Name) == ch.Name) Bump("offline");
            foreach (var e in nodeEntries)
                if (e.Task.Devices.Any(dv => ChannelOf(dv) == ch.Name)) Bump(e.Severity);
            foreach (var d in devices)
                if (d.State == ConnectionState.Connected && !d.Stats.PeriodHealthy && ChannelOf(d.Name) == ch.Name)
                    Bump("slow");
            foreach (var d in devices)
                if (d.State == ConnectionState.Connected && d.Diagnostic is { Active: true, Scope: ConnectionDiagnosticScope.DataAccess } && ChannelOf(d.Name) == ch.Name)
                    Bump("fail");
            int devTotal = devices.Count(d => ChannelOf(d.Name) == ch.Name);
            int devOnline = devices.Count(d => ChannelOf(d.Name) == ch.Name && d.State == ConnectionState.Connected);
            int devStandby = devices.Count(d => ChannelOf(d.Name) == ch.Name && !d.DemandActive && d.State == ConnectionState.Disconnected);
            tiles.Add(new { name = ch.Name, level = worst, count, devices = devTotal, online = devOnline, standby = devStandby });
        }
        return tiles;
    }
}
