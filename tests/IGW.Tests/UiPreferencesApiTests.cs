using System.Net;
using System.Net.Http.Json;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class UiPreferencesApiTests
{
    [Fact]
    public async Task Locale_endpoint_reads_validates_and_persists_immediately()
    {
        string dir = Path.Combine(Path.GetTempPath(), "igw-ui-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new UiPreferenceStore(Path.Combine(dir, "ui-preferences.db"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapUiPreferencesApi(store);
            await app.StartAsync();
            using var client = app.GetTestClient();

            Assert.Equal("zh-CN", (await client.GetFromJsonAsync<PreferenceDto>("/api/v1/ui/preferences"))!.Locale);

            using var saved = await client.PutAsJsonAsync("/api/v1/ui/preferences", new { locale = "en-US" });
            saved.EnsureSuccessStatusCode();
            Assert.Equal("en-US", store.GetLocale());

            using var invalid = await client.PutAsJsonAsync("/api/v1/ui/preferences", new { locale = "fr-FR" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("en-US", store.GetLocale());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed record PreferenceDto(string Locale);
}
