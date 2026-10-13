using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator.Kep;
using IGW.Northbound.RestApi;
using IGW.Persistence.Sqlite;
using IGW.ProjectExchange;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectExchangeApiTests
{
    [Fact]
    public async Task Inspect_is_read_only_and_commit_applies_the_exact_previewed_file_once()
    {
        await using ApiFixture source = await ApiFixture.CreateAsync(ProjectStorageSmokePerformanceTests.BuildProject(4));
        HttpResponseMessage exportResponse = await source.Client.GetAsync("/api/v1/project/export");
        string exportBody = await exportResponse.Content.ReadAsStringAsync();
        Assert.True(exportResponse.IsSuccessStatusCode, exportBody);
        byte[] exported = System.Text.Encoding.UTF8.GetBytes(exportBody);
        await using ApiFixture target = await ApiFixture.CreateAsync(ProjectStorageSmokePerformanceTests.BuildProject(1));
        ProjectRevision before = await target.Repository.GetRevisionAsync(default);

        HttpResponseMessage inspected = await target.Client.PostAsync(
            "/api/v1/project/import/inspect", Upload(exported));
        JsonElement preview = await inspected.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
        Assert.Equal(4, preview.GetProperty("tags").GetInt64());
        Assert.Equal(before, await target.Repository.GetRevisionAsync(default));

        HttpResponseMessage committed = await target.Client.PostAsync(
            "/api/v1/project/import/commit",
            Upload(exported,
                ("token", preview.GetProperty("token").GetString()!),
                ("fileHash", preview.GetProperty("fileHash").GetString()!)));
        JsonElement result = await committed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal(before.Value + 1, (await target.Repository.GetRevisionAsync(default)).Value);
        Assert.Equal(4, CountTags(await target.Repository.LoadRuntimeProjectAsync(default)));
    }

    [Fact]
    public async Task Export_is_streamed_with_a_safe_filename_and_secret_export_requires_password()
    {
        IgwProject project = ProjectStorageSmokePerformanceTests.BuildProject(2);
        project.Name = "Line/One:*?";
        project.MqttPublish.Password = "portable-plain-marker";
        await using ApiFixture fixture = await ApiFixture.CreateAsync(project);

        HttpResponseMessage rejected = await fixture.Client.GetAsync(
            "/api/v1/project/export?includePortableSecrets=true");
        JsonElement error = await rejected.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("migration_password_required", error.GetProperty("error").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/project/export?includePortableSecrets=true");
        request.Headers.TryAddWithoutValidation("X-Igw-Migration-Password", "migration-password");
        HttpResponseMessage response = await fixture.Client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, json);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain('/', response.Content.Headers.ContentDisposition?.FileNameStar ?? "");
        Assert.DoesNotContain("portable-plain-marker", json, StringComparison.Ordinal);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("project", out _));
        Assert.True(parsed.RootElement.GetProperty("igw").TryGetProperty("secrets", out _));
    }

    [Fact]
    public async Task Browser_native_form_can_stream_an_export_with_portable_secrets()
    {
        IgwProject project = ProjectStorageSmokePerformanceTests.BuildProject(1);
        project.MqttPublish.Password = "portable-plain-marker";
        await using ApiFixture fixture = await ApiFixture.CreateAsync(project);

        HttpResponseMessage response = await fixture.Client.PostAsync(
            "/api/v1/project/export",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["includePortableSecrets"] = "true",
                ["migrationPassword"] = "migration-password",
            }));
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, json);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("portable-plain-marker", json, StringComparison.Ordinal);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("igw").TryGetProperty("secrets", out _));
    }

    private static MultipartFormDataContent Upload(byte[] json, params (string Name, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(json), "file", "project.json");
        foreach ((string name, string value) in fields) content.Add(new StringContent(value), name);
        return content;
    }

    private static int CountTags(IgwProject project) => project.Channels.Sum(channel =>
        channel.Devices.Sum(device => device.Tags.Count + CountGroups(device.Groups)));
    private static int CountGroups(IEnumerable<TagGroupDef> groups) => groups.Sum(group =>
        group.Tags.Count + CountGroups(group.SubGroups));

    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly WebApplication _application;
        private readonly ProjectJsonImporter _service;
        public SqliteProjectRepository Repository { get; }
        public HttpClient Client { get; }

        private ApiFixture(
            string directory,
            WebApplication application,
            ProjectJsonImporter service,
            SqliteProjectRepository repository)
        {
            _directory = directory;
            _application = application;
            _service = service;
            Repository = repository;
            Client = application.GetTestClient();
        }

        public static async Task<ApiFixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-exchange-api", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var repository = new SqliteProjectRepository(
                new ProjectDatabase(Path.Combine(directory, "project.db")),
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            var service = new ProjectJsonImporter(repository,
                [new SimulatorKepDriverCodec()],
                new ProjectImportSessionStore(Path.Combine(directory, "imports")));

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            WebApplication application = builder.Build();
            application.MapProjectExchangeApi(service, () => project.Name);
            await application.StartAsync();
            return new ApiFixture(directory, application, service, repository);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
            await _service.DisposeAsync();
            await Repository.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
