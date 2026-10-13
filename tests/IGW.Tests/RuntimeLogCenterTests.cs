using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Json;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public sealed class RuntimeLogCenterTests
{
    [Fact]
    public async Task Invalid_storage_or_failed_persistence_does_not_change_the_active_directory()
    {
        string dir = NewDirectory();
        try
        {
            string original = Path.Combine(dir, "original");
            string selected = Path.Combine(dir, "new");
            string file = Path.Combine(dir, "not-a-directory");
            File.WriteAllText(file, "existing user data");
            using var provider = new FileLoggerProvider(original);
            bool persistFailed = false;
            int writes = 0;
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapRuntimeLogApi(provider, () => 30, (_, _) =>
            {
                writes++;
                if (persistFailed) throw new IOException("database unavailable");
                return Task.CompletedTask;
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            foreach (string invalid in new[] { file, "relative/logs" })
            {
                using var failed = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 7, storageDirectory = invalid });
                Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
                Assert.Equal(original, provider.StorageDirectory);
                Assert.Equal(30, provider.RetentionDays);
            }
            Assert.Equal(0, writes);
            persistFailed = true;
            using var notSaved = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 7, storageDirectory = selected });
            Assert.Equal(HttpStatusCode.BadRequest, notSaved.StatusCode);
            Assert.Equal(original, provider.StorageDirectory);
            Assert.Equal(30, provider.RetentionDays);
            Assert.Equal("existing user data", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(selected));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Restore_uses_saved_location_and_falls_back_visibly_when_it_is_unavailable()
    {
        string dir = NewDirectory();
        try
        {
            string original = Path.Combine(dir, "default"), selected = Path.Combine(dir, "saved");
            using (var restored = new FileLoggerProvider(original))
            {
                restored.RestoreStorageSettings(14, selected);
                restored.CreateLogger("PLC").LogInformation("restored location");
                Assert.Equal(selected, restored.Query(new(null, null, null)).StorageDirectory);
                Assert.Single(Directory.GetFiles(selected, "*.log"));
                Assert.Equal(14, restored.RetentionDays);
            }
            string blocked = Path.Combine(dir, "blocked");
            File.WriteAllText(blocked, "cannot be a directory");
            using var fallback = new FileLoggerProvider(original);
            fallback.RestoreStorageSettings(14, blocked);
            Assert.Equal(original, fallback.StorageDirectory);
            Assert.Contains(blocked, Assert.Single(fallback.Query(new(null, null, null)).Warnings!));
            fallback.ApplyStorageSettings(14, fallback.PrepareStorageDirectory(selected));
            Assert.Empty(fallback.Query(new(null, null, null)).Warnings!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Directory_picker_lists_only_immediate_folders_on_the_gateway()
    {
        string dir = NewDirectory();
        try
        {
            string selected = Path.Combine(dir, "子目录");
            Directory.CreateDirectory(Path.Combine(selected, "nested"));
            File.WriteAllText(Path.Combine(dir, "private-file.txt"), "not listed");
            using var provider = new FileLoggerProvider(dir);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapRuntimeLogApi(provider, () => 30, (_, _) => Task.CompletedTask);
            await app.StartAsync();
            using var client = app.GetTestClient();
            var listing = await client.GetFromJsonAsync<IGW.Observability.GatewayDirectoryListing>("/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(dir));
            Assert.Equal(dir, listing!.CurrentPath);
            Assert.Equal(selected, Assert.Single(listing.Directories).Path);
            Assert.False(listing.Truncated);
            using var invalid = await client.GetAsync("/api/v1/runtime-logs/directories?path=relative");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Log_settings_switch_the_existing_logger_to_the_selected_directory()
    {
        string dir = NewDirectory();
        string selected = Path.Combine(dir, "selected");
        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(dir, "original"));
            var logger = provider.CreateLogger("PLC");
            logger.LogInformation("before switch");
            int persistedDays = 30;
            string? persistedDirectory = null;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapRuntimeLogApi(provider, () => persistedDays, (settings, _) =>
            {
                persistedDays = settings.RetentionDays;
                persistedDirectory = settings.StorageDirectory;
                return Task.CompletedTask;
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings",
                new { retentionDays = 14, storageDirectory = selected });
            response.EnsureSuccessStatusCode();
            Assert.Equal(selected, persistedDirectory);
            logger.LogInformation("after switch");
            Assert.True(Directory.Exists(selected));
            Assert.Contains("after switch", File.ReadAllText(Assert.Single(Directory.GetFiles(selected, "*.log"))));
            Assert.DoesNotContain("after switch", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(dir, "original"), "*.log"))));
            using var legacy = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 15 });
            legacy.EnsureSuccessStatusCode();
            Assert.Equal(selected, provider.StorageDirectory);
            using var reset = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 15, storageDirectory = "" });
            reset.EnsureSuccessStatusCode();
            Assert.Equal(provider.DefaultDirectory, provider.StorageDirectory);
            Assert.Equal("", persistedDirectory);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_unreadable_newest_file_does_not_hide_other_logs()
    {
        string dir = NewDirectory();
        try
        {
            using var provider = new FileLoggerProvider(dir);
            string readable = Path.Combine(dir, "igw-readable.log");
            File.WriteAllText(readable, "2026-09-05 10:00:00.000 INF [PLC] readable\n");
            string blocked = Path.Combine(dir, "igw-blocked.log");
            File.WriteAllText(blocked, "blocked");
            File.SetLastWriteTimeUtc(blocked, DateTime.UtcNow.AddMinutes(1));
            using var exclusive = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = provider.Query(new(null, null, null));
            Assert.Equal("readable", Assert.Single(result.Items).Message);
            Assert.Contains("igw-blocked.log", Assert.Single(result.Warnings!));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Queries_do_not_wait_for_the_writer_lock()
    {
        string dir = NewDirectory();
        try
        {
            using var provider = new FileLoggerProvider(dir);
            provider.CreateLogger("PLC").LogInformation("visible");
            object writerLock = typeof(FileLoggerProvider).GetField("_lock",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                lock (writerLock) { entered.SetResult(); release.Wait(); }
            });
            await entered.Task;
            try
            {
                var result = await Task.Run(() => provider.Query(new(null, null, null))).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Single(result.Items);
            }
            finally { release.Set(); await writer; }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Latest_query_preserves_multiline_utf8_messages_and_filters()
    {
        string dir = NewDirectory();
        try
        {
            using var provider = new FileLoggerProvider(dir);
            string file = Path.Combine(dir, "igw-fixture.log");
            File.WriteAllText(file, "2026-09-05 10:00:00.000 INF [PLC] old\r\n" +
                "2026-09-05 10:00:01.000 ERR [PLC] 连接失败\r\n" + new string('中', 30_000) +
                "\r\nstack trace\r\n2026-09-05 10:00:02.000 INF [PLC] latest\r\n", System.Text.Encoding.UTF8);
            Assert.Equal("latest", Assert.Single(provider.Query(new(null, null, null, 1)).Items).Message);
            var error = Assert.Single(provider.Query(new("ERR", "stack trace", null, 1)).Items);
            Assert.Equal("连接失败" + Environment.NewLine + new string('中', 30_000) + Environment.NewLine + "stack trace", error.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Filled_page_does_not_scan_older_archives()
    {
        string dir = NewDirectory();
        try
        {
            using var provider = new FileLoggerProvider(dir);
            string old = Path.Combine(dir, "igw-old.log");
            File.WriteAllText(old, "unreadable archive");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
            using var exclusive = new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(Path.Combine(dir, "igw-current.log"),
                "2026-09-05 10:00:01.000 INF [PLC] latest without trailing newline");
            var result = provider.Query(new(null, null, null, 1));
            Assert.Equal("latest without trailing newline", Assert.Single(result.Items).Message);
            Assert.Empty(result.Warnings!);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => provider.Query(new(null, null, null), cancellation.Token));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Logger_writes_queryable_entries_with_level_and_category()
    {
        string dir = NewDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 26, 9, 30, 0, TimeSpan.FromHours(8));
            using var provider = new IGW.Runtime.FileLoggerProvider(
                dir, LogLevel.Information, retentionDays: 30, new FrozenTimeProvider(now));

            provider.CreateLogger("Device:PLC8").LogWarning("连接远端 {url} 失败", "opc.tcp://10.217.152.80:4840");

            var result = provider.Query(new IGW.Runtime.RuntimeLogQuery(Level: "WRN", Search: "10.217.152.80", Date: null, Limit: 50));
            var entry = Assert.Single(result.Items);
            Assert.Equal("WRN", entry.Level);
            Assert.Equal("Device:PLC8", entry.Category);
            Assert.Contains("10.217.152.80", entry.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Updating_retention_deletes_only_expired_log_days()
    {
        string dir = NewDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.FromHours(8));
            string expired = Path.Combine(dir, "igw-20260810.log");
            string kept = Path.Combine(dir, "igw-20260825.log");
            File.WriteAllText(expired, "old");
            File.WriteAllText(kept, "new");
            File.SetLastWriteTime(expired, now.AddDays(-16).LocalDateTime);
            File.SetLastWriteTime(kept, now.AddDays(-1).LocalDateTime);

            using var provider = new IGW.Runtime.FileLoggerProvider(
                dir, LogLevel.Information, retentionDays: 30, new FrozenTimeProvider(now));
            provider.UpdateRetentionDays(7);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(kept));
            Assert.Equal(7, provider.RetentionDays);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Log_api_queries_entries_and_persists_valid_retention_without_reloading_runtime()
    {
        string dir = NewDirectory();
        try
        {
            using var provider = new IGW.Runtime.FileLoggerProvider(dir);
            provider.CreateLogger("GatewayHost").LogError("远端连接失败");
            int persistedDays = 30;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapRuntimeLogApi(provider, () => persistedDays, (settings, _) =>
            {
                persistedDays = settings.RetentionDays;
                return Task.CompletedTask;
            });
            await app.StartAsync();
            using var client = app.GetTestClient();

            var queried = await client.GetFromJsonAsync<LogResponse>("/api/v1/runtime-logs?level=ERR&limit=20");
            Assert.NotNull(queried);
            Assert.Single(queried.Items);
            Assert.Equal("GatewayHost", queried.Items[0].Category);

            using var updated = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 14 });
            updated.EnsureSuccessStatusCode();
            Assert.Equal(14, persistedDays);
            Assert.Equal(14, provider.RetentionDays);

            using var invalid = await client.PutAsJsonAsync("/api/v1/runtime-logs/settings", new { retentionDays = 0 });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(14, persistedDays);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-runtime-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    }

    private sealed record LogResponse(int RetentionDays, List<LogItem> Items);
    private sealed record LogItem(DateTimeOffset Timestamp, string Level, string Category, string Message);
}
