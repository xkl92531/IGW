using System.Text.Json;
using Opc.Ua;

namespace IGW.Drivers.OpcUaClient;

public enum OpcUaCertificateTrustMode
{
    Strict,
    TrustOnFirstUse,
    AcceptAll,
}

internal static class OpcUaCertificatePolicy
{
    public static OpcUaCertificateTrustMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "strict" or "严格" => OpcUaCertificateTrustMode.Strict,
        "acceptall" or "accept-all" or "全部接受" => OpcUaCertificateTrustMode.AcceptAll,
        _ => OpcUaCertificateTrustMode.TrustOnFirstUse,
    };

    public static bool ShouldAccept(
        OpcUaCertificateTrustMode mode,
        uint validationStatus,
        string thumbprint,
        string? rememberedThumbprint,
        out bool remember)
    {
        remember = false;
        if (mode == OpcUaCertificateTrustMode.Strict) return false;
        if (mode == OpcUaCertificateTrustMode.AcceptAll) return true;
        if (validationStatus != StatusCodes.BadCertificateUntrusted) return false;
        if (string.IsNullOrWhiteSpace(rememberedThumbprint))
        {
            remember = true;
            return true;
        }
        return string.Equals(thumbprint, rememberedThumbprint, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class OpcUaTofuStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public OpcUaTofuStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "IGW", "pki", "opcua-server-thumbprints.json");
    }

    public string? Get(string endpoint)
    {
        lock (Gate) return Read().GetValueOrDefault(endpoint);
    }

    public void Remember(string endpoint, string thumbprint)
    {
        lock (Gate)
        {
            var entries = Read();
            entries[endpoint] = thumbprint;
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, _path, overwrite: true);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
