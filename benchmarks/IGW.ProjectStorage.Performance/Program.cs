using System.Diagnostics;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Persistence.Sqlite;
using IGW.ProjectStorage.Performance;

StorageBenchmarkOptions options;
try { options = StorageBenchmarkOptions.Parse(args); }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
if (options.Scenario != "storage") return await BulkTagBenchmarks.RunAsync(options);

string databasePath = options.DatabasePath ?? Path.Combine(
    Path.GetTempPath(), "igw-project-storage-benchmark", Guid.NewGuid().ToString("N"), "project.db");
bool temporaryDatabase = options.DatabasePath is null;
databasePath = Path.GetFullPath(databasePath);
if (File.Exists(databasePath))
{
    Console.Error.WriteLine("基准数据库已存在，为避免覆盖请指定一个不存在的 --database 路径：" + databasePath);
    return 2;
}
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

var repository = new SqliteProjectRepository(
    new ProjectDatabase(databasePath),
    new ProjectSecretVault(new InstallationSecretProtector(
        Path.Combine(Path.GetDirectoryName(databasePath)!, "igw.masterkey"))));
try
{
    await repository.InitializeAsync(default);
    IgwProject project = BuildProject(options.Points);
    var stopwatch = Stopwatch.StartNew();
    ProjectRevision imported = await repository.ReplaceFromSnapshotAsync(
        project, new ProjectRevision(0), default);
    long importMs = NonZeroMilliseconds(stopwatch.Elapsed);
    project = null!;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    var pageTimes = new List<double>(20);
    for (int sample = 0; sample < 20; sample++)
    {
        int offset = options.Points <= 200
            ? 0
            : (int)((long)sample * (options.Points - 200) / 19);
        stopwatch.Restart();
        ProjectPage<ProjectTagRow> page = await repository.GetTagsAsync(
            new ProjectTagQuery("group-points", null, offset, Math.Min(200, options.Points)), default);
        pageTimes.Add(Math.Max(0.001, stopwatch.Elapsed.TotalMilliseconds));
        if (page.Total != options.Points || page.Items.Count > 200)
            throw new InvalidDataException($"分页计数不正确：total={page.Total}，items={page.Items.Count}。");
    }

    string updateId = $"tag-{options.Points / 2:D7}";
    string document = await repository.GetEntityDocumentAsync(
        new ProjectNodeRef(ProjectNodeKind.Tag, updateId), default);
    TagDef updatedTag = JsonSerializer.Deserialize<TagDef>(document, ProjectStore.Options)
        ?? throw new InvalidDataException("无法解析单点更新文档。");
    updatedTag.Description = "benchmark-update";
    stopwatch.Restart();
    ProjectMutationResult update = await repository.ExecuteAsync(
        new ProjectMutation(
            ProjectMutationKind.UpsertEntity,
            ProjectNodeKind.Tag,
            [updatedTag.Id],
            "group-points",
            JsonSerializer.Serialize(updatedTag, ProjectStore.Options)),
        imported,
        default);
    long singleUpdateMs = NonZeroMilliseconds(stopwatch.Elapsed);
    if (!update.Applied || update.Revision.Value != imported.Value + 1)
        throw new InvalidDataException("单点更新或修订号不正确：" + update.Error);

    stopwatch.Restart();
    long exported = 0;
    await using (IProjectReadSnapshot snapshot = await repository.OpenReadSnapshotAsync(default))
    {
        await foreach (ProjectEntityDocument _ in snapshot.StreamEntitiesAsync(
                           ProjectNodeKind.Tag, null, default))
            exported++;
        if (snapshot.Revision != update.Revision)
            throw new InvalidDataException("导出快照修订号不正确。");
    }
    long exportPreparationMs = NonZeroMilliseconds(stopwatch.Elapsed);
    if (exported != options.Points)
        throw new InvalidDataException($"导出快照点数不正确：{exported} != {options.Points}。");

    pageTimes.Sort();
    long databaseBytes = DatabaseBytes(databasePath);
    var report = new StorageBenchmarkReport(
        options.Points,
        importMs,
        Percentile(pageTimes, 0.50),
        Percentile(pageTimes, 0.95),
        singleUpdateMs,
        exportPreparationMs,
        Process.GetCurrentProcess().PeakWorkingSet64,
        databaseBytes,
        update.Revision.Value);
    if (databaseBytes <= 0) throw new InvalidDataException("数据库文件大小为零。");

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
finally
{
    await repository.DisposeAsync();
    if (temporaryDatabase)
    {
        string? directory = Path.GetDirectoryName(databasePath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static IgwProject BuildProject(int points)
{
    var group = new TagGroupDef { Id = "group-points", Name = "Points" };
    for (int index = 0; index < points; index++)
    {
        group.Tags.Add(new TagDef
        {
            Id = $"tag-{index:D7}",
            Name = $"Value{index:D7}",
            Address = $"DB1.DBD{index * 4}",
            DataType = IgwDataType.Int32,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
        });
    }
    return new IgwProject
    {
        Name = $"StorageBenchmark-{points}",
        Channels =
        [
            new ChannelDef
            {
                Id = "channel-benchmark",
                Name = "Factory",
                DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "device-benchmark",
                        Name = "PLC",
                        Groups = [group],
                    },
                ],
            },
        ],
    };
}

static long NonZeroMilliseconds(TimeSpan elapsed) => Math.Max(1, (long)Math.Ceiling(elapsed.TotalMilliseconds));

static double Percentile(IReadOnlyList<double> sorted, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
    return Math.Round(sorted[index], 3);
}

static long DatabaseBytes(string databasePath) =>
    new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
        .Where(File.Exists)
        .Sum(path => new FileInfo(path).Length);

internal sealed record StorageBenchmarkOptions(
    int Points,
    string? DatabasePath,
    string? OutputPath,
    string Scenario = "storage",
    int Rounds = 3,
    bool Readers = true,
    bool OpcUa = false)
{
    public static StorageBenchmarkOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("参数格式应为 --name value。");
            values[args[index][2..]] = args[++index];
        }
        int points = 1_000_000;
        if (values.TryGetValue("points", out string? raw)
            && (!int.TryParse(raw, out points) || points is < 1 or > 2_000_000))
            throw new ArgumentOutOfRangeException(nameof(args), "--points 必须在 1..2000000 之间。");
        // --scenario：storage（默认，原单设备百万点场景）| bulk-empty | bulk-preseeded | bulk-10x | bulk-host
        string scenario = values.GetValueOrDefault("scenario") ?? "storage";
        string[] scenarios = ["storage", "bulk-empty", "bulk-preseeded", "bulk-10x", "bulk-host", "bulk-increment"];
        if (!scenarios.Contains(scenario, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(args), "--scenario 必须是 " + string.Join(" / ", scenarios) + "。");
        int rounds = 3;
        if (values.TryGetValue("rounds", out string? rawRounds)
            && (!int.TryParse(rawRounds, out rounds) || rounds is < 1 or > 20))
            throw new ArgumentOutOfRangeException(nameof(args), "--rounds 必须在 1..20 之间。");
        bool readers = true;
        if (values.TryGetValue("readers", out string? rawReaders) && !bool.TryParse(rawReaders, out readers))
            throw new ArgumentOutOfRangeException(nameof(args), "--readers 必须是 true 或 false。");
        bool opcUa = false;
        if (values.TryGetValue("opcua", out string? rawOpcUa) && !bool.TryParse(rawOpcUa, out opcUa))
            throw new ArgumentOutOfRangeException(nameof(args), "--opcua 必须是 true 或 false。");
        return new StorageBenchmarkOptions(
            points,
            values.GetValueOrDefault("database"),
            values.GetValueOrDefault("output"),
            scenario,
            rounds,
            readers,
            // --opcua true：bulk-host 场景启用 OPC UA 服务端（随机空闲端口），计入设备子树替换耗时
            opcUa);
    }
}

internal sealed record StorageBenchmarkReport(
    int Points,
    long ImportMs,
    double PageP50Ms,
    double PageP95Ms,
    long SingleUpdateMs,
    long ExportPreparationMs,
    long PeakWorkingSetBytes,
    long DatabaseBytes,
    long Revision);
