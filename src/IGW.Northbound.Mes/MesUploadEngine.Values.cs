using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

public sealed partial class MesUploadEngine
{
    private sealed class FlowValues(Dictionary<string, string> variables)
    {
        public Dictionary<string, string> Variables { get; } = variables;
        public JsonElement? Response { get; set; }
    }
    private readonly AsyncLocal<FlowValues?> _flowValues = new();

    private bool TryResolveValue(string source, string path, out object? value, out string error)
    {
        value = null;
        error = source + ":" + path + " 缺失或不是有效标量";
        if (source == "Tag")
        {
            var device = string.Join('.', path.Split('.').Take(2));
            if (_deviceState(device) is { } state && state != ConnectionState.Connected)
            { error = source + ":" + path + " 设备离线"; return false; }
            if (!_hub.TryGet(path, out var tag)) { error = source + ":" + path + " 点位不存在"; return false; }
            if (!tag.IsGood) { error = source + ":" + path + " 质量 " + tag.Quality.Class; return false; }
            value = tag.Value;
        }
        else
        {
            JsonElement? root = source switch
            {
                "Input" => _interfaceContext.Value?.Input,
                "Mapped" => _interfaceContext.Value?.Mapped,
                "Response" => _flowValues.Value?.Response,
                "Variable" => _flowValues.Value is { } execution ? JsonSerializer.SerializeToElement(execution.Variables) : null,
                _ => null,
            };
            if (root is { } document && MesJsonCodec.TrySelect(document, path, out var selected)) value = selected;
        }
        if (value is JsonElement element)
            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => null,
            };
        if (value is not (string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)) return false;
        if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)) return false;
        error = ""; return true;
    }

    private static string ScalarText(object value) => value is bool flag ? flag ? "true" : "false" : Convert.ToString(value, CultureInfo.InvariantCulture)!;
}
