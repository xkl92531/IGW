using IGW.Abstractions;

namespace IGW.Core;

/// <summary>数组点位工程模型的规范化与递归布局校验。</summary>
public static class ArrayProjectValidator
{
    public static void NormalizeAndValidate(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        NormalizeAndValidate(EnumerateTags(project));
    }

    /// <summary>
    /// 只校验一台设备（含模板差异追加点）的数组点位；规则与全工程校验完全一致（批量注册写库前的设备级预检）。
    /// 与全工程校验不同，这里逐点收集全部错误而不是遇到第一个就抛出（调用方要区分历史遗留与本次新增的错误）。
    /// 会像全工程校验一样就地规范化数组定义：调用方须传入不与运行中工程共享的对象。
    /// </summary>
    public static void CollectDeviceErrors(ChannelDef channel, DeviceDef device, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(errors);
        foreach (var located in EnumerateDeviceTags(channel, device))
        {
            try { NormalizeAndValidate(located); }
            catch (InvalidDataException ex) { errors.Add(ex.Message); }
        }
    }

    private static void NormalizeAndValidate(IEnumerable<LocatedTag> tags)
    {
        foreach (var located in tags) NormalizeAndValidate(located);
    }

    private static void NormalizeAndValidate(LocatedTag located)
    {
        var tag = located.Tag;
        if (tag.Array is null)
        {
            if (tag.IsArray)
                throw Error(located, "数组长度缺失，不能按标量运行");
            return;
        }

        tag.IsArray = true;
        if (tag.DataType != tag.Array.ElementType)
            throw Error(located, "TagDef.DataType 与 Array.ElementType 不一致");

        try
        {
            ValidateArray(tag.Array, new HashSet<string>(StringComparer.Ordinal),
                new HashSet<StructDef>(ReferenceEqualityComparer.Instance));
        }
        catch (InvalidDataException ex)
        {
            throw Error(located, ex.Message);
        }
    }

    private static void ValidateArray(ArrayDef array, HashSet<string> structIds, HashSet<StructDef> structRefs)
    {
        array.Dimensions ??= [];
        array.LowerBounds ??= [];
        if (array.Dimensions.Length == 0)
            throw new InvalidDataException("数组长度缺失");
        if (array.LowerBounds.Length == 0)
            array.LowerBounds = new int[array.Dimensions.Length];
        if (array.Dimensions.Length != array.LowerBounds.Length)
            throw new InvalidDataException("维度与下界数量不一致");
        if (array.Dimensions.Any(static dimension => dimension <= 0))
            throw new InvalidDataException("数组维度必须大于零");
        if (array.ElementStrideBytes < 0)
            throw new InvalidDataException("数组元素步长不能为负数");
        if (array.ElementStrideBits < 0)
            throw new InvalidDataException("数组元素位步长不能为负数");
        if (array.ElementStrideBits > 0 && array.ElementType != IgwDataType.Bool)
            throw new InvalidDataException("只有 Bool 数组可以使用位步长");
        if (array.ElementStrideBits > 0 && array.ElementStrideBytes > 0)
            throw new InvalidDataException("数组不能同时设置字节步长和位步长");

        try
        {
            _ = array.Shape.ElementCount;
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("数组元素总数超出支持范围");
        }

        ValidateStringCapacity(array.ElementType, array.StringLength);

        if (array.ElementType == IgwDataType.Struct)
        {
            if (array.Struct is null)
                throw new InvalidDataException("结构体数组缺少成员布局");
            ValidateStruct(array.Struct, structIds, structRefs);
            if (array.ElementStrideBytes <= 0)
                throw new InvalidDataException("结构体数组必须提供元素步长");
            if (array.ElementStrideBytes < array.Struct.SizeBytes)
                throw new InvalidDataException("结构体数组元素步长小于结构体大小");
        }
        else if (array.Struct is not null)
        {
            throw new InvalidDataException("非结构体数组不能包含结构体布局");
        }
    }

    private static void ValidateStruct(StructDef schema, HashSet<string> structIds, HashSet<StructDef> structRefs)
    {
        var idAdded = false;
        if (!string.IsNullOrWhiteSpace(schema.Id))
        {
            idAdded = structIds.Add(schema.Id);
            if (!idAdded)
                throw new InvalidDataException($"结构体定义存在递归循环:{schema.Id}");
        }
        if (!structRefs.Add(schema))
            throw new InvalidDataException($"结构体定义存在递归循环:{schema.Name}");

        try
        {
            if (schema.SizeBytes <= 0)
                throw new InvalidDataException($"结构体 {schema.Name} 大小必须大于零");
            schema.Fields ??= [];

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var occupied = new List<(long Start, long End, string Name)>();
            foreach (var field in schema.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name))
                    throw new InvalidDataException($"结构体 {schema.Name} 存在未命名字段");
                if (!names.Add(field.Name))
                    throw new InvalidDataException($"结构体 {schema.Name} 字段名重复:{field.Name}");
                if (field.ByteOffset < 0)
                    throw new InvalidDataException($"结构体字段 {field.Name} 字节偏移不能为负数");
                if (field.BitOffset is < -1 or > 7)
                    throw new InvalidDataException($"结构体字段 {field.Name} 位偏移必须为 0..7 或 -1");
                if (field.BitOffset >= 0 && field.DataType != IgwDataType.Bool)
                    throw new InvalidDataException($"只有 Bool 字段可以设置位偏移:{field.Name}");

                long sizeBits;
                if (field.Array is not null)
                {
                    if (field.DataType != field.Array.ElementType)
                        throw new InvalidDataException($"结构体字段 {field.Name} 的类型与数组元素类型不一致");
                    ValidateArray(field.Array, structIds, structRefs);
                    sizeBits = field.Array.ElementStrideBits > 0
                        ? checked(field.Array.Shape.ElementCount * field.Array.ElementStrideBits)
                        : checked(field.Array.Shape.ElementCount * ResolveStrideBytes(field.Array) * 8L);
                }
                else if (field.DataType == IgwDataType.Struct)
                {
                    if (field.Struct is null)
                        throw new InvalidDataException($"结构体字段 {field.Name} 缺少成员布局");
                    ValidateStruct(field.Struct, structIds, structRefs);
                    sizeBits = checked(field.Struct.SizeBytes * 8L);
                }
                else
                {
                    if (field.Struct is not null)
                        throw new InvalidDataException($"非结构体字段 {field.Name} 不能包含结构体布局");
                    ValidateStringCapacity(field.DataType, field.StringLength);
                    sizeBits = checked(ResolveScalarSizeBytes(field.DataType, field.StringLength) * 8L);
                }

                var start = checked(field.ByteOffset * 8L + Math.Max(field.BitOffset, 0));
                if (field.DataType == IgwDataType.Bool && field.Array is null && field.BitOffset >= 0)
                    sizeBits = 1;
                var end = checked(start + sizeBits);
                if (end > schema.SizeBytes * 8L)
                    throw new InvalidDataException($"结构体字段 {field.Name} 超出结构体大小");

                var overlap = occupied.FirstOrDefault(range => start < range.End && end > range.Start);
                if (overlap.End > overlap.Start)
                    throw new InvalidDataException($"结构体字段布局重叠:{overlap.Name} 与 {field.Name}");
                occupied.Add((start, end, field.Name));
            }
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"结构体 {schema.Name} 的字段布局超出支持范围");
        }
        finally
        {
            structRefs.Remove(schema);
            if (idAdded) structIds.Remove(schema.Id);
        }
    }

    private static int ResolveStrideBytes(ArrayDef array)
    {
        if (array.ElementStrideBytes > 0) return array.ElementStrideBytes;
        if (array.ElementType == IgwDataType.Struct) return array.Struct?.SizeBytes ?? 0;
        return ResolveScalarSizeBytes(array.ElementType, array.StringLength);
    }

    private static int ResolveScalarSizeBytes(IgwDataType type, int? stringLength) => type switch
    {
        IgwDataType.SInt or IgwDataType.S7Char => 1,
        IgwDataType.Word or IgwDataType.WChar or IgwDataType.Date or IgwDataType.S5Time or IgwDataType.Timer or IgwDataType.Counter => 2,
        IgwDataType.DWord or IgwDataType.Time or IgwDataType.Tod => 4,
        IgwDataType.LWord or IgwDataType.Ltime or IgwDataType.Ltod or IgwDataType.Dt or IgwDataType.Ldt or IgwDataType.DateTime => 8,
        IgwDataType.Dtl => 12,
        IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char => 1,
        IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
        IgwDataType.String => checked((stringLength ?? 254) + 2),
        IgwDataType.WString => checked((stringLength ?? 254) * 2 + 4),
        IgwDataType.ByteArray => stringLength is > 0 ? stringLength.Value : 1,
        IgwDataType.Struct => throw new InvalidDataException("结构体字段缺少成员布局"),
        _ => throw new InvalidDataException($"不支持的数据类型:{type}"),
    };

    private static void ValidateStringCapacity(IgwDataType type, int? stringLength)
    {
        if (type is IgwDataType.String or IgwDataType.WString && stringLength is <= 0)
            throw new InvalidDataException("字符串数组容量必须大于零");
    }

    private static IEnumerable<LocatedTag> EnumerateTags(IgwProject project)
    {
        foreach (var template in project.Templates ?? [])
        {
            foreach (var tag in template.Tags ?? [])
                yield return new LocatedTag($"模板.{template.Name}.{tag.Name}", tag);
            foreach (var tag in EnumerateGroups(template.Groups, $"模板.{template.Name}"))
                yield return tag;
        }

        foreach (var channel in project.Channels ?? [])
        foreach (var device in channel.Devices ?? [])
        foreach (var tag in EnumerateDeviceTags(channel, device))
            yield return tag;
    }

    private static IEnumerable<LocatedTag> EnumerateDeviceTags(ChannelDef channel, DeviceDef device)
    {
        var prefix = $"{channel.Name}.{device.Name}";
        foreach (var tag in device.Tags ?? [])
            yield return new LocatedTag($"{prefix}.{tag.Name}", tag);
        foreach (var tag in EnumerateGroups(device.Groups, prefix))
            yield return tag;
        foreach (var add in device.Delta?.Adds ?? [])
            if (add.Tag is not null)
                yield return new LocatedTag($"{prefix}.{add.GroupPath}.{add.Tag.Name}".Replace("..", "."), add.Tag);
    }

    private static IEnumerable<LocatedTag> EnumerateGroups(IEnumerable<TagGroupDef>? groups, string prefix)
    {
        foreach (var group in groups ?? [])
        {
            var groupPath = $"{prefix}.{group.Name}";
            foreach (var tag in group.Tags ?? [])
                yield return new LocatedTag($"{groupPath}.{tag.Name}", tag);
            foreach (var tag in EnumerateGroups(group.SubGroups, groupPath))
                yield return tag;
        }
    }

    private static InvalidDataException Error(LocatedTag tag, string message) =>
        new($"数组标记 {tag.Path}: {message}。");

    private sealed record LocatedTag(string Path, TagDef Tag);
}
