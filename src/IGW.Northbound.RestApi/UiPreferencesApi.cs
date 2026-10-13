using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

public sealed record UiPreferencesRequest(string? Locale, bool? ProjectAutoSave = null, int? ProjectAutoSaveSeconds = null);

public static class UiPreferencesApi
{
    public static void MapUiPreferencesApi(this IEndpointRouteBuilder app, UiPreferenceStore store)
    {
        app.MapGet("/api/v1/ui/preferences", () => Results.Ok(Snapshot(store)));
        app.MapPut("/api/v1/ui/preferences", (UiPreferencesRequest request) =>
        {
            try
            {
                // 各项可单独提交：只改语言时不影响自动保存设置，反之亦然。
                if (request.Locale is not null) store.SetLocale(request.Locale);
                if (request.ProjectAutoSave is not null || request.ProjectAutoSaveSeconds is not null)
                    store.SetProjectAutoSave(
                        request.ProjectAutoSave ?? store.GetProjectAutoSave(),
                        request.ProjectAutoSaveSeconds ?? store.GetProjectAutoSaveSeconds());
                return Results.Ok(Snapshot(store));
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });
    }

    private static object Snapshot(UiPreferenceStore store) => new
    {
        locale = store.GetLocale(),
        projectAutoSave = store.GetProjectAutoSave(),
        projectAutoSaveSeconds = store.GetProjectAutoSaveSeconds(),
    };
}
