namespace IGW.Licensing;

/// <summary>Records the explicit driver and feature grants selected by a new issuer.</summary>
public static class LicenseIssueScope
{
    public static void Apply(
        LicensePayload payload,
        IReadOnlyCollection<string> driverIds,
        IReadOnlyCollection<string> featureIds)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(driverIds);
        ArgumentNullException.ThrowIfNull(featureIds);

        string[] drivers = [.. driverIds];
        string[] features = [.. featureIds];
        if (drivers.Length == 0 || !Valid(drivers, LicenseCatalog.Drivers))
            throw new ArgumentException("Select at least one known driver without duplicates.", nameof(driverIds));
        if (!Valid(features, LicenseCatalog.Features))
            throw new ArgumentException("Select only known features without duplicates.", nameof(featureIds));

        payload.Version = 2;
        payload.Drivers = [.. drivers];
        payload.Features = [.. features];
    }

    private static bool Valid(IEnumerable<string> ids, IReadOnlyList<LicenseCatalog.Item> catalog)
    {
        var known = new HashSet<string>(catalog.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ids.All(id => known.Contains(id) && seen.Add(id));
    }
}
