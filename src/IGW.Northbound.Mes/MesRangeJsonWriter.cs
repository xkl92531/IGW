using System.Buffers;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

public sealed record MesPayloadBuildResult(
    string Json,
    IReadOnlyList<string> OfflineSources,
    IReadOnlyList<string> BadTags,
    int TagCount)
{
    public bool Partial => OfflineSources.Count > 0 || BadTags.Count > 0;
}

internal static class MesPayloadValuePolicy
{
    public static object? Read(IDataHub hub, string tagId, out bool bad)
    {
        if (hub.TryGet(tagId, out var data) && data.IsGood && data.Value is not null)
        {
            bad = false;
            return data.Value;
        }

        bad = true;
        return hub.TryGetDef(tagId, out var definition) &&
               definition.DataType is IgwDataType.String or IgwDataType.WString
            ? "Bad"
            : null;
    }
}

public static class MesRangeJsonWriter
{
    public static MesPayloadBuildResult Build(
        CompiledMesPayload payload,
        IReadOnlyDictionary<string, string> append,
        IDataHub hub,
        Func<string, ConnectionState?> deviceState,
        MesNumberFormat? number = null,
        IReadOnlyList<MesFixedValue>? fixedValues = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var badTags = new List<string>();
        var offlineSources = new List<string>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteOutputProperties(writer, payload.Root.Properties, hub, deviceState, offlineSources, badTags, number);
            foreach (var item in append)
                writer.WriteString(item.Key, item.Value);
            if (fixedValues is { Count: > 0 })
                foreach (var pair in MesUploadEngine.FixedValuesJson(fixedValues))
                {
                    writer.WritePropertyName(pair.Key);
                    if (pair.Value is null) writer.WriteNullValue();
                    else pair.Value.WriteTo(writer);
                }
            writer.WriteEndObject();
        }
        return new MesPayloadBuildResult(
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            offlineSources,
            badTags,
            payload.TagCount);
    }

    private static void WriteOutputProperties(
        Utf8JsonWriter writer,
        IReadOnlyList<CompiledMesOutputProperty> properties,
        IDataHub hub,
        Func<string, ConnectionState?> deviceState,
        List<string> offlineSources,
        List<string> badTags,
        MesNumberFormat? number)
    {
        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Name);
            switch (property.Value)
            {
                case CompiledMesOutputObject child:
                    writer.WriteStartObject();
                    WriteOutputProperties(writer, child.Properties, hub, deviceState, offlineSources, badTags, number);
                    writer.WriteEndObject();
                    break;
                case CompiledMesOutputRange range:
                    if (deviceState(range.Range.DevicePath) != ConnectionState.Connected)
                    {
                        writer.WriteNullValue();
                        offlineSources.Add(range.Range.SourcePath);
                        break;
                    }
                    WriteSourceValue(writer, range.Range.Source, hub, badTags, number);
                    break;
            }
        }
    }

    private static void WriteSourceObject(
        Utf8JsonWriter writer,
        CompiledMesSourceObject source,
        IDataHub hub,
        List<string> badTags,
        MesNumberFormat? number)
    {
        writer.WriteStartObject();
        foreach (var property in source.Properties)
        {
            writer.WritePropertyName(property.Name);
            WriteSourceValue(writer, property.Value, hub, badTags, number);
        }
        writer.WriteEndObject();
    }

    private static void WriteSourceValue(
        Utf8JsonWriter writer,
        CompiledMesSourceValue value,
        IDataHub hub,
        List<string> badTags,
        MesNumberFormat? number)
    {
        switch (value)
        {
            case CompiledMesSourceObject child:
                WriteSourceObject(writer, child, hub, badTags, number);
                break;
            case CompiledMesSourceArray legacyArray:
                writer.WriteStartArray();
                foreach (var element in legacyArray.Elements) WriteSourceValue(writer, element, hub, badTags, number);
                writer.WriteEndArray();
                break;
            case CompiledMesSourceField field:
                // 无穷大/NaN 直接序列化会抛异常(整包发不出去)：统一按数值规则处理
                object? valueObject = MesNumberPolicy.Apply(MesPayloadValuePolicy.Read(hub, field.TagId, out bool bad), number);
                if (bad)
                    badTags.Add(field.TagId);
                if (valueObject is null)
                {
                    writer.WriteNullValue();
                    break;
                }
                JsonSerializer.Serialize(writer, valueObject, valueObject.GetType());
                break;
            case CompiledMesSourceLogicalArray logical:
                if (!hub.TryGet(logical.TagId, out var data) || data.Value is not ArrayRuntimeValue array)
                {
                    writer.WriteNullValue();
                    badTags.Add(logical.TagId);
                    break;
                }
                try
                {
                    var selected = MesArrayProjection.Select(array, logical.Slice, logical.MemberPath);
                    var wire = MesNumberPolicy.Apply(MesArrayProjection.ToWireValue(selected, logical.MemberNames, logical.OmittedMembers), number);
                    JsonSerializer.Serialize(writer, wire, wire?.GetType() ?? typeof(object));
                    if (selected.BadCount > 0) badTags.Add(logical.TagId);
                }
                catch
                {
                    writer.WriteNullValue();
                    badTags.Add(logical.TagId);
                }
                break;
        }
    }
}
