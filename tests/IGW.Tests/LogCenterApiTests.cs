using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class LogCenterApiTests
{
    [Fact]
    public async Task Query_returns_stored_logs_newest_first_with_filters_and_total()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        fixture.Write(now.AddMinutes(-3), 2, "state", "设备上线", ("igw_device", "PLC8.OP140"), ("igw_status", "online"));
        fixture.Write(now.AddMinutes(-2), 4, "system", "读取失败：timeout", ("igw_device", "PLC8.OP140"));
        fixture.Write(now.AddMinutes(-1), 4, "system", "写入失败", ("igw_device", "PLC5.OP050"));
        await fixture.Store.FlushAsync();

        using JsonDocument all = await fixture.GetJsonAsync(QueryUrl());
        Assert.Equal(3, all.RootElement.GetProperty("total").GetInt64());
        Assert.Equal("写入失败", all.RootElement.GetProperty("items")[0].GetProperty("message").GetString());

        using JsonDocument filtered = await fixture.GetJsonAsync(QueryUrl("&level=error&device=PLC8.OP140"));
        JsonElement item = Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("读取失败：timeout", item.GetProperty("message").GetString());
        Assert.Equal("ERROR", item.GetProperty("level").GetString());
        Assert.Equal("PLC8.OP140", item.GetProperty("attributes").GetProperty("igw_device").GetString());

        using JsonDocument search = await fixture.GetJsonAsync(QueryUrl("&search=timeout"));
        Assert.Equal(1, search.RootElement.GetProperty("total").GetInt64());
        using JsonDocument category = await fixture.GetJsonAsync(QueryUrl("&category=state"));
        Assert.Equal("设备上线", Assert.Single(category.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("StationEntrance", 1)]
    [InlineData("mes.example", 1)]
    [InlineData("/api/report", 1)]
    [InlineData("report", 2)]
    [InlineData("http://mes.example/api/report", 1)]
    [InlineData("", 3)]
    [InlineData("private-body", 0)]
    public async Task Typed_group_or_interface_query_searches_only_log_context_attributes(string text, int expected)
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Write(at, 2, "interface", "MES HTTP 请求完成", ("igw_task_name", "StationEntrance-16"),
            ("igw_target", "http://mes.example/api/report"), ("igw_payload_id", "not-read-private-body"));
        fixture.Write(at, 2, "interface", "MES HTTP 请求完成", ("igw_task_name", "Other"),
            ("igw_target", "http://other.example/report"));
        fixture.Write(at, 2, "system", "private-body report", ("igw_error_type", "private-body"));
        await fixture.Store.FlushAsync();
        fixture.Store.SealNow();
        await fixture.Store.WaitForCompressionAsync();

        using JsonDocument response = await fixture.GetJsonAsync(QueryUrl("&taskOrInterface=" + Uri.EscapeDataString(text)));
        Assert.Equal(expected, response.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(expected, response.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(expected, response.RootElement.GetProperty("histogram").EnumerateArray().Sum(bucket => bucket.GetProperty("total").GetInt32()));
    }

    [Theory]
    [InlineData("PLC6", 4)]
    [InlineData("PLC6.D1", 4)]
    [InlineData("PLC6.D1.OP051", 3)]
    [InlineData("PLC6.D1.OP051.Sub", 2)]
    [InlineData("PLC6.D1.OP05", 0)]
    [InlineData("PLC6.D1.OP051.Sub.Tag", 0)]
    [InlineData("", 5)]
    public async Task Project_path_includes_bound_tasks_from_server_configuration_without_client_task_ids(string path, int expected)
    {
        // 分页/懒加载工程只发 path，不发当前内存里的任务列表；服务端仍有完整绑定，含已停用任务的历史日志。
        await using ApiFixture fixture = await ApiFixture.StartAsync(projectTasks: () =>
        [
            new("task-1", "PLC6.D1.OP051"),
            new("task-sub", "PLC6.D1.OP051.Sub"),
            new("task-10", "PLC66.D1.OP051"),
            new("sibling", "PLC6.D10.OP051"),
        ]);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Write(at, 2, "state", "设备上线", ("igw_device", "PLC6.D1"));
        fixture.Write(at, 2, "interface", "父组上报", ("igw_task_id", "task-1"));
        fixture.Write(at, 2, "interface", "子组上报", ("igw_task_id", "task-sub"));
        fixture.Write(at, 2, "collection", "共享采集", ("igw_task_ids", "other, task-sub"));
        fixture.Write(at, 2, "interface", "别的通道", ("igw_task_id", "task-10"));
        await fixture.Store.FlushAsync();
        fixture.Store.SealNow();
        await fixture.Store.WaitForCompressionAsync();

        string url = QueryUrl("&path=" + Uri.EscapeDataString(path));
        using JsonDocument response = await fixture.GetJsonAsync(url);
        Assert.Equal(expected, response.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(expected, response.RootElement.GetProperty("items").GetArrayLength());
        using HttpResponseMessage exported = await fixture.Client.GetAsync(url.Replace("/logs?", "/export?", StringComparison.Ordinal) + "&payloads=false");
        exported.EnsureSuccessStatusCode();
        Assert.Equal(expected.ToString(), exported.Headers.GetValues("X-Igw-Log-Entries").Single());
    }

    [Theory]
    [InlineData("OP051", "", 4)]
    [InlineData("PLC6.D1.OP051", "", 3)]
    [InlineData("OP051", "PLC6.D1", 3)]
    [InlineData("OP051", "PLC6.D2", 1)]
    [InlineData("Other", "PLC6.D1.OP051", 0)]
    [InlineData("api/report", "PLC6.D1.OP051", 1)]
    public async Task Typed_group_path_matches_scope_or_bound_task_independently_of_the_project_path(string term, string path, int expected)
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync(projectTasks: () =>
        [
            new("task-a", "PLC6.D1.OP051"),
            new("task-b", "PLC6.D2.OP051"),
            new("task-other", "PLC6.D1.Other"),
        ]);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Write(at, 2, "collection", "组名与任务名不同", ("igw_scope", "PLC6.D1.OP051"), ("igw_task_name", "工位上报"));
        fixture.Write(at, 2, "interface", "仅有绑定ID", ("igw_task_id", "task-a"), ("igw_task_name", "工位上报"), ("igw_target", "http://mes/api/report"));
        fixture.Write(at, 2, "collection", "共享绑定ID", ("igw_task_ids", "unrelated, task-a"));
        fixture.Write(at, 2, "interface", "另一个设备同组名", ("igw_task_id", "task-b"));
        fixture.Write(at, 2, "interface", "同设备的其他组", ("igw_task_id", "task-other"));
        await fixture.Store.FlushAsync();
        string url = QueryUrl("&taskOrInterface=" + Uri.EscapeDataString(term) + "&path=" + Uri.EscapeDataString(path));

        // 同一请求覆盖活跃段、封存段与缓存重复查询。
        for (int pass = 0; pass < 3; pass++)
        {
            using JsonDocument response = await fixture.GetJsonAsync(url);
            Assert.Equal(expected, response.RootElement.GetProperty("total").GetInt64());
            Assert.Equal(expected, response.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(expected, response.RootElement.GetProperty("histogram").EnumerateArray().Sum(bucket => bucket.GetProperty("total").GetInt32()));
            if (pass == 0) { fixture.Store.SealNow(); await fixture.Store.WaitForCompressionAsync(); }
        }
        using HttpResponseMessage exported = await fixture.Client.GetAsync(url.Replace("/logs?", "/export?", StringComparison.Ordinal) + "&payloads=false");
        exported.EnsureSuccessStatusCode();
        Assert.Equal(expected.ToString(), exported.Headers.GetValues("X-Igw-Log-Entries").Single());
    }

    [Fact]
    public async Task Typed_group_binding_changes_do_not_reuse_old_sealed_counts()
    {
        LogTaskBinding[] bindings = [new("task-a", "PLC6.D1.OP051")];
        await using ApiFixture fixture = await ApiFixture.StartAsync(projectTasks: () => bindings);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Write(at, 2, "interface", "任务A", ("igw_task_id", "task-a"));
        fixture.Write(at, 2, "interface", "任务B", ("igw_task_id", "task-b"));
        fixture.Write(at, 2, "interface", "任务B再次", ("igw_task_id", "task-b"));
        await fixture.Store.FlushAsync();
        fixture.Store.SealNow();
        await fixture.Store.WaitForCompressionAsync();
        string url = QueryUrl("&taskOrInterface=OP051");
        using JsonDocument first = await fixture.GetJsonAsync(url);
        Assert.Equal(1, first.RootElement.GetProperty("total").GetInt64());
        bindings = [new("task-b", "PLC6.D1.OP051")];
        using JsonDocument second = await fixture.GetJsonAsync(url);
        Assert.Equal(2, second.RootElement.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Export_keeps_the_typed_interface_filter_in_both_results_and_manifest()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-1);
        fixture.Write(at, 2, "interface", "匹配上报", ("igw_target", "http://mes.example/api/report"));
        fixture.Write(at, 2, "interface", "其他上报", ("igw_target", "http://other.example/ping"));
        await fixture.Store.FlushAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync(QueryUrl("&taskOrInterface=report&payloads=false")
            .Replace("/logs?", "/export?", StringComparison.Ordinal));
        response.EnsureSuccessStatusCode();
        Assert.Equal("1", response.Headers.GetValues("X-Igw-Log-Entries").Single());
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        using JsonDocument manifest = await JsonDocument.ParseAsync(archive.GetEntry("manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("filters").TryGetProperty("taskOrInterface", out JsonElement filter));
        Assert.Equal("report", filter.GetString());
    }

    [Fact]
    public async Task Empty_store_is_an_empty_successful_result_and_bad_parameters_are_rejected()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        using JsonDocument json = await fixture.GetJsonAsync(QueryUrl());
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt64());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());

        using HttpResponseMessage bad = await fixture.Client.GetAsync(QueryUrl("&level=loud"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Status_exposes_queue_and_storage_health_without_credentials()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/v1/log-center/status");
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.True(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(fixture.Settings.StorageDirectory, json.RootElement.GetProperty("storageDirectory").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("spool").GetProperty("dropped").GetInt64());
        Assert.DoesNotContain(fixture.Settings.Password, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenObserve_credentials_endpoint_no_longer_exists()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/v1/log-center/settings/openobserve-credentials");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retention_applies_immediately_and_storage_migration_keeps_existing_logs_queryable()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        using HttpResponseMessage invalid = await fixture.Client.PutAsJsonAsync(
            "/api/v1/log-center/settings/retention", new { retentionDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using HttpResponseMessage saved = await fixture.Client.PutAsJsonAsync(
            "/api/v1/log-center/settings/retention", new { retentionDays = 14 });
        saved.EnsureSuccessStatusCode();
        Assert.Equal(14, ObservabilitySettings.Load(fixture.DataDirectory)!.RetentionDays);
        Assert.Equal(14, fixture.Store.RetentionDays);

        fixture.Write(DateTimeOffset.UtcNow.AddMinutes(-1), 2, "system", "迁移前的日志");
        await fixture.Store.FlushAsync();
        string target = Path.Combine(Path.GetTempPath(), "igw-log-target", Guid.NewGuid().ToString("N"));
        try
        {
            using HttpResponseMessage migration = await fixture.Client.PostAsJsonAsync(
                "/api/v1/log-center/settings/storage", new { storageDirectory = target });
            migration.EnsureSuccessStatusCode();
            for (int i = 0; i < 100 && LogStorageChange.Load(fixture.DataDirectory)!.Status != LogStorageChangeStatus.Completed; i++)
                await Task.Delay(50);
            LogStorageChange finished = LogStorageChange.Load(fixture.DataDirectory)!;
            Assert.True(finished.Status == LogStorageChangeStatus.Completed, $"迁移状态 {finished.Status}：{finished.FailureReason}");
            Assert.Equal(target, ObservabilitySettings.Load(fixture.DataDirectory)!.StorageDirectory);
            Assert.StartsWith(target, fixture.Store.Root, StringComparison.OrdinalIgnoreCase);

            fixture.Write(DateTimeOffset.UtcNow, 2, "system", "迁移后的日志");
            await fixture.Store.FlushAsync();
            using JsonDocument json = await fixture.GetJsonAsync(QueryUrl());
            Assert.Equal(2, json.RootElement.GetProperty("total").GetInt64());
        }
        finally
        {
            await fixture.Store.DisposeAsync();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Compatibility_endpoint_keeps_the_legacy_response_shape()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        fixture.Write(DateTimeOffset.UtcNow.AddMinutes(-1), 4, "system", "连接失败", ("igw_logger", "Device:PLC8"));
        await fixture.Store.FlushAsync();

        using JsonDocument json = await fixture.GetJsonAsync("/api/v1/runtime-logs?level=ERR&limit=20");
        JsonElement item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("ERR", item.GetProperty("level").GetString());
        Assert.Equal("连接失败", item.GetProperty("message").GetString());
        Assert.Equal(30, json.RootElement.GetProperty("retentionDays").GetInt32());
    }

    [Fact]
    public async Task Export_endpoint_returns_a_zip_with_the_matching_logs_in_time_order()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        fixture.Write(DateTimeOffset.UtcNow.AddMinutes(-2), 2, "system", "第一条");
        fixture.Write(DateTimeOffset.UtcNow.AddMinutes(-1), 3, "system", "第二条");
        await fixture.Store.FlushAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync(
            QueryUrl().Replace("/logs?", "/export?", StringComparison.Ordinal));
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("2", response.Headers.GetValues("X-Igw-Log-Entries").Single());
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("logs.txt")!.Open());
        string logs = await reader.ReadToEndAsync();
        Assert.True(logs.IndexOf("第一条", StringComparison.Ordinal) < logs.IndexOf("第二条", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Directory_picker_empty_path_lists_the_gateway_drives()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();

        // 前端“磁盘列表”按钮传空路径；线上接口曾把它当成“打开日志存储目录”。
        GatewayDirectoryListing? listing = await fixture.Client.GetFromJsonAsync<GatewayDirectoryListing>(
            "/api/v1/runtime-logs/directories?path=");

        Assert.NotNull(listing);
        Assert.Equal("", listing!.CurrentPath);
        Assert.Null(listing.ParentPath);
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Contains(listing.Directories, drive =>
            string.Equals(drive.Path, systemRoot, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(listing.Directories, drive =>
            string.Equals(drive.Path, fixture.Settings.StorageDirectory, StringComparison.OrdinalIgnoreCase));
        GatewayDirectoryEntry system = listing.Directories.First(drive =>
            string.Equals(drive.Path, systemRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(system.FreeBytes > 0);
        Assert.True(system.TotalBytes >= system.FreeBytes);
    }

    [Fact]
    public async Task Directory_picker_hides_hidden_and_system_folders_and_sorts_by_name()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        string root = Path.Combine(fixture.DataDirectory, "browse");
        foreach (string name in new[] { "b-logs", "A-logs", "c-logs" }) Directory.CreateDirectory(Path.Combine(root, name));
        var hidden = new DirectoryInfo(Path.Combine(root, "hidden"));
        hidden.Create();
        hidden.Attributes |= FileAttributes.Hidden;
        var system = new DirectoryInfo(Path.Combine(root, "system"));
        system.Create();
        system.Attributes |= FileAttributes.System;
        File.WriteAllText(Path.Combine(root, "file.txt"), "not a folder");

        GatewayDirectoryListing? listing = await fixture.Client.GetFromJsonAsync<GatewayDirectoryListing>(
            "/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(root));

        Assert.Equal(root, listing!.CurrentPath);
        Assert.Equal(fixture.DataDirectory, listing.ParentPath);
        Assert.Equal(["A-logs", "b-logs", "c-logs"], listing.Directories.Select(entry => entry.Name));
        Assert.False(listing.Truncated);
        system.Attributes &= ~FileAttributes.System;
        hidden.Attributes &= ~FileAttributes.Hidden;
    }

    [Fact]
    public async Task Directory_picker_marks_truncation_only_beyond_the_limit()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        string exact = Path.Combine(fixture.DataDirectory, "exact");
        for (int i = 0; i < GatewayDirectoryBrowser.MaxEntries; i++)
            Directory.CreateDirectory(Path.Combine(exact, i.ToString("D3")));

        GatewayDirectoryListing? full = await fixture.Client.GetFromJsonAsync<GatewayDirectoryListing>(
            "/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(exact));
        Assert.Equal(GatewayDirectoryBrowser.MaxEntries, full!.Directories.Count);
        Assert.False(full.Truncated);

        Directory.CreateDirectory(Path.Combine(exact, "extra"));
        GatewayDirectoryListing? over = await fixture.Client.GetFromJsonAsync<GatewayDirectoryListing>(
            "/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(exact));
        Assert.Equal(GatewayDirectoryBrowser.MaxEntries, over!.Directories.Count);
        Assert.True(over.Truncated);
    }

    [Theory]
    [InlineData("relative\\logs")]
    [InlineData("C:")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public async Task Directory_picker_rejects_relative_and_device_paths(string path)
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        using HttpResponseMessage response = await fixture.Client.GetAsync(
            "/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(path));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Directory_picker_reports_a_missing_folder_instead_of_an_empty_selectable_listing()
    {
        await using ApiFixture fixture = await ApiFixture.StartAsync();
        string missing = Path.Combine(fixture.DataDirectory, "does-not-exist");

        using HttpResponseMessage response = await fixture.Client.GetAsync(
            "/api/v1/runtime-logs/directories?path=" + Uri.EscapeDataString(missing));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string QueryUrl(string extra = "")
    {
        string from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        string to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
        return $"/api/v1/log-center/logs?from={from}&to={to}&page=1&pageSize=50{extra}";
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public string DataDirectory { get; }
        public ObservabilitySettings Settings { get; }
        public LogSegmentStore Store { get; }

        private ApiFixture(WebApplication app, HttpClient client, string dataDirectory, ObservabilitySettings settings, LogSegmentStore store)
        {
            _app = app;
            Client = client;
            DataDirectory = dataDirectory;
            Settings = settings;
            Store = store;
        }

        public static async Task<ApiFixture> StartAsync(IPAddress? remoteAddress = null,
            Func<IEnumerable<LogTaskBinding>>? projectTasks = null)
        {
            string data = Path.Combine(Path.GetTempPath(), "igw-log-api", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            ObservabilitySettings settings = ObservabilitySettings.Initialize(data, Path.Combine(data, "store"), 5088);
            var store = new LogSegmentStore(settings.StorageDirectory, settings.RetentionDays);
            var payloads = new LogPayloadStore(Path.Combine(data, "payloads"));
            var backend = new LogCenterBackend(data, settings, store, payloads);
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            WebApplication app = builder.Build();
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = remoteAddress ?? IPAddress.Loopback;
                return next();
            });
            app.MapLogCenterApi(backend, projectTasks);
            await app.StartAsync();
            return new ApiFixture(app, app.GetTestClient(), data, settings, store);
        }

        public void Write(DateTimeOffset at, int level, string category, string message, params (string Key, object? Value)[] fields) =>
            Store.Append(new LogRecord(LogRecordCodec.ToMicros(at), level, category, message,
                fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal)));

        public async Task<JsonDocument> GetJsonAsync(string url)
        {
            using HttpResponseMessage response = await Client.GetAsync(url);
            string content = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, content);
            return JsonDocument.Parse(content);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await Store.DisposeAsync();
            try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
        }
    }
}
