using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 接口报文跟着日志目录走。
///
/// 现场在页面上把日志目录换到别的盘后，日志分段迁过去了，报文却一直留在数据目录的
/// log-payloads 下：旧版本报文路径是写死的，不随日志目录变化，也不参与迁移。
/// 现在报文放在日志根目录的 payloads 子目录里，更换日志目录时一起复制；旧版本留下的
/// log-payloads 在启动时后台搬进去。迁移期间旧报文照常能读，文件修改时间不变
/// (保留期按它算，搬一次不能让报文多活一个周期)。
/// </summary>
public sealed class LogPayloadMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igw-payload-migration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Payloads_left_in_an_old_directory_stay_readable_until_adopted_then_move_with_their_timestamps()
    {
        string legacy = Path.Combine(_root, "log-payloads");
        var old = new LogPayloadStore(legacy);
        LogPayloadReference saved = await old.SaveTextAsync("text/plain", "旧报文", CancellationToken.None);
        var written = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(legacy)) File.SetLastWriteTimeUtc(file, written);

        var store = new LogPayloadStore(Path.Combine(_root, "logs", "payloads"), [legacy]);
        Assert.Equal("旧报文", (await store.ReadPageAsync(saved.Id, 0, CancellationToken.None)).Content);

        Assert.Equal(1, store.AdoptFrom(legacy, move: true));

        Assert.Empty(store.FallbackDirectories);
        Assert.False(Directory.Exists(legacy));
        string moved = Path.Combine(store.CurrentDirectory, saved.Id + ".payload");
        Assert.Equal(written, File.GetLastWriteTimeUtc(moved));
        Assert.Equal("旧报文", (await store.ReadPageAsync(saved.Id, 0, CancellationToken.None)).Content);
    }

    [Fact]
    public async Task Changing_the_log_directory_takes_the_payloads_along()
    {
        string data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        ObservabilitySettings settings = ObservabilitySettings.Initialize(data, Path.Combine(_root, "disk-a"), 5088);
        await using var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays);
        var payloads = new LogPayloadStore(LogPayloadStore.DirectoryFor(settings.StorageDirectory));
        var backend = new LogCenterBackend(data, settings, store, payloads);
        LogPayloadReference saved = await payloads.SaveTextAsync("text/plain", "迁移前的报文", CancellationToken.None);
        string target = Path.Combine(_root, "disk-b");

        await backend.BeginStorageChangeAsync(target, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        LogCenterStatus status;
        while ((status = await backend.StatusAsync(timeout.Token)).MigrationStatus
               is not (LogStorageChangeStatus.Completed or LogStorageChangeStatus.Failed))
            await Task.Delay(20, timeout.Token);

        Assert.True(status.MigrationStatus == LogStorageChangeStatus.Completed, "迁移失败：" + status.MigrationFailure);
        Assert.Equal(LogPayloadStore.DirectoryFor(target), payloads.CurrentDirectory);
        Assert.Empty(payloads.FallbackDirectories);
        Assert.True(File.Exists(Path.Combine(LogPayloadStore.DirectoryFor(target), saved.Id + ".json")));
        Assert.Equal("迁移前的报文", (await payloads.ReadPageAsync(saved.Id, 0, CancellationToken.None)).Content);
        LogPayloadReference after = await payloads.SaveTextAsync("text/plain", "迁移后的报文", CancellationToken.None);
        Assert.StartsWith(LogPayloadStore.DirectoryFor(target), after.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Startup_moves_payloads_from_the_legacy_data_directory_into_the_log_directory()
    {
        string data = Path.Combine(_root, "data");
        string legacy = Path.Combine(data, "log-payloads");
        LogPayloadReference saved = await new LogPayloadStore(legacy)
            .SaveTextAsync("text/plain", "升级前的报文", CancellationToken.None);
        ObservabilitySettings settings = ObservabilitySettings.Initialize(data, Path.Combine(_root, "disk-b"), 5088);

        await using RuntimeTelemetry telemetry = RuntimeTelemetry.Create(settings, data, useQuestDb: false);
        Assert.Equal("升级前的报文", (await telemetry.Payloads.ReadPageAsync(saved.Id, 0, CancellationToken.None)).Content);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 先移出回退目录再删空目录，两步之间有个很短的间隙
        while (telemetry.Payloads.FallbackDirectories.Count > 0 || Directory.Exists(legacy)) await Task.Delay(20, timeout.Token);

        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(LogPayloadStore.DirectoryFor(settings.StorageDirectory), saved.Id + ".payload")));
    }

    /// <summary>
    /// 网关启动恢复日志分段时不进入报文目录：那里可能有几百万个文件，逐个扫会拖慢启动，
    /// Windows 服务 30 秒内起不来就被判启动失败，现场升级后看起来就是「装不上」。
    /// 以报文目录里一个空 .jsonl 为证：原先启动恢复会把它当成空分段删掉，现在根本不去碰。
    /// </summary>
    [Fact]
    public async Task Startup_recovery_never_walks_the_payload_directory()
    {
        string storage = Path.Combine(_root, "logs");
        string payloads = LogPayloadStore.DirectoryFor(storage);
        Directory.CreateDirectory(payloads);
        string untouched = Path.Combine(payloads, "not-a-segment.jsonl");
        File.WriteAllText(untouched, "");

        await using (var store = new LogSegmentStore(storage, 30)) { }

        Assert.True(File.Exists(untouched));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
