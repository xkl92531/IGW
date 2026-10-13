using System.Text;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Persistence.Sqlite;

public sealed record StoredProjectSecret(
    string OwnerType,
    string OwnerId,
    string SecretName,
    string Ciphertext,
    int KeyVersion = 1)
{
    public string Reference => ProjectSecretVault.CreateReference(OwnerType, OwnerId, SecretName);
}

/// <summary>把工程模型中的可逆密钥替换为引用，并用安装主密钥保护实际值。</summary>
public sealed class ProjectSecretVault
{
    private const string ReferencePrefix = "secret://v1/";
    private readonly InstallationSecretProtector _protector;

    public ProjectSecretVault(InstallationSecretProtector protector) =>
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public IReadOnlyList<StoredProjectSecret> Protect(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = new List<StoredProjectSecret>();

        project.MqttPublish.Password = Capture(entries, "project", "mqtt", "password", project.MqttPublish.Password);
        project.MesUpload.AuthBearerToken = Capture(
            entries, "project", "mesUpload", "authBearerToken", project.MesUpload.AuthBearerToken);

        for (int index = 0; index < project.OpcUa.Users.Count; index++)
        {
            OpcUaUser user = project.OpcUa.Users[index];
            user.Password = Capture(entries, "opcua-user", $"{index}:{user.Username}", "password", user.Password);
        }

        foreach (ChannelDef channel in project.Channels)
        {
            ProtectSettings(entries, "channel", Identity(channel.Id, channel.Name), channel.Settings);
            foreach (DeviceDef device in channel.Devices)
                ProtectSettings(entries, "device", Identity(device.Id, $"{channel.Name}.{device.Name}"), device.Settings);
        }

        foreach (DeviceTemplate template in project.Templates)
            ProtectSettings(entries, "device-template", Identity(template.Id, template.Name), template.DefaultSettings);

        return entries;
    }

    public void Restore(IgwProject project, IReadOnlyCollection<StoredProjectSecret> entries)
    {
        ArgumentNullException.ThrowIfNull(project);
        var values = entries.ToDictionary(item => item.Reference, item => item, StringComparer.Ordinal);

        project.MqttPublish.Password = RestoreValue(project.MqttPublish.Password, values);
        project.MesUpload.AuthBearerToken = RestoreValue(project.MesUpload.AuthBearerToken, values);
        foreach (OpcUaUser user in project.OpcUa.Users)
            user.Password = RestoreValue(user.Password, values);
        foreach (ChannelDef channel in project.Channels)
        {
            RestoreSettings(channel.Settings, values);
            foreach (DeviceDef device in channel.Devices) RestoreSettings(device.Settings, values);
        }
        foreach (DeviceTemplate template in project.Templates)
            RestoreSettings(template.DefaultSettings, values);
    }

    internal static string CreateReference(string ownerType, string ownerId, string secretName) =>
        ReferencePrefix + Encode(ownerType) + "/" + Encode(ownerId) + "/" + Encode(secretName);

    internal string Reveal(string ciphertext) => _protector.Unprotect(ciphertext);

    private string Capture(
        ICollection<StoredProjectSecret> entries,
        string ownerType,
        string ownerId,
        string secretName,
        string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith(ReferencePrefix, StringComparison.Ordinal))
            return value;
        var entry = new StoredProjectSecret(
            ownerType, ownerId, secretName, _protector.Protect(value));
        entries.Add(entry);
        return entry.Reference;
    }

    private static string Identity(string id, string fallback) =>
        string.IsNullOrWhiteSpace(id) ? fallback : id;

    private void ProtectSettings(
        ICollection<StoredProjectSecret> entries,
        string ownerType,
        string ownerId,
        IDictionary<string, string> settings)
    {
        foreach (string key in settings.Keys.Where(ProjectSecrets.IsSecretSettingKey).ToArray())
            settings[key] = Capture(entries, ownerType, ownerId, key, settings[key]);
    }

    private void RestoreSettings(
        IDictionary<string, string> settings,
        IReadOnlyDictionary<string, StoredProjectSecret> entries)
    {
        foreach (string key in settings.Keys.ToArray())
            settings[key] = RestoreValue(settings[key], entries);
    }

    private string RestoreValue(
        string value,
        IReadOnlyDictionary<string, StoredProjectSecret> entries)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(ReferencePrefix, StringComparison.Ordinal))
            return value;
        if (!entries.TryGetValue(value, out StoredProjectSecret? entry))
            throw new InvalidDataException($"工程数据库缺少密钥引用:{value}");
        return _protector.Unprotect(entry.Ciphertext);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
