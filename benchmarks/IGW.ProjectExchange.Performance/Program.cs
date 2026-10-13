using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator.Kep;
using IGW.Persistence.Sqlite;
using IGW.ProjectExchange;

int points = 1_000_000;
string? reportPath = null;
for (int index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("参数格式应为 --points N --output report.json。");
    if (args[index] == "--points") points = int.Parse(args[index + 1]);
    else if (args[index] == "--output") reportPath = Path.GetFullPath(args[index + 1]);
    else throw new ArgumentException("未知参数：" + args[index]);
}
if (points is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(points));

string root = Path.Combine(Path.GetTempPath(), "igw-exchange-benchmark", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string exportPath = Path.Combine(root, "project.json");
SqliteProjectRepository source = await OpenAsync(Path.Combine(root, "source"));
SqliteProjectRepository target = await OpenAsync(Path.Combine(root, "target"));
var stopwatch = Stopwatch.StartNew();
long seedMs = 0, exportMs = 0, inspectMs = 0, importMs = 0;
try
{
    IgwProject project = BuildProject(points);
    ProjectRevision sourceRevision = await source.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
    seedMs = stopwatch.ElapsedMilliseconds;
    project = null!;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    var codecs = new IKepDriverCodec[] { new SimulatorKepDriverCodec() };
    await target.ReplaceFromSnapshotAsync(new IgwProject { Name = "Initial" }, new ProjectRevision(0), default);
    var sessions = new ProjectImportSessionStore(Path.Combine(root, "sessions"));
    await using var sourceExchange = new ProjectJsonImporter(source, codecs, sessions);
    stopwatch.Restart();
    await using (FileStream output = new(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                     256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await sourceExchange.ExportAsync(output, new ProjectExportOptions(true, "benchmark-password"), default);
    exportMs = stopwatch.ElapsedMilliseconds;

    var targetSessions = new ProjectImportSessionStore(Path.Combine(root, "target-sessions"));
    await using var targetExchange = new ProjectJsonImporter(target, codecs, targetSessions);
    stopwatch.Restart();
    ProjectImportPreview preview;
    await using (FileStream input = File.OpenRead(exportPath))
        preview = await targetExchange.InspectAsync(input, default);
    inspectMs = stopwatch.ElapsedMilliseconds;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    stopwatch.Restart();
    ProjectImportResult imported;
    await using (FileStream input = File.OpenRead(exportPath))
        imported = await targetExchange.ImportAsync(input,
            new ProjectImportOptions(preview.Token, preview.FileHash, "benchmark-password"), default);
    importMs = stopwatch.ElapsedMilliseconds;
    if (!imported.Applied) throw new InvalidDataException("import_failed:" + imported.ErrorCode);

    ProjectPage<ProjectTagRow> page = await target.GetTagsAsync(new ProjectTagQuery(null, null, 0, 1), default);
    if (page.Total != points) throw new InvalidDataException($"tag_count_mismatch:{page.Total}/{points}");
    string sourceHash = await SnapshotHashAsync(source);
    string targetHash = await SnapshotHashAsync(target);
    if (sourceHash != targetHash) throw new InvalidDataException("snapshot_hash_mismatch");
    bool secretRestored = false;
    await using (IProjectReadSnapshot snapshot = await target.OpenReadSnapshotAsync(default))
        await foreach (ProjectPortableSecret secret in snapshot.StreamPortableSecretsAsync(default))
            secretRestored |= secret.Value == "million-point-portable-secret";
    if (!secretRestored) throw new InvalidDataException("portable_secret_missing");

    var report = new
    {
        points,
        seedMs,
        exportMs,
        inspectMs,
        importMs,
        jsonBytes = new FileInfo(exportPath).Length,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        sourceRevision = sourceRevision.Value,
        importedRevision = imported.Revision.Value,
        stableSnapshotHash = sourceHash,
        portableSecretRestored = secretRestored,
    };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (reportPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, json);
    }
    Console.WriteLine(json);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.ToString() }));
    return 1;
}
finally
{
    await source.DisposeAsync();
    await target.DisposeAsync();
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task<SqliteProjectRepository> OpenAsync(string directory)
{
    Directory.CreateDirectory(directory);
    var repository = new SqliteProjectRepository(
        new ProjectDatabase(Path.Combine(directory, "project.db")),
        new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
    await repository.InitializeAsync(default);
    return repository;
}

static IgwProject BuildProject(int count)
{
    var group = new TagGroupDef { Id = "group-points", Name = "Points", Description = "百万点数据块" };
    for (int index = 0; index < count; index++)
        group.Tags.Add(new TagDef
        {
            Id = $"tag-{index:D7}",
            Name = $"Value{index:D7}",
            Address = $"DB1.DBD{index * 4}",
            DataType = IgwDataType.Int32,
            Access = TagAccess.ReadWrite,
            ScanRateMs = 100,
        });
    return new IgwProject
    {
        Name = $"ExchangeBenchmark-{count}",
        MqttPublish = new MqttPublishSettings { Password = "million-point-portable-secret" },
        Channels =
        [
            new ChannelDef
            {
                Id = "channel-benchmark", Name = "Factory", DriverId = "Simulator",
                Devices = [new DeviceDef { Id = "device-benchmark", Name = "PLC", Groups = [group] }],
            },
        ],
    };
}

static async Task<string> SnapshotHashAsync(IProjectRepository repository)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    await using IProjectReadSnapshot snapshot = await repository.OpenReadSnapshotAsync(default);
    foreach (ProjectNodeKind kind in new[] { ProjectNodeKind.Channel, ProjectNodeKind.Device, ProjectNodeKind.Group, ProjectNodeKind.Tag })
        await foreach (ProjectEntityDocument entity in snapshot.StreamEntitiesAsync(kind, null, default))
            Append(hash, $"{kind}|{entity.Entity.Id}|{entity.ParentId}|{entity.SortOrder}|{entity.DocumentJson}\n");
    foreach (ProjectDocumentKind kind in Enum.GetValues<ProjectDocumentKind>())
        await foreach (ProjectDocument document in snapshot.StreamDocumentsAsync(kind, default))
            Append(hash, $"{kind}|{document.Key}|{document.DocumentJson}\n");
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
