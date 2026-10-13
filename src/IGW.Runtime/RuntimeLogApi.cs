using IGW.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IGW.Runtime;

public static class RuntimeLogApi
{
    public static void MapRuntimeLogApi(
        this WebApplication app,
        FileLoggerProvider logs,
        Func<int> readRetentionDays,
        Func<RuntimeLogSettingsRequest, CancellationToken, Task> persistSettings)
    {
        var settingsGate = new SemaphoreSlim(1, 1);
        app.MapGet("/api/v1/runtime-logs", (string? level, string? search, DateOnly? date, int? limit, CancellationToken ct) =>
            Results.Ok(logs.Query(new RuntimeLogQuery(level, search, date, limit ?? 300), ct)));

        app.MapPut("/api/v1/runtime-logs/settings", async (RuntimeLogSettingsRequest request, CancellationToken ct) =>
        {
            if (request.RetentionDays is < 1 or > 3650)
                return Results.BadRequest(new { error = "日志保留天数必须在 1 到 3650 天之间。" });

            await settingsGate.WaitAsync(ct);
            try
            {
                // Missing directory preserves it for older clients; empty explicitly restores default.
                string directory = logs.PrepareStorageDirectory(request.StorageDirectory ?? logs.StorageDirectory);
                string savedDirectory = string.Equals(directory, logs.DefaultDirectory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "" : directory;
                await persistSettings(new(request.RetentionDays, savedDirectory), ct);
                logs.ApplyStorageSettings(request.RetentionDays, directory);
                return Results.Ok(new { retentionDays = readRetentionDays(), storageDirectory = logs.StorageDirectory, defaultDirectory = logs.DefaultDirectory });
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Results.BadRequest(new { error = $"日志设置保存失败：{ex.Message}" });
            }
            finally { settingsGate.Release(); }
        });

        app.MapGet("/api/v1/runtime-logs/directories", (string? path, CancellationToken ct) =>
        {
            try { return Results.Ok(GatewayDirectoryBrowser.List(path, ct)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Results.BadRequest(new { error = $"无法浏览网关目录：{ex.Message}" });
            }
        });
    }

    public sealed record RuntimeLogSettingsRequest(int RetentionDays, string? StorageDirectory = null);
}
