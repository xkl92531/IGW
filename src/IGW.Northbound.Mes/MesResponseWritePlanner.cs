using System.Globalization;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

internal readonly record struct MesResponseWritePlanItem(string TagId, string Value);

/// <summary>
/// 把接口响应字典及上报时的字段/结构映射反向解析成 PLC 写入计划。
/// JSON 别名只影响接口字段名，最终目标始终是原 PLC 点位。
/// </summary>
internal static class MesResponseWritePlanner
{
    public static IReadOnlyList<MesResponseWritePlanItem> Plan(
        MesPayload payload,
        JsonElement response,
        string? parameterDicName,
        IEnumerable<string> tagIds,
        CompiledMesPayload? compiled = null,
        MesHttp? http = null,
        Func<string, bool>? isRegisteredScalarTag = null,
        IReadOnlyList<MesParameterRouting.Route>? parameterRoutes = null)
    {
        bool useTemplateNames = http is not null &&
            (!string.IsNullOrWhiteSpace(http.JsonTemplate) || !string.IsNullOrWhiteSpace(http.BodyTemplate));
        var expected = MesPayloadModes.IsDataRanges(payload) && compiled is not null
            ? compiled.Root.Properties.Select(property => property.Name).ToArray()
            : FieldMappingRootNames(payload, useTemplateNames).ToArray();
        var knownTags = payload.Structs.Count > 0 ? tagIds as IReadOnlyCollection<string> ?? tagIds.ToArray() : Array.Empty<string>();
        IEnumerable<string> Elements(MesStruct item, string path) => ResolveElementPaths(new MesStruct
        { Kind = item.Kind, CountMode = item.CountMode, BaseGroup = path, StartIndex = item.StartIndex, FixedCount = item.FixedCount }, knownTags);
        var routes = useTemplateNames ? new List<MesParameterRouting.Route>() : (parameterRoutes ?? MesParameterRouting.Routes(payload, Elements)).ToList();
        MesParameterRouting.Validate(routes);
        var explicitRoutes = routes.ToArray();
        // 兼容直接返回普通点完整 ID，并保留映射缩放；未上报点继续按注册标量解析。
        routes.AddRange(payload.Fields.Where(f => !IsCoveredByStruct(f.TagId, payload.Structs)).Select(f => new MesParameterRouting.Route(f.TagId, f.TagId, f.Scale)));
        var dictionaryRoutes = new Dictionary<string, MesParameterRouting.Route>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            dictionaryRoutes[route.Key] = route;
            dictionaryRoutes[route.TagId] = route;
        }
        foreach (var field in payload.Fields.Where(f => !IsCoveredByStruct(f.TagId, payload.Structs) &&
                     (useTemplateNames || string.IsNullOrWhiteSpace(f.ParameterCode))))
        {
            string alias = MesFieldKeyModes.Resolve(payload, field);
            dictionaryRoutes.TryAdd(alias, new(alias, field.TagId, field.Scale));
        }
        // 显式参数码可以恰好长得像别的点位路径；已配置码的归属优先于兼容的完整 ID/别名。
        foreach (var route in explicitRoutes) dictionaryRoutes[route.Key] = route;
        var itemIdKeys = dictionaryRoutes.Keys.ToArray();
        JsonElement source = FindBestPayloadObject(response, expected, parameterDicName, itemIdKeys);
        var result = new List<MesResponseWritePlanItem>();

        // 字典允许返回未参与上报的点位；完整 ID 必须按注册大小写精确命中，不猜短名称。
        // 字典命名空间独立于普通根字段；按路由归属解析一次，配置字典优先于标准兼容字典。
        var dictionaryValues = new Dictionary<string, DictionaryResponse>(StringComparer.OrdinalIgnoreCase);
        PlanParameterDictionaries(response, parameterDicName,
            isRegisteredScalarTag ?? (id => tagIds.Contains(id, StringComparer.Ordinal)), dictionaryValues, dictionaryRoutes,
            explicitRoutes.Select(route => route.Key).ToHashSet(StringComparer.Ordinal));
        result.AddRange(dictionaryValues.Values.Where(value => value.Item.HasValue).Select(value => value.Item!.Value));

        if (MesPayloadModes.IsDataRanges(payload) && compiled is not null)
            PlanOutputObject(source, compiled.Root, result);
        else
            PlanFieldMapping(source, payload, knownTags, result, useTemplateNames, dictionaryValues.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

        // 同一个点位只保留最后一个映射，配置后项可覆盖前项。
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<MesResponseWritePlanItem>(result.Count);
        foreach (var item in result)
        {
            if (positions.TryGetValue(item.TagId, out int index)) deduplicated[index] = item;
            else { positions[item.TagId] = deduplicated.Count; deduplicated.Add(item); }
        }
        return deduplicated;
    }

    private readonly record struct DictionaryResponse(MesResponseWritePlanItem? Item, int Priority);

    private static void PlanParameterDictionaries(
        JsonElement source,
        string? parameterDicName,
        Func<string, bool> isRegisteredScalarTag,
        Dictionary<string, DictionaryResponse> result,
        IReadOnlyDictionary<string, MesParameterRouting.Route> routes,
        IReadOnlySet<string> explicitKeys,
        int depth = 0)
    {
        if (source.ValueKind != JsonValueKind.Object || depth >= 4) return;
        foreach (var property in source.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (IsDictionaryName(property.Name, parameterDicName))
            {
                int containerPriority = !string.IsNullOrWhiteSpace(parameterDicName) &&
                    string.Equals(property.Name, parameterDicName, StringComparison.OrdinalIgnoreCase) ? 20 : 10;
                foreach (var entry in property.Value.EnumerateObject())
                {
                    string target;
                    double scale;
                    int keyPriority;
                    if (routes.TryGetValue(entry.Name, out var route))
                    {
                        target = route.TagId; scale = route.Scale;
                        keyPriority = explicitKeys.Contains(entry.Name) ? 3 : entry.Name == target ? 2 : 1;
                    }
                    else if (isRegisteredScalarTag(entry.Name)) { target = entry.Name; scale = 1; keyPriority = 0; }
                    else continue;
                    int priority = containerPriority + keyPriority;
                    if (result.TryGetValue(target, out var previous) && previous.Priority > priority) continue;
                    // null/数组也占用其已配置目标，不能退回普通根字段猜另一个值。
                    MesResponseWritePlanItem? item = TryScalar(entry.Value, scale, out string text) ? new(target, text) : null;
                    result[target] = new(item, priority);
                }
            }
            // 仅下钻响应包裹，不能把业务结构里同名的成员当作回写字典。
            else if (property.Name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("value", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("backResult", StringComparison.OrdinalIgnoreCase))
                PlanParameterDictionaries(property.Value, parameterDicName, isRegisteredScalarTag, result, routes, explicitKeys, depth + 1);
        }
    }

    private static IEnumerable<string> FieldMappingKeys(MesPayload payload, MesField field, bool useTemplateNames)
    {
        string key = !useTemplateNames && !string.IsNullOrWhiteSpace(field.ParameterCode) ? field.ParameterCode : MesFieldKeyModes.Resolve(payload, field);
        yield return key;
        if (!string.Equals(key, field.TagId, StringComparison.Ordinal)) yield return field.TagId;
    }

    private static IEnumerable<string> FieldMappingRootNames(MesPayload payload, bool useTemplateNames)
    {
        foreach (var field in payload.Fields)
            if (!IsCoveredByStruct(field.TagId, payload.Structs))
                foreach (string key in FieldMappingKeys(payload, field, useTemplateNames))
                    yield return key;
        foreach (var item in payload.Structs)
            if ((useTemplateNames || !item.AsDictionary) && !string.IsNullOrWhiteSpace(item.AliasName))
                yield return item.AliasName;
    }

    private static void PlanFieldMapping(
        JsonElement source,
        MesPayload payload,
        IEnumerable<string> tagIds,
        List<MesResponseWritePlanItem> result,
        bool useTemplateNames,
        IReadOnlySet<string> dictionaryTargets)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var field in payload.Fields)
        {
            if (IsCoveredByStruct(field.TagId, payload.Structs) || dictionaryTargets.Contains(field.TagId)) continue;
            // 这里只读普通根字段；字典已经按自身命名空间与归属处理，不能再作根字段解释。
            foreach (string key in FieldMappingKeys(payload, field, useTemplateNames).OrderByDescending(key => key == field.TagId))
            {
                if (!TryGetProperty(source, key, out var value)) continue;
                if (TryScalar(value, field.Scale, out string text))
                    result.Add(new MesResponseWritePlanItem(field.TagId, text));
                break;
            }
        }

        if (payload.Structs.Count == 0) return;
        var knownTags = tagIds as IReadOnlyCollection<string> ?? tagIds.ToArray();
        foreach (var item in payload.Structs)
        {
            if (!useTemplateNames && item.AsDictionary) continue;
            if (string.IsNullOrWhiteSpace(item.AliasName) || item.Members.Count == 0 && item.Structs.Count == 0 ||
                !TryGetProperty(source, item.AliasName, out var structure)) continue;
            PlanStruct(structure, item, item.BaseGroup, knownTags, result, useTemplateNames);
        }
    }

    /// <summary>
    /// 按上报时同样的层级把一个结构体的响应值写回原点位；子结构体(结构体里套结构体)的 BaseGroup
    /// 相对上一层元素，递归处理。basePath 是这个结构体在工程里的绝对路径。
    /// </summary>
    private static void PlanStruct(
        JsonElement structure,
        MesStruct item,
        string basePath,
        IReadOnlyCollection<string> knownTags,
        List<MesResponseWritePlanItem> result,
        bool useTemplateNames = false)
    {
        if (string.Equals(item.Kind, "Object", StringComparison.OrdinalIgnoreCase))
        {
            PlanElement(structure, item, basePath, knownTags, result, useTemplateNames);
            return;
        }
        if (string.Equals(item.Kind, "List", StringComparison.OrdinalIgnoreCase))
        {
            if (structure.ValueKind != JsonValueKind.Array) return;
            int index = 0;
            foreach (var value in structure.EnumerateArray())
            {
                var members = item.Members.Where(m => useTemplateNames || string.IsNullOrWhiteSpace(m.ParameterCode)).ToList();
                if (index >= members.Count) break;
                var member = members[index++];
                if (TryScalar(value, member.Scale, out string text))
                    result.Add(new MesResponseWritePlanItem($"{basePath}.{member.TagId}", text));
            }
            return;
        }
        if (structure.ValueKind != JsonValueKind.Array) return;
        var elementPaths = ResolveElementPaths(new MesStruct
        {
            Kind = item.Kind, CountMode = item.CountMode, BaseGroup = basePath,
            StartIndex = item.StartIndex, FixedCount = item.FixedCount,
        }, knownTags);
        int elementIndex = 0;
        foreach (var element in structure.EnumerateArray())
        {
            if (elementIndex >= elementPaths.Count) break;
            PlanElement(element, item, elementPaths[elementIndex++], knownTags, result, useTemplateNames);
        }
    }

    private static void PlanElement(
        JsonElement element,
        MesStruct item,
        string elementPath,
        IReadOnlyCollection<string> knownTags,
        List<MesResponseWritePlanItem> result,
        bool useTemplateNames = false)
    {
        PlanMembers(element, elementPath, item.Members.Where(m => useTemplateNames || string.IsNullOrWhiteSpace(m.ParameterCode)).ToList(), result);
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var child in item.Structs)
            if ((useTemplateNames || !child.AsDictionary) && !string.IsNullOrWhiteSpace(child.AliasName) && TryGetProperty(element, child.AliasName, out var value))
                PlanStruct(value, child, $"{elementPath}.{child.BaseGroup}", knownTags, result, useTemplateNames);
    }

    private static void PlanMembers(
        JsonElement element,
        string groupPath,
        IReadOnlyList<MesField> members,
        List<MesResponseWritePlanItem> result)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var member in members)
        {
            if (!TryGetMember(element, member, out var value) ||
                !TryScalar(value, member.Scale, out string text)) continue;
            result.Add(new MesResponseWritePlanItem($"{groupPath}.{member.TagId}", text));
        }
    }

    /// <summary>
    /// 找结构体成员的值：改过字段名按字段名；成员路径带点(结构体里套结构体)先按层级 {"Inner":{"A":…}} 找，
    /// 再兼容以前平铺成 "Inner.A" 的键和只写最后一段 "A" 的响应。
    /// </summary>
    private static bool TryGetMember(JsonElement element, MesField member, out JsonElement value)
    {
        if (!string.IsNullOrWhiteSpace(member.FieldName)) return TryGetProperty(element, member.FieldName, out value);
        string[] path = member.TagId.Split('.');
        if (path.Length > 1)
        {
            var current = element;
            bool found = true;
            foreach (string segment in path)
                if (!TryGetProperty(current, segment, out current)) { found = false; break; }
            if (found) { value = current; return true; }
            if (TryGetProperty(element, member.TagId, out value)) return true;
        }
        return TryGetProperty(element, LastSegment(member.TagId), out value);
    }

    private static List<string> ResolveElementPaths(MesStruct item, IEnumerable<string> tagIds)
    {
        // 与上报用同一套元素组扫描，两边认出的元素一致
        if (string.Equals(item.CountMode, "Enum", StringComparison.OrdinalIgnoreCase))
            return MesUploadEngine.EnumStructElements(item, tagIds);

        int count = Math.Max(0, item.FixedCount);
        return Enumerable.Range(item.StartIndex, count)
            .Select(index => $"{item.BaseGroup}[{index}]").ToList();
    }

    private static void PlanOutputObject(
        JsonElement source,
        CompiledMesOutputObject output,
        List<MesResponseWritePlanItem> result)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in output.Properties)
        {
            if (!TryGetProperty(source, property.Name, out var value)) continue;
            switch (property.Value)
            {
                case CompiledMesOutputObject child:
                    PlanOutputObject(value, child, result);
                    break;
                case CompiledMesOutputRange range:
                    PlanSourceValue(value, range.Range.Source, result);
                    break;
            }
        }
    }

    private static void PlanSourceObject(
        JsonElement source,
        CompiledMesSourceObject mapping,
        List<MesResponseWritePlanItem> result)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in mapping.Properties)
        {
            if (!TryGetProperty(source, property.Name, out var value)) continue;
            switch (property.Value)
            {
                case CompiledMesSourceField field when TryScalar(value, 1, out string text):
                    result.Add(new MesResponseWritePlanItem(field.TagId, text));
                    break;
                case CompiledMesSourceObject child:
                    PlanSourceObject(value, child, result);
                    break;
                case CompiledMesSourceArray array when value.ValueKind == JsonValueKind.Array:
                {
                    int index = 0;
                    foreach (var element in value.EnumerateArray())
                    {
                        if (index >= array.Elements.Count) break;
                        PlanSourceValue(element, array.Elements[index++], result);
                    }
                    break;
                }
                case CompiledMesSourceLogicalArray logical:
                    PlanLogicalArray(value, logical, result);
                    break;
            }
        }
    }

    private static void PlanSourceValue(
        JsonElement source,
        CompiledMesSourceValue mapping,
        List<MesResponseWritePlanItem> result)
    {
        switch (mapping)
        {
            case CompiledMesSourceField field when TryScalar(source, 1, out string text):
                result.Add(new MesResponseWritePlanItem(field.TagId, text));
                break;
            case CompiledMesSourceObject sourceObject:
                PlanSourceObject(source, sourceObject, result);
                break;
            case CompiledMesSourceArray array when source.ValueKind == JsonValueKind.Array:
            {
                int index = 0;
                foreach (var element in source.EnumerateArray())
                {
                    if (index >= array.Elements.Count) break;
                    PlanSourceValue(element, array.Elements[index++], result);
                }
                break;
            }
            case CompiledMesSourceLogicalArray logical:
                PlanLogicalArray(source, logical, result);
                break;
        }
    }

    private static void PlanLogicalArray(
        JsonElement source,
        CompiledMesSourceLogicalArray logical,
        List<MesResponseWritePlanItem> result)
    {
        var shape = logical.Slice.ValidateAndCreateShape(logical.Definition.Shape);
        foreach (var element in MesArrayProjection.FlattenResponseElements(source, shape))
        {
            if (logical.MemberPath.Count > 0)
            {
                if (!TryScalar(element.Value, 1, out var text)) continue;
                var memberPath = CanonicalMemberPath(logical.Definition.Struct, logical.MemberPath);
                result.Add(new MesResponseWritePlanItem(
                    MesArrayProjection.Alias(logical.TagId, element.Indices, memberPath), text));
                continue;
            }
            if (logical.Definition.ElementType == IgwDataType.Struct && logical.Definition.Struct is not null)
            {
                PlanStructElement(element.Value, logical.TagId, element.Indices,
                    logical.Definition.Struct, [], result);
                continue;
            }
            if (TryScalar(element.Value, 1, out var scalar))
                result.Add(new MesResponseWritePlanItem(
                    MesArrayProjection.Alias(logical.TagId, element.Indices), scalar));
        }
    }

    private static void PlanStructElement(
        JsonElement source,
        string tagId,
        IReadOnlyList<int> indices,
        StructDef schema,
        IReadOnlyList<string> parentPath,
        List<MesResponseWritePlanItem> result)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var field in schema.Fields)
        {
            if (!TryGetProperty(source, field.JsonName ?? field.Name, out var value) &&
                !TryGetProperty(source, field.Name, out value)) continue;
            var path = parentPath.Concat([field.Name]).ToArray();
            if (field.Array is not null) continue; // 成员数组需完整切片写，不能伪装成单标量写入
            if (field.Struct is not null)
            {
                PlanStructElement(value, tagId, indices, field.Struct, path, result);
                continue;
            }
            if (TryScalar(value, 1, out var text))
                result.Add(new MesResponseWritePlanItem(
                    MesArrayProjection.Alias(tagId, indices, path), text));
        }
    }

    private static IReadOnlyList<string> CanonicalMemberPath(StructDef? schema, IReadOnlyList<string> requested)
    {
        var result = new List<string>(requested.Count);
        var current = schema;
        foreach (var segment in requested)
        {
            var field = current?.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.JsonName, segment, StringComparison.OrdinalIgnoreCase));
            result.Add(field?.Name ?? segment);
            current = field?.Struct;
        }
        return result;
    }

    private static JsonElement FindBestPayloadObject(
        JsonElement root, IReadOnlyCollection<string> expected, string? parameterDicName, IReadOnlyCollection<string>? itemIdKeys)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;
        var candidates = new List<(JsonElement Element, int Priority)> { (root, 0) };
        CollectObjectCandidates(root, 0, 3, parameterDicName, candidates);

        int Score(JsonElement candidate)
        {
            if (candidate.ValueKind != JsonValueKind.Object) return -1;
            int score = 0;
            foreach (string name in expected)
                if (TryGetProperty(candidate, name, out _)) score++;
            if (itemIdKeys is not null)
            {
                // 标准与配置字典一起评分；字典本身永远不能成为普通 payload 根候选。
                int matches = candidate.EnumerateObject().Where(property => IsDictionaryName(property.Name, parameterDicName) &&
                        property.Value.ValueKind == JsonValueKind.Object)
                    .Sum(property => itemIdKeys.Count(key => property.Value.TryGetProperty(key, out _)));
                if (matches > 0) score += matches + 1;
            }
            return score;
        }

        return candidates
            .OrderByDescending(candidate => Score(candidate.Element))
            .ThenByDescending(candidate => candidate.Priority)
            .First().Element;
    }

    private static void CollectObjectCandidates(
        JsonElement current,
        int depth,
        int maxDepth,
        string? parameterDicName,
        List<(JsonElement Element, int Priority)> result)
    {
        if (depth >= maxDepth || current.ValueKind != JsonValueKind.Object) return;
        foreach (var property in current.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (IsDictionaryName(property.Name, parameterDicName)) continue;
            int priority = string.Equals(property.Name, parameterDicName, StringComparison.OrdinalIgnoreCase) ? 3
                : string.Equals(property.Name, "backResult", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            result.Add((property.Value, priority));
            CollectObjectCandidates(property.Value, depth + 1, maxDepth, parameterDicName, result);
        }
    }

    private static bool TryScalar(JsonElement value, double scale, out string text)
    {
        text = "";
        switch (value.ValueKind)
        {
            case JsonValueKind.String: text = value.GetString() ?? ""; break;
            case JsonValueKind.Number: text = value.GetRawText(); break;
            case JsonValueKind.True: text = "true"; break;
            case JsonValueKind.False: text = "false"; break;
            default: return false; // null/对象/数组不能直接写 PLC
        }
        if (scale != 0 && scale != 1 && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
            text = (number / scale).ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    internal static bool TryGetProperty(JsonElement source, string name, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
            foreach (var property in source.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static bool IsDictionaryName(string name, string? configuredName) =>
        string.Equals(name, MesFieldKeyModes.ParameterDictionaryName, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(configuredName) && string.Equals(name, configuredName, StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot >= 0 ? path[(dot + 1)..] : path;
    }

    private static bool IsCoveredByStruct(string path, IReadOnlyList<MesStruct> structs) =>
        structs.Any(item => !string.IsNullOrWhiteSpace(item.BaseGroup) &&
            (string.Equals(path, item.BaseGroup, StringComparison.Ordinal) ||
             path.StartsWith(item.BaseGroup + ".", StringComparison.Ordinal) ||
             path.StartsWith(item.BaseGroup + "[", StringComparison.Ordinal) ||
             path.StartsWith(item.BaseGroup + "_", StringComparison.Ordinal)));
}
