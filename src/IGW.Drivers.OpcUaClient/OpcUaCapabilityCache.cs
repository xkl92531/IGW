using System.Text.Json;

namespace IGW.Drivers.OpcUaClient;

internal sealed record OpcUaLearnedCapability(int Read, int Write, DateTime UpdatedUtc);

/// <summary>按服务器指纹保存已经在真实会话中验证过的安全批量窗口。</summary>
internal sealed class OpcUaCapabilityCache
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private readonly string _path;

    public OpcUaCapabilityCache(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "IGW", "cache", "opcua-capabilities.json");
    }

    public async Task<OpcUaLearnedCapability?> LoadAsync(string key, CancellationToken ct)
    {
        await FileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = File.OpenRead(_path);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, OpcUaLearnedCapability>>(
                stream, cancellationToken: ct).ConfigureAwait(false);
            return entries is not null && entries.TryGetValue(key, out var item) ? item : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { FileGate.Release(); }
    }

    public async Task SaveAsync(string key, int read, int write, CancellationToken ct)
    {
        await FileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, OpcUaLearnedCapability> entries = new(StringComparer.Ordinal);
            if (File.Exists(_path))
            {
                try
                {
                    await using var input = File.OpenRead(_path);
                    entries = await JsonSerializer.DeserializeAsync<Dictionary<string, OpcUaLearnedCapability>>(
                        input, cancellationToken: ct).ConfigureAwait(false) ?? entries;
                }
                catch (JsonException) { }
            }
            entries[key] = new OpcUaLearnedCapability(Math.Max(1, read), Math.Max(1, write), DateTime.UtcNow);
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, entries, cancellationToken: ct).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { FileGate.Release(); }
    }
}
