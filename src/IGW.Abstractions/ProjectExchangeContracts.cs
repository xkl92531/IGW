using System.Text.Json;

namespace IGW.Abstractions;

public enum KepIssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record KepCompatibilityIssue(
    string Path,
    string Code,
    KepIssueSeverity Severity,
    string Message);

public sealed record KepCompatibilityReport(IReadOnlyList<KepCompatibilityIssue> Issues)
{
    public static KepCompatibilityReport Compatible { get; } = new(Array.Empty<KepCompatibilityIssue>());

    public bool CanImportIntoKep => Issues.All(issue => issue.Severity != KepIssueSeverity.Error);
}

public sealed record ProjectExportOptions(bool IncludePortableSecrets, string? MigrationPassword)
{
    public void Validate(bool hasPortableSecrets)
    {
        if (IncludePortableSecrets
            && hasPortableSecrets
            && string.IsNullOrWhiteSpace(MigrationPassword))
        {
            throw new ArgumentException("migration_password_required", nameof(MigrationPassword));
        }
    }
}

public sealed record ProjectImportOptions(
    string Token,
    string FileHash,
    string? MigrationPassword);

public sealed record ProjectImportPreview(
    string Token,
    string FileHash,
    long Channels,
    long Devices,
    long Groups,
    long Tags,
    long MesTasks,
    bool HasPortableSecrets,
    KepCompatibilityReport Compatibility);

public sealed record ProjectImportResult(
    bool Applied,
    ProjectRevision Revision,
    string? ErrorCode,
    KepCompatibilityReport Compatibility);

public interface IKepDriverCodec
{
    string IgwDriverId { get; }
    IReadOnlyCollection<string> KepDriverNames { get; }
    KepCompatibilityReport Inspect(ChannelDef channel);
    void WriteChannelProperties(Utf8JsonWriter writer, ChannelDef channel);
    void WriteDeviceProperties(Utf8JsonWriter writer, ChannelDef channel, DeviceDef device);
    void ReadChannelProperties(
        JsonElement source,
        ChannelDef target,
        IDictionary<string, JsonElement> unknown);
    void ReadDeviceProperties(
        JsonElement source,
        DeviceDef target,
        IDictionary<string, JsonElement> unknown);
}

public interface IProjectExchangeService
{
    Task<ProjectImportPreview> InspectAsync(Stream input, CancellationToken cancellationToken);
    Task<ProjectImportResult> ImportAsync(
        Stream input,
        ProjectImportOptions options,
        CancellationToken cancellationToken);
    Task ExportAsync(
        Stream output,
        ProjectExportOptions options,
        CancellationToken cancellationToken);
}
