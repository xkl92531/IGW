using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

internal sealed record WriteFailureDto(
    string Id,
    string Error,
    string Quality,
    uint StatusCode,
    string StatusHex);

internal sealed record TagScanTimingRequest(string[]? TagIds);

/// <summary>REST 北向 + 管理 API。读全部走缓存;PUT /project 下发热重载。</summary>
public static class IgwRestApi
{
    public static void MapIgwApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        // 数据面门禁:热重载中→503;未授权/已过期(采集与北向未起、Hub 为空)→403 附授权状态,便于前端弹激活窗
        IResult? DataGate() =>
            host.Reloading ? Results.Json(new { error = "网关正在热重载,请稍后重试" }, statusCode: 503)
            : host.Hub is null ? Results.Json(new { error = "网关未授权或已过期,采集已停止,请激活授权码", license = host.LicenseSnapshot() }, statusCode: 403)
            : null;

        // 设备连接状态来自南向协议会话。MES/OPC UA 等北向组件重载时 Hub/Engine 仍然有效，
        // 不能套用 DataGate 返回 503，否则工程树会把真实的在线/离线状态短暂覆盖成“未知”。
        IResult? DeviceStatusGate() =>
            host.Hub is null
                ? Results.Json(new { error = "网关未授权或已过期,采集已停止,请激活授权码", license = host.LicenseSnapshot() }, statusCode: 403)
                : null;

        app.MapGet("/api/v1/health", () =>
        {
            if (host.Reloading) return Results.Json(new { status = "reloading" }, statusCode: 503);
            var lic = host.LicenseSnapshot();
            if (host.Hub is null)   // 未授权/已过期(过宽限):进程活着、REST 在,但采集与北向已停 → 明确回 unlicensed 便于 UI 提示激活
                return Results.Ok(new { status = "unlicensed", version = GatewayVersion, project = host.Project.Name, tags = 0, devices = 0, devicesOnline = 0, license = lic });
            var devs = host.Engine.DeviceStatuses();
            var realtime = host.RealtimeStreams?.SnapshotMetricsCached();
            return Results.Ok(new
            {
                status = "ok",
                version = GatewayVersion,   // 现场要能直接看到装的是哪一版
                project = host.Project.Name,
                tags = host.Hub.Count,   // 只要数量,别 ListTagIds() 全量拷贝
                activeTags = devs.Sum(d => d.ActiveTags),   // 正在轮询的点(all 模式=全部, active 模式=被引用/订阅的)
                devices = devs.Count,
                devicesOnline = devs.Count(d => d.State == ConnectionState.Connected),
                devicesStandby = devs.Count(d => !d.DemandActive && d.State == ConnectionState.Disconnected),
                opcUaSessions = host.OpcUaSessions,   // 数据客户端(OPC UA 会话)
                components = host.ComponentsSnapshot(),   // 各北向组件 off/running/failed(failed=已下线待重试,别再静默)
                dataplane = host.Hub.Stats(),   // 数据面背压:分片/订阅的深度与丢弃计数(丢弃不静默,监控中心展示)
                realtime = realtime is null ? null : new
                {
                    realtime.ActiveStreams,
                    realtime.PeakStreams,
                    realtime.UniqueTags,
                    realtime.SubscriptionRelationships,
                    realtime.IngressChanges,
                    realtime.IngressDropped,
                    realtime.IngressQueueDepth,
                    realtime.PeakIngressQueueDepth,
                    realtime.CoalescedChanges,
                    realtime.DeliveredChanges,
                    realtime.ResetCount,
                    realtime.SentMessages,
                    realtime.SentValues,
                    realtime.SentBytes,
                    realtime.Latency,
                },
                license = lic,   // 授权状态(status/blocked/customer/expiry/daysLeft/machineId),UI 据此显示告警条
            });
        });

        app.MapGet("/api/v1/mes", () => Results.Ok(host.MesStatusSnapshot()));
        app.MapGet("/api/v1/mes/log", (int? limit) => Results.Ok(host.MesLogSnapshot(limit ?? 50)));
        // 任务时间线:节点流转事件(内存环形缓冲,时间正序;供画布侧边"执行轨迹"回放)
        app.MapGet("/api/v1/mes/timeline", (string? task) => Results.Ok(host.MesTimeline(task ?? "")));
        // 异常中心聚合:设备离线根因折叠 + 节点 fail/卡住/慢于基线,严重度排序;q=按 设备/任务/节点/信息 子串过滤
        app.MapGet("/api/v1/anomalies", (string? q) => Results.Ok(AnomalyService.Build(host, q)));
        app.MapGet("/api/v1/connection-diagnostics", () =>
            DataGate() ?? Results.Ok(ConnectionDiagnosticsApi.ToDto(host.Engine.ConnectionDiagnostics())));
        // 点位级故障:哪个点位读不了、为什么、该怎么改(连接诊断只回答「这台设备为什么连不上」)
        app.MapGet("/api/v1/tag-faults", () => DataGate() ?? Results.Ok(host.Engine.TagFaults()));
        // 按完整路径解析节点 ID:监控中心要把配错设备的点位挪走,先得知道它是哪个节点、现在挂在谁下面
        app.MapPost("/api/v1/project/resolve-paths", (ResolvePathsBody body) =>
        {
            var paths = body.Paths ?? [];
            if (paths.Length > 500) return Results.BadRequest(new { error = "单次最多解析 500 条路径" });
            var project = host.Project;
            return Results.Ok(paths.Select(path => ProjectPathResolver.Resolve(project, path)).ToArray());
        });
        // 数据存储：外部主库(SQL Server/PostgreSQL/MySQL)的同步状态；SQLite 时只返回 state=sqlite。
        app.MapGet("/api/v1/database/status", () => Results.Ok(host.DatabaseSync?.Snapshot()
            ?? new { databaseKind = "SQLite", target = "SQLite(本机)", state = "sqlite", pending = 0L, sources = Array.Empty<object>() }));
        // 内存诊断：进程占用 + GC 堆构成，现场判断“内存高”是数据本身还是 GC 预留。
        app.MapGet("/api/v1/diagnostics/memory", (bool? collect) =>
        {
            long? pauseMs = null;
            if (collect == true)
            {
                // 大对象堆默认不压缩；手动回收时顺带压缩一次，才能看出碎片能回收多少。
                var watch = System.Diagnostics.Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                pauseMs = watch.ElapsedMilliseconds;
            }
            var info = GC.GetGCMemoryInfo();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            static long Mb(long bytes) => bytes / 1048576;
            string[] names = ["gen0", "gen1", "gen2", "loh", "poh"];
            return Results.Ok(new
            {
                collectPauseMs = pauseMs,
                workingSetMb = Mb(process.WorkingSet64),
                privateMb = Mb(process.PrivateMemorySize64),
                gcHeapMb = Mb(info.HeapSizeBytes),
                gcCommittedMb = Mb(info.TotalCommittedBytes),
                gcFragmentedMb = Mb(info.FragmentedBytes),
                gcTotalAvailableMb = Mb(info.TotalAvailableMemoryBytes),
                generations = info.GenerationInfo.ToArray().Select((g, i) => new
                {
                    name = i < names.Length ? names[i] : $"gen{i}",
                    sizeAfterMb = Mb(g.SizeAfterBytes),
                    fragmentationAfterMb = Mb(g.FragmentationAfterBytes),
                }),
                serverGc = System.Runtime.GCSettings.IsServerGC,
                processorCount = Environment.ProcessorCount,
                pauseTimePercentage = Math.Round(info.PauseTimePercentage, 2),
                tags = host.Hub?.SortedTagIds.Count ?? 0,
            });
        });
        app.MapGet("/api/v1/diagnostics/acquisition-export", async (CancellationToken ct) =>
        {
            if (DataGate() is { } gate) return gate;
            try
            {
                var snapshot = host.Engine.AcquisitionDiagnostics(ct);
                var stream = await AcquisitionDiagnosticsExport.CreateAsync(snapshot, host.ProjectRevision, ct);
                return Results.Stream(stream, "application/zip", "igw-acquisition-diagnostics.zip");
            }
            catch (InvalidDataException)
            {
                return Results.Json(new { error = "诊断包超过 32 MiB 未压缩数据上限" }, statusCode: 413);
            }
        });
        // 测试上报:传入任务定义,发一次、返回 请求/响应/可用响应字段(不回写、不改统计)
        app.MapPost("/api/v1/mes/test", async (MesTask task, CancellationToken ct) => Results.Ok(await host.MesTestAsync(task, ct)));

        app.MapGet("/api/v1/tags", () => DataGate() ?? Results.Ok(host.Hub.SortedTagIds));   // 缓存的已排序快照(注册/注销时才重建),不再每请求全量拷贝+排序

        app.MapGet("/api/v1/tags/{id}/value", async (string id, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            // 没在持续采集的点先从 PLC 读一次，外部系统拿到的是最新值；读后激活，活动窗口内走轮询缓存。
            await host.Engine.RefreshIdleAsync([id], GetRefreshTimeout, ct, activate: true);
            return host.Hub.TryGet(id, out var v) ? Results.Ok(ToDto(id, v)) : Results.NotFound(new { error = "tag not found", id });
        });

        app.MapGet("/api/v1/values", async (string? ids, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            if (string.IsNullOrWhiteSpace(ids)) return Results.BadRequest(new { error = "缺少 ids 参数" });
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length > MaxReadNowTags) return Results.BadRequest(new { error = $"单次最多读取 {MaxReadNowTags} 个点位" });
            await host.Engine.RefreshIdleAsync(list, GetRefreshTimeout, ct, activate: true);
            return Results.Ok(list.Select(id => host.Hub.TryGet(id, out var v) ? ToDto(id, v) : ToMissing(id)).ToArray());
        });

        // QC 点击刷新/定时刷新：显式从 PLC 读取这些点并返回最新值。只读这一次，不登记采集需求，
        // QC 窗口开着也不会让范围内的点持续轮询或订阅。
        app.MapPost(PublicPlcApiPolicy.PlcReadNowPath, async (ReadValuesBody body, HttpContext context, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            var ids = (body.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) return Results.BadRequest(new { error = "缺少 ids 参数" });
            // 鉴权中间件看不到请求体里的点位，这里逐点检查读分支。
            var user = context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] as PublicPlcPrincipal;
            if (!PublicPlcApiPolicy.AreReadBranchesAllowed(user, ids))
                return Results.Json(new { error = "read_branch_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            if (ids.Length > MaxReadNowTags)
                return Results.BadRequest(new { error = $"单次最多读取 {MaxReadNowTags} 个点位" });
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(ReadNowTimeout);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            OnDemandReadResult result;
            try { result = await host.Engine.ReadNowAsync(ids, bounded.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { error = $"读取 PLC 超过 {ReadNowTimeout.TotalSeconds:0} 秒未完成" }, statusCode: 504);
            }
            return Results.Ok(new
            {
                goodCount = result.GoodCount,
                sampleCount = result.SampleCount,
                error = result.Error,
                elapsedMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
                values = ids.Select(id => host.Hub.TryGet(id, out var v) ? ToDto(id, v) : ToMissing(id)).ToArray(),
            });
        });

        app.MapPost("/api/v1/tags/{id}/value", async (string id, WriteBody body, HttpRequest request, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            if (PublicPlcApiPolicy.RejectStaleProjectWrite(request, host.ProjectRevision) is { } stale) return stale;
            if (!host.Hub.TryGetDef(id, out var def)) return Results.NotFound(new { error = "tag not found", id });
            // 单点写入只下发这一次写入并回读确认(回读值会发布到缓存)，不再顺带激活该点持续采集。
            object? val = JsonToValue(body.Value, def.DataType);
            if (val is null) return Results.BadRequest(new { error = $"value 无法转换为 {def.DataType}" });
            var st = await host.Engine.WriteAsync(id, val, ct);
            return st.IsGood
                ? Results.Ok(new
                {
                    id,
                    written = val,
                    normalizedValue = BrowserValueJson.Normalize(val),
                    quality = st.SymbolicName,
                    statusCode = st.Code,
                    statusHex = st.Hex,
                })
                : Results.Json(WriteFailure(id, st), statusCode: 502);
        });

        app.MapGet("/api/v1/devices", () =>
            DeviceStatusGate() ??
            Results.Ok(host.Engine.DeviceStatuses().Select(d => new
            {
                d.Name,
                state = !d.DemandActive && d.State == ConnectionState.Disconnected ? "Standby" : d.State.ToString(),
                d.TagCount,
                activeTags = d.ActiveTags,
                d.DemandActive,
                detailState = d.Detail?.Code,
                detailMessage = d.Detail?.Message,
                diagnostic = d.Diagnostic is null ? null : ConnectionDiagnosticsApi.ToDto(d.Diagnostic),
                stats = new
                {
                    d.Stats.ReadsOk, d.Stats.ReadsErr, d.Stats.WritesOk, d.Stats.WritesErr,
                    d.Stats.LastScanMs, d.Stats.SuccessRate, d.Stats.LastError,
                    d.Stats.ConfiguredMinPeriodMs,
                    actualAveragePeriodMs = Math.Round(d.Stats.ActualAveragePeriodMs, 2),
                    d.Stats.ActualP95PeriodMs,
                    d.Stats.ActualP99PeriodMs,
                    d.Stats.OverduePeriods,
                    d.Stats.ConsecutiveOverruns,
                    d.Stats.PlannedBlocks,
                    samplesPerSecond = Math.Round(d.Stats.SamplesPerSecond, 1),
                    d.Stats.PeriodHealthy,
                    d.Stats.ArrayTags,
                    d.Stats.ArrayElements,
                    d.Stats.ArrayGood,
                    d.Stats.ArrayUncertain,
                    d.Stats.ArrayBad,
                    d.Stats.ArrayBytes,
                    d.Stats.ArrayBlocks,
                    d.Stats.ArrayFailedRanges,
                    d.Stats.WriteQueueP95Ms,
                    d.Stats.WriteResponseP95Ms,
                    d.Stats.WriteVerificationP95Ms,
                    d.Stats.PeakWriteQueueDepth,
                    d.Stats.SameSessionRetries,
                    d.Stats.BatchDownshifts,
                },
            })));

        app.MapGet("/api/v1/scan-timings", (string? device, string? tag, int? limit) =>
        {
            if (DeviceStatusGate() is { } gate) return gate;
            try { return Results.Ok(host.Engine.ScanTimings(device, tag, limit ?? 100)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/scan-timings/tags", (TagScanTimingRequest request) =>
        {
            if (DeviceStatusGate() is { } gate) return gate;
            if (request.TagIds is null) return Results.BadRequest(new { error = "缺少标签 ID 列表。" });
            try
            {
                return Results.Ok(new { items = host.Engine.TagScanTimings(request.TagIds), capturedAt = DateTimeOffset.UtcNow });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/v1/channels/{name}/reconnect", async (string name, CancellationToken ct) =>
        {
            if (host.Reloading)
                return Results.Json(new { error = "网关正在热重载，请稍后重试。" }, statusCode: 503);
            try { return Results.Ok(await host.ReconnectChannelAsync(name, ct)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // 返回 Runtime 所在服务器的网卡，不是浏览器所在电脑的网卡。
        app.MapGet("/api/v1/network-adapters", () => Results.Ok(host.NetworkAdapters.Available()));

        app.MapGet("/api/v1/devices/{name}/authentication", async (string name, CancellationToken ct) =>
        {
            try { return Results.Ok(await host.SiemensAuthenticationStatusAsync(name, ct)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapPost("/api/v1/devices/{name}/authentication/verify", async (string name, SiemensAuthVerifyRequest request, CancellationToken ct) =>
        {
            try
            {
                if (request is null || request.Username?.Length > 256 || request.Password?.Length is 0 or > 1024)
                    return Results.BadRequest(new { error = "用户名或密码长度无效" });
                return Results.Ok(await host.VerifySiemensAuthenticationAsync(name, request.Username ?? "", request.Password ?? "", ct));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (NotSupportedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (Exception ex) when (ex.GetType().Name == "SiemensPlusException")
            {
                return Results.Json(new { error = ex.Message }, statusCode: 401);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });

        app.MapDelete("/api/v1/devices/{name}/authentication", async (string name, CancellationToken ct) =>
        {
            try
            {
                await host.DeleteSiemensAuthenticationAsync(name, ct);
                return Results.Ok(new { mode = "anonymous", hasCredential = false, state = "Anonymous", message = "已恢复匿名连接" });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        // 标记生成:浏览设备地址空间,返回发现的标记(name/address/dataType)
        app.MapPost("/api/v1/devices/{name}/browse", async (string name, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            try { return Results.Json((await host.Engine.BrowseAsync(name, ct)).Tags, ProjectStore.Options); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // 导入 DB 源文件时按块名查 DB 号：在线列出 PLC 上的数据块(S7-1200/1500)。
        // supported=false 表示设备没在运行或 PLC/原生组件不支持，界面改为让用户填写；查询失败也不算错误。
        app.MapGet("/api/v1/devices/{name}/siemens/data-blocks", async (string name, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var blocks = await host.Engine.ListDataBlocksAsync(name, timeout.Token);
                return Results.Ok(new { supported = blocks is not null, blocks = blocks ?? [], error = (string?)null });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new { supported = false, blocks = Array.Empty<DataBlockInfo>(),
                    error = ex is OperationCanceledException ? "查询 PLC 数据块超时" : ex.Message });
            }
        });

        app.MapPost("/api/v1/opcua/browse-preview", async (BrowsePreviewRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Json(await host.BrowsePreviewAsync(request, ct), ProjectStore.Options);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.MapPost("/api/v1/opcua/browse-children", async (OpcUaBrowseNodeRequest request, CancellationToken ct) =>
        {
            if (request.Connection is null)
                return Results.BadRequest(new { error = "缺少 OPC UA 连接参数。" });
            try
            {
                return Results.Ok(await host.BrowseOpcUaChildrenAsync(
                    request.Connection,
                    request.NodeId ?? "",
                    request.PathSegments ?? Array.Empty<string>(),
                    ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapPost("/api/v1/opcua/browse-branch", async (OpcUaBrowseNodeRequest request, CancellationToken ct) =>
        {
            if (request.Connection is null)
                return Results.BadRequest(new { error = "缺少 OPC UA 连接参数。" });
            if (string.IsNullOrWhiteSpace(request.NodeId) || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "未选择需要导入的 OPC UA 节点。" });
            try
            {
                var selected = new OpcUaBrowseNode(
                    request.NodeId,
                    request.Name,
                    request.IsVariable,
                    MayHaveChildren: true,
                    request.PathSegments ?? Array.Empty<string>());
                return Results.Json(await host.BrowseOpcUaBranchAsync(
                    request.Connection, selected, request.Limit ?? 10_000, ct), ProjectStore.Options);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // 历史记录:查询某点最近 N 条(数据记录启用时)
        app.MapGet("/api/v1/history", (string? id, int? limit) =>
        {
            if (DataGate() is { } g) return g;
            if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "缺少 id 参数" });
            if (host.Logger is null) return Results.Json(new { error = "数据记录未启用(工程 History.Enabled=false)" }, statusCode: 409);
            var rows = host.Logger.Query(id, limit ?? 100).Select(r => new { value = r.Value, qualityCode = r.Quality, ts = r.Ts });
            return Results.Ok(new { id, count = host.Logger.Count(), rows });
        });

        // ---- KEP IoT Gateway 兼容端点(对标 KEPServerEX REST Server Agent) ----
        // 原来对接 KEP http://ip:39320/iotgateway/... 的系统,把地址换成本网关端口即可零改动切换。
        // 读:GET /iotgateway/read?ids=A&ids=B(也支持逗号分隔)→ {"readResults":[{id,s,r,v,t}]},t=毫秒时间戳
        app.MapGet("/iotgateway/read", async (HttpRequest req, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            var ids = req.Query["ids"]
                .SelectMany(s => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
            if (ids.Count == 0) return Results.BadRequest(new { error = "缺少 ids 参数,如 /iotgateway/read?ids=通道.设备.标记" });
            if (ids.Count > MaxReadNowTags) return Results.BadRequest(new { error = $"单次最多读取 {MaxReadNowTags} 个点位" });
            await host.Engine.RefreshIdleAsync(ids, GetRefreshTimeout, ct, activate: true);   // Kepware 兼容接口保持“读即激活”；未在采集的点先读 PLC
            var results = ids.Select(id =>
            {
                if (!host.Hub.TryGet(id, out var v))
                    return new { id, s = false, r = "tag not found", v = (object?)null, t = ToMs(default) };
                return new { id, s = v.IsGood, r = v.IsGood ? "" : v.Quality.Class.ToString(), v = (object?)v.Value, t = ToMs(v.SourceTimestamp) };
            });
            return Results.Ok(new { readResults = results });
        });

        // 写:POST /iotgateway/write 体 [{"id":"通道.设备.标记","v":值}] → {"writeResults":[{id,s,r}]}
        app.MapPost("/iotgateway/write", async (List<IotWriteItem> items, CancellationToken ct) =>
        {
            if (DataGate() is { } g) return g;
            if (items is null || items.Count == 0)
                return Results.BadRequest(new { error = "请求体应为 [{\"id\":\"通道.设备.标记\",\"v\":值}]" });
            var results = new List<object>(items.Count);
            foreach (var it in items)
            {
                string id = it.Id ?? "";
                if (!host.Hub.TryGetDef(id, out var def)) { results.Add(new { id, s = false, r = "tag not found" }); continue; }
                host.Engine.Touch(id);
                object? val = JsonToValue(it.V, def.DataType);
                if (val is null) { results.Add(new { id, s = false, r = $"value 无法转换为 {def.DataType}" }); continue; }
                var st = await host.Engine.WriteAsync(id, val, ct);
                results.Add(new { id, s = st.IsGood, r = st.IsGood ? "" : st.Class.ToString() });
            }
            return Results.Ok(new { writeResults = results });
        });

        // 浏览:GET /iotgateway/browse → {"browseResults":[{"id":...}]}
        app.MapGet("/iotgateway/browse", () =>
            DataGate() ?? Results.Ok(new { browseResults = host.Hub.SortedTagIds.Select(id => new { id }) }));

        // 报警:当前活动报警 + 最近事件(重载窗口内组件在重建,返回 503 而不是 NRE)
        app.MapGet("/api/v1/alarms", () =>
        {
            var al = host.Reloading ? null : host.Alarms;
            return al is null ? Results.Json(new { error = "网关正在热重载,请稍后重试" }, statusCode: 503)
                : Results.Ok(al.ActiveAlarms().Select(a => new { a.TagId, level = a.Level.ToString(), a.Limit, a.Value, since = a.Since }));
        });
        app.MapGet("/api/v1/alarms/events", (int? limit) =>
        {
            var al = host.Reloading ? null : host.Alarms;
            return al is null ? Results.Json(new { error = "网关正在热重载,请稍后重试" }, statusCode: 503)
                : Results.Ok(al.RecentEvents(limit ?? 50).Select(e => new { e.TagId, level = e.Level.ToString(), active = e.Active, e.Limit, e.Value, ts = e.Timestamp }));
        });

        // 计划任务状态(已触发次数)
        app.MapGet("/api/v1/scheduler", () =>
        {
            var sc = host.Reloading ? null : host.Scheduler;
            return sc is null ? Results.Json(new { error = "网关正在热重载,请稍后重试" }, statusCode: 503)
                : Results.Ok(new { fired = sc.Fired });
        });

        app.MapGet("/api/v1/drivers", () => Results.Ok(host.Drivers));
        LogFilterOptions.Map(app, host);   // 日志中心的协议/通道/设备下拉、标记组提示

        // 配置态工程参数搜索：只读 Project，不经过 DataGate/DataHub，也不会唤醒或连接 PLC。
        app.MapGet("/api/v1/project/parameter-options", async (
            string? q,
            string? scopeGroup,
            string? kind,
            string? access,
            int? limit,
            bool? scopeOnly,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(host.ProjectRepository is { } repository
                    ? await ProjectParameterSearch.SearchAsync(
                        repository, q, scopeGroup, kind, access, limit, ct, scopeOnly).ConfigureAwait(false)
                    : ProjectParameterSearch.Search(
                        host.Project, q, scopeGroup, kind, access, limit, ct, scopeOnly));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // MES 范围目录由后端统一识别数组并分页；不会把上万数组元素展开给浏览器。
        app.MapGet("/api/v1/project/mes-range-options", async (
            string? scopeGroup,
            string? q,
            string? cursor,
            int? limit,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(host.ProjectRepository is { } repository
                    ? await MesRangeOptionsApi.SearchAsync(
                        repository, scopeGroup, q, cursor, limit, ct).ConfigureAwait(false)
                    : MesRangeOptionsApi.Search(
                        host.Project, scopeGroup, q, cursor, limit, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 「配置数据」树：按层取子项(含单个点，数组已识别)，q 不为空时在数据块里搜索；目录按工程版本缓存。
        // 不带 scopeGroup 时按整个工程浏览：数据块以上取通道/设备/数据块，数据块里面按 parent 所在的数据块取。
        app.MapGet("/api/v1/project/mes-range-children", async (
            string? scopeGroup, string? parent, string? q, string? cursor, int? limit, CancellationToken ct) =>
        {
            try
            {
                string scope = scopeGroup?.Trim().Trim('.') ?? "";
                if (scope.Length == 0 && string.IsNullOrWhiteSpace(q))
                {
                    string parentPath = parent?.Trim().Trim('.') ?? "";
                    if (parentPath.Length == 0 || parentPath.Split('.').Length < 3)
                        return Results.Ok(MesRangeTreeApi.ProjectLevels(host.Project, parentPath, cursor, limit));
                    scope = MesRangeTreeApi.BlockOf(parentPath);
                }
                var catalog = await MesRangeTreeApi.CatalogAsync(host.ProjectRepository, host.Project, scope, ct).ConfigureAwait(false);
                return Results.Ok(MesRangeTreeApi.Children(catalog, scope, parent, q, cursor, limit));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 「报文高级设置」：把数据范围编译成报文结构(按原名，含已排除的项)，并用实际配置检查改名是否冲突
        app.MapPost("/api/v1/project/mes-payload-preview", async (MesPayloadPreviewRequest request, CancellationToken ct) =>
        {
            try
            {
                var ranges = request.Ranges ?? [];
                var catalogs = new Dictionary<string, MesDataRangeCatalog>(StringComparer.OrdinalIgnoreCase);
                foreach (string block in ranges.Select(range => MesRangeTreeApi.BlockOf(range.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase))
                    catalogs[block] = await MesRangeTreeApi.CatalogAsync(host.ProjectRepository, host.Project, block, ct).ConfigureAwait(false);
                return Results.Ok(MesRangeTreeApi.Preview(path => catalogs[MesRangeTreeApi.BlockOf(path)], ranges));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // 「配置数据」→ 旧版字段/结构映射：普通点成字段，子组成对象，按下标命名的子组成数组(枚举子组)
        app.MapPost("/api/v1/project/mes-field-mapping", async (MesPayloadPreviewRequest request, CancellationToken ct) =>
        {
            try
            {
                var fields = new List<MesField>();
                var structs = new List<MesStruct>();
                foreach (var group in (request.Ranges ?? []).GroupBy(range => MesRangeTreeApi.BlockOf(range.SourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    var catalog = await MesRangeTreeApi.CatalogAsync(host.ProjectRepository, host.Project, group.Key, ct).ConfigureAwait(false);
                    var (blockFields, blockStructs) = MesFieldMappingGenerator.Generate(group, catalog, request.ScopeGroup);
                    fields.AddRange(blockFields);
                    structs.AddRange(blockStructs);
                }
                return Results.Ok(new MesFieldMappingResponse(fields, structs));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/v1/project", async (HttpRequest req, HttpResponse resp, CancellationToken ct) =>
        {
            SetLegacyProjectHeaders(resp, host.ProjectRevision);
            if (await IsLegacyFullProjectDisabledAsync(host, ct).ConfigureAwait(false))
            {
                if (IsBrowserNavigation(req)) return Results.Redirect("/#/project");
                return Results.Json(LegacyFullProjectDisabled(), statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            string revision = host.ProjectRevision;
            // 浏览器已有同一版本(再次打开工程配置)：直接 304，不再重新下载整个工程。
            if (req.Headers.IfNoneMatch.Any(value => string.Equals(value, revision, StringComparison.Ordinal)))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            // 同一版本只序列化一次；多个页面/客户端反复打开时直接复用。
            var cached = Volatile.Read(ref s_projectJson);
            if (cached is null || !string.Equals(cached.Revision, revision, StringComparison.Ordinal))
            {
                var redacted = ProjectConfigurationRedactor.Create(host.Project);
                cached = new SerializedProject(revision, JsonSerializer.SerializeToUtf8Bytes(redacted, ProjectStore.Options));
                Volatile.Write(ref s_projectJson, cached);
            }
            resp.ContentType = "application/json; charset=utf-8";
            await resp.Body.WriteAsync(cached.Json, ct).ConfigureAwait(false);
            return Results.Empty;
        });

        // 授权:查看状态(含本机机器码,现场读来报给厂商发码)
        app.MapGet("/api/v1/license", () => Results.Ok(host.LicenseSnapshot()));

        // 授权:提交授权码激活(验签+本机机器码校验通过则写盘并即时生效)
        app.MapPost("/api/v1/license", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            string code = body.Trim();
            // 兼容 JSON 体 {"code":"..."} 或纯文本
            if (code.StartsWith("{"))
            {
                try { code = System.Text.Json.JsonDocument.Parse(code).RootElement.GetProperty("code").GetString() ?? ""; }
                catch { return Results.BadRequest(new { error = "请求体应为授权码文本或 {\"code\":\"...\"}" }); }
            }
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { error = "授权码为空" });
            var snap = await host.ActivateLicenseAsync(code);
            return Results.Ok(snap);
        });

        // MES 画布专用保存：请求体只有 MesUploadSettings，不再让浏览器复制/上传整个点位工程。
        // 服务端以当前工程为基线只替换 MES 配置，因此工程配置页的并发修改仍由 rev 严格保护。
        app.MapPut("/api/v1/project/mes-upload", async (HttpRequest req, HttpResponse resp) =>
        {
            string? baseRev = req.Headers["X-Igw-Rev"].FirstOrDefault();

            MesUploadSettings? mesUpload;
            try { mesUpload = await JsonSerializer.DeserializeAsync<MesUploadSettings>(req.Body, ProjectStore.Options, req.HttpContext.RequestAborted); }
            catch (OperationCanceledException) { return Results.Json(new { error = "保存请求已取消" }, statusCode: 499); }
            catch (Exception ex) { return Results.BadRequest(new { error = "MES 配置解析失败:" + ex.Message }); }
            if (mesUpload is null) return Results.BadRequest(new { error = "MES 配置为空" });
            ProjectConfigurationRedactor.MergeMesSecrets(host.Project.MesUpload, mesUpload);

            var proj = ProjectStore.WithMesUpload(host.Project, mesUpload);
            var mesTopology = MesTaskTopology.Reconcile(proj);
            bool allowDestructive = string.Equals(req.Headers["X-Igw-Allow-Destructive"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var destructive = ProjectStore.MesDestructiveChanges(host.Project.MesUpload, proj.MesUpload);
            if (destructive.Count > 0 && !allowDestructive)
                return Results.Json(new
                {
                    error = "检测到 MES 配置数量减少，已阻止后台静默覆盖：" + string.Join("，", destructive) + "。请检查后点击“保存并应用”明确确认删除。",
                    destructive,
                }, statusCode: 409);

            ProjectUpdateResult applied;
            try { applied = await host.ApplyMesUploadAsync(proj.MesUpload, baseRev, req.HttpContext.RequestAborted); }
            catch (Exception ex) { return Results.Json(new { error = "MES 配置应用失败:" + ex.Message }, statusCode: 500); }
            if (applied.Conflict)
                return Results.Json(new
                {
                    error = "配置已被其他人修改过(另一浏览器/桌面工具先保存了),请重新载入后再改",
                    rev = applied.Revision,
                }, statusCode: 409);
            string newRev = applied.Revision;
            resp.Headers["X-Igw-Rev"] = newRev;
            return Results.Ok(new
            {
                status = "applied",
                rev = newRev,
                tasks = proj.MesUpload.Tasks.Count,
                removedOrphanMesTasks = mesTopology.RemovedTasks,
                clearedMesUploadPayloads = mesTopology.ClearedPayloads,
            });
        });

        // 管理:下发新工程并热重载
        app.MapPut("/api/v1/project", async (HttpRequest req, HttpResponse resp) =>
        {
            SetLegacyProjectHeaders(resp, host.ProjectRevision);
            if (await IsLegacyFullProjectDisabledAsync(host, req.HttpContext.RequestAborted).ConfigureAwait(false))
                return Results.Json(LegacyFullProjectDisabled(), statusCode: StatusCodes.Status413PayloadTooLarge);
            // 并发编辑保护(可选头,Web 端带;桌面工具不带则跳过=旧行为):
            // 客户端 GET 时记下 X-Igw-Rev,PUT 带回;当前工程已变(别人先保存过)→ 409,前端提示重新载入
            string? baseRev = req.Headers["X-Igw-Rev"].FirstOrDefault();
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            IgwProject? proj;
            try { proj = JsonSerializer.Deserialize<IgwProject>(json, ProjectStore.Options); }
            catch (Exception ex) { return Results.BadRequest(new { error = "JSON 解析失败:" + ex.Message }); }
            if (proj is null) return Results.BadRequest(new { error = "空工程" });
            RetiredProjectContent.RemoveOpenProtocolChannels(proj);
            ProjectConfigurationRedactor.MergeUnchangedSecrets(host.Project, proj);
            var mesTopology = MesTaskTopology.Reconcile(proj);
            bool allowDestructive = string.Equals(req.Headers["X-Igw-Allow-Destructive"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var destructive = ProjectStore.DestructiveChanges(host.Project, proj);
            if (destructive.Count > 0 && !allowDestructive)
                return Results.Json(new
                {
                    error = "检测到配置数量减少，已阻止后台静默覆盖：" + string.Join("，", destructive) + "。请检查后点击“保存并应用”明确确认删除。",
                    destructive,
                }, statusCode: 409);
            ProjectUpdateResult applied;
            try { applied = await host.ApplyProjectAsync(proj, baseRev, req.HttpContext.RequestAborted); }
            catch (Exception ex) { return Results.Json(new { error = "应用失败:" + ex.Message }, statusCode: 500); }
            if (applied.Conflict)
                return Results.Json(new
                {
                    error = "配置已被其他人修改过(另一浏览器/桌面工具先保存了),请重新载入后再改",
                    rev = applied.Revision,
                }, statusCode: 409);
            string newRev = applied.Revision;
            resp.Headers["X-Igw-Rev"] = newRev;
            return Results.Ok(new
            {
                status = "applied",
                rev = newRev,
                channels = proj.Channels.Count,
                tags = host.Hub?.ListTagIds().Count ?? 0,
                removedOrphanMesTasks = mesTopology.RemovedTasks,
                clearedMesUploadPayloads = mesTopology.ClearedPayloads,
                timing = host.LastApplyTiming,
            });
        });

        app.MapTemplateApi(host);   // 模板化配置:提取/套用/导出导入 + 验证作业 + CSV 批量导入
        app.MapArrayValueApi(host); // 逻辑数组形状、切片读取与切片写入
        app.MapProjectTagScopeApi(host); // Quick Client:按精确路径懒加载工程定义,不下发整库点表
        app.MapProjectConfigurationApi(host); // 大工程:树懒加载、点位/MES 分页和局部事务
        app.MapProjectBulkTagApi(host);       // 点位批量注册:一次请求写一台设备,行级批量落库
    }

    internal static WriteFailureDto WriteFailure(string id, StatusCode status) =>
        new(id, "写入失败", status.SymbolicName, status.Code, status.Hex);

    private static async Task<bool> IsLegacyFullProjectDisabledAsync(IGatewayHost host, CancellationToken ct)
    {
        if (host.ProjectRepository is { } repository)
        {
            long totalTagCount = await repository.GetTagCountAsync(ct).ConfigureAwait(false);
            return totalTagCount > ProjectConfigurationApi.FullProjectTagLimit;
        }
        return CountProjectTags(host.Project) > ProjectConfigurationApi.FullProjectTagLimit;
    }

    private static bool IsBrowserNavigation(HttpRequest request)
    {
        if (string.Equals(request.Headers["Sec-Fetch-Mode"].FirstOrDefault(), "navigate", StringComparison.OrdinalIgnoreCase))
            return true;
        return request.Headers.Accept.Any(value =>
            value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static long CountProjectTags(IgwProject project)
    {
        long count = 0;
        foreach (ChannelDef channel in project.Channels)
        foreach (DeviceDef device in channel.Devices)
        {
            count += device.Tags.Count;
            foreach (TagGroupDef group in device.Groups) count += CountGroupTags(group);
        }
        return count;
    }

    private static long CountGroupTags(TagGroupDef group)
    {
        long count = group.Tags.Count;
        foreach (TagGroupDef child in group.SubGroups) count += CountGroupTags(child);
        return count;
    }

    /// <summary>最近一次序列化的完整工程(按工程版本缓存，版本变化时重新生成)。</summary>
    private sealed record SerializedProject(string Revision, byte[] Json);
    private static SerializedProject? s_projectJson;

    private static void SetLegacyProjectHeaders(HttpResponse response, string revision)
    {
        response.Headers["X-Igw-Rev"] = revision;
        response.Headers.ETag = revision;
        response.Headers["Deprecation"] = "true";
        response.Headers["Sunset"] = "Mon, 01 Mar 2027 00:00:00 GMT";
        response.Headers["Link"] = "</api/v1/project/summary>; rel=successor-version";
    }

    private static object LegacyFullProjectDisabled() => new
    {
        error = "full_project_endpoint_disabled",
        message = "工程超过 50000 个点，完整工程接口已禁用，请使用分页配置接口。",
        links = new
        {
            summary = "/api/v1/project/summary",
            tree = "/api/v1/project/tree/{kind}/{id}/children",
            tags = "/api/v1/project/tags",
            mesTasks = "/api/v1/project/mes-tasks",
            mutations = "/api/v1/project/mutations",
        },
    };

    public sealed record WriteBody(JsonElement Value);
    public sealed record ReadValuesBody(string[]? Ids);
    public sealed record ResolvePathsBody(string[]? Paths);
    /// <summary>安装包版本(打包时写入程序集)。未标记版本的本地调试构建显示 1.0.0.0。</summary>
    internal static readonly string GatewayVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    internal const int MaxReadNowTags = 10_000;
    internal static readonly TimeSpan ReadNowTimeout = TimeSpan.FromSeconds(30);
    /// <summary>GET 读取为未在采集的点补读 PLC 的最长等待；超时返回缓存值(质量和时间戳如实反映)。</summary>
    internal static readonly TimeSpan GetRefreshTimeout = TimeSpan.FromSeconds(10);
    public sealed record SiemensAuthVerifyRequest(string? Username, string? Password);
    public sealed record OpcUaBrowseNodeRequest(
        BrowsePreviewRequest? Connection,
        string? NodeId,
        string? Name,
        bool IsVariable,
        string[]? PathSegments,
        int? Limit);

    /// <summary>KEP IoT Gateway 写请求项:[{"id":"...","v":...}]。</summary>
    public sealed record IotWriteItem(string? Id, JsonElement V);

    // KEP IoT Gateway 的 t = Unix 毫秒。缓存时间戳按 UTC 处理(引擎统一 UtcNow 打戳;default=无值给当前时刻)
    private static long ToMs(DateTime t)
    {
        if (t == default) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var utc = t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private static object ToDto(string id, DataValue v) => new
    {
        id,
        value = BrowserValueJson.Normalize(v.Value),
        quality = v.Quality.Class.ToString(),
        qualityCode = v.Quality.Code,
        good = v.IsGood,
        sourceTs = v.SourceTimestamp,
        serverTs = v.ServerTimestamp,
    };

    private static object ToMissing(string id) => new { id, value = (object?)null, quality = "Bad", good = false, error = "not found" };

    internal static object? JsonToValue(JsonElement e, IgwDataType t)
    {
        try
        {
            if (SiemensValueCodec.IsSemantic(t)) return SiemensValueCodec.Normalize(t, e);
            if (e.ValueKind == JsonValueKind.String && t is not (IgwDataType.String or IgwDataType.WString))
                return PlcWriteValueNormalizer.Normalize(new TagDef { DataType = t }, e.GetString()!);
            return t switch
            {
                IgwDataType.Bool => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : e.GetDouble() != 0,
                IgwDataType.String or IgwDataType.WString => e.GetString(),
                IgwDataType.Float or IgwDataType.Double => e.GetDouble(),
                _ => PlcWriteValueNormalizer.Normalize(new TagDef { DataType = t }, e.GetDecimal()),
            };
        }
        catch { return null; }
    }
}
