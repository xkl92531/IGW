using System.Text.Json;
using System.Text.Json.Nodes;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

public sealed partial class MesUploadEngine
{
    private static IEnumerable<string> ExtraFieldTagIds(MesPayload payload) =>
        (payload.ExtraFields ?? []).Where(f => f.Source == "Tag" && !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path);

    private BuiltBody AddExtraFields(MesPayload payload, BuiltBody built, Dictionary<string, string> variables, bool automatic = true)
    {
        MesExtraFieldValidation.Validate(payload, automatic);
        var output = JsonNode.Parse(built.Body) as JsonObject ?? throw new InvalidDataException("业务字段需要 JSON 对象报文。");
        var context = JsonContext(null, variables);
        foreach (var field in payload.ExtraFields)
        {
            object? raw = null;
            switch (field.Source)
            {
                case "Fixed": raw = field.Value; break;
                case "Tag":
                    var device = string.Join('.', field.Path.Split('.').Take(2));
                    var state = _deviceState(device);
                    // A known offline device may retain an old Good cache value.
                    if (state is null or ConnectionState.Connected && _hub.TryGet(field.Path, out var value) && value.IsGood) raw = value.Value;
                    break;
                case "Input":
                    if (context.TryGetProperty("input", out var source) &&
                        MesJsonCodec.TrySelect(source, field.Path, out var selected)) raw = selected;
                    break;
            }
            var typed = MesExtraFieldValidation.ConvertValue(field, raw, field.Source == "Fixed");
            var parts = field.Name.Split('.');
            JsonObject current = output;
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                bool leaf = index == parts.Length - 1;
                if (current.TryGetPropertyValue(part, out var existing))
                {
                    if (leaf || existing is not JsonObject obj)
                        throw new InvalidDataException("业务字段与现有报文路径冲突：" + field.Name);
                    current = obj;
                }
                else if (leaf) current[part] = JsonNode.Parse(typed.GetRawText());
                else { var child = new JsonObject(); current[part] = child; current = child; }
            }
        }
        return built with { Body = output.ToJsonString() };
    }
}
