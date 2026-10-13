using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

public sealed record ArraySliceRequest(int[] Start, int[] Length, JsonElement Values);
public sealed record SiemensDbPreviewRequest(string Source, int DefaultDb = 1, List<string>? TypeSources = null);

public sealed record ArraySliceResponse(
    string Id,
    IgwDataType ElementType,
    int[] Dimensions,
    int[] LowerBounds,
    int[] Start,
    int[] Length,
    long TotalElements,
    object?[] Values,
    uint[] QualityCodes,
    string OverallQuality);

public static class ArrayValueApi
{
    private const int DefaultSliceElements = 1000;
    private const int MaxSliceElements = 100_000;

    public static void MapArrayValueApi(this IEndpointRouteBuilder app, IGatewayHost host)
    {
        app.MapPost("/api/v1/siemens/db-import/preview", (SiemensDbPreviewRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Source))
                return Results.BadRequest(new { error = "DB 源文件内容不能为空" });
            try
            {
                var result = SiemensDbTypeParser.Parse(request.Source, request.DefaultDb, request.TypeSources);
                var validationProject = new IgwProject
                {
                    Channels = [new ChannelDef
                    {
                        Name = "Import", DriverId = "Siemens.S7",
                        Devices = [new DeviceDef { Name = "Preview", Groups = [result.Root] }],
                    }],
                };
                ArrayProjectValidator.NormalizeAndValidate(validationProject);
                return Results.Json(result, ProjectStore.Options);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("工程配置")
          .WithName("PreviewSiemensDbImport")
          .Produces<SiemensDbParseResult>(StatusCodes.Status200OK)
          .Produces(StatusCodes.Status400BadRequest);

        var group = app.MapGroup("/api/v1/tags/{id}/array")
            .WithTags(PublicPlcApiPolicy.OperationTag);

        group.MapGet("", async (string id, CancellationToken ct) =>
        {
            if (Unavailable(host) is { } blocked) return blocked;
            // 未在采集的数组先从 PLC 读一次，外部读取拿到最新值。
            await host.Engine.RefreshIdleAsync([id], IgwRestApi.GetRefreshTimeout, ct, activate: true);
            if (!host.Hub.TryGetDef(id, out var definition) || definition.Array is null)
                return Results.NotFound(new { error = "array tag not found", id });
            if (!host.Hub.TryGet(id, out var data) || data.Value is not ArrayRuntimeValue array)
            {
                // 尚无运行数据(如设备未连上/按需扫描未激活):按定义回 Bad 壳,
                // 监控可显示数组形状与元素级 Bad 质量,而不是神秘的 409。
                var badArray = ArrayBadShell.CreateBadShell(definition.Array, StatusCode.BadNotConnected);
                if (badArray is null)
                    return Results.Json(new { error = "array runtime value unavailable", id }, statusCode: 409);
                array = badArray;
            }
            return Results.Json(new
            {
                id,
                elementType = array.ElementType,
                dimensions = array.Shape.Dimensions,
                lowerBounds = array.Shape.LowerBounds,
                totalElements = array.Shape.ElementCount,
                overallQuality = array.OverallQuality.Class.ToString(),
                good = array.GoodCount,
                uncertain = array.UncertainCount,
                bad = array.BadCount,
            }, ProjectStore.Options);
        }).WithName("GetArrayShape");

        group.MapGet("/slice", (string id, string? start, string? length, string? count) =>
        {
            if (Unavailable(host) is { } blocked) return blocked;
            if (!host.Hub.TryGetDef(id, out var definition) || definition.Array is null)
                return Results.NotFound(new { error = "array tag not found", id });
            try
            {
                var shape = definition.Array.Shape;
                var starts = ParseVector(start, shape.LowerBounds.ToArray());
                var defaults = DefaultLengths(shape);
                var lengths = ParseVector(length ?? count, defaults);
                return Results.Json(BuildSlice(host, id, new ArraySlice(starts, lengths)), ProjectStore.Options);
            }
            catch (KeyNotFoundException)
            {
                // 尚无运行数据:按定义回全 Bad 壳切片(结构体数组等无法合成时退回 400)
                var shell = ArrayBadShell.CreateBadShell(definition.Array, StatusCode.BadNotConnected);
                if (shell is not null)
                    try { return Results.Json(BuildSliceResponse(id, definition.Array, new ArraySlice(
                        ParseVector(start, definition.Array.Shape.LowerBounds.ToArray()),
                        ParseVector(length ?? count, DefaultLengths(definition.Array.Shape))), shell), ProjectStore.Options); }
                    catch (ArgumentException) { }
                return Results.BadRequest(new { error = $"数组标记 {id} 尚无运行数据。", id });
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                return Results.BadRequest(new { error = ex.Message, id });
            }
        }).WithName("ReadArraySlice")
          .Produces<ArraySliceResponse>(StatusCodes.Status200OK)
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/slice", async (string id, ArraySliceRequest request, HttpRequest httpRequest, CancellationToken ct) =>
        {
            if (Unavailable(host) is { } blocked) return blocked;
            if (PublicPlcApiPolicy.RejectStaleProjectWrite(httpRequest, host.ProjectRevision) is { } stale) return stale;
            if (!host.Hub.TryGetDef(id, out var definition) || definition.Array is null)
                return Results.NotFound(new { error = "array tag not found", id });
            try
            {
                var slice = new ArraySlice(request.Start, request.Length);
                var shape = slice.ValidateAndCreateShape(definition.Array.Shape);
                EnsureSliceLimit(shape);
                var value = ArrayJsonValueConverter.FromJson(definition.Array, shape, request.Values);
                var result = await host.Engine.WriteArrayAsync(id, slice, value, ct);
                return result.Status.IsGood && result.Failures.Count == 0
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: result.IsPartial ? 207 : 502);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
            {
                return Results.BadRequest(new { error = ex.Message, id });
            }
        }).WithName("WriteArraySlice")
          .Produces<ArrayWriteResult>(StatusCodes.Status200OK)
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound)
          .Produces(StatusCodes.Status502BadGateway);
    }

    internal static ArraySliceResponse BuildSlice(IGatewayHost host, string id, ArraySlice slice)
    {
        var value = host.Engine.ReadArraySlice(id, slice);
        EnsureSliceLimit(value.Shape);
        return host.Hub.TryGetDef(id, out var definition) && definition.Array is not null
            ? BuildSliceResponse(id, definition.Array, slice, value)
            : new ArraySliceResponse(
                id, value.ElementType,
                value.Shape.Dimensions.ToArray(), value.Shape.LowerBounds.ToArray(),
                slice.Start.ToArray(), slice.Lengths.ToArray(), value.Shape.ElementCount,
                BrowserValueJson.ArrayValues(value),
                value.ElementQuality.Span.ToArray().Select(static status => status.Code).ToArray(),
                value.OverallQuality.Class.ToString());
    }

    private static ArraySliceResponse BuildSliceResponse(string id, ArrayDef definition, ArraySlice slice, ArrayRuntimeValue value)
    {
        var sliced = value.Slice(slice);
        EnsureSliceLimit(sliced.Shape);
        return new ArraySliceResponse(
            id,
            sliced.ElementType,
            definition.Dimensions.ToArray(),
            definition.LowerBounds.ToArray(),
            slice.Start.ToArray(),
            slice.Lengths.ToArray(),
            definition.Shape.ElementCount,
            BrowserValueJson.ArrayValues(sliced),
            sliced.ElementQuality.Span.ToArray().Select(static status => status.Code).ToArray(),
            sliced.OverallQuality.Class.ToString());
    }

    private static IResult? Unavailable(IGatewayHost host) => host.Reloading
        ? Results.Json(new { error = "网关正在热重载,请稍后重试" }, statusCode: 503)
        : host.Hub is null
            ? Results.Json(new { error = "网关未授权或已过期" }, statusCode: 403)
            : null;

    private static int[] ParseVector(string? text, int[] defaults)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaults;
        var items = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length != defaults.Length)
            throw new ArgumentException($"参数需要 {defaults.Length} 个逗号分隔整数。");
        var result = new int[items.Length];
        for (var i = 0; i < items.Length; i++)
            if (!int.TryParse(items[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                throw new ArgumentException($"参数第 {i + 1} 项不是有效整数。");
        return result;
    }

    private static int[] DefaultLengths(ArrayShape shape)
    {
        var result = Enumerable.Repeat(1, shape.Rank).ToArray();
        result[^1] = Math.Min(shape.Dimensions[^1], DefaultSliceElements);
        return result;
    }

    private static void EnsureSliceLimit(ArrayShape shape)
    {
        if (shape.ElementCount > MaxSliceElements)
            throw new ArgumentException($"单次切片最多 {MaxSliceElements} 个元素，当前请求 {shape.ElementCount} 个。");
    }

}

internal static class ArrayJsonValueConverter
{
    public static ArrayRuntimeValue FromJson(ArrayDef definition, ArrayShape shape, JsonElement source)
    {
        var items = Flatten(source, definition.ElementType == IgwDataType.Struct).ToArray();
        if (items.Length != shape.ElementCount)
            throw new FormatException($"数组元素数量不一致，期望 {shape.ElementCount}，实际 {items.Length}。");
        return Create(definition.ElementType, shape, items, definition.StringLength, definition.Struct);
    }

    private static ArrayRuntimeValue Create(IgwDataType type, ArrayShape shape, IReadOnlyList<JsonElement> items,
        int? stringLength, StructDef? schema)
    {
        return type switch
        {
            var semantic when SiemensValueCodec.IsText(semantic) => Primitive<string>(shape, items, type, e => (string)SiemensValueCodec.Normalize(type, e)),
            IgwDataType.SInt => Primitive<sbyte>(shape, items, type, e => (sbyte)SiemensValueCodec.Normalize(type, e)),
            IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => Primitive<ushort>(shape, items, type, e => (ushort)SiemensValueCodec.Normalize(type, e)),
            IgwDataType.DWord => Primitive<uint>(shape, items, type, e => (uint)SiemensValueCodec.Normalize(type, e)),
            IgwDataType.LWord => Primitive<ulong>(shape, items, type, e => (ulong)SiemensValueCodec.Normalize(type, e)),
            IgwDataType.Bool => Primitive<bool>(shape, items, type, static e => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : e.GetDouble() != 0),
            IgwDataType.Byte => Primitive<byte>(shape, items, type, static e => e.GetByte()),
            IgwDataType.Char => Primitive<sbyte>(shape, items, type, static e => e.GetSByte()),
            IgwDataType.Int16 => Primitive<short>(shape, items, type, static e => e.GetInt16()),
            IgwDataType.UInt16 or IgwDataType.Bcd => Primitive<ushort>(shape, items, type, static e => e.GetUInt16()),
            IgwDataType.Int32 => Primitive<int>(shape, items, type, static e => e.GetInt32()),
            IgwDataType.UInt32 or IgwDataType.LBcd => Primitive<uint>(shape, items, type, static e => e.GetUInt32()),
            IgwDataType.Int64 => Primitive<long>(shape, items, type, static e => e.ValueKind == JsonValueKind.String ? long.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : e.GetInt64()),
            IgwDataType.UInt64 => Primitive<ulong>(shape, items, type, static e => e.ValueKind == JsonValueKind.String ? ulong.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : e.GetUInt64()),
            IgwDataType.Float => Primitive<float>(shape, items, type, static e => e.GetSingle()),
            IgwDataType.Double => Primitive<double>(shape, items, type, static e => e.GetDouble()),
            IgwDataType.String or IgwDataType.WString => Primitive<string>(shape, items, type,
                e => Truncate(e.GetString() ?? "", stringLength)),
            IgwDataType.ByteArray => Primitive<byte[]>(shape, items, type,
                static e => e.ValueKind == JsonValueKind.String
                    ? Convert.FromBase64String(e.GetString() ?? "")
                    : e.EnumerateArray().Select(static b => b.GetByte()).ToArray()),
            IgwDataType.Struct => CreateStruct(shape, items,
                schema ?? throw new FormatException("结构体数组缺少成员定义。")),
            _ => throw new FormatException($"不支持数组类型 {type}。"),
        };
    }

    private static StructArrayValue CreateStruct(ArrayShape outerShape, IReadOnlyList<JsonElement> objects, StructDef schema)
    {
        if (objects.Any(static item => item.ValueKind != JsonValueKind.Object))
            throw new FormatException("结构体数组元素必须是 JSON 对象。");
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            var values = objects.Select(item => GetProperty(item, field.JsonName ?? field.Name)).ToArray();
            if (field.Array is not null)
            {
                var flattened = values.SelectMany(value => Flatten(value, field.Array.ElementType == IgwDataType.Struct)).ToArray();
                var memberShape = ArrayShape.Create(
                    outerShape.Dimensions.Concat(field.Array.Dimensions).ToArray(),
                    outerShape.LowerBounds.Concat(field.Array.LowerBounds).ToArray());
                if (flattened.LongLength != memberShape.ElementCount)
                    throw new FormatException($"结构体数组成员 {field.Name} 的元素数量不一致。");
                members[field.Name] = Create(field.Array.ElementType, memberShape, flattened,
                    field.Array.StringLength, field.Array.Struct);
            }
            else
            {
                members[field.Name] = Create(field.DataType, outerShape, values, field.StringLength, field.Struct);
            }
        }
        return StructArrayValue.Create(outerShape, schema, members);
    }

    private static PrimitiveArrayValue<T> Primitive<T>(ArrayShape shape, IReadOnlyList<JsonElement> items,
        IgwDataType type, Func<JsonElement, T> convert) where T : notnull
    {
        try
        {
            var values = new T[items.Count];
            for (var i = 0; i < values.Length; i++) values[i] = convert(items[i]);
            return PrimitiveArrayValue<T>.Create(shape, values, elementType: type);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            throw new FormatException($"数组值无法转换为 {type}:{ex.Message}", ex);
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement source, bool objectIsLeaf)
    {
        if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray())
                foreach (var leaf in Flatten(item, objectIsLeaf)) yield return leaf;
            yield break;
        }
        if (objectIsLeaf && source.ValueKind == JsonValueKind.Object) { yield return source; yield break; }
        if (!objectIsLeaf && source.ValueKind == JsonValueKind.Object)
            throw new FormatException("基础类型数组不能包含 JSON 对象。");
        yield return source;
    }

    private static JsonElement GetProperty(JsonElement source, string name)
    {
        foreach (var property in source.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new FormatException($"结构体成员 {name} 缺失。");
    }

    private static string Truncate(string value, int? capacity) =>
        capacity is > 0 && value.Length > capacity ? value[..capacity.Value] : value;
}
