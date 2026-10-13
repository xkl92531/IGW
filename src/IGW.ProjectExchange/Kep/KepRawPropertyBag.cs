using System.Text.Json;

namespace IGW.ProjectExchange.Kep;

public static class KepRawPropertyBag
{
    private static readonly HashSet<string> HierarchyNames = new(StringComparer.Ordinal)
    {
        "project", "channels", "devices", "tags", "tag_groups", "igw",
    };

    public static Dictionary<string, JsonElement> Capture(
        JsonElement source,
        IEnumerable<string>? excluded = null)
    {
        var exclusions = excluded is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(excluded, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in source.EnumerateObject())
        {
            if (HierarchyNames.Contains(property.Name) || exclusions.Contains(property.Name)) continue;
            result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    public static void WriteUnknown(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, JsonElement>? properties,
        IEnumerable<string> reserved)
    {
        if (properties is null || properties.Count == 0) return;
        var blocked = new HashSet<string>(reserved, StringComparer.Ordinal);
        blocked.UnionWith(HierarchyNames);
        foreach ((string name, JsonElement value) in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (blocked.Contains(name)) continue;
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }
}
