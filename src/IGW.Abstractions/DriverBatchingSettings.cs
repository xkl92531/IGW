namespace IGW.Abstractions;

/// <summary>Controls whether a driver uses its protocol-safe batching rule or a legacy gap cap.</summary>
public enum DriverBatchingMode
{
    Auto,
    Manual,
}

/// <summary>
/// Parses batching settings without changing their source dictionary. This keeps legacy projects and
/// exports lossless while making an absent or unrecognised mode use the current protocol rule.
/// </summary>
public sealed record DriverBatchingSettings(DriverBatchingMode Mode, int MaxGap)
{
    public static DriverBatchingSettings Parse(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var mode = TryGet(settings, "batchingMode", out var value) &&
            string.Equals(value?.Trim(), "manual", StringComparison.OrdinalIgnoreCase)
            ? DriverBatchingMode.Manual
            : DriverBatchingMode.Auto;

        if (mode != DriverBatchingMode.Manual || !TryGet(settings, "maxGap", out var maxGap) ||
            !int.TryParse(maxGap, out var parsed))
            return new DriverBatchingSettings(mode, 0);

        return new DriverBatchingSettings(mode, Math.Clamp(parsed, 0, 4096));
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> settings, string key, out string? value)
    {
        if (settings.TryGetValue(key, out var exact))
        {
            value = exact;
            return true;
        }
        foreach (var pair in settings)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}
