using System.Security.Cryptography;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>工程内 API 用户密码的单向哈希与旧工程兼容验证。</summary>
public static class ProjectSecrets
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Prefix, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool VerifyPassword(UserDef user, string candidate)
    {
        ArgumentNullException.ThrowIfNull(user);
        candidate ??= string.Empty;
        if (TryParse(user.PasswordHash, out int iterations, out byte[] salt, out byte[] expected))
        {
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                candidate, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        return FixedTimeTextEquals(user.Password, candidate);
    }

    public static void HashPendingApiPasswords(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var user in project.Security?.Users ?? [])
        {
            if (string.IsNullOrEmpty(user.Password)) continue;
            user.PasswordHash = HashPassword(user.Password);
            user.Password = string.Empty;
        }
    }

    public static void CopyApiPasswordHashes(IgwProject persisted, IgwProject runtime)
    {
        var stored = persisted.Security.Users.ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
        foreach (var user in runtime.Security.Users)
        {
            if (!stored.TryGetValue(user.Username, out var saved)) continue;
            user.Password = string.Empty;
            user.PasswordHash = saved.PasswordHash;
        }
    }

    public static void TransformReversibleSecrets(IgwProject project, Func<string, string> transform)
    {
        project.MqttPublish.Password = Transform(project.MqttPublish.Password, transform);
        project.MesUpload.AuthBearerToken = Transform(project.MesUpload.AuthBearerToken, transform);
        foreach (var task in project.MesUpload.Tasks)
            foreach (string key in task.Listener.Headers.Keys.Where(IsSecretHeader).ToArray())
                task.Listener.Headers[key] = Transform(task.Listener.Headers[key], transform);
        foreach (var user in project.OpcUa.Users)
            user.Password = Transform(user.Password, transform);
        foreach (var channel in project.Channels)
        {
            TransformSettings(channel.Settings, transform);
            foreach (var device in channel.Devices) TransformSettings(device.Settings, transform);
        }
        foreach (var template in project.Templates)
            TransformSettings(template.DefaultSettings, transform);
    }

    public static bool IsSecretSettingKey(string key)
    {
        string normalized = key.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("passphrase", StringComparison.Ordinal);
    }

    public static bool IsSecretHeader(string key) => IsSecretSettingKey(key) ||
        key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        key.Replace("-", "", StringComparison.Ordinal).Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static void TransformSettings(IDictionary<string, string> settings, Func<string, string> transform)
    {
        foreach (string key in settings.Keys.Where(IsSecretSettingKey).ToArray())
            settings[key] = Transform(settings[key], transform);
    }

    private static string Transform(string value, Func<string, string> transform) =>
        string.IsNullOrEmpty(value) ? string.Empty : transform(value);

    private static bool TryParse(
        string? encoded, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (string.IsNullOrWhiteSpace(encoded)) return false;
        string[] parts = encoded.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out iterations)
            || iterations < 100_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
            return salt.Length >= SaltBytes && hash.Length >= HashBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeTextEquals(string? expected, string candidate)
    {
        byte[] left = System.Text.Encoding.UTF8.GetBytes(expected ?? string.Empty);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(candidate);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
