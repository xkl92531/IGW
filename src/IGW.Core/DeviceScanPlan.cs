using IGW.Abstractions;

namespace IGW.Core;

internal readonly record struct ScanBlockKey(
    int PeriodMs,
    ScanDemandPriority Priority,
    int Area,
    int Start,
    int Count,
    int TagCount,
    string FirstTagId,
    string LastTagId);

internal sealed class CompiledScanBlock
{
    public required int Id { get; init; }
    public required int PeriodMs { get; init; }
    public required ScanDemandPriority Priority { get; init; }
    public required int Area { get; init; }
    public required int Start { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<TagBinding> Tags { get; init; }
    public required int[] Handles { get; init; }
    public required object[] HubHandles { get; init; }
    public long DueMs { get; set; }
    public long LastCompletedMs { get; set; }
    public long? LastScanStartedMs { get; set; }
    public IReadOnlyList<string>? TimingTagIds { get; set; }
    public IReadOnlyList<ScanDemandOwner> TelemetryOwners { get; set; } = Array.Empty<ScanDemandOwner>();
    public IReadOnlyList<string> TelemetryOwnerKinds { get; set; } = Array.Empty<string>();
    public int TelemetryOwnerCount { get; set; }
    public bool TelemetryOwnersTruncated { get; set; }
    public int ConsecutiveFailures { get; set; }
    public ReadFailureLogGate FailureLog { get; set; } = new();

    public ScanBlockKey Key => new(
        PeriodMs,
        Priority,
        Area,
        Start,
        Count,
        Tags.Count,
        Tags.Count == 0 ? "" : Tags[0].TagId,
        Tags.Count == 0 ? "" : Tags[^1].TagId);

    internal static CompiledScanBlock ForTest(
        int id,
        int periodMs,
        ScanDemandPriority priority = ScanDemandPriority.Background) => new()
    {
        Id = id,
        PeriodMs = periodMs,
        Priority = priority,
        Area = 0,
        Start = 0,
        Count = 0,
        Tags = Array.Empty<TagBinding>(),
        Handles = Array.Empty<int>(),
        HubHandles = Array.Empty<object>(),
    };
}

internal static class DeviceScanPlanCompiler
{
    public static IReadOnlyList<CompiledScanBlock> Compile(
        IReadOnlyList<TagBinding> bindings,
        DataHub hub,
        DeviceScanDemandSnapshot demands,
        DriverLimits limits,
        DriverCapabilitySnapshot capabilities,
        bool includeAllTags,
        Func<string, bool>? includeActiveTag = null)
    {
        var byHandle = new Dictionary<int, TagBinding>(bindings.Count);
        var periods = new Dictionary<int, (int PeriodMs, ScanDemandPriority Priority)>(
            includeAllTags ? bindings.Count : demands.EffectivePeriods.Count);

        foreach (var binding in bindings)
        {
            if (!hub.TryGetHandleId(binding.TagId, out int handle)) continue;
            byHandle[handle] = binding;
            if (includeAllTags)
                periods[handle] = (NormalizePeriod(binding.Def.ScanRateMs), ScanDemandPriority.Background);
            else if (includeActiveTag?.Invoke(binding.TagId) == true)
                periods[handle] = (NormalizePeriod(binding.Def.ScanRateMs), ScanDemandPriority.Interactive);
        }

        foreach (var demand in demands.EffectivePeriods)
        {
            if (!byHandle.TryGetValue(demand.Handle, out var binding) ||
                !string.Equals(binding.TagId, demand.TagId, StringComparison.OrdinalIgnoreCase)) continue;

            // MES/任务需求的周期就是用户配置的真实周期；同一点还被其它消费者激活时，
            // periods 中已有默认周期，下面会自然取两者最短值。
            int effective = Math.Max(1, demand.PeriodMs);
            if (!periods.TryGetValue(demand.Handle, out var current))
                periods[demand.Handle] = (effective, demand.Priority);
            else
                periods[demand.Handle] = (
                    Math.Min(current.PeriodMs, effective),
                    (ScanDemandPriority)Math.Min((int)current.Priority, (int)demand.Priority));
        }

        int id = 0;
        var result = new List<CompiledScanBlock>();
        foreach (var periodGroup in periods
                     .GroupBy(x => x.Value)
                     .OrderBy(x => x.Key.PeriodMs)
                     .ThenBy(x => x.Key.Priority))
        {
            var tags = periodGroup.Select(x => byHandle[x.Key]).ToArray();
            if (capabilities.PartitionsReadRequests)
            {
                result.Add(CreateBlock(id++, periodGroup.Key.PeriodMs, periodGroup.Key.Priority,
                    area: -1, start: 0, count: 0, tags, hub));
                continue;
            }

            foreach (var readBlock in AddressBlockOptimizer.Plan(
                         tags,
                         limits.MaxReadUnits,
                         limits.MaxGap,
                         capabilities.SupportsBatchRead
                             ? Math.Max(1, capabilities.MaxTagsPerRead)
                             : 1))
            {
                foreach (var byteLimitedTags in TransferBatchPlanner.Partition(
                             readBlock.Tags,
                             int.MaxValue,
                             capabilities.MaxReadBytes,
                             static tag => TransferBatchPlanner.EstimateBytes(tag.Def, tag.Address)))
                {
                    int start = byteLimitedTags.Min(tag => tag.Address.Offset);
                    int end = byteLimitedTags.Max(tag => tag.Address.End);
                    result.Add(CreateBlock(id++, periodGroup.Key.PeriodMs, periodGroup.Key.Priority,
                        readBlock.Area, start, end - start, byteLimitedTags, hub));
                }
            }
        }

        return result;
    }

    private static int NormalizePeriod(int periodMs) => periodMs <= 0 ? 1000 : periodMs;

    private static CompiledScanBlock CreateBlock(
        int id,
        int periodMs,
        ScanDemandPriority priority,
        int area,
        int start,
        int count,
        IReadOnlyList<TagBinding> tags,
        DataHub hub)
    {
        int[] handles = new int[tags.Count];
        object[] hubHandles = new object[tags.Count];
        for (int i = 0; i < tags.Count; i++)
        {
            handles[i] = hub.TryGetHandleId(tags[i].TagId, out int handle) ? handle : -1;
            hubHandles[i] = hub.GetHandle(tags[i].TagId)!;
        }

        return new CompiledScanBlock
        {
            Id = id,
            PeriodMs = periodMs,
            Priority = priority,
            Area = area,
            Start = start,
            Count = count,
            Tags = tags,
            Handles = handles,
            HubHandles = hubHandles,
        };
    }
}

internal static class TransferBatchPlanner
{
    public static IReadOnlyList<IReadOnlyList<T>> Partition<T>(
        IReadOnlyList<T> items,
        int maxItems,
        int maxBytes,
        Func<T, int> estimateBytes)
    {
        if (items.Count == 0) return Array.Empty<IReadOnlyList<T>>();
        int itemLimit = Math.Max(1, maxItems);
        long byteLimit = Math.Max(1, maxBytes);
        var result = new List<IReadOnlyList<T>>();
        var current = new List<T>(Math.Min(items.Count, itemLimit));
        long currentBytes = 0;

        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            int itemBytes = Math.Max(1, estimateBytes(item));
            if (current.Count > 0 &&
                (current.Count >= itemLimit || currentBytes + itemBytes > byteLimit))
            {
                result.Add(current);
                current = new List<T>(Math.Min(items.Count - i, itemLimit));
                currentBytes = 0;
            }

            // 单点本身超过服务器声明的字节数时仍单独下发，让驱动返回准确的范围/容量错误。
            current.Add(item);
            currentBytes += itemBytes;
        }

        if (current.Count > 0) result.Add(current);
        return result;
    }

    public static int EstimateBytes(TagDef definition, TagAddress address)
    {
        long estimated = definition.Array is { } array
            ? EstimateArrayBytes(array)
            : definition.DataType switch
            {
                IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char => 1,
                IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
                IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
                IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
                IgwDataType.String => Math.Max(1, definition.StringLength ?? 254) + 2L,
                IgwDataType.WString => (Math.Max(1, definition.StringLength ?? 254) * 2L) + 4L,
                IgwDataType.Struct => Math.Max(1, definition.Array?.Struct?.SizeBytes ?? address.Length),
                IgwDataType.ByteArray => Math.Max(1, address.Length),
                _ => Math.Max(1, address.Length),
            };
        return (int)Math.Clamp(estimated, 1, int.MaxValue);
    }

    private static long EstimateArrayBytes(ArrayDef array)
    {
        long elements;
        try { elements = array.Shape.ElementCount; }
        catch { elements = 1; }

        long stride = array.ElementStrideBytes > 0
            ? array.ElementStrideBytes
            : array.ElementStrideBits > 0
                ? Math.Max(1, (array.ElementStrideBits + 7L) / 8L)
                : array.Struct?.SizeBytes > 0
                    ? array.Struct.SizeBytes
                    : array.ElementType switch
                    {
                        IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char => 1,
                        IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
                        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
                        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
                        IgwDataType.String => Math.Max(1, array.StringLength ?? 254) + 2L,
                        IgwDataType.WString => (Math.Max(1, array.StringLength ?? 254) * 2L) + 4L,
                        _ => 1,
                    };
        try { return checked(elements * stride); }
        catch (OverflowException) { return int.MaxValue; }
    }
}
