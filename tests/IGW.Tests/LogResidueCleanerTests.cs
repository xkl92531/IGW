using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 日志中心「一键清理」：删掉网关已经不再写入的日志残留，不停服务。
/// 现场日志目录已改到其它盘，但旧版本的报文一直写在 C:\ProgramData\IGW\log-payloads，堆了几百万个文件，
/// 资源管理器右键删除直接卡死。当前日志、报文、工程配置一律不能碰。
/// </summary>
public sealed class LogResidueCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igw-cleanup", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task One_click_cleanup_removes_only_residues_and_keeps_current_logs_and_configuration()
    {
        string data = Path.Combine(_root, "ProgramData", "IGW");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "project.db"), "工程");
        string legacy = Fill(Path.Combine(data, "log-payloads"), 50);
        string oldStorage = Path.Combine(data, "observability");
        string oldRoot = Fill(LogSegmentStore.RootFor(oldStorage), 20);
        File.WriteAllText(Path.Combine(oldStorage, "用户自己的文件.txt"), "不能删");
        string parked = Fill(Path.Combine(_root, "ProgramData", "IGW-大目录-20260924-120000"), 10);
        string currentStorage = Path.Combine(_root, "D", "logs");
        var settings = ObservabilitySettings.Initialize(data, oldStorage, 5088).WithStorageDirectory(currentStorage);
        settings.Persist(data);
        await using var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays);
        var payloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(currentStorage));
        LogPayloadReference current = await payloads.SaveTextAsync("text/plain", "当前报文", CancellationToken.None);
        var backend = new LogCenterBackend(data, settings, store, payloads);

        var residues = backend.Cleanup.Find().Select(item => item.Path).ToArray();
        Assert.Equal(new[] { legacy, oldRoot, parked }.OrderBy(path => path), residues.OrderBy(path => path));

        Assert.True(backend.Cleanup.Start());
        await backend.Cleanup.Completion;

        LogCleanupStatus status = backend.Cleanup.Status;
        Assert.Equal(LogCleanupState.Completed, status.State);
        Assert.Equal(80, status.DeletedFiles);
        Assert.Empty(status.Residues);
        Assert.False(Directory.Exists(legacy));
        Assert.False(Directory.Exists(oldRoot));
        Assert.False(Directory.Exists(parked));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(data)!, "IGW-清理中-*"));
        Assert.True(File.Exists(Path.Combine(oldStorage, "用户自己的文件.txt")), "所选旧目录里的其它文件不能删");
        Assert.True(File.Exists(Path.Combine(data, "project.db")));
        Assert.Equal("当前报文", (await payloads.ReadPageAsync(current.Id, 0, CancellationToken.None)).Content);
        Assert.False(File.Exists(Path.Combine(data, LogResidueCleaner.StateFileName)));
    }

    [Fact]
    public async Task A_cleanup_interrupted_by_a_restart_continues_on_the_next_start()
    {
        string data = Path.Combine(_root, "IGW");
        Directory.CreateDirectory(data);
        string trash = Fill(Path.Combine(_root, "IGW-清理中-abcd1234"), 30);
        File.WriteAllText(Path.Combine(data, LogResidueCleaner.StateFileName), $$"""{"pending":["{{trash.Replace("\\", "\\\\")}}"]}""");
        var settings = ObservabilitySettings.Initialize(data, Path.Combine(_root, "logs"), 5088);
        await using var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays);

        var backend = new LogCenterBackend(data, settings, store, new LogPayloadStore(LogPayloadStore.DirectoryFor(settings.StorageDirectory)));
        await backend.Cleanup.Completion;

        Assert.Equal(LogCleanupState.Completed, backend.Cleanup.Status.State);
        Assert.False(Directory.Exists(trash));
        Assert.False(File.Exists(Path.Combine(data, LogResidueCleaner.StateFileName)));
    }

    [Fact]
    public async Task Abandoning_a_fallback_stops_the_background_migration_of_old_payloads()
    {
        string legacy = Path.Combine(_root, "log-payloads");
        var old = new LogPayloadStore(legacy);
        for (int i = 0; i < 20; i++) await old.SaveTextAsync("text/plain", "旧报文" + i, CancellationToken.None);
        var store = new LogPayloadStore(Path.Combine(_root, "logs", "payloads"), [legacy]);

        store.AbandonFallback(legacy);

        Assert.Equal(0, store.AdoptFrom(legacy, move: true));
        Assert.Equal(40, Directory.EnumerateFiles(legacy).Count());   // 一个没搬：交给一键清理删
        Assert.Empty(store.FallbackDirectories);
    }

    private static string Fill(string directory, int files)
    {
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(directory, i % 2 == 0 ? "" : "sub", $"f{i}.dat"), new string('x', 10));
        return Path.GetFullPath(directory);
    }

    /// <summary>
    /// 装过 C、E、F、G 好几个盘、卸载重装后没有换盘记录：打开清理窗口时在各盘上按网关特有的名字和结构去找。
    /// 无关的同名文件夹、用户自己的文件、当前日志一律不碰。
    /// </summary>
    [Fact]
    public async Task Scanning_other_drives_finds_old_gateway_logs_and_text_logs_without_touching_anything_else()
    {
        string data = Path.Combine(_root, "C", "ProgramData", "IGW");
        Directory.CreateDirectory(data);
        string currentStorage = Path.Combine(_root, "D", "logs");
        var settings = ObservabilitySettings.Initialize(data, currentStorage, 5088);
        var payloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(currentStorage));
        LogPayloadReference current = await payloads.SaveTextAsync("text/plain", "当前报文", CancellationToken.None);
        File.WriteAllText(Path.Combine(currentStorage, "igw-20260101.log"), "和新日志放在一起的旧文本日志");

        string driveE = Path.Combine(_root, "E"), driveF = Path.Combine(_root, "F");
        string oldE = Path.Combine(driveE, "IGW", "日志", "igw-logs");
        Directory.CreateDirectory(Path.Combine(oldE, "2026-09"));
        File.WriteAllText(Path.Combine(oldE, "2026-09", "20260901.seg"), "旧分段");
        string unrelated = Path.Combine(driveE, "别的软件", "igw-logs");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "readme.txt"), "不是网关的");
        string textLogs = Path.Combine(driveF, "gateway");
        Directory.CreateDirectory(textLogs);
        File.WriteAllText(Path.Combine(textLogs, "igw-20260801.log"), "旧文本日志");
        File.WriteAllText(Path.Combine(textLogs, "igw-20260801.1.log"), "旧文本日志分卷");
        File.WriteAllText(Path.Combine(textLogs, "igw-notes.log"), "名字不符，不是网关日志");
        File.WriteAllText(Path.Combine(textLogs, "报表.xlsx"), "用户文件");

        var cleaner = new LogResidueCleaner(data, () => settings, payloads, () => [driveE, driveF, Path.Combine(_root, "D")]);
        cleaner.ScanOtherLocations();
        await cleaner.ScanCompletion;

        var residues = cleaner.Find();
        Assert.Equal(
            new[] { (Path.GetFullPath(currentStorage), (string?)"igw-*.log"), (Path.GetFullPath(oldE), null), (Path.GetFullPath(textLogs), "igw-*.log") }
                .OrderBy(item => item.Item1),
            residues.Select(item => (item.Path, item.FilePattern)).OrderBy(item => item.Path));

        Assert.True(cleaner.Start());
        await cleaner.Completion;

        Assert.Equal(LogCleanupState.Completed, cleaner.Status.State);
        Assert.False(Directory.Exists(oldE));
        Assert.True(File.Exists(Path.Combine(unrelated, "readme.txt")), "不是网关结构的同名文件夹不能删");
        Assert.False(File.Exists(Path.Combine(textLogs, "igw-20260801.log")));
        Assert.False(File.Exists(Path.Combine(textLogs, "igw-20260801.1.log")));
        Assert.True(File.Exists(Path.Combine(textLogs, "igw-notes.log")));
        Assert.True(File.Exists(Path.Combine(textLogs, "报表.xlsx")));
        Assert.Empty(Directory.EnumerateDirectories(textLogs, "IGW-清理中-*"));
        Assert.False(File.Exists(Path.Combine(currentStorage, "igw-20260101.log")));
        Assert.True(Directory.Exists(LogSegmentStore.RootFor(currentStorage)), "当前日志目录不能动");
        Assert.Equal("当前报文", (await payloads.ReadPageAsync(current.Id, 0, CancellationToken.None)).Content);
        Assert.Empty(cleaner.Find());
    }

    [Fact]
    public async Task Only_the_ticked_residues_are_cleaned()
    {
        string data = Path.Combine(_root, "ProgramData", "IGW");
        string legacy = Fill(Path.Combine(data, "log-payloads"), 4);
        string parked = Fill(Path.Combine(_root, "ProgramData", "IGW-大目录-20260924-120000"), 4);
        var settings = ObservabilitySettings.Initialize(data, Path.Combine(_root, "D", "logs"), 5088);
        var cleaner = new LogResidueCleaner(data, () => settings, new LogPayloadStore(Path.Combine(_root, "p")), () => []);

        Assert.True(cleaner.Start([parked, Path.Combine(_root, "不在列表里的目录")]));
        await cleaner.Completion;

        Assert.False(Directory.Exists(parked));
        Assert.True(Directory.Exists(legacy), "没勾选的不能删");
    }

    [Fact]
    public void Early_version_text_logs_in_the_data_directory_are_listed()
    {
        string data = Path.Combine(_root, "ProgramData", "IGW");
        string logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "igw-20260801.log"), "早期文本日志");
        var settings = ObservabilitySettings.Initialize(data, Path.Combine(_root, "D", "logs"), 5088);
        var cleaner = new LogResidueCleaner(data, () => settings, new LogPayloadStore(Path.Combine(_root, "p")), () => []);

        LogResidue residue = Assert.Single(cleaner.Find());
        Assert.Equal(Path.GetFullPath(logs), residue.Path);
        Assert.Equal("igw-*.log", residue.FilePattern);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
