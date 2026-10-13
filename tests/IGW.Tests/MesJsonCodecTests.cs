using System.Text.Json;
using IGW.Abstractions;
using IGW.Northbound.Mes;
using Xunit;

namespace IGW.Tests;

public sealed class MesJsonCodecTests
{
    [Theory]
    [InlineData("$.input.parts[1].count", "2")]
    [InlineData("input.parts[0].ok", "true")]
    [InlineData("input.parts[0].none", "null")]
    [InlineData("/a.b/~1~0", "42")]
    public void Select_reads_nested_arrays_and_literal_pointer_keys(string path, string expected)
    {
        using var doc = JsonDocument.Parse("""{"input":{"parts":[{"ok":true,"none":null},{"count":2}]},"a.b":{"/~":42}}""");
        Assert.True(MesJsonCodec.TrySelect(doc.RootElement, path, out var value));
        Assert.Equal(expected, value.GetRawText());
    }

    [Theory]
    [InlineData("input.parts[-1]")]
    [InlineData("input.parts[99]")]
    [InlineData("input.parts[*]")]
    [InlineData("input..parts")]
    [InlineData("input.parts[0]x")]
    [InlineData("input.parts[0].")]
    [InlineData("input.parts[999999999999999]")]
    [InlineData("input.missing")]
    [InlineData("/input/~2bad")]
    public void Select_returns_false_for_invalid_or_missing_paths(string path)
    {
        using var doc = JsonDocument.Parse("""{"input":{"parts":[1]}}""");
        Assert.False(MesJsonCodec.TrySelect(doc.RootElement, path, out _));
    }

    [Fact]
    public void Select_accepts_root_array_and_whole_root()
    {
        using var doc = JsonDocument.Parse("[3,4]");
        Assert.True(MesJsonCodec.TrySelect(doc.RootElement, "$[1]", out var value));
        Assert.Equal(4, value.GetInt32());
        Assert.True(MesJsonCodec.TrySelect(doc.RootElement, "$", out value));
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    [Fact]
    public void Render_preserves_native_values_in_whole_placeholders_and_nested_templates()
    {
        using var context = JsonDocument.Parse("""{"job":{"count":2,"ok":true,"note":null,"parts":[{"id":1}]}}""");
        using var rendered = JsonDocument.Parse(MesJsonCodec.Render("""{"count":"${job.count}","ok":"${job.ok}","note":"${job.note}","parts":"${job.parts}","nested":["${job.count}","literal"]}""", context.RootElement));
        var root = rendered.RootElement;
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
        Assert.Equal(1, root.GetProperty("parts")[0].GetProperty("id").GetInt32());
        Assert.Equal(2, root.GetProperty("nested")[0].GetInt32());
    }

    [Fact]
    public void Render_escapes_embedded_values_and_does_not_recursively_expand_input()
    {
        using var context = JsonDocument.Parse("""{"text":"quote\"\\\n${absent}","ok":false,"n":2,"object":{"a":1}}""");
        using var rendered = JsonDocument.Parse(MesJsonCodec.Render("""{"text":"prefix:${text}|${ok}|${n}|${object}"}""", context.RootElement));
        Assert.Equal("prefix:quote\"\\\n${absent}|false|2|{\"a\":1}", rendered.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Render_supports_native_root_placeholder()
    {
        using var context = JsonDocument.Parse("""{"value":[1,true]}""");
        using var rendered = JsonDocument.Parse(MesJsonCodec.Render("\"${value}\"", context.RootElement));
        Assert.Equal(JsonValueKind.Array, rendered.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("\"${absent}\"")]
    [InlineData("\"prefix ${absent}\"")]
    [InlineData("\"${parts[*]}\"")]
    public void Render_rejects_missing_or_invalid_placeholder_paths(string template)
    {
        using var context = JsonDocument.Parse("{}");
        Assert.Throws<InvalidDataException>(() => MesJsonCodec.Render(template, context.RootElement));
    }

    [Fact]
    public void Map_builds_nested_objects_and_arrays_with_native_values()
    {
        using var doc = JsonDocument.Parse("""{"result":{"count":3,"ok":true,"none":null,"items":[1,2]}}""");
        var mapped = MesJsonCodec.Map(doc.RootElement,
        [
            new() { SourcePath = "result.count", TargetPath = "job.items[1].count" },
            new() { SourcePath = "result.ok", TargetPath = "job.items[1].ok" },
            new() { SourcePath = "result.none", TargetPath = "optional" },
            new() { SourcePath = "result.items", TargetPath = "values" },
        ]);
        Assert.Equal(JsonValueKind.Null, mapped.GetProperty("job").GetProperty("items")[0].ValueKind);
        Assert.Equal(3, mapped.GetProperty("job").GetProperty("items")[1].GetProperty("count").GetInt32());
        Assert.True(mapped.GetProperty("job").GetProperty("items")[1].GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mapped.GetProperty("optional").ValueKind);
        Assert.Equal(2, mapped.GetProperty("values")[1].GetInt32());
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "$.a")]
    [InlineData("a", "a.b")]
    [InlineData("a.b", "a")]
    [InlineData("a[0]", "a[0].b")]
    [InlineData("a.b", "a[0]")]
    public void Map_rejects_duplicate_ancestor_and_container_conflicts(string first, string second)
    {
        using var doc = JsonDocument.Parse("""{"value":null}""");
        Assert.Throws<InvalidDataException>(() => MesJsonCodec.Map(doc.RootElement,
        [new() { SourcePath = "value", TargetPath = first }, new() { SourcePath = "value", TargetPath = second }]));
    }

    [Theory]
    [InlineData("missing", "ok")]
    [InlineData("value", "a..b")]
    [InlineData("value", "a[-1]")]
    [InlineData("value", "a[2147483647]")]
    [InlineData("value", "[0]")]
    [InlineData("value", "$")]
    public void Map_rejects_missing_source_or_invalid_target(string source, string target)
    {
        using var doc = JsonDocument.Parse("""{"value":1}""");
        Assert.Throws<InvalidDataException>(() => MesJsonCodec.Map(doc.RootElement,
            [new() { SourcePath = source, TargetPath = target }]));
    }

    [Fact]
    public void Map_returns_an_independent_clone_when_mappings_are_empty()
    {
        JsonElement result;
        using (var doc = JsonDocument.Parse("""{"value":[1]}"""))
            result = MesJsonCodec.Map(doc.RootElement, Array.Empty<MesJsonMapping>());
        Assert.Equal(1, result.GetProperty("value")[0].GetInt32());
    }

    [Fact]
    public void Map_clones_objects_independently_of_source_document_lifetime()
    {
        JsonElement result;
        using (var doc = JsonDocument.Parse("""{"value":{"name":"part"}}"""))
            result = MesJsonCodec.Map(doc.RootElement,
                [new() { SourcePath = "value", TargetPath = "job" }]);
        Assert.Equal("part", result.GetProperty("job").GetProperty("name").GetString());
    }
}
