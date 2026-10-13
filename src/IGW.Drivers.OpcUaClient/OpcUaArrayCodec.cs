using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using IGW.Abstractions;
using Opc.Ua;
using IgwStatusCode = IGW.Abstractions.StatusCode;

namespace IGW.Drivers.OpcUaClient;

/// <summary>OPC UA CLR Array/ExtensionObject 与 IGW 逻辑数组之间的无损边界转换。</summary>
public static class OpcUaArrayCodec
{
    private static readonly IServiceMessageContext MessageContext =
        new ServiceMessageContext(DefaultTelemetry.Create(_ => { }));
    public static ArrayRuntimeValue Decode(object? source, TagDef tag, IgwStatusCode status)
    {
        var definition = tag.Array ?? throw new ArgumentException("点位不是数组。", nameof(tag));
        var shape = definition.Shape;
        var values = SiemensValueCodec.IsSemantic(definition.ElementType)
            ? SiemensOpcUaCodec.Elements(source, definition.ElementType).Select(v => v is null ? null : SiemensOpcUaCodec.Decode(definition.ElementType, v)).ToArray()
            : Flatten(source).ToArray();
        if (values.Length != shape.ElementCount)
            throw new InvalidDataException($"OPC UA 数组元素数量不匹配(期望 {shape.ElementCount},实得 {values.Length})");
        var quality = Enumerable.Repeat(status, values.Length).ToArray();
        return definition.ElementType == IgwDataType.Struct
            ? DecodeStruct(shape, definition.Struct ?? throw new InvalidDataException("结构体数组缺少布局"),
                values.Select(UnwrapExtension).ToArray(), quality)
            : CreatePrimitive(shape, definition.ElementType, values, quality);
    }

    public static object Encode(ArrayRuntimeValue value, IReadOnlyList<object?>? templates = null)
    {
        if (value is StructArrayValue structured)
        {
            if (templates is null || templates.Count != value.Count)
                throw new InvalidOperationException("OPC UA 结构体数组写入前必须先成功读取一次，以保留服务器的数据类型编码。");
            var result = new ExtensionObject[value.Count];
            for (int i = 0; i < result.Length; i++)
            {
                if (templates[i] is ExtensionObject igwExtension && IsIgwJsonEncoding(igwExtension))
                {
                    result[i] = new ExtensionObject(new OpcUaIgwJsonStructure(
                        JsonSerializer.Serialize(structured.GetBoxedValue(i))));
                    continue;
                }
                var template = templates[i] is ExtensionObject extension ? extension.Body : templates[i];
                if (template is not IEncodeable encodeable)
                    throw new InvalidOperationException("OPC UA 服务器未提供可编辑的结构体类型编码，无法安全写回结构体数组。");
                var clone = (IEncodeable)encodeable.Clone();
                FillStruct(clone, structured, i);
                result[i] = new ExtensionObject(clone);
            }
            return result;
        }

        if (SiemensValueCodec.IsSemantic(value.ElementType))
        {
            var values = Enumerable.Range(0, value.Count).Select(i => SiemensOpcUaCodec.Encode(value.ElementType,
                value.GetBoxedValue(i)!, templates?[i])).ToArray();
            var dims = value.Shape.Dimensions.ToArray();
            if (value.ElementType == IgwDataType.Dt)
            {
                var bytes = Array.CreateInstance(typeof(byte), dims.Concat(new[] { 8 }).ToArray());
                for (int i = 0; i < values.Length; i++)
                    for (int b = 0; b < 8; b++) bytes.SetValue(((byte[])values[i])[b], ZeroBasedIndices(i, dims).Append(b).ToArray());
                return bytes;
            }
            var typed = Array.CreateInstance(values[0].GetType(), dims);
            for (int i = 0; i < values.Length; i++) typed.SetValue(values[i], ZeroBasedIndices(i, dims));
            return typed;
        }
        var elementType = ClrType(value.ElementType);
        var dimensions = value.Shape.Dimensions.ToArray();
        var resultArray = Array.CreateInstance(elementType, dimensions);
        for (int i = 0; i < value.Count; i++)
            resultArray.SetValue(ConvertValue(value.GetBoxedValue(i), value.ElementType), ZeroBasedIndices(i, dimensions));
        return resultArray;
    }

    public static int ValueRank(ArrayShape shape) => shape.Rank;

    public static object?[] CaptureTemplates(object? source) => Flatten(source).ToArray();

    public static StructDef? TryInferStructure(object? source, string identity)
    {
        var first = Flatten(source).FirstOrDefault();
        var body = UnwrapExtension(first);
        if (body is null || body is byte[]) return null;

        var fields = new List<StructFieldDef>();
        if (body is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
                fields.Add(InferField(property.Name, property.Value));
        }
        else
        {
            foreach (var property in body.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    property.Name is nameof(IEncodeable.TypeId) or nameof(IEncodeable.BinaryEncodingId) or nameof(IEncodeable.XmlEncodingId))
                    continue;
                fields.Add(InferField(property.Name, JsonSerializer.SerializeToElement(property.GetValue(body))));
            }
        }
        if (fields.Count == 0) return null;
        return new StructDef
        {
            Id = $"opcua:{identity}", Name = identity, SizeBytes = Math.Max(1, fields.Sum(ApproximateSize)), Fields = fields,
        };
    }

    public static IReadOnlyList<object?> SliceTemplates(
        IReadOnlyList<object?> templates, ArrayShape whole, ArraySlice slice)
    {
        var sliced = slice.ValidateAndCreateShape(whole);
        var result = new object?[checked((int)sliced.ElementCount)];
        for (var i = 0; i < result.Length; i++)
            result[i] = templates[checked((int)whole.GetLinearOffset(sliced.GetIndices(i)))];
        return result;
    }

    private static StructArrayValue DecodeStruct(ArrayShape shape, StructDef schema,
        IReadOnlyList<object?> rows, IReadOnlyList<IgwStatusCode> quality)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            var fieldValues = rows.Select(row => Member(row, field.JsonName ?? field.Name, field.Name)).ToArray();
            if (field.Array is { } fieldArray)
            {
                int innerCount = checked((int)fieldArray.Shape.ElementCount);
                var flattened = fieldValues.SelectMany(Flatten).ToArray();
                if (flattened.Length != rows.Count * innerCount)
                    throw new InvalidDataException($"OPC UA 结构体成员 {field.Name} 数组长度不匹配");
                var memberShape = ArrayShape.Create(
                    shape.Dimensions.Concat(fieldArray.Dimensions).ToArray(),
                    shape.LowerBounds.Concat(fieldArray.LowerBounds).ToArray());
                var memberQuality = RepeatOuterQuality(quality, innerCount);
                members[field.Name] = fieldArray.ElementType == IgwDataType.Struct
                    ? DecodeStruct(memberShape, fieldArray.Struct!, flattened.Select(UnwrapExtension).ToArray(), memberQuality)
                    : CreatePrimitive(memberShape, fieldArray.ElementType, flattened, memberQuality);
            }
            else if (field.Struct is { } nested)
                members[field.Name] = DecodeStruct(shape, nested,
                    fieldValues.Select(UnwrapExtension).ToArray(), quality);
            else members[field.Name] = CreatePrimitive(shape, field.DataType, fieldValues, quality);
        }
        return StructArrayValue.Create(shape, schema, members, quality);
    }

    private static ArrayRuntimeValue CreatePrimitive(ArrayShape shape, IgwDataType type,
        IReadOnlyList<object?> values, IReadOnlyList<IgwStatusCode> quality) => type switch
    {
        IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(shape, ConvertAll(values, Convert.ToBoolean), quality, type),
        IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(shape, ConvertAll(values, Convert.ToByte), quality, type),
        IgwDataType.Char or IgwDataType.SInt => PrimitiveArrayValue<sbyte>.Create(shape, ConvertAll(values, Convert.ToSByte), quality, type),
        IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, ConvertAll(values, Convert.ToInt16), quality, type),
        IgwDataType.UInt16 or IgwDataType.Bcd or IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => PrimitiveArrayValue<ushort>.Create(shape, ConvertAll(values, Convert.ToUInt16), quality, type),
        IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(shape, ConvertAll(values, Convert.ToInt32), quality, type),
        IgwDataType.UInt32 or IgwDataType.LBcd or IgwDataType.DWord => PrimitiveArrayValue<uint>.Create(shape, ConvertAll(values, Convert.ToUInt32), quality, type),
        IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(shape, ConvertAll(values, Convert.ToInt64), quality, type),
        IgwDataType.UInt64 or IgwDataType.LWord => PrimitiveArrayValue<ulong>.Create(shape, ConvertAll(values, Convert.ToUInt64), quality, type),
        IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, ConvertAll(values, Convert.ToSingle), quality, type),
        IgwDataType.Double => PrimitiveArrayValue<double>.Create(shape, ConvertAll(values, Convert.ToDouble), quality, type),
        IgwDataType.String or IgwDataType.WString => PrimitiveArrayValue<string>.Create(shape,
            values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ToArray(), quality, type),
        IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(shape,
            values.Select(value => value as byte[] ?? []).ToArray(), quality, type),
        _ when SiemensValueCodec.IsText(type) => PrimitiveArrayValue<string>.Create(shape,
            values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").ToArray(), quality, type),
        _ => throw new NotSupportedException($"OPC UA 数组元素类型尚不支持:{type}"),
    };

    private static IEnumerable<object?> Flatten(object? source)
    {
        if (source is null) yield break;
        if (source is Matrix matrix)
        {
            foreach (var value in matrix.Elements) yield return value;
            yield break;
        }
        if (source is Array array)
        {
            foreach (var value in array) yield return value;
            yield break;
        }
        if (source is IList list)
        {
            foreach (var value in list) yield return value;
            yield break;
        }
        yield return source;
    }

    private static object? UnwrapExtension(object? value)
    {
        if (value is not ExtensionObject extension) return value;
        if (IsIgwJsonEncoding(extension))
        {
            string json;
            if (extension.Body is byte[] bytes)
            {
                using var decoder = new BinaryDecoder(bytes, MessageContext);
                json = decoder.ReadString("Json") ?? "{}";
            }
            else
            {
                json = Convert.ToString(extension.Body?.GetType().GetProperty("Json")?.GetValue(extension.Body),
                    CultureInfo.InvariantCulture) ?? "{}";
            }
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        return extension.Body;
    }

    private static bool IsIgwJsonEncoding(ExtensionObject extension) =>
        string.Equals(extension.TypeId.Identifier?.ToString(), "IgwJsonStructure.Binary", StringComparison.Ordinal);

    private static object? Member(object? body, string preferred, string fallback)
    {
        body = UnwrapExtension(body);
        if (body is null) return null;
        if (body is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            if (json.TryGetProperty(preferred, out var found) || json.TryGetProperty(fallback, out found))
                return JsonValue(found);
            foreach (var jsonProperty in json.EnumerateObject())
                if (string.Equals(jsonProperty.Name, preferred, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(jsonProperty.Name, fallback, StringComparison.OrdinalIgnoreCase))
                    return JsonValue(jsonProperty.Value);
        }
        if (body is IReadOnlyDictionary<string, object?> readOnly)
        {
            if (readOnly.TryGetValue(preferred, out var value) || readOnly.TryGetValue(fallback, out value)) return value;
        }
        if (body is IDictionary dictionary)
        {
            if (dictionary.Contains(preferred)) return dictionary[preferred];
            if (dictionary.Contains(fallback)) return dictionary[fallback];
        }
        var property = body.GetType().GetProperty(preferred,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?? body.GetType().GetProperty(fallback,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(body);
    }

    private static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone(),
    };

    private static StructFieldDef InferField(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            var sample = items.FirstOrDefault(item => item.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);
            var element = InferField(name, sample);
            return new StructFieldDef
            {
                Name = name,
                DataType = element.DataType,
                Struct = element.Struct,
                Array = new ArrayDef
                {
                    ElementType = element.Struct is null ? element.DataType : IgwDataType.Struct,
                    Dimensions = [items.Length], LowerBounds = [0],
                    ElementStrideBytes = Math.Max(1, ApproximateSize(element)), Struct = element.Struct,
                },
            };
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var nested = new StructDef
            {
                Id = $"opcua:{name}", Name = name,
                Fields = value.EnumerateObject().Select(p => InferField(p.Name, p.Value)).ToList(),
            };
            nested.SizeBytes = Math.Max(1, nested.Fields.Sum(ApproximateSize));
            return new StructFieldDef { Name = name, DataType = IgwDataType.Struct, Struct = nested };
        }
        return new StructFieldDef
        {
            Name = name,
            DataType = value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => IgwDataType.Bool,
                JsonValueKind.Number when value.TryGetInt16(out _) => IgwDataType.Int16,
                JsonValueKind.Number when value.TryGetInt32(out _) => IgwDataType.Int32,
                JsonValueKind.Number when value.TryGetInt64(out _) => IgwDataType.Int64,
                JsonValueKind.Number => IgwDataType.Double,
                _ => IgwDataType.String,
            },
            StringLength = value.ValueKind == JsonValueKind.String ? Math.Max(1, value.GetString()?.Length ?? 1) : null,
        };
    }

    private static int ApproximateSize(StructFieldDef field)
    {
        var unit = field.Struct?.SizeBytes ?? field.DataType switch
        {
            IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char => 1,
            IgwDataType.Int16 or IgwDataType.UInt16 => 2,
            IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float => 4,
            IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
            IgwDataType.String or IgwDataType.WString => Math.Max(1, field.StringLength ?? 1),
            _ => 1,
        };
        return field.Array is null ? unit : checked(unit * (int)field.Array.Shape.ElementCount);
    }

    private static void FillStruct(object target, StructArrayValue value, int row)
    {
        foreach (var field in value.Schema.Fields)
        {
            var property = target.GetType().GetProperty(field.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?? (!string.IsNullOrWhiteSpace(field.JsonName)
                    ? target.GetType().GetProperty(field.JsonName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                    : null);
            if (property?.CanWrite != true) continue;

            object? source;
            if (field.Array is not null)
                source = Encode(value.GetArrayMemberSlice(field, row));
            else if (field.Struct is not null && value.Members[field.Name] is StructArrayValue nested)
            {
                var current = property.GetValue(target);
                if (current is null) continue;
                FillStruct(current, nested, row);
                source = current;
            }
            else source = value.Members[field.Name].GetBoxedValue(row);

            property.SetValue(target, ConvertForProperty(source, property.PropertyType));
        }
    }

    private static object? ConvertForProperty(object? value, Type destination)
    {
        if (value is null || destination.IsInstanceOfType(value)) return value;
        var target = Nullable.GetUnderlyingType(destination) ?? destination;
        if (target.IsEnum) return Enum.ToObject(target, value);
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static T[] ConvertAll<T>(IReadOnlyList<object?> values, Func<object?, IFormatProvider?, T> convert)
    {
        var result = new T[values.Count];
        for (int i = 0; i < result.Length; i++) result[i] = convert(values[i], CultureInfo.InvariantCulture);
        return result;
    }

    private static IgwStatusCode[] RepeatOuterQuality(IReadOnlyList<IgwStatusCode> quality, int count)
    {
        var result = new IgwStatusCode[checked(quality.Count * count)];
        for (int outer = 0; outer < quality.Count; outer++)
            Array.Fill(result, quality[outer], outer * count, count);
        return result;
    }

    private static Type ClrType(IgwDataType type) => type switch
    {
        IgwDataType.Bool => typeof(bool), IgwDataType.Byte => typeof(byte), IgwDataType.Char => typeof(sbyte),
        IgwDataType.Int16 => typeof(short), IgwDataType.UInt16 or IgwDataType.Bcd => typeof(ushort),
        IgwDataType.Int32 => typeof(int), IgwDataType.UInt32 or IgwDataType.LBcd => typeof(uint),
        IgwDataType.Int64 => typeof(long), IgwDataType.UInt64 => typeof(ulong), IgwDataType.Float => typeof(float),
        IgwDataType.Double => typeof(double), IgwDataType.String or IgwDataType.WString => typeof(string),
        IgwDataType.ByteArray => typeof(byte[]), _ => typeof(object),
    };

    private static object? ConvertValue(object? value, IgwDataType type) => type switch
    {
        IgwDataType.Bcd => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
        IgwDataType.LBcd => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    private static int[] ZeroBasedIndices(int linear, IReadOnlyList<int> dimensions)
    {
        var result = new int[dimensions.Count];
        for (int i = dimensions.Count - 1; i >= 0; i--)
        {
            result[i] = linear % dimensions[i];
            linear /= dimensions[i];
        }
        return result;
    }
}

public sealed class OpcUaIgwJsonStructure : IEncodeable
{
    private static readonly ExpandedNodeId DataType = new("IgwJsonStructure", "urn:igw:opcua");
    private static readonly ExpandedNodeId Binary = new("IgwJsonStructure.Binary", "urn:igw:opcua");
    private static readonly ExpandedNodeId Xml = new("IgwJsonStructure.Xml", "urn:igw:opcua");

    public OpcUaIgwJsonStructure() { }
    public OpcUaIgwJsonStructure(string json) => Json = json;
    public string Json { get; set; } = "{}";
    public ExpandedNodeId TypeId => DataType;
    public ExpandedNodeId BinaryEncodingId => Binary;
    public ExpandedNodeId XmlEncodingId => Xml;
    public void Encode(IEncoder encoder) => encoder.WriteString(nameof(Json), Json);
    public void Decode(IDecoder decoder) => Json = decoder.ReadString(nameof(Json)) ?? "{}";
    public bool IsEqual(IEncodeable encodeable) => encodeable is OpcUaIgwJsonStructure other && other.Json == Json;
    public object Clone() => new OpcUaIgwJsonStructure(Json);
}
