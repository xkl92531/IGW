using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

public static class MesJsonCodec
{
    // Limit sparse target expansion caused by a mistyped array index.
    private const int MaxTargetArrayLength = 10_000;
    private static readonly Regex Placeholder = new(@"\$\{([^{}]*)\}", RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Reads dot names and integer array indices, or RFC 6901 pointer paths for literal keys.</summary>
    public static bool TrySelect(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] == '/') return TrySelectPointer(root, path, out value);
        if (!TryParsePath(path, out var tokens)) return false;
        var current = root;
        foreach (var token in tokens)
        {
            if (token.Name is { } name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                    return false;
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Array || token.Index >= current.GetArrayLength())
                    return false;
                current = current[token.Index];
            }
        }
        value = current;
        return current.ValueKind != JsonValueKind.Undefined;
    }

    /// <summary>Renders JSON values; whole placeholders retain their JSON type, embedded placeholders become text.</summary>
    public static string Render(string template, JsonElement context)
    {
        using var document = JsonDocument.Parse(template);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteTemplate(writer, document.RootElement, context);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Maps source values into an independent object. Target paths use dot names and array indices below 10000.</summary>
    public static JsonElement Map(JsonElement root, IReadOnlyList<MesJsonMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0) return root.Clone();
        var output = new JsonObject();
        var assigned = new List<List<PathToken>>();
        foreach (var mapping in mappings)
        {
            if (mapping is null || !TryParsePath(mapping.TargetPath, out var target) || target.Count == 0 ||
                target[0].Name is null || target.Any(t => t.Name is null && t.Index >= MaxTargetArrayLength))
                throw new InvalidDataException($"Invalid JSON mapping target path: {mapping?.TargetPath}");
            foreach (var previous in assigned)
            {
                int shared = Math.Min(previous.Count, target.Count);
                if (previous.Take(shared).SequenceEqual(target.Take(shared)))
                    throw new InvalidDataException($"Conflicting JSON mapping target path: {mapping.TargetPath}");
            }
            var source = SelectRequired(root, mapping.SourcePath);
            JsonNode current = output;
            for (int index = 0; index < target.Count; index++)
            {
                var token = target[index];
                bool leaf = index == target.Count - 1;
                JsonNode? child;
                if (token.Name is { } name && current is JsonObject obj)
                {
                    if (leaf) { obj[name] = JsonNode.Parse(source.GetRawText()); break; }
                    if (!obj.TryGetPropertyValue(name, out child) || child is null)
                        obj[name] = child = NewContainer(target[index + 1]);
                }
                else if (token.Name is null && current is JsonArray array)
                {
                    while (array.Count <= token.Index) array.Add((JsonNode?)null);
                    if (leaf) { array[token.Index] = JsonNode.Parse(source.GetRawText()); break; }
                    child = array[token.Index];
                    if (child is null) array[token.Index] = child = NewContainer(target[index + 1]);
                }
                else throw new InvalidDataException($"Conflicting JSON mapping container: {mapping.TargetPath}");
                current = child;
            }
            assigned.Add(target);
        }
        return JsonSerializer.SerializeToElement(output);
    }

    private static JsonNode NewContainer(PathToken next) => next.Name is null ? new JsonArray() : new JsonObject();

    private static JsonElement SelectRequired(JsonElement root, string path)
    {
        if (!TrySelect(root, path, out var value))
            throw new InvalidDataException($"JSON path is missing or invalid: {path}");
        return value;
    }

    private static void WriteTemplate(Utf8JsonWriter writer, JsonElement template, JsonElement context)
    {
        switch (template.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in template.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteTemplate(writer, property.Value, context);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in template.EnumerateArray()) WriteTemplate(writer, item, context);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                string text = template.GetString()!;
                var match = Placeholder.Match(text);
                if (match.Success && match.Index == 0 && match.Length == text.Length)
                    SelectRequired(context, match.Groups[1].Value).WriteTo(writer);
                else
                    writer.WriteStringValue(Placeholder.Replace(text, m =>
                    {
                        var value = SelectRequired(context, m.Groups[1].Value);
                        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                    }));
                break;
            default:
                template.WriteTo(writer);
                break;
        }
    }

    private readonly record struct PathToken(string? Name, int Index = 0);

    private static bool TryParsePath(string path, out List<PathToken> tokens)
    {
        tokens = [];
        if (string.IsNullOrWhiteSpace(path)) return false;
        int offset = 0;
        if (path[0] == '$')
        {
            if (path.Length == 1) return true;
            offset = 1;
            if (path[offset] == '.')
            {
                offset++;
                if (offset == path.Length || path[offset] == '[') return false;
            }
            else if (path[offset] != '[') return false;
        }
        while (offset < path.Length)
        {
            if (path[offset] == '[')
            {
                int start = ++offset;
                while (offset < path.Length && char.IsAsciiDigit(path[offset])) offset++;
                if (offset == start || offset == path.Length || path[offset] != ']' ||
                    !int.TryParse(path.AsSpan(start, offset - start), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    return false;
                tokens.Add(new(null, index));
                offset++;
            }
            else
            {
                int start = offset;
                while (offset < path.Length && path[offset] != '.' && path[offset] != '[' && path[offset] != ']') offset++;
                if (start == offset) return false;
                tokens.Add(new(path[start..offset]));
            }
            if (offset == path.Length) return true;
            if (path[offset] == '[') continue;
            if (path[offset++] != '.' || offset == path.Length || path[offset] == '[') return false;
        }
        return true;
    }

    private static bool TrySelectPointer(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        var current = root;
        foreach (var encoded in path[1..].Split('/'))
        {
            var decoded = new StringBuilder();
            for (int index = 0; index < encoded.Length; index++)
            {
                if (encoded[index] != '~') { decoded.Append(encoded[index]); continue; }
                if (++index >= encoded.Length || (encoded[index] != '0' && encoded[index] != '1')) return false;
                decoded.Append(encoded[index] == '0' ? '~' : '/');
            }
            string part = decoded.ToString();
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(part, out current)) return false;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (part.Length == 0 || (part.Length > 1 && part[0] == '0') ||
                    !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                    index >= current.GetArrayLength()) return false;
                current = current[index];
            }
            else return false;
        }
        value = current;
        return true;
    }
}
