using IGW.Abstractions;
using IGW.Core;

namespace IGW.ProjectExchange.Security;

public static class PortableSecretCollector
{
    public static string CreatePortablePath(string ownerType, string ownerId, string secretName) => ownerType switch
    {
        "project" when ownerId == "mqtt" && secretName == "password" => "mqttPublish/password",
        "project" when ownerId == "mesUpload" && secretName == "authBearerToken" => "mesUpload/authBearerToken",
        "opcua-user" => $"opcUa/users/{Escape(ownerId[(ownerId.IndexOf(':') + 1)..])}/password",
        "channel" => $"channels/{Escape(ownerId)}/settings/{Escape(secretName)}",
        "device" => $"devices/{Escape(ownerId)}/settings/{Escape(secretName)}",
        "device-template" => $"templates/{Escape(ownerId)}/defaultSettings/{Escape(secretName)}",
        _ => throw new InvalidDataException($"portable_secret_owner_invalid:{ownerType}/{ownerId}/{secretName}"),
    };

    public static IReadOnlyDictionary<string, string> CollectAndRedact(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(secrets, "mqttPublish/password", project.MqttPublish.Password,
            value => project.MqttPublish.Password = value);
        Collect(secrets, "mesUpload/authBearerToken", project.MesUpload.AuthBearerToken,
            value => project.MesUpload.AuthBearerToken = value);
        foreach (OpcUaUser user in project.OpcUa.Users)
            Collect(secrets, $"opcUa/users/{Escape(user.Username)}/password", user.Password,
                value => user.Password = value);
        foreach (ChannelDef channel in project.Channels)
        {
            CollectSettings(secrets, $"channels/{Escape(channel.Id)}/settings", channel.Settings);
            foreach (DeviceDef device in channel.Devices)
                CollectSettings(secrets, $"devices/{Escape(device.Id)}/settings", device.Settings);
        }
        foreach (DeviceTemplate template in project.Templates)
            CollectSettings(secrets, $"templates/{Escape(template.Id)}/defaultSettings", template.DefaultSettings);
        return secrets;
    }

    public static void Restore(IgwProject project, IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(secrets);
        foreach ((string path, string value) in secrets)
        {
            string[] parts = path.Split('/').Select(Unescape).ToArray();
            if (parts.SequenceEqual(["mqttPublish", "password"])) project.MqttPublish.Password = value;
            else if (parts.SequenceEqual(["mesUpload", "authBearerToken"])) project.MesUpload.AuthBearerToken = value;
            else if (parts.Length == 4 && parts[0] == "opcUa" && parts[1] == "users" && parts[3] == "password")
                Find(project.OpcUa.Users, user => user.Username == parts[2], path).Password = value;
            else if (parts.Length == 4 && parts[0] == "channels" && parts[2] == "settings")
                Find(project.Channels, channel => channel.Id == parts[1], path).Settings[parts[3]] = value;
            else if (parts.Length == 4 && parts[0] == "devices" && parts[2] == "settings")
                Find(project.Channels.SelectMany(channel => channel.Devices), device => device.Id == parts[1], path)
                    .Settings[parts[3]] = value;
            else if (parts.Length == 4 && parts[0] == "templates" && parts[2] == "defaultSettings")
                Find(project.Templates, template => template.Id == parts[1], path).DefaultSettings[parts[3]] = value;
            else throw new InvalidDataException($"portable_secret_target_invalid:{path}");
        }
    }

    private static void CollectSettings(
        IDictionary<string, string> secrets,
        string prefix,
        IDictionary<string, string> settings)
    {
        foreach (string key in settings.Keys.Where(ProjectSecrets.IsSecretSettingKey).ToArray())
            Collect(secrets, $"{prefix}/{Escape(key)}", settings[key], value => settings[key] = value);
    }

    private static void Collect(
        IDictionary<string, string> secrets,
        string path,
        string value,
        Action<string> replace)
    {
        if (string.IsNullOrEmpty(value)) return;
        secrets.Add(path, value);
        replace(string.Empty);
    }

    private static T Find<T>(IEnumerable<T> source, Func<T, bool> predicate, string path) where T : class =>
        source.SingleOrDefault(predicate)
        ?? throw new InvalidDataException($"portable_secret_target_missing:{path}");

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
    private static string Unescape(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);
}
