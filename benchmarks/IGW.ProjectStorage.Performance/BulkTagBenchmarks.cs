using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator;
using IGW.Northbound.RestApi;
using IGW.Persistence.Sqlite;
using IGW.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace IGW.ProjectStorage.Performance;

/// <summary>
/// 批量注册点位（POST /api/v1/project/bulk/tags）的百万点基准。仓储层 + 协调器层不起真实 PLC；
/// bulk-host 场景起真实 GatewayHost（Simulator 驱动、OPC UA 关闭），并用 DcsTagOperations 并发读其它设备统计 503/失败。
/// 场景（--scenario）：
///   bulk-empty      空工程单请求 10 万点（仓储层 / 协调器层各 --rounds 轮，每轮新库）；
///   bulk-preseeded  预置 100 台设备 × 1 万点后对新设备单请求 10 万点（仓储层 + 协调器层）；
///   bulk-10x        100 万点分 10 次请求写 10 台设备（协调器层，no-op 运行态），并发读者持续读已有设备；
///   bulk-host       同 bulk-10x，但走 GatewayHost.ApplyBulkTagsAsync，并发读者走 DcsTagOperations.ReadAsList。
/// </summary>
internal static class BulkTagBenchmarks
{
    private const int PointsPerRequest = 100_000;
    private const int TagsPerGroup = 1_000;

    public static async Task<int> RunAsync(StorageBenchmarkOptions options)
    {
        try
        {
            object report = options.Scenario switch
            {
                "bulk-empty" => await EmptyProjectAsync(options),
                "bulk-preseeded" => await PreseededAsync(options),
                "bulk-10x" => await TenRequestsAsync(options),
                "bulk-host" => await HostTenRequestsAsync(options),
                "bulk-increment" => await HostIncrementAsync(options),
                _ => throw new ArgumentException("未知场景：" + options.Scenario),
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                string output = Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, json);
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }));
            return 3;
        }
    }

    // ---------------- 场景 1：空工程单请求 10 万点 ----------------

    private static async Task<object> EmptyProjectAsync(StorageBenchmarkOptions options)
    {
        EmptyBaseline baseline = await MeasureEmptyBaselineAsync(options.Rounds);
        return new
        {
            scenario = "bulk-empty",
            pointsPerRequest = PointsPerRequest,
            // cold* = 进程内第一次（丢弃的预热轮，含 JIT/首次分配）；其余为预热后的轮次
            coldRepositoryMs = baseline.ColdRepositoryMs,
            coldCoordinatorMs = baseline.ColdCoordinatorMs,
            repositoryMs = baseline.RepositoryMs,
            repositoryMedianMs = Median(baseline.RepositoryMs),
            coordinatorMs = baseline.CoordinatorMs,
            coordinatorMedianMs = Median(baseline.CoordinatorMs),
            loadDeviceMs = baseline.LoadDeviceMs,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        };
    }

    private sealed record EmptyBaseline(
        long ColdRepositoryMs, long ColdCoordinatorMs,
        List<long> RepositoryMs, List<long> CoordinatorMs, List<long> LoadDeviceMs);

    /// <summary>
    /// 空工程单请求 10 万点：先跑 1 轮丢弃的预热（每轮都是全新库），再跑 <paramref name="rounds"/> 轮。
    /// 预置场景也在同一进程内调用它，得到同负载下的空工程基线。
    /// </summary>
    private static async Task<EmptyBaseline> MeasureEmptyBaselineAsync(int rounds)
    {
        var repositoryMs = new List<long>();
        var coordinatorMs = new List<long>();
        var loadDeviceMs = new List<long>();
        long coldRepository = 0, coldCoordinator = 0;
        for (int round = -1; round < rounds; round++)
        {
            await using (var store = await TempStore.CreateAsync())
            {
                var watch = Stopwatch.StartNew();
                ProjectBulkTagResult result = await store.Repository.BulkUpsertTagsAsync(
                    Request("Bench", "Dev0", PointsPerRequest), null, default);
                if (round < 0) coldRepository = watch.ElapsedMilliseconds;
                else repositoryMs.Add(watch.ElapsedMilliseconds);
                EnsureCreated(result, PointsPerRequest);
            }

            await using (var store = await TempStore.CreateAsync())
            {
                ProjectConfigurationCoordinator coordinator = await NoOpCoordinatorAsync(store.Repository);
                var watch = Stopwatch.StartNew();
                ProjectBulkApplyResult result = await coordinator.ExecuteBulkAsync(
                    Request("Bench", "Dev0", PointsPerRequest), null, default);
                long elapsed = watch.ElapsedMilliseconds;
                EnsureApplied(result, PointsPerRequest);
                long load = await TimeLoadDeviceAsync(store.Repository, result.Persisted.DeviceId!);
                if (round < 0) coldCoordinator = elapsed;
                else { coordinatorMs.Add(elapsed); loadDeviceMs.Add(load); }
                await coordinator.DisposeAsync();
            }
            Collect();
        }
        return new EmptyBaseline(coldRepository, coldCoordinator, repositoryMs, coordinatorMs, loadDeviceMs);
    }

    // ---------------- 场景 2：100 万点工程上对新设备单请求 10 万点 ----------------

    private static async Task<object> PreseededAsync(StorageBenchmarkOptions options)
    {
        // 同一进程、同一时段的空工程基线：预置前后各测一次（夹住负载漂移），比值按两次合并的中位数计算
        EmptyBaseline before = await MeasureEmptyBaselineAsync(options.Rounds);
        Collect();
        IgwProject seed = SeedProject(devices: 100, tagsPerDevice: 10_000);
        var watch = Stopwatch.StartNew();
        var store = await TempStore.CreateAsync(seed);
        long seedMs = watch.ElapsedMilliseconds;
        seed = null!;
        ProjectRevision seeded = await store.Repository.GetRevisionAsync(default);
        Collect();

        watch.Restart();
        IgwProject healthy = await store.Repository.LoadRuntimeProjectAsync(default);
        long loadProjectMs = watch.ElapsedMilliseconds;
        long seededTags = healthy.Channels.Sum(c => c.Devices.Sum(d => d.Groups.Sum(g => g.Tags.Count)));
        if (seededTags != 1_000_000) throw new InvalidDataException("预置点数不正确：" + seededTags);

        var repositoryMs = new List<long>();
        long coldRepositoryMs = 0;
        for (int round = -1; round < options.Rounds; round++)
        {
            // round=-1：丢弃的预热请求（写入单独的 RepoWarm 设备）
            watch.Restart();
            ProjectBulkTagResult result = await store.Repository.BulkUpsertTagsAsync(
                Request("Bench", round < 0 ? "RepoWarm" : "RepoNew" + round, PointsPerRequest), null, default);
            if (round < 0) coldRepositoryMs = watch.ElapsedMilliseconds;
            else repositoryMs.Add(watch.ElapsedMilliseconds);
            EnsureCreated(result, PointsPerRequest);
        }

        // 协调器的健康工程需包含仓储层轮次写入的设备，否则后续投影会漏；重新加载一次（不计时）
        healthy = await store.Repository.LoadRuntimeProjectAsync(default);
        var coordinator = new ProjectConfigurationCoordinator(
            store.Repository, healthy,
            (_, _, _) => Task.CompletedTask, _ => { }, (_, _) => Task.CompletedTask);
        healthy = null!;
        Collect();
        var coordinatorMs = new List<long>();
        var loadDeviceMs = new List<long>();
        long coldCoordinatorMs = 0;
        for (int round = -1; round < options.Rounds; round++)
        {
            watch.Restart();
            ProjectBulkApplyResult result = await coordinator.ExecuteBulkAsync(
                Request("Bench", round < 0 ? "CoordWarm" : "CoordNew" + round, PointsPerRequest), null, default);
            long elapsed = watch.ElapsedMilliseconds;
            EnsureApplied(result, PointsPerRequest);
            long load = await TimeLoadDeviceAsync(store.Repository, result.Persisted.DeviceId!);
            if (round < 0) coldCoordinatorMs = elapsed;
            else { coordinatorMs.Add(elapsed); loadDeviceMs.Add(load); }
        }
        long databaseBytes = store.DatabaseBytes();
        await coordinator.DisposeAsync();
        await store.DisposeAsync();
        Collect();
        EmptyBaseline after = await MeasureEmptyBaselineAsync(options.Rounds);
        List<long> baselineRepository = [.. before.RepositoryMs, .. after.RepositoryMs];
        List<long> baselineCoordinator = [.. before.CoordinatorMs, .. after.CoordinatorMs];

        return new
        {
            scenario = "bulk-preseeded",
            seededTags,
            seedMs,
            loadProjectMs,
            seededRevision = seeded.Value,
            pointsPerRequest = PointsPerRequest,
            coldRepositoryMs,
            coldCoordinatorMs,
            repositoryMs,
            repositoryMedianMs = Median(repositoryMs),
            coordinatorMs,
            coordinatorMedianMs = Median(coordinatorMs),
            loadDeviceMs,
            emptyBaselineBefore = new { repositoryMs = before.RepositoryMs, coordinatorMs = before.CoordinatorMs },
            emptyBaselineAfter = new { repositoryMs = after.RepositoryMs, coordinatorMs = after.CoordinatorMs },
            emptyRepositoryMedianMs = Median(baselineRepository),
            emptyCoordinatorMedianMs = Median(baselineCoordinator),
            // 目标：与空工程相差 ±30%（0.70～1.30）
            repositoryRatio = Math.Round((double)Median(repositoryMs) / Median(baselineRepository), 2),
            coordinatorRatio = Math.Round((double)Median(coordinatorMs) / Median(baselineCoordinator), 2),
            databaseBytes,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        };
    }

    // ---------------- 场景 3 + 4：100 万点分 10 次请求（协调器层）+ 并发读者 ----------------

    private static async Task<object> TenRequestsAsync(StorageBenchmarkOptions options)
    {
        await using var store = await TempStore.CreateAsync();
        // 读者读取的「已有设备」：先注册 1 万点（不计时）
        ProjectBulkTagResult reader = await store.Repository.BulkUpsertTagsAsync(
            Request("Existing", "ReaderDev", 10_000), null, default);
        EnsureCreated(reader, 10_000);
        string[] readerGroups = [.. (await store.Repository.LoadDeviceAsync(reader.DeviceId!, default)).Device.Groups.Select(g => g.Id)];
        await using var coordinator = await NoOpCoordinatorAsync(store.Repository);
        // 丢弃的预热请求（写入单独的 Warm 设备，不计时；工程总点数因此多 10 万）
        var coldWatch = Stopwatch.StartNew();
        EnsureApplied(await coordinator.ExecuteBulkAsync(Request("Bench", "Warm", PointsPerRequest), null, default),
            PointsPerRequest);
        long coldRequestMs = coldWatch.ElapsedMilliseconds;
        Collect();

        using var stop = new CancellationTokenSource();
        var readerStats = new ReaderStats();
        Task readerTask = options.Readers
            ? Task.Run(() => PersistenceReaderLoopAsync(store.Repository, readerGroups, readerStats, stop.Token))
            : Task.CompletedTask;
        await Task.Delay(300); // 读者先跑一会建立空闲基线

        var perRequestMs = new List<long>();
        var requestWindows = new List<string>();
        TimeSpan gcBefore = GC.GetTotalPauseDuration();
        var total = Stopwatch.StartNew();
        for (int device = 0; device < 10; device++)
        {
            var watch = Stopwatch.StartNew();
            long startedAt = readerStats.Now;
            ProjectBulkApplyResult result = await coordinator.ExecuteBulkAsync(
                Request("Bench", "Dev" + device, PointsPerRequest), null, default);
            perRequestMs.Add(watch.ElapsedMilliseconds);
            requestWindows.Add($"{startedAt}-{readerStats.Now}");
            EnsureApplied(result, PointsPerRequest);
        }
        long totalMs = total.ElapsedMilliseconds;
        stop.Cancel();
        await readerTask;

        long totalTags = coordinator.HealthyProject.Channels.Sum(c => c.Devices.Sum(d => d.Groups.Sum(g => CountTags(g))));
        return new
        {
            scenario = "bulk-10x",
            requests = 10,
            pointsPerRequest = PointsPerRequest,
            coldRequestMs,
            totalMs,
            perRequestMs,
            requestWindows,
            gcPauseTotalMs = (long)(GC.GetTotalPauseDuration() - gcBefore).TotalMilliseconds,
            healthyProjectTags = totalTags,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            databaseBytes = store.DatabaseBytes(),
            reader = readerStats.Summary(),
        };
    }

    private static async Task PersistenceReaderLoopAsync(
        IProjectRepository repository, string[] groupIds, ReaderStats stats, CancellationToken stop)
    {
        var random = new Random(42);
        int iteration = 0;
        while (!stop.IsCancellationRequested)
        {
            var watch = Stopwatch.StartNew();
            string kind = "tags-page";
            try
            {
                if (iteration++ % 10 == 9)
                {
                    // 每 10 次做一次读快照流式导出（取前 1000 个点）
                    kind = "read-snapshot";
                    int read = 0;
                    await using IProjectReadSnapshot snapshot = await repository.OpenReadSnapshotAsync(default);
                    await foreach (ProjectEntityDocument _ in snapshot.StreamEntitiesAsync(ProjectNodeKind.Tag, null, default))
                        if (++read >= 1000) break;
                }
                else
                {
                    ProjectPage<ProjectTagRow> page = await repository.GetTagsAsync(
                        new ProjectTagQuery(groupIds[random.Next(groupIds.Length)], null, random.Next(0, 800), 200), default);
                    if (page.Items.Count != 200 || page.Total != TagsPerGroup)
                        throw new InvalidDataException($"分页结果不正确：total={page.Total} items={page.Items.Count}");
                }
                stats.Success(kind, watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stats.Failure(kind, watch.Elapsed.TotalMilliseconds, ex);
            }
            try { await Task.Delay(10, stop); } catch (OperationCanceledException) { }
        }
    }

    // ---------------- 场景 3 + 4（宿主层）：GatewayHost + Simulator + DcsTagOperations 读者 ----------------

    private static async Task<object> HostTenRequestsAsync(StorageBenchmarkOptions options)
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-bulk-host-benchmark", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        GatewayHost? host = null;
        try
        {
            var initial = new IgwProject
            {
                Name = "bulk-host-benchmark",
                OpcUa = options.OpcUa
                    ? new OpcUaSettings { Enabled = true, Port = FreeTcpPort(), AllowAnonymous = true, AllowEncryption = false }
                    : new OpcUaSettings { Enabled = false },
                Channels =
                [
                    new ChannelDef
                    {
                        Name = "Existing",
                        DriverId = SimulatorDriverFactory.Id,
                        Devices =
                        [
                            new DeviceDef
                            {
                                Name = "ReaderDev",
                                Groups =
                                [
                                    new TagGroupDef
                                    {
                                        Name = "G",
                                        Tags = [.. Enumerable.Range(0, 1000).Select(i => new TagDef
                                        {
                                            Name = "T" + i, Address = "RAMP", DataType = IgwDataType.Int32,
                                        })],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
            string legacy = Path.Combine(dir, "project.igwproj");
            ProjectStore.Save(initial, legacy);
            ProjectBootstrapResult storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
                new ProjectStoragePaths(Path.Combine(dir, "project.db"), legacy, Path.Combine(dir, "igw.masterkey")),
                default);
            IgwProject project = await storage.Repository.LoadRuntimeProjectAsync(default);
            ProjectRevision revision = await storage.Repository.GetRevisionAsync(default);
            var registry = new DriverRegistry();
            registry.Register(new SimulatorDriverFactory());
            host = new GatewayHost(project, storage.Repository, revision, registry, NullLoggerFactory.Instance, dir);
            await host.StartAsync();

            var operations = new DcsTagOperations(new GatewayDcsTagRuntime(host));
            string[] readTags = [.. Enumerable.Range(0, 100).Select(i => "Existing.ReaderDev.G.T" + (i * 10))];
            // 预热：别名解析器首建 + 空闲点首读
            await operations.PrepareReadAsync(readTags, null, default);
            var warm = operations.ReadAsList(readTags, null);
            if (warm.StatusCode != 200 || !warm.Body.IsSuccess)
                throw new InvalidDataException("预热读取失败：" + warm.Body.ErrorMessage);

            using var stop = new CancellationTokenSource();
            var stats = new ReaderStats();
            Task readerTask = Task.Run(() => HostReaderLoopAsync(operations, readTags, stats, stop.Token));
            await Task.Delay(300);

            // 丢弃的预热请求（写入单独的 Warm 设备）
            HostRequestTiming cold = await TimedHostRequestAsync(host, "Warm");

            var perRequestMs = new List<long>();
            var serverMs = new List<long>();
            var parseMs = new List<long>();
            var validateMs = new List<long>();
            var applyMs = new List<long>();
            var clientSerializeMs = new List<long>();
            var physicalReadableMs = new List<long>();
            var timings = new List<string>();
            var total = Stopwatch.StartNew();
            for (int device = 0; device < 10; device++)
            {
                HostRequestTiming timing = await TimedHostRequestAsync(host, "Dev" + device);
                clientSerializeMs.Add(timing.SerializeMs);
                parseMs.Add(timing.ParseMs);
                validateMs.Add(timing.ValidateMs);
                applyMs.Add(timing.ApplyMs);
                serverMs.Add(timing.ServerMs);
                perRequestMs.Add(timing.SerializeMs + timing.ServerMs);
                timings.Add(timing.Timing);
                // 请求返回后，新设备的点按物理点名经 DCS 读取成功所需时间。物理点名由解析器按 Hub 定义直接放行，
                // 不等后台别名解析器重建；按 DCS 逻辑设备别名读取的一致性窗口不在此测量（见 release notes）。
                // 不计入总耗时（总耗时只算 10 次请求本身）
                total.Stop();
                physicalReadableMs.Add(await WaitDcsReadableAsync(operations, $"Bench.Dev{device}.G0.T0"));
                total.Start();
            }
            long totalMs = total.ElapsedMilliseconds;
            // 批量结束后再读 1 秒，覆盖 DCS 别名解析器后台重建窗口
            await Task.Delay(1000);
            stop.Cancel();
            await readerTask;

            bool newTagReadable = host.Hub.TryGetDef("Bench.Dev9.G99.T99999", out _);

            // 界面局部保存(Batch → 内存合并 → 与批量注册同一个局部应用入口)：在约 100 万点的工程上改 1 个点的扫描周期
            // (运行签名变化 → 该 10 万点设备重启)。首个编辑含合并用的「点位 → 设备」位置索引首建。
            var uiEditMs = new List<long>();
            var uiEditTimings = new List<string>();
            for (int edit = 0; edit < 4; edit++)
            {
                DeviceDef target = host.Project.Channels.Single(c => c.Name == "Bench").Devices.Single(d => d.Name == "Dev5");
                TagGroupDef targetGroup = target.Groups[0];
                TagDef edited = JsonSerializer.Deserialize<TagDef>(
                    JsonSerializer.Serialize(targetGroup.Tags[0], ProjectStore.Options), ProjectStore.Options)!;
                edited.ScanRateMs = edit % 2 == 0 ? 250 : 500;
                var batch = new ProjectMutation(ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null,
                    JsonSerializer.Serialize(new[]
                    {
                        new ProjectMutation(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Tag, [], targetGroup.Id,
                            JsonSerializer.Serialize(edited, ProjectStore.Options)),
                    }, ProjectStore.Options));
                var editWatch = Stopwatch.StartNew();
                ProjectUpdateResult edit_ = await host.ApplyProjectMutationAsync(batch, host.ProjectRevision, default);
                uiEditMs.Add(editWatch.ElapsedMilliseconds);
                if (!edit_.Applied || host.ConfigurationDegraded)
                    throw new InvalidDataException("界面单点保存失败：" + edit_.Error);
                uiEditTimings.Add(host.LastApplyTiming ?? "");
            }

            return new
            {
                scenario = "bulk-host",
                requests = 10,
                pointsPerRequest = PointsPerRequest,
                // serverMs = 请求体反序列化（ProjectStore.Options，内存流）+ 请求级校验 + ApplyBulkTagsAsync，
                // 即接口端点内的服务端耗时（不含 HTTP 传输）；perRequestMs 另加客户端序列化
                cold = new { cold.SerializeMs, cold.ParseMs, cold.ValidateMs, cold.ApplyMs, cold.ServerMs },
                totalMs,
                perRequestMs,
                serverMs,
                serverMedianMs = Median(serverMs),
                clientSerializeMs,
                parseMs,
                validateMs,
                applyMs,
                timings,
                physicalReadableMs,
                opcUa = options.OpcUa,
                newTagInHub = newTagReadable,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                reader = stats.Summary(),
                uiSingleTagEditMs = uiEditMs,
                uiSingleTagEditTimings = uiEditTimings,
            };
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed record HostRequestTiming(long SerializeMs, long ParseMs, long ValidateMs, long ApplyMs, string Timing)
    {
        public long ServerMs => ParseMs + ValidateMs + ApplyMs;
    }

    /// <summary>
    /// 一次宿主层请求，按接口端点的顺序计时：客户端序列化 → 服务端 DeserializeAsync(ProjectStore.Options) →
    /// ProjectBulkTagValidator.Validate → GatewayHost.ApplyBulkTagsAsync。请求体放内存流，不含 HTTP 传输。
    /// </summary>
    // ---------------- 场景：大设备小增量(HSJM 端到端 T9：32.4 万点设备改 3.7k 点约 10.8 秒) ----------------

    /// <summary>
    /// 一台设备先注册 32.4 万点(324 组 × 1000)，再发 --rounds 次“改 3000 个已有点的说明 + 新增 700 点”的请求，
    /// 走 GatewayHost.ApplyBulkTagsAsync(含写库、读回设备、设备重启与 OPC UA 关闭)。
    /// </summary>
    private static async Task<object> HostIncrementAsync(StorageBenchmarkOptions options)
    {
        const int DevicePoints = 324_000;
        string dir = Path.Combine(Path.GetTempPath(), "igw-bulk-increment-benchmark", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        GatewayHost? host = null;
        try
        {
            var initial = new IgwProject { Name = "bulk-increment-benchmark", OpcUa = new OpcUaSettings { Enabled = false } };
            string legacy = Path.Combine(dir, "project.igwproj");
            ProjectStore.Save(initial, legacy);
            ProjectBootstrapResult storage = await new ProjectDatabaseBootstrapper().OpenOrMigrateAsync(
                new ProjectStoragePaths(Path.Combine(dir, "project.db"), legacy, Path.Combine(dir, "igw.masterkey")),
                default);
            IgwProject project = await storage.Repository.LoadRuntimeProjectAsync(default);
            ProjectRevision revision = await storage.Repository.GetRevisionAsync(default);
            var registry = new DriverRegistry();
            registry.Register(new SimulatorDriverFactory());
            host = new GatewayHost(project, storage.Repository, revision, registry, NullLoggerFactory.Instance, dir);
            await host.StartAsync();

            var seedWatch = Stopwatch.StartNew();
            for (int offset = 0; offset < DevicePoints; offset += PointsPerRequest)
            {
                var seed = RangeRequest("Big", offset, Math.Min(PointsPerRequest, DevicePoints - offset), "seed");
                ProjectBulkTagResponse seeded = await host.ApplyBulkTagsAsync(seed, null, default);
                if (!seeded.Applied) throw new InvalidDataException($"预置失败：{seeded.ErrorCode} {seeded.Error}");
            }
            long seedMs = seedWatch.ElapsedMilliseconds;
            Collect();

            var applyMs = new List<long>();
            var timings = new List<string>();
            for (int round = -1; round < options.Rounds; round++)
            {
                // 重新注册一个 DB 的典型形态：改前 3 组的 3000 个已有点 + 在第 4 组新增 700 点(改动集中在少数组)
                var request = RangeRequest("Big", 0, 0, "round " + round);
                request.Items = [.. Enumerable.Range(0, 3000).Select(i => Item(i, "round " + round)),
                    .. Enumerable.Range(0, 700).Select(i => NewItem(3, (round + 1) * 700 + i))];
                var watch = Stopwatch.StartNew();
                ProjectBulkTagResponse response = await host.ApplyBulkTagsAsync(request, null, default);
                long elapsed = watch.ElapsedMilliseconds;
                if (!response.Applied) throw new InvalidDataException($"增量请求失败：{response.ErrorCode} {response.Error}");
                if (round < 0) continue;   // 丢弃预热轮
                applyMs.Add(elapsed);
                timings.Add(response.Timing ?? "");
            }
            return new
            {
                scenario = "bulk-increment",
                devicePoints = DevicePoints,
                changedPoints = 3700,
                seedMs,
                applyMs,
                applyMedianMs = Median(applyMs),
                timings,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            };
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        static ProjectBulkTagItem Item(int i, string description) => new()
        {
            GroupPath = ["G" + (i / TagsPerGroup)],
            Name = "T" + i,
            Address = "RAMP",
            DataType = IgwDataType.Int32,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
            Description = description,
        };

        static ProjectBulkTagItem NewItem(int group, int i) => new()
        {
            GroupPath = ["G" + group],
            Name = "N" + i,
            Address = "RAMP",
            DataType = IgwDataType.Int32,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
            Description = "new",
        };

        static ProjectBulkTagRequest RangeRequest(string device, int offset, int count, string description) => new()
        {
            Channel = new ProjectBulkChannelSpec("Bench", SimulatorDriverFactory.Id, null),
            Device = new ProjectBulkDeviceSpec(device, "", null),
            Items = [.. Enumerable.Range(offset, count).Select(i => Item(i, description))],
        };
    }

    private static async Task<HostRequestTiming> TimedHostRequestAsync(GatewayHost host, string deviceName)
    {
        var watch = Stopwatch.StartNew();
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            Request("Bench", deviceName, PointsPerRequest, driverId: SimulatorDriverFactory.Id, address: "RAMP"),
            ProjectStore.Options);
        long serializeMs = watch.ElapsedMilliseconds;

        watch.Restart();
        ProjectBulkTagRequest request;
        await using (var stream = new MemoryStream(body, writable: false))
            request = await JsonSerializer.DeserializeAsync<ProjectBulkTagRequest>(stream, ProjectStore.Options)
                ?? throw new InvalidDataException("请求体为空");
        long parseMs = watch.ElapsedMilliseconds;

        watch.Restart();
        var errors = ProjectBulkTagValidator.Validate(request);
        long validateMs = watch.ElapsedMilliseconds;
        if (errors.Count > 0) throw new InvalidDataException("请求校验失败：" + errors[0].Reason);

        watch.Restart();
        ProjectBulkTagResponse response = await host.ApplyBulkTagsAsync(request, null, default);
        long applyMs = watch.ElapsedMilliseconds;
        if (!response.Applied || response.Created != PointsPerRequest)
            throw new InvalidDataException($"设备 {deviceName} 请求失败：{response.ErrorCode} {response.Error}");
        return new HostRequestTiming(serializeMs, parseMs, validateMs, applyMs, response.Timing);
    }

    private static async Task<long> WaitDcsReadableAsync(DcsTagOperations operations, string tag)
    {
        var watch = Stopwatch.StartNew();
        string[] tags = [tag];
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await operations.PrepareReadAsync(tags, null, default);
            var outcome = operations.ReadAsList(tags, null);
            if (outcome.StatusCode == 200 && outcome.Body.IsSuccess) return watch.ElapsedMilliseconds;
            await Task.Delay(5);
        }
        throw new TimeoutException("新点 60 秒内仍无法经 DCS 读取：" + tag);
    }

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task HostReaderLoopAsync(
        DcsTagOperations operations, string[] tags, ReaderStats stats, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                // 与 DCS 读取端点一致：先补读未在采集的点，再读缓存
                await operations.PrepareReadAsync(tags, null, default);
                var outcome = operations.ReadAsList(tags, null);
                if (outcome.StatusCode == 503) stats.Failure("dcs-503", watch.Elapsed.TotalMilliseconds, null, outcome.Body.ErrorMessage);
                else if (outcome.StatusCode != 200 || !outcome.Body.IsSuccess)
                    stats.Failure("dcs-failed", watch.Elapsed.TotalMilliseconds, null,
                        $"{outcome.StatusCode} {outcome.Body.ErrorMessage}");
                else stats.Success("dcs-read", watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stats.Failure("dcs-exception", watch.Elapsed.TotalMilliseconds, ex);
            }
            try { await Task.Delay(10, stop); } catch (OperationCanceledException) { }
        }
    }

    // ---------------- 公共 ----------------

    private static ProjectBulkTagRequest Request(
        string channel, string device, int count, string driverId = "Simulator", string? address = null) => new()
    {
        Channel = new ProjectBulkChannelSpec(channel, driverId, null),
        Device = new ProjectBulkDeviceSpec(device, "", null),
        Items = [.. Enumerable.Range(0, count).Select(i => new ProjectBulkTagItem
        {
            GroupPath = ["G" + (i / TagsPerGroup)],
            Name = "T" + i,
            Address = address ?? "R" + i,
            DataType = IgwDataType.Int32,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
            Description = "bulk benchmark",
        })],
    };

    private static IgwProject SeedProject(int devices, int tagsPerDevice)
    {
        var channel = new ChannelDef { Id = Guid.NewGuid().ToString("N"), Name = "Seed", DriverId = "Simulator" };
        for (int d = 0; d < devices; d++)
        {
            var device = new DeviceDef { Id = Guid.NewGuid().ToString("N"), Name = "Seed" + d };
            for (int g = 0; g < tagsPerDevice / TagsPerGroup; g++)
            {
                var group = new TagGroupDef { Id = Guid.NewGuid().ToString("N"), Name = "G" + g };
                for (int t = 0; t < TagsPerGroup; t++)
                {
                    int index = g * TagsPerGroup + t;
                    group.Tags.Add(new TagDef
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "T" + index,
                        Address = "R" + index,
                        DataType = IgwDataType.Int32,
                        Access = TagAccess.ReadWrite,
                        ScanRateMs = 100,
                    });
                }
                device.Groups.Add(group);
            }
            channel.Devices.Add(device);
        }
        return new IgwProject { Name = "BulkSeed", Channels = [channel] };
    }

    private static async Task<ProjectConfigurationCoordinator> NoOpCoordinatorAsync(IProjectRepository repository)
    {
        IgwProject healthy = await repository.LoadRuntimeProjectAsync(default);
        return new ProjectConfigurationCoordinator(
            repository, healthy,
            (_, _, _) => Task.CompletedTask,
            _ => { },
            (_, _) => Task.CompletedTask);
    }

    private static async Task<long> TimeLoadDeviceAsync(IProjectRepository repository, string deviceId)
    {
        var watch = Stopwatch.StartNew();
        ProjectDeviceSnapshot snapshot = await repository.LoadDeviceAsync(deviceId, default);
        long ms = watch.ElapsedMilliseconds;
        if (snapshot.Device.Groups.Sum(CountTags) != PointsPerRequest)
            throw new InvalidDataException("单设备读回点数不正确。");
        return ms;
    }

    private static int CountTags(TagGroupDef group) => group.Tags.Count + group.SubGroups.Sum(CountTags);

    private static void EnsureCreated(ProjectBulkTagResult result, int expected)
    {
        if (!result.Applied || result.Created != expected)
            throw new InvalidDataException($"批量写入失败：{result.ErrorCode} {result.Error} created={result.Created}");
    }

    private static void EnsureApplied(ProjectBulkApplyResult result, int expected)
    {
        EnsureCreated(result.Persisted, expected);
        if (!result.RuntimeApplied) throw new InvalidDataException("运行态应用失败：" + result.ErrorMessage);
    }

    private static long Median(List<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static void Collect() => GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    /// <summary>临时工程库（每个实例一个独立目录，释放时删除）。</summary>
    private sealed class TempStore : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _databasePath;
        public SqliteProjectRepository Repository { get; }

        private TempStore(string directory, string databasePath, SqliteProjectRepository repository)
        {
            _directory = directory;
            _databasePath = databasePath;
            Repository = repository;
        }

        /// <summary>新建库并以 <paramref name="initial"/>（缺省为空工程）初始化修订号，与仓储单元测试的夹具一致。</summary>
        public static async Task<TempStore> CreateAsync(IgwProject? initial = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-bulk-benchmark", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "project.db");
            var repository = new SqliteProjectRepository(
                new ProjectDatabase(databasePath),
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(
                initial ?? new IgwProject { Name = "BulkBenchmark" }, new ProjectRevision(0), default);
            // IGW_BENCH_CAPTURE=1：与选择外部数据库时一样挂上复制触发器，度量待同步队列对批量写入的开销
            // IGW_BENCH_CAPTURE=2：只建队列表不挂触发器(拆分开销用)
            string? capture = Environment.GetEnvironmentVariable("IGW_BENCH_CAPTURE");
            if (capture is "1" or "2")
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
                connection.Open();
                IGW.Persistence.Replication.SqliteChangeCapture.EnsureInfrastructure(connection);
                if (capture == "1")
                    IGW.Persistence.Replication.SqliteChangeCapture.EnsureTriggers(connection,
                        IGW.Persistence.Replication.SqliteChangeCapture.ReadTables(connection));
            }
            return new TempStore(directory, databasePath, repository);
        }

        public long DatabaseBytes() =>
            new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" }
                .Where(File.Exists).Sum(path => new FileInfo(path).Length);

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    /// <summary>并发读者统计：成功/失败次数、按类型的延迟分位与失败原因。</summary>
    private sealed class ReaderStats
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _latencies = new();
        private readonly ConcurrentDictionary<string, int> _failures = new();
        private readonly ConcurrentQueue<object> _slow = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _ok;
        private int _failed;

        /// <summary>自读者启动起的毫秒数，用于把慢读与写请求时间窗对齐。</summary>
        public long Now => _clock.ElapsedMilliseconds;

        public void Success(string kind, double ms)
        {
            Interlocked.Increment(ref _ok);
            _latencies.GetOrAdd(kind, _ => []).Add(ms);
            // 记录前 30 次慢读（>200ms）的起止时刻，便于判断阻塞发生在写事务/提交/GC 的哪一段
            if (ms > 200 && _slow.Count < 30)
                _slow.Enqueue(new { kind, startMs = Now - (long)ms, ms = Math.Round(ms, 1), gen2Total = GC.CollectionCount(2) });
        }

        public void Failure(string kind, double ms, Exception? ex, string? message = null)
        {
            Interlocked.Increment(ref _failed);
            _latencies.GetOrAdd(kind + "(failed)", _ => []).Add(ms);
            string reason = kind + ": " + (ex is null ? message : ex.GetType().Name + " " + ex.Message);
            if (reason.Length > 200) reason = reason[..200];
            _failures.AddOrUpdate(reason, 1, (_, n) => n + 1);
        }

        public object Summary() => new
        {
            succeeded = _ok,
            failed = _failed,
            failures = _failures.ToDictionary(p => p.Key, p => p.Value),
            slowReads = _slow.ToArray(),
            latency = _latencies.ToDictionary(p => p.Key, p =>
            {
                var sorted = p.Value.OrderBy(v => v).ToList();
                double At(double q) => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * q) - 1, 0, sorted.Count - 1)], 1);
                return new { count = sorted.Count, p50Ms = At(0.5), p99Ms = At(0.99), maxMs = Math.Round(sorted[^1], 1) };
            }),
        };
    }
}
