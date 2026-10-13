using System.Text.Json;
using IGW.Core;

namespace IGW.Persistence.Sqlite;

internal static class ProjectEntityMapper
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ProjectStore.Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ProjectStore.Options)
        ?? throw new InvalidDataException($"工程数据库中的 {typeof(T).Name} 文档为空。");

    public static string EnsureId(string? id)
        => string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
}
