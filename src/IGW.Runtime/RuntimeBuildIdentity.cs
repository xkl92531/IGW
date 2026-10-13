using System.Reflection;

namespace IGW.Runtime;

/// <summary>Executable identity only. Never reads command-line arguments or application configuration.</summary>
public sealed record RuntimeBuildIdentity(
    string AssemblyVersion,
    string BuildIdentifier,
    Guid ModuleVersionId,
    string AssemblyPath,
    string? ProcessPath,
    int ProcessId,
    Guid StartupId)
{
    private static readonly Guid CurrentStartupId = Guid.NewGuid();

    public static RuntimeBuildIdentity Capture(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string version = assembly.GetName().Version?.ToString() ?? "unknown";
        return new RuntimeBuildIdentity(
            version,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version,
            assembly.ManifestModule.ModuleVersionId,
            string.IsNullOrEmpty(assembly.Location) ? "(single-file bundle)" : assembly.Location,
            Environment.ProcessPath,
            Environment.ProcessId,
            CurrentStartupId);
    }
}
