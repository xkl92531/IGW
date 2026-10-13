using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Security;

namespace IGW.ProjectExchange.Igw;

public sealed class IgwExtensionWriter
{
    public const int SchemaVersion = 4;
    public const string Format = "IGW.JSON.Configuration";

    public void Write(
        Utf8JsonWriter writer,
        IgwProject project,
        KepCompatibilityReport compatibility)
        => Write(writer, project, compatibility, new ProjectExportOptions(false, null));

    public void Write(
        Utf8JsonWriter writer,
        IgwProject project,
        KepCompatibilityReport compatibility,
        ProjectExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compatibility);

        IgwProject portable = ProjectStore.Clone(project);
        ProjectSecrets.HashPendingApiPasswords(portable);
        IReadOnlyDictionary<string, string> secrets = PortableSecretCollector.CollectAndRedact(portable);
        options.Validate(secrets.Count > 0);

        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
        writer.WritePropertyName("project");
        JsonSerializer.Serialize(writer, portable, ProjectStore.Options);
        writer.WritePropertyName("compatibility");
        JsonSerializer.Serialize(writer, compatibility, ProjectStore.Options);
        if (options.IncludePortableSecrets && secrets.Count > 0)
        {
            byte[] envelope = PortableSecretEnvelope.Encrypt(secrets, options.MigrationPassword!);
            try
            {
                using JsonDocument document = JsonDocument.Parse(envelope);
                writer.WritePropertyName("secrets");
                document.RootElement.WriteTo(writer);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(envelope);
            }
        }
        writer.WriteEndObject();
        writer.Flush();
    }
}
