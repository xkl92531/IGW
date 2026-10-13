using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using IGW.Persistence.Sqlite;
using IGW.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectConfigurationApiTests
{
    [Fact]
    public async Task Rejected_mutation_exposes_the_specific_reason_without_changing_its_error_code()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());
        using var request = BatchRequest(new
        {
            tasks = Array.Empty<MesTask>(), periodTaskIds = new[] { "missing" }, collectIntervalMs = 200,
        });

        HttpResponseMessage response = await fixture.Client.SendAsync(request);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("mutation_rejected", body.GetProperty("error").GetString());
        Assert.Contains("流程不存在", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Batch_mes_save_updates_selected_periods_and_drafts_in_one_revision_without_enabling_tasks()
    {
        var initial = ProjectWithConfigurationRows();
        initial.MesUpload.Tasks[0].CollectIntervalMs = 5000;
        await using var fixture = await ApiFixture.CreateAsync(initial);
        var draft = initial.MesUpload.Tasks[0];
        draft.Payload.Append["WorkStationCode"] = "OP200";
        using var request = BatchRequest(new { tasks = new[] { draft }, periodTaskIds = new[] { "task-device-2", "task-channel-b" }, collectIntervalMs = 200 });

        var response = await fixture.Client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("\"project-2\"", response.Headers.ETag?.Tag);
        var saved = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/mes-tasks/task-visible");
        Assert.Equal(5000, saved.GetProperty("collectIntervalMs").GetInt32());
        Assert.Equal("OP200", saved.GetProperty("payload").GetProperty("append").GetProperty("WorkStationCode").GetString());
        foreach (var id in new[] { "task-device-2", "task-channel-b" })
        {
            var task = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/mes-tasks/" + id);
            Assert.Equal(200, task.GetProperty("collectIntervalMs").GetInt32());
            Assert.Equal(initial.MesUpload.Tasks.Single(item => item.Id == id).Enabled, task.GetProperty("enabled").GetBoolean());
        }
        var catalog = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/mes-tasks?scope=all");
        Assert.Contains(catalog.GetProperty("items").EnumerateArray(), item => item.GetProperty("collectIntervalMs").GetInt32() == 200);
    }

    [Theory]
    [InlineData("missing", 200)]
    [InlineData("task-hidden", 200)]
    [InlineData("task-visible", 0)]
    [InlineData("task-visible", 3600001)]
    public async Task Invalid_batch_mes_selection_or_period_keeps_all_documents_and_revision(string selected, int period)
    {
        var initial = ProjectWithConfigurationRows();
        await using var fixture = await ApiFixture.CreateAsync(initial);
        var draft = initial.MesUpload.Tasks[0];
        draft.Name = "must not persist";
        using var request = BatchRequest(new { tasks = new[] { draft }, periodTaskIds = new[] { selected }, collectIntervalMs = period });

        var response = await fixture.Client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        var savedResponse = await fixture.Client.GetAsync("/api/v1/project/mes-tasks/task-visible");
        Assert.Equal("\"project-1\"", savedResponse.Headers.ETag?.Tag);
        var saved = await savedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Visible", saved.GetProperty("name").GetString());
        Assert.Equal(1000, saved.GetProperty("collectIntervalMs").GetInt32());
    }

    [Fact]
    public async Task Save_all_mes_without_period_edit_keeps_existing_cycles_and_rejects_stale_repeat()
    {
        var initial = ProjectWithConfigurationRows();
        initial.MesUpload.Tasks[0].CollectIntervalMs = 5000;
        initial.MesUpload.Tasks[2].CollectIntervalMs = 200;
        await using var fixture = await ApiFixture.CreateAsync(initial);
        using var request = BatchRequest(new { tasks = Array.Empty<MesTask>(), periodTaskIds = Array.Empty<string>() });
        var response = await fixture.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var stale = BatchRequest(new { tasks = Array.Empty<MesTask>(), periodTaskIds = new[] { "task-visible" }, collectIntervalMs = 100 });
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Client.SendAsync(stale)).StatusCode);
        foreach (var (id, period) in new[] { ("task-visible", 5000), ("task-device-2", 200) })
        {
            var saved = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/mes-tasks/" + id);
            Assert.Equal(period, saved.GetProperty("collectIntervalMs").GetInt32());
        }
    }

    private static HttpRequestMessage BatchRequest(object batch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/mutations")
        {
            Content = JsonContent.Create(new { kind = "saveMesTasks", entityKind = "project", entityIds = Array.Empty<string>(),
                targetId = (string?)null, documentJson = JsonSerializer.Serialize(batch, ProjectStore.Options) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"project-1\"");
        return request;
    }

    [Fact]
    public async Task Browser_contract_uses_camel_case_enum_names_for_tree_and_mutations()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        JsonElement summary = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/summary");
        Assert.Equal("project", summary.GetProperty("root").GetProperty("kind").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/mutations")
        {
            Content = JsonContent.Create(new
            {
                kind = "setEnabled",
                entityKind = "group",
                entityIds = new[] { "group-visible" },
                targetId = (string?)null,
                documentJson = "false",
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"project-1\"");
        HttpResponseMessage mutation = await fixture.Client.SendAsync(request);

        Assert.True(mutation.IsSuccessStatusCode, await mutation.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tags_are_paged_searched_and_return_complete_paths_with_revision()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        HttpResponseMessage response = await fixture.Client.GetAsync(
            "/api/v1/project/tags?groupId=group-visible&offset=100&limit=50&q=Temperature");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"project-1\"", response.Headers.ETag?.Tag);
        Assert.Equal(150, body.GetProperty("total").GetInt64());
        Assert.Equal(100, body.GetProperty("offset").GetInt32());
        Assert.Equal(50, body.GetProperty("limit").GetInt32());
        JsonElement[] items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(50, items.Length);
        Assert.Equal("LineA.PLC1.Visible.Temperature100", items[0].GetProperty("actualPath").GetString());
        Assert.Equal("温度点 100", items[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Mes_tasks_are_scoped_to_device_or_channel_and_hide_disabled_flow_groups()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        HttpResponseMessage deviceResponse = await fixture.Client.GetAsync(
            "/api/v1/project/mes-tasks?scope=device&deviceId=device-1&offset=0&limit=50");
        HttpResponseMessage channelResponse = await fixture.Client.GetAsync(
            "/api/v1/project/mes-tasks?scope=channel&channelId=channel-a&offset=0&limit=50");
        HttpResponseMessage runningResponse = await fixture.Client.GetAsync(
            "/api/v1/project/mes-tasks?scope=channel&channelId=channel-a&enabled=true&offset=0&limit=50");
        HttpResponseMessage stoppedResponse = await fixture.Client.GetAsync(
            "/api/v1/project/mes-tasks?scope=channel&channelId=channel-a&enabled=false&offset=0&limit=50");
        string deviceJson = await deviceResponse.Content.ReadAsStringAsync();
        string channelJson = await channelResponse.Content.ReadAsStringAsync();
        Assert.True(deviceResponse.IsSuccessStatusCode, deviceJson);
        Assert.True(channelResponse.IsSuccessStatusCode, channelJson);
        JsonElement device = JsonSerializer.Deserialize<JsonElement>(deviceJson);
        JsonElement channel = JsonSerializer.Deserialize<JsonElement>(channelJson);
        JsonElement running = await runningResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement stopped = await stoppedResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["task-visible"], TaskIds(device));
        Assert.Equal(["task-visible", "task-device-2"], TaskIds(channel));
        Assert.DoesNotContain("task-hidden", TaskIds(device));
        Assert.DoesNotContain("task-channel-b", TaskIds(channel));
        Assert.Equal(["task-visible"], TaskIds(running));
        Assert.Equal(["task-device-2"], TaskIds(stopped));
        JsonElement first = device.GetProperty("items")[0];
        Assert.Equal("device-1", first.GetProperty("deviceId").GetString());
        Assert.Equal("channel-a", first.GetProperty("channelId").GetString());
        Assert.Equal("可用流程组", first.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Mes_task_document_is_loaded_without_the_full_project()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/v1/project/mes-tasks/task-visible");
        JsonElement task = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"project-1\"", response.Headers.ETag?.Tag);
        Assert.Equal("task-visible", task.GetProperty("id").GetString());
        Assert.Equal("LineA.PLC1.Visible", task.GetProperty("groupRef").GetString());
    }

    [Fact]
    public async Task Gateway_settings_are_read_and_replaced_without_the_full_project()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        HttpResponseMessage before = await fixture.Client.GetAsync("/api/v1/project/settings");
        JsonElement settings = await before.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(48020, settings.GetProperty("settings").GetProperty("restPort").GetInt32());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/mutations")
        {
            Content = JsonContent.Create(new
            {
                kind = "replaceProjectSections",
                entityKind = "project",
                entityIds = new[] { "settings", "opcUa" },
                targetId = (string?)null,
                documentJson = "{\"settings\":{\"restPort\":49020,\"opcUaPort\":4840},\"opcUa\":{\"enabled\":true,\"port\":4840,\"allowAnonymous\":true,\"allowEncryption\":true}}",
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"project-1\"");
        HttpResponseMessage mutation = await fixture.Client.SendAsync(request);
        HttpResponseMessage after = await fixture.Client.GetAsync("/api/v1/project/settings");
        JsonElement updated = await after.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(mutation.IsSuccessStatusCode, await mutation.Content.ReadAsStringAsync());
        Assert.Equal(49020, updated.GetProperty("settings").GetProperty("restPort").GetInt32());
        Assert.Equal("\"project-2\"", after.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Mutation_requires_if_match_and_stale_revision_returns_latest_etag()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());
        var mutation = new ProjectMutation(
            ProjectMutationKind.SetEnabled,
            ProjectNodeKind.Tag,
            ["tag-000"],
            null,
            "false");

        HttpResponseMessage missing = await fixture.Client.PostAsJsonAsync(
            "/api/v1/project/mutations", mutation, ProjectStore.Options);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/mutations")
        {
            Content = JsonContent.Create(mutation, options: ProjectStore.Options),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"project-0\"");
        HttpResponseMessage stale = await fixture.Client.SendAsync(request);
        JsonElement body = await stale.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("\"project-1\"", stale.Headers.ETag?.Tag);
        Assert.Equal("revision_conflict", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Legacy_full_project_endpoint_is_deprecated_and_disabled_above_50000_tags()
    {
        IgwProject project = ProjectWithConfigurationRows();
        TagGroupDef group = project.Channels[0].Devices[0].Groups[0];
        for (int index = group.Tags.Count; index < 50_001; index++)
        {
            group.Tags.Add(new TagDef
            {
                Id = $"large-{index:D5}",
                Name = $"Large{index:D5}",
                Address = $"DB20.DBD{index * 4}",
                DataType = IgwDataType.Int32,
            });
        }
        await using var fixture = await ApiFixture.CreateAsync(project, mapFullApi: true);

        JsonElement summary = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/summary");
        HttpResponseMessage get = await fixture.Client.GetAsync("/api/v1/project");
        HttpResponseMessage put = await fixture.Client.PutAsJsonAsync(
            "/api/v1/project", new IgwProject { Name = "must-not-load" }, ProjectStore.Options);
        using var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/project");
        browserRequest.Headers.Accept.ParseAdd("text/html");
        browserRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        HttpResponseMessage browser = await fixture.Client.SendAsync(browserRequest);
        string getBody = await get.Content.ReadAsStringAsync();

        Assert.Equal(50_001, summary.GetProperty("totalTagCount").GetInt64());
        Assert.False(summary.GetProperty("fullProjectSupported").GetBoolean());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, get.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, browser.StatusCode);
        Assert.Equal("/#/project", browser.Headers.Location?.OriginalString);
        Assert.Contains("full_project_endpoint_disabled", getBody);
        Assert.Equal("true", get.Headers.GetValues("Deprecation").Single());
        Assert.True(get.Headers.Contains("Sunset"));
        Assert.Equal("\"project-1\"", get.Headers.ETag?.Tag);
    }

    private static string[] TaskIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

    [Fact]
    public async Task Full_project_is_not_resent_when_the_browser_already_has_the_current_revision()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows(), mapFullApi: true);
        HttpResponseMessage first = await fixture.Client.GetAsync("/api/v1/project");
        string etag = first.Headers.ETag!.Tag;

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/project");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage notModified = await fixture.Client.SendAsync(again);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/v1/project");
        stale.Headers.TryAddWithoutValidation("If-None-Match", "\"project-0\"");
        HttpResponseMessage changed = await fixture.Client.SendAsync(stale);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await changed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Large_project_shell_lists_devices_without_groups_and_children_load_on_demand()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());

        HttpResponseMessage shellResponse = await fixture.Client.GetAsync("/api/v1/project/shell");
        JsonElement shell = await shellResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement device = shell.GetProperty("project").GetProperty("channels")[0].GetProperty("devices")[0];
        HttpResponseMessage childrenResponse = await fixture.Client.GetAsync("/api/v1/project/children/device/device-1");
        JsonElement children = await childrenResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement group = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/children/group/group-visible");
        JsonElement deep = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/children/device/device-1?deep=true");

        Assert.Equal("\"project-1\"", shellResponse.Headers.ETag?.Tag);
        Assert.Equal(0, device.GetProperty("groups").GetArrayLength());
        Assert.Equal(150, shell.GetProperty("lazy").GetProperty("device-1").GetProperty("totalTags").GetInt64());
        Assert.Equal(4, shell.GetProperty("project").GetProperty("mesUpload").GetProperty("tasks").GetArrayLength());
        Assert.Equal("\"project-1\"", childrenResponse.Headers.ETag?.Tag);
        Assert.Equal(["Visible", "Hidden"], children.GetProperty("groups").EnumerateArray().Select(item => item.GetProperty("name").GetString()!));
        Assert.Equal(0, children.GetProperty("groups")[0].GetProperty("tags").GetArrayLength());
        Assert.Equal(150, children.GetProperty("lazy").GetProperty("group-visible").GetProperty("tags").GetInt32());
        Assert.Equal(150, group.GetProperty("tags").GetArrayLength());
        Assert.Equal(150, deep.GetProperty("groups")[0].GetProperty("tags").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/v1/project/children/group/missing")).StatusCode);
    }

    [Fact]
    public async Task Batch_mutation_applies_all_changes_in_one_revision_or_rolls_back_entirely()
    {
        await using var fixture = await ApiFixture.CreateAsync(ProjectWithConfigurationRows());
        object[] items =
        [
            new { kind = "deleteEntities", entityKind = "group", entityIds = new[] { "group-hidden" }, targetId = (string?)null, documentJson = (string?)null },
            new { kind = "upsertEntity", entityKind = "group", entityIds = new[] { "group-visible" }, targetId = "device-1",
                documentJson = "{\"id\":\"group-visible\",\"name\":\"Renamed\",\"kepProperties\":{\"servermain.GROUP_X\":1}}" },
            new { kind = "upsertEntity", entityKind = "group", entityIds = new[] { "group-new" }, targetId = "group-visible",
                documentJson = "{\"id\":\"group-new\",\"name\":\"Child\"}" },
            new { kind = "upsertEntity", entityKind = "tag", entityIds = new[] { "tag-new" }, targetId = "group-new",
                documentJson = "{\"id\":\"tag-new\",\"name\":\"Speed\",\"address\":\"DB2.DBW0\",\"dataType\":\"int16\"}" },
        ];

        HttpResponseMessage applied = await fixture.Client.SendAsync(Batch(items, "\"project-1\""));
        JsonElement appliedBody = await applied.Content.ReadFromJsonAsync<JsonElement>();
        string appliedRevision = applied.Headers.ETag!.Tag;
        JsonElement device = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/children/device/device-1?deep=true");
        HttpResponseMessage failed = await fixture.Client.SendAsync(Batch(
        [
            new { kind = "upsertEntity", entityKind = "tag", entityIds = new[] { "tag-other" }, targetId = "group-new",
                documentJson = "{\"id\":\"tag-other\",\"name\":\"Other\",\"address\":\"DB2.DBW2\",\"dataType\":\"int16\"}" },
            new { kind = "deleteEntities", entityKind = "tag", entityIds = new[] { "missing" }, targetId = (string?)null, documentJson = (string?)null },
        ], appliedRevision));
        JsonElement child = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/children/group/group-new");

        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        // 与整包保存一样清理失效的 MES 任务(再存一次 MES 配置，修订号多进一位)：
        // Hidden 组被删；Visible 组改了名，但这批修改没有一起提交迁移后的任务(工程页会同批提交)，其任务同样失效
        Assert.True(appliedBody.GetProperty("mesTasksReconciled").GetBoolean());
        Assert.Equal("\"project-3\"", appliedRevision);
        Assert.Equal(["task-device-2", "task-channel-b"], fixture.Host.Project.MesUpload.Tasks.Select(task => task.Id));
        JsonElement renamed = Assert.Single(device.GetProperty("groups").EnumerateArray());
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal(1, renamed.GetProperty("kepProperties").GetProperty("servermain.GROUP_X").GetInt32());
        Assert.Equal("Speed", renamed.GetProperty("subGroups")[0].GetProperty("tags")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);   // 修订号匹配，因“删除不存在的点”整批回滚
        Assert.Equal(["Speed"], child.GetProperty("tags").EnumerateArray().Select(item => item.GetProperty("name").GetString()!));
    }

    [Fact]
    public async Task Unchanged_secret_placeholders_from_the_shell_keep_the_stored_password()
    {
        var initial = ProjectWithConfigurationRows();
        initial.Channels[0].Settings["password"] = "plc-secret";
        await using var fixture = await ApiFixture.CreateAsync(initial);
        JsonElement shell = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/v1/project/shell");
        JsonElement channel = shell.GetProperty("project").GetProperty("channels")[0];
        Assert.Equal(ProjectConfigurationRedactor.UnchangedSecret, channel.GetProperty("settings").GetProperty("password").GetString());

        var document = new ChannelDef { Id = "channel-a", Name = "LineA", DriverId = "Simulator", Description = "改说明",
            Settings = new() { ["password"] = ProjectConfigurationRedactor.UnchangedSecret } };
        HttpResponseMessage response = await fixture.Client.SendAsync(Batch(
        [
            new { kind = "upsertEntity", entityKind = "channel", entityIds = new[] { "channel-a" }, targetId = (string?)null,
                documentJson = JsonSerializer.Serialize(document, ProjectStore.Options) },
            new { kind = "deleteEntities", entityKind = "group", entityIds = new[] { "group-hidden" }, targetId = (string?)null, documentJson = (string?)null },
        ], "\"project-1\""));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        ChannelDef live = fixture.Host.Project.Channels.Single(item => item.Id == "channel-a");
        Assert.Equal("改说明", live.Description);
        Assert.Equal("plc-secret", live.Settings["password"]);
    }

    private static HttpRequestMessage Batch(object[] items, string revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/mutations")
        {
            Content = JsonContent.Create(new { kind = "batch", entityKind = "project", entityIds = Array.Empty<string>(),
                targetId = (string?)null, documentJson = JsonSerializer.Serialize(items) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", revision);
        return request;
    }

    private static IgwProject ProjectWithConfigurationRows()
    {
        var visible = new TagGroupDef
        {
            Id = "group-visible",
            Name = "Visible",
            Description = "可用流程组",
        };
        for (int index = 0; index < 150; index++)
        {
            visible.Tags.Add(new TagDef
            {
                Id = $"tag-{index:D3}",
                Name = $"Temperature{index:D3}",
                Address = $"DB1.DBW{index * 2}",
                DataType = IgwDataType.Int16,
                Description = $"温度点 {index}",
            });
        }

        return new IgwProject
        {
            Name = "分页配置 API",
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel-a",
                    Name = "LineA",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-1",
                            Name = "PLC1",
                            Groups =
                            [
                                visible,
                                new TagGroupDef { Id = "group-hidden", Name = "Hidden" },
                            ],
                        },
                        new DeviceDef
                        {
                            Id = "device-2",
                            Name = "PLC2",
                            Groups = [new TagGroupDef { Id = "group-device-2", Name = "Other" }],
                        },
                    ],
                },
                new ChannelDef
                {
                    Id = "channel-b",
                    Name = "LineB",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-3",
                            Name = "PLC3",
                            Groups = [new TagGroupDef { Id = "group-channel-b", Name = "Remote" }],
                        },
                    ],
                },
            ],
            MesUpload = new MesUploadSettings
            {
                Tasks =
                [
                    new MesTask { Id = "task-visible", Name = "Visible", GroupRef = "LineA.PLC1.Visible", DeviceRef = "LineA.PLC1", FlowAvailable = true, Enabled = true },
                    new MesTask { Id = "task-hidden", Name = "Hidden", GroupRef = "LineA.PLC1.Hidden", DeviceRef = "LineA.PLC1", FlowAvailable = false },
                    new MesTask { Id = "task-device-2", Name = "Other", GroupRef = "LineA.PLC2.Other", DeviceRef = "LineA.PLC2", FlowAvailable = true, Enabled = false },
                    new MesTask { Id = "task-channel-b", Name = "Remote", GroupRef = "LineB.PLC3.Remote", DeviceRef = "LineB.PLC3", FlowAvailable = true },
                ],
            },
        };
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly WebApplication _app;
        private readonly GatewayHost _host;
        public HttpClient Client { get; }
        public GatewayHost Host => _host;

        private ApiFixture(string directory, WebApplication app, GatewayHost host)
        {
            _directory = directory;
            _app = app;
            _host = host;
            Client = app.GetTestClient();
        }

        public static async Task<ApiFixture> CreateAsync(IgwProject initial, bool mapFullApi = false)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-project-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new ProjectDatabase(Path.Combine(directory, "project.db"));
            var repository = new SqliteProjectRepository(
                database,
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            ProjectRevision revision = await repository.ReplaceFromSnapshotAsync(initial, new ProjectRevision(0), default);
            var host = new GatewayHost(
                await repository.LoadRuntimeProjectAsync(default),
                repository,
                revision,
                new DriverRegistry(),
                NullLoggerFactory.Instance,
                directory);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            WebApplication app = builder.Build();
            if (mapFullApi) app.MapIgwApi(host);
            else app.MapProjectConfigurationApi(host);
            await app.StartAsync();
            return new ApiFixture(directory, app, host);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _host.DisposeAsync();
            foreach (string file in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
