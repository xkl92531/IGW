using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 把「配置数据」选中的数据范围转成旧版字段/结构映射(平铺报文)，操作和报文都与旧版一致：
/// <list type="bullet">
/// <item>普通点、普通数组点 → 上报字段(字段名留空=用点名)；</item>
/// <item>子组(结构体/UDT) → 结构体「对象」：{别名:{成员:值}}；子组里再套的子组、按下标命名的子组作为子结构体套在里面；</item>
/// <item>UDT 数组点(Array of "UDT"/Array of Struct，一个点位带完整结构) → 结构体「数组(套对象)」，
/// 元素里的 Struct 成对象子结构体、Array of Struct 成数组子结构体，一层层套着，和 TIA 里的结构一致；</item>
/// <item>按下标命名的子组(名字[下标]、名字_下标、0..N) → 结构体「数组(套对象)」+ 枚举子组，起始和数量取自选择；</item>
/// <item>选中整个数据块时展开其内容；只选某个结构体时，它本身作为一个「对象」；</item>
/// <item>只是容器的子组(不直接挂点)，往下展开；带成员的子组保留对象及其子数组。</item>
/// </list>
/// 数据块/结构体下被排除的点不生成。
/// </summary>
public static class MesFieldMappingGenerator
{
    /// <param name="scopeGroup">任务绑定的数据块(可能是二级组)：选中它时展开内容，而不是包成一个对象。</param>
    public static (List<MesField> Fields, List<MesStruct> Structs) Generate(
        IEnumerable<MesDataRange> ranges, MesDataRangeCatalog catalog, string? scopeGroup = null)
    {
        var fields = new List<MesField>();
        var structs = new List<MesStruct>();
        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStructs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddField(string tagId)
        {
            if (seenFields.Add(tagId)) fields.Add(new MesField { TagId = tagId, FieldName = "" });
        }
        void AddStruct(MesStruct item)
        {
            if (item.Members.Count + item.Structs.Count > 0 && seenStructs.Add(item.BaseGroup)) structs.Add(item);
        }
        // 一个组的内容：直接挂的点是成员；子组、按下标命名的子组、UDT 数组点是子结构体，一层层套着
        (List<MesField> Members, List<MesStruct> Structs) Body(MesCatalogGroup group, string relative, Func<string, bool> excluded)
        {
            var members = new List<MesField>();
            var children = new List<MesStruct>();
            foreach (var tag in group.Tags)
            {
                if (excluded(Join(relative, tag.Name))) continue;
                if (NativeStruct(tag.Name, tag.Name, tag.Array) is { } nested) children.Add(nested);
                else members.Add(new MesField { TagId = tag.Name, FieldName = "" });
            }
            var seenIndexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in group.Children)
            {
                if (MesIndexedGroupName.TryParse(child.Name, out string arrayName, out _) &&
                    catalog.TryGetIndexedArray(group.SourcePath + "." + arrayName, out var named))
                {
                    if (seenIndexed.Add(named.SourcePath)) children.Add(EnumStruct(named, arrayName, null, null));
                    continue;
                }
                if (catalog.TryGetIndexedArray(child.SourcePath, out var numeric)) { children.Add(EnumStruct(numeric, child.Name, null, null)); continue; }
                var (childMembers, childStructs) = Body(child, Join(relative, child.Name), excluded);
                if (childMembers.Count + childStructs.Count > 0)
                    children.Add(new MesStruct { Kind = "Object", BaseGroup = child.Name, AliasName = child.Name, Members = childMembers, Structs = childStructs });
            }
            return (members, children);
        }
        MesStruct EnumStruct(MesCatalogIndexedArray array, string baseGroup, int? start, int? count)
        {
            List<MesField> members = [];
            List<MesStruct> children = [];
            if (array.Elements.Count > 0) (members, children) = Body(array.Elements[0].Group, "", _ => false);
            return new MesStruct
            {
                Kind = "Array", CountMode = "Enum", BaseGroup = baseGroup, AliasName = array.Name,
                StartIndex = start ?? array.LowerBound, FixedCount = count ?? array.Length, Members = members, Structs = children,
            };
        }
        void AddEnum(MesCatalogIndexedArray array, int? start, int? count) => AddStruct(EnumStruct(array, array.SourcePath, start, count));
        void AddObject(MesCatalogGroup group, Func<string, bool> excluded)
        {
            var (members, children) = Body(group, "", excluded);
            AddStruct(new MesStruct { Kind = "Object", BaseGroup = group.SourcePath, AliasName = group.Name, Members = members, Structs = children });
        }
        // 普通点直接成字段；UDT 数组点成结构体
        void AddTag(string tagId, string name, ArrayDef? array, int? start = null, int? count = null)
        {
            if (NativeStruct(tagId, name, array, start, count) is { } nested) AddStruct(nested);
            else AddField(tagId);
        }
        void AddLogical(MesCatalogLogicalArray logical)
        {
            // 按地址识别出的数组没有对应的旧版结构体写法：逐个元素点作为字段
            foreach (var leaf in logical.Descriptor.Leaves) AddField(leaf.TagId);
        }
        void Expand(MesCatalogGroup group, string relative, HashSet<string> excludes)
        {
            foreach (var tag in group.Tags)
                if (!excludes.Contains(Join(relative, tag.Name))) AddTag(tag.TagId, tag.Name, tag.Array);
            var consumed = new HashSet<int>();
            foreach (var logical in catalog.GetLogicalChildren(group.SourcePath)
                         .Where(child => child.Descriptor.BindingKind != ArrayBindingKind.NativeTag &&
                                         !catalog.TryGetIndexedArray(child.SourcePath, out _)))
            {
                var bound = logical.Descriptor.Leaves.Select(leaf => leaf.TagId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < group.Children.Count; i++)
                    if (TagIds(group.Children[i]).Any(bound.Contains)) consumed.Add(i);
                AddLogical(logical);
            }
            for (int i = 0; i < group.Children.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                var child = group.Children[i];
                if (MesIndexedGroupName.TryParse(child.Name, out string arrayName, out _) &&
                    catalog.TryGetIndexedArray(group.SourcePath + "." + arrayName, out var named))
                {
                    AddEnum(named, null, null);
                    continue;
                }
                if (catalog.TryGetIndexedArray(child.SourcePath, out var numeric)) { AddEnum(numeric, null, null); continue; }
                string childRelative = Join(relative, child.Name);
                // 仅没有直接成员的容器继续展开，例如 ShoulderGlue.GlueData_1..N → GlueData 数组。
                // 带成员的结构体由 Body 保留对象、成员和子数组的原始层级。
                if (IsContainer(child)) Expand(child, childRelative, excludes);
                else AddObject(child, path => excludes.Contains(Join(childRelative, path)));
            }
        }
        bool IsContainer(MesCatalogGroup group) => group.Tags.Count == 0 && group.Children.Count > 0;

        foreach (var range in ranges)
        {
            string path = range.SourcePath?.Trim() ?? "";
            var excludes = new HashSet<string>(range.Excludes ?? [], StringComparer.OrdinalIgnoreCase);
            int? start = range.ArrayStart is { Length: > 0 } ? range.ArrayStart[0] : null;
            int? count = range.ArrayLengths is { Length: > 0 } ? range.ArrayLengths[0] : null;
            if (catalog.TryGetScalarTag(path, out var tag, out _)) AddField(tag.TagId);
            else if (catalog.TryGetArray(path, out var native)) AddTag(native.TagId, native.Name, native.Definition, start, count);
            else if (catalog.TryGetIndexedArray(path, out var indexed)) AddEnum(indexed, start, count);
            else if (catalog.TryGetLogicalArray(path, out var logical) && logical.Descriptor.BindingKind != ArrayBindingKind.NativeTag)
                AddLogical(logical);
            else if (catalog.TryGet(path, out var group))
            {
                // 整个数据块：展开内容；单独选的结构体：本身作为一个对象
                if (IsBlock(path) || string.Equals(path, scopeGroup?.Trim(), StringComparison.OrdinalIgnoreCase) || IsContainer(group))
                    Expand(group, "", excludes);
                else AddObject(group, excludes.Contains);
            }
        }
        return (fields, structs);
    }

    /// <summary>
    /// UDT 数组点(一维、元素是结构体)的结构体：数组(套对象)，起止取 PLC 下界和长度；
    /// 元素里的 Struct 成对象子结构体、Array of Struct 成数组子结构体，普通成员(含普通数组)成成员。
    /// 不是这种数组时返回 null(照常作为字段)。
    /// </summary>
    private static MesStruct? NativeStruct(string baseGroup, string alias, ArrayDef? array, int? start = null, int? count = null)
    {
        if (array?.Struct is not { Fields.Count: > 0 } schema || array.Dimensions.Length != 1) return null;
        var (members, children) = SchemaBody(schema);
        int lower = array.LowerBounds is { Length: > 0 } ? array.LowerBounds[0] : 0;
        return new MesStruct
        {
            Kind = "Array", CountMode = "Fixed", BaseGroup = baseGroup, AliasName = alias,
            StartIndex = start ?? lower, FixedCount = count ?? array.Dimensions[0], Members = members, Structs = children,
        };
    }

    private static (List<MesField> Members, List<MesStruct> Structs) SchemaBody(StructDef schema)
    {
        var members = new List<MesField>();
        var children = new List<MesStruct>();
        foreach (var field in schema.Fields)
        {
            string alias = field.JsonName ?? field.Name;
            if (field.Struct is { } nested)
            {
                var (childMembers, childStructs) = SchemaBody(nested);
                children.Add(new MesStruct { Kind = "Object", BaseGroup = field.Name, AliasName = alias, Members = childMembers, Structs = childStructs });
            }
            else if (NativeStruct(field.Name, alias, field.Array) is { } nestedArray) children.Add(nestedArray);
            else members.Add(new MesField { TagId = field.Name, FieldName = "" });
        }
        return (members, children);
    }

    private static IEnumerable<string> TagIds(MesCatalogGroup group)
    {
        foreach (var tag in group.Tags) yield return tag.TagId;
        foreach (var child in group.Children)
        foreach (string id in TagIds(child))
            yield return id;
    }

    private static bool IsBlock(string path) => path.Count(ch => ch == '.') == 2;

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "." + name;
}
