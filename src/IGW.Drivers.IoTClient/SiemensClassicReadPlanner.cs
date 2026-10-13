using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public sealed record SiemensClassicReadRange(
    int DbNumber, int Start, int Count, int GapBytes, IReadOnlyList<TagBinding> Tags);

/// <summary>Plans bounded configured DB fields. Unproven layouts and oversized members use their own reader.</summary>
public static class SiemensClassicReadPlanner
{
    public static IReadOnlyList<SiemensClassicReadRange> Plan(IReadOnlyList<TagBinding> tags, int maxPayloadBytes,
        DriverBatchingSettings? batchingSettings = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        int payload = Math.Min(maxPayloadBytes, ushort.MaxValue);
        int allowedGap = Math.Min(16, payload / 16);
        if (batchingSettings?.Mode == DriverBatchingMode.Manual)
            allowedGap = Math.Min(allowedGap, batchingSettings.MaxGap);
        var fields = tags.Select(tag => (Tag: tag, Address: SiemensAddressDescriptor.Parse(tag.Def.Address)))
            .Where(x => x.Address.Kind == SiemensAddressKind.Classic && x.Address.DbNumber is not null && HasKnownSpan(x.Tag))
            .Select(x => (x.Tag, Db: x.Address.DbNumber!.Value, Start: x.Address.ByteOffset!.Value,
                Count: ProtocolStorageSize.Estimate(x.Tag.Def, x.Tag.Address)))
            .Where(x => x.Count > 0 && x.Count <= payload)
            .OrderBy(x => x.Db).ThenBy(x => x.Start).ThenBy(x => x.Tag.TagId, StringComparer.Ordinal);
        var ranges = new List<SiemensClassicReadRange>();
        foreach (var field in fields)
        {
            int end = checked(field.Start + field.Count);
            if (ranges.Count > 0)
            {
                var previous = ranges[^1];
                int previousEnd = checked(previous.Start + previous.Count);
                if (previous.DbNumber == field.Db && field.Start - previousEnd <= allowedGap &&
                    Math.Max(end, previousEnd) - previous.Start <= payload)
                {
                    ranges[^1] = previous with
                    {
                        Count = Math.Max(end, previousEnd) - previous.Start,
                        GapBytes = previous.GapBytes + Math.Max(0, field.Start - previousEnd),
                        Tags = previous.Tags.Append(field.Tag).ToArray(),
                    };
                    continue;
                }
            }
            ranges.Add(new(field.Db, field.Start, field.Count, 0, [field.Tag]));
        }
        return ranges;
    }

    internal static bool HasKnownSpan(TagBinding tag) => tag.Def.Array is not { } array ||
        array.ElementStrideBits > 0 || array.ElementStrideBytes > 0 ||
        array.ElementType == IgwDataType.Struct && array.Struct?.SizeBytes > 0;
}
