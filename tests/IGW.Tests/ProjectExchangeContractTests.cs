using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectExchangeContractTests
{
    [Fact]
    public void Compatibility_is_failed_by_connection_changing_errors()
    {
        var report = new KepCompatibilityReport([
            new KepCompatibilityIssue(
                "PLC1",
                "driver_not_equivalent",
                KepIssueSeverity.Error,
                "No target KEP driver mapping")
        ]);

        Assert.False(report.CanImportIntoKep);
    }

    [Fact]
    public void Compatibility_allows_warnings_and_information()
    {
        var report = new KepCompatibilityReport([
            new KepCompatibilityIssue("PLC1", "raw_property_preserved", KepIssueSeverity.Warning, "Preserved"),
            new KepCompatibilityIssue("PLC1", "mapped", KepIssueSeverity.Info, "Mapped")
        ]);

        Assert.True(report.CanImportIntoKep);
    }

    [Fact]
    public void Export_options_require_password_when_portable_secrets_exist()
    {
        var options = new ProjectExportOptions(true, null);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            options.Validate(hasPortableSecrets: true));

        Assert.Contains("migration_password_required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_options_do_not_require_password_without_portable_secrets()
    {
        new ProjectExportOptions(true, null).Validate(hasPortableSecrets: false);
        new ProjectExportOptions(false, null).Validate(hasPortableSecrets: true);
    }
}
