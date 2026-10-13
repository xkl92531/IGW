#nullable enable
using System.Text.Json;

namespace PlcSimulator.Core;

/// <summary>数组编辑使用 JSON，避免字符串中的逗号和引号被拆坏。</summary>
public static class SimulationValueText
{
    public static object Parse(SimulationTag tag, string text)
    {
        if (!tag.IsArray) return text;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new SimulationValueException("请输入 JSON 数组，例如 [1, 2, 3]");
            return json.RootElement.EnumerateArray().Select(item => item.ValueKind switch
            {
                JsonValueKind.String => (object)item.GetString()!,
                JsonValueKind.Number => item.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new SimulationValueException("数组元素必须是数字、布尔值或字符串"),
            }).ToArray();
        }
        catch (JsonException ex) { throw new SimulationValueException("数组格式无效，请输入 JSON 数组，例如 [1, 2, 3]", ex); }
    }

    public static string Format(object? value) => value is Array array
        ? "[" + string.Join(", ", array.Cast<object?>().Take(20).Select(item => JsonSerializer.Serialize(item))) + (array.Length > 20 ? $", … 共 {array.Length} 项]" : "]")
        : value?.ToString() ?? string.Empty;
}
