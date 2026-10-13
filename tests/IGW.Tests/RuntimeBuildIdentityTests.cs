using System.Reflection;
using IGW.Runtime;
using Xunit;

namespace IGW.Tests;

public class RuntimeBuildIdentityTests
{
    [Fact]
    public void Identity_uses_actual_runtime_assembly_and_process_and_has_per_start_identifier()
    {
        var assembly = typeof(GatewayHost).Assembly;
        var type = assembly.GetType("IGW.Runtime.RuntimeBuildIdentity");
        Assert.NotNull(type);
        var identity = type.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [assembly])!;
        object? Field(string name) => identity.GetType().GetProperty(name)!.GetValue(identity);
        Assert.Equal(assembly.GetName().Version!.ToString(), Field("AssemblyVersion"));
        Assert.Equal(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, Field("BuildIdentifier"));
        Assert.Equal(assembly.Location, Field("AssemblyPath"));
        Assert.Equal(assembly.ManifestModule.ModuleVersionId, Field("ModuleVersionId"));
        Assert.Equal(Environment.ProcessId, Field("ProcessId"));
        Assert.Equal(Environment.ProcessPath, Field("ProcessPath"));
        Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(Field("StartupId")));
        var again = type.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [assembly])!;
        Assert.Equal(Field("StartupId"), again.GetType().GetProperty("StartupId")!.GetValue(again));
    }
}
