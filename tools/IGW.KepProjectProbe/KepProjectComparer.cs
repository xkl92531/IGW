using System.Text.Json;

namespace IGW.KepProjectProbe;

public sealed record KepDifference(string Path, string Kind, string? Expected, string? Actual);
public sealed record KepComparisonReport(IReadOnlyList<KepDifference> Differences)
{
    public bool Equivalent => Differences.Count == 0;
}

public static class KepProjectComparer
{
    public static KepComparisonReport Compare(JsonElement expectedSource, JsonElement actualSource)
    {
        JsonElement expected = Project(expectedSource);
        JsonElement actual = Project(actualSource);
        var differences = new List<KepDifference>();
        CompareObjectProperties(expected, actual, "project", ["channels"], differences);
        CompareNamedCollection(expected, actual, "channels", "project/channels", differences, CompareChannel);
        return new KepComparisonReport(differences);
    }

    private static void CompareChannel(JsonElement expected, JsonElement actual, string path, List<KepDifference> output)
    {
        CompareObjectProperties(expected, actual, path, ["devices"], output);
        CompareNamedCollection(expected, actual, "devices", path + "/devices", output, CompareDevice);
    }

    private static void CompareDevice(JsonElement expected, JsonElement actual, string path, List<KepDifference> output)
    {
        CompareObjectProperties(expected, actual, path, ["tags", "tag_groups"], output);
        CompareNamedCollection(expected, actual, "tags", path + "/tags", output, CompareLeaf);
        CompareNamedCollection(expected, actual, "tag_groups", path + "/tag_groups", output, CompareGroup);
    }

    private static void CompareGroup(JsonElement expected, JsonElement actual, string path, List<KepDifference> output)
    {
        CompareObjectProperties(expected, actual, path, ["tags", "tag_groups"], output);
        CompareNamedCollection(expected, actual, "tags", path + "/tags", output, CompareLeaf);
        CompareNamedCollection(expected, actual, "tag_groups", path + "/tag_groups", output, CompareGroup);
    }

    private static void CompareLeaf(JsonElement expected, JsonElement actual, string path, List<KepDifference> output) =>
        CompareObjectProperties(expected, actual, path, [], output);

    private static void CompareNamedCollection(
        JsonElement expectedParent,
        JsonElement actualParent,
        string property,
        string path,
        List<KepDifference> output,
        Action<JsonElement, JsonElement, string, List<KepDifference>> compare)
    {
        Dictionary<string, JsonElement> expected = Named(expectedParent, property, path, output, "expected");
        Dictionary<string, JsonElement> actual = Named(actualParent, property, path, output, "actual");
        foreach ((string name, JsonElement item) in expected.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string itemPath = $"{path}[{name}]";
            if (!actual.TryGetValue(name, out JsonElement actualItem))
                output.Add(new KepDifference(itemPath, "missing", item.GetRawText(), null));
            else compare(item, actualItem, itemPath, output);
        }
        foreach ((string name, JsonElement item) in actual.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            if (!expected.ContainsKey(name))
                output.Add(new KepDifference($"{path}[{name}]", "unexpected", null, item.GetRawText()));
    }

    private static Dictionary<string, JsonElement> Named(
        JsonElement parent,
        string property,
        string path,
        List<KepDifference> output,
        string side)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(property, out JsonElement array)) return result;
        if (array.ValueKind != JsonValueKind.Array)
        {
            output.Add(new KepDifference(path, "invalid", side == "expected" ? "array" : null,
                side == "actual" ? "array" : array.ValueKind.ToString()));
            return result;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            string name = item.TryGetProperty("common.ALLTYPES_NAME", out JsonElement value)
                ? value.GetString() ?? "" : "";
            if (!result.TryAdd(name, item))
                output.Add(new KepDifference($"{path}[{name}]", "duplicate", side, side));
        }
        return result;
    }

    private static void CompareObjectProperties(
        JsonElement expected,
        JsonElement actual,
        string path,
        IReadOnlyCollection<string> ignored,
        List<KepDifference> output)
    {
        Dictionary<string, JsonElement> left = Properties(expected, ignored);
        Dictionary<string, JsonElement> right = Properties(actual, ignored);
        foreach ((string name, JsonElement value) in left.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string propertyPath = path + "/" + name;
            if (!right.TryGetValue(name, out JsonElement other))
                output.Add(new KepDifference(propertyPath, "missing", value.GetRawText(), null));
            else if (!JsonElement.DeepEquals(value, other))
                output.Add(new KepDifference(propertyPath, "changed", value.GetRawText(), other.GetRawText()));
        }
        foreach ((string name, JsonElement value) in right.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!left.ContainsKey(name))
                output.Add(new KepDifference(path + "/" + name, "unexpected", null, value.GetRawText()));
    }

    private static Dictionary<string, JsonElement> Properties(
        JsonElement source,
        IReadOnlyCollection<string> ignored)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in source.EnumerateObject())
            if (!ignored.Contains(property.Name)) result[property.Name] = property.Value;
        return result;
    }

    private static JsonElement Project(JsonElement source) =>
        source.TryGetProperty("project", out JsonElement nested) ? nested : source;
}
