using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using Opc.Ua;
using IgwStatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Northbound.OpcUaServer;

/// <summary>IGW 运行时数组与北向 OPC UA Variant 之间的转换。</summary>
internal static class IgwOpcUaArrayCodec
{
    private static readonly IServiceMessageContext MessageContext =
        new ServiceMessageContext(DefaultTelemetry.Create(_ => { }));
    public static object Encode(ArrayRuntimeValue value)
    {
        if (value is StructArrayValue structured)
        {
            var result = new ExtensionObject[value.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = new ExtensionObject(new IgwJsonStructure(
                    JsonSerializer.Serialize(structured.GetBoxedValue(i))));
            return result;
        }

        var dimensions = value.Shape.Dimensions.ToArray();
        var resultArray = Array.CreateInstance(ClrType(value.ElementType), dimensions);
        for (var i = 0; i < value.Count; i++)
            resultArray.SetValue(ConvertValue(value.GetBoxedValue(i), value.ElementType),
                ZeroBasedIndices(i, dimensions));
        return resultArray;
    }

    public static ArrayRuntimeValue Decode(object? source, ArrayDef definition)
    {
        var shape = definition.Shape;
        var items = Flatten(source).ToArray();
        if (items.Length != shape.ElementCount)
            throw new InvalidDataException($"OPC UA 数组元素数量不匹配(期望 {shape.ElementCount},实得 {items.Length})");
        var quality = Enumerable.Repeat(IgwStatusCode.Good, items.Length).ToArray();
        if (definition.ElementType == IgwDataType.Struct)
        {
            var schema = definition.Struct ?? throw new InvalidDataException("结构体数组缺少布局");
            var rows = items.Select(ParseRow).ToArray();
            return CreateStruct(shape, schema, rows, quality);
        }

        return Primitive(shape, definition.ElementType,
            items.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(), quality);
    }

    private static ArrayRuntimeValue Primitive(ArrayShape shape, IgwDataType type,
        IReadOnlyList<JsonElement> values, IReadOnlyList<IgwStatusCode> quality) => type switch
    {
        _ when SiemensValueCodec.IsText(type) => PrimitiveArrayValue<string>.Create(shape,
            values.Select(v => (string)SiemensValueCodec.Normalize(type, v)).ToArray(), quality, type),
        IgwDataType.SInt => PrimitiveArrayValue<sbyte>.Create(shape,
            values.Select(v => (sbyte)SiemensValueCodec.Normalize(type, v)).ToArray(), quality, type),
        IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => PrimitiveArrayValue<ushort>.Create(shape,
            values.Select(v => (ushort)SiemensValueCodec.Normalize(type, v)).ToArray(), quality, type),
        IgwDataType.DWord => PrimitiveArrayValue<uint>.Create(shape,
            values.Select(v => (uint)SiemensValueCodec.Normalize(type, v)).ToArray(), quality, type),
        IgwDataType.LWord => PrimitiveArrayValue<ulong>.Create(shape,
            values.Select(v => (ulong)SiemensValueCodec.Normalize(type, v)).ToArray(), quality, type),
        IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, values.Select(v => v.GetBoolean()).ToArray(), quality, type),
        IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, values.Select(v => v.GetByte()).ToArray(), quality, type),
        IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(shape, values.Select(v => v.GetSByte()).ToArray(), quality, type),
        IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, values.Select(v => v.GetInt16()).ToArray(), quality, type),
        IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(shape, values.Select(v => v.GetUInt16()).ToArray(), quality, type),
        IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, values.Select(v => v.GetInt32()).ToArray(), quality, type),
        IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(shape, values.Select(v => v.GetUInt32()).ToArray(), quality, type),
        IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, values.Select(v => v.GetInt64()).ToArray(), quality, type),
        IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(shape, values.Select(v => v.GetUInt64()).ToArray(), quality, type),
        IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, values.Select(v => v.GetSingle()).ToArray(), quality, type),
        IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, values.Select(v => v.GetDouble()).ToArray(), quality, type),
        IgwDataType.String or IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape,
            values.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).ToArray(), quality, type),
        _ => throw new NotSupportedException($"OPC UA 数组元素类型尚不支持:{type}"),
    };

    private static StructArrayValue CreateStruct(ArrayShape outer, StructDef schema,
        IReadOnlyList<JsonElement> rows, IReadOnlyList<IgwStatusCode> quality)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            var values = rows.Select(row => Property(row, field.JsonName ?? field.Name, field.Name)).ToArray();
            if (field.Array is { } memberArray)
            {
                var flattened = values.SelectMany(FlattenJson).ToArray();
                var memberShape = ArrayShape.Create(
                    outer.Dimensions.Concat(memberArray.Dimensions).ToArray(),
                    outer.LowerBounds.Concat(memberArray.LowerBounds).ToArray());
                var memberQuality = RepeatQuality(quality, checked((int)memberArray.Shape.ElementCount));
                members[field.Name] = memberArray.ElementType == IgwDataType.Struct
                    ? CreateStruct(memberShape, memberArray.Struct!, flattened, memberQuality)
                    : Primitive(memberShape, memberArray.ElementType, flattened, memberQuality);
            }
            else if (field.Struct is { } nested)
                members[field.Name] = CreateStruct(outer, nested, values, quality);
            else members[field.Name] = Primitive(outer, field.DataType, values, quality);
        }
        return StructArrayValue.Create(outer, schema, members, quality);
    }

    private static JsonElement Property(JsonElement row, string preferred, string fallback)
    {
        if (row.ValueKind != JsonValueKind.Object) return default;
        if (row.TryGetProperty(preferred, out var value) || row.TryGetProperty(fallback, out value)) return value;
        foreach (var property in row.EnumerateObject())
            if (string.Equals(property.Name, preferred, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, fallback, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static IEnumerable<JsonElement> FlattenJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var nested in FlattenJson(item)) yield return nested;
        }
        else yield return value;
    }

    private static IgwStatusCode[] RepeatQuality(IReadOnlyList<IgwStatusCode> quality, int count)
    {
        var result = new IgwStatusCode[checked(quality.Count * count)];
        for (var i = 0; i < quality.Count; i++) Array.Fill(result, quality[i], i * count, count);
        return result;
    }

    private static JsonElement ParseRow(object? value)
    {
        string? encodedJson = null;
        if (value is ExtensionObject extension)
        {
            value = extension.Body;
            if (value is byte[] bytes &&
                string.Equals(extension.TypeId.Identifier?.ToString(), "IgwJsonStructure.Binary", StringComparison.Ordinal))
            {
                using var decoder = new BinaryDecoder(bytes, MessageContext);
                encodedJson = decoder.ReadString("Json");
            }
        }
        var json = encodedJson ?? value switch
        {
            IgwJsonStructure structured => structured.Json,
            string text => text,
            _ => Convert.ToString(value?.GetType().GetProperty("Json")?.GetValue(value), CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("OPC UA 结构体值不是 IGW 可识别的结构编码"),
        };
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IEnumerable<object?> Flatten(object? source)
    {
        if (source is Matrix matrix) { foreach (var item in matrix.Elements) yield return item; yield break; }
        if (source is Array array) { foreach (var item in array) yield return item; yield break; }
        if (source is not null) yield return source;
    }

    private static Type ClrType(IgwDataType type) => type switch
    {
        _ when SiemensValueCodec.IsText(type) => typeof(string),
        IgwDataType.SInt => typeof(sbyte),
        IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => typeof(ushort),
        IgwDataType.DWord => typeof(uint), IgwDataType.LWord => typeof(ulong),
        IgwDataType.Bool => typeof(bool), IgwDataType.Byte => typeof(byte), IgwDataType.Char => typeof(sbyte),
        IgwDataType.Int16 => typeof(short), IgwDataType.UInt16 or IgwDataType.Bcd => typeof(ushort),
        IgwDataType.Int32 => typeof(int), IgwDataType.UInt32 or IgwDataType.LBcd => typeof(uint),
        IgwDataType.Int64 => typeof(long), IgwDataType.UInt64 => typeof(ulong), IgwDataType.Float => typeof(float),
        IgwDataType.Double => typeof(double), IgwDataType.String or IgwDataType.WString => typeof(string),
        _ => typeof(object),
    };

    private static object? ConvertValue(object? value, IgwDataType type) => type switch
    {
        _ when value is null && SiemensValueCodec.IsText(type) => null,
        _ when SiemensValueCodec.IsSemantic(type) => SiemensValueCodec.Normalize(type,
            value ?? throw new InvalidDataException("Siemens 数组元素不能为空")),
        IgwDataType.Bcd => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
        IgwDataType.LBcd => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    private static int[] ZeroBasedIndices(int linear, IReadOnlyList<int> dimensions)
    {
        var result = new int[dimensions.Count];
        for (var i = dimensions.Count - 1; i >= 0; i--)
        {
            result[i] = linear % dimensions[i];
            linear /= dimensions[i];
        }
        return result;
    }
}

/// <summary>跨进程稳定的通用结构体载体；字段 JSON 由持久化 StructDef 解释。</summary>
public sealed class IgwJsonStructure : IEncodeable
{
    internal static readonly ExpandedNodeId DataTypeId = new("IgwJsonStructure", IgwNodeManager.NamespaceUri);
    internal static readonly ExpandedNodeId BinaryId = new("IgwJsonStructure.Binary", IgwNodeManager.NamespaceUri);
    internal static readonly ExpandedNodeId XmlId = new("IgwJsonStructure.Xml", IgwNodeManager.NamespaceUri);

    public IgwJsonStructure() { }
    public IgwJsonStructure(string json) => Json = json;
    public string Json { get; set; } = "{}";
    public ExpandedNodeId TypeId => DataTypeId;
    public ExpandedNodeId BinaryEncodingId => BinaryId;
    public ExpandedNodeId XmlEncodingId => XmlId;
    public void Encode(IEncoder encoder) => encoder.WriteString(nameof(Json), Json);
    public void Decode(IDecoder decoder) => Json = decoder.ReadString(nameof(Json)) ?? "{}";
    public bool IsEqual(IEncodeable encodeable) => encodeable is IgwJsonStructure other && other.Json == Json;
    public object Clone() => new IgwJsonStructure(Json);
}
