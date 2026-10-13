using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Core.Security;

public sealed record DeviceCredential(string Username, string Password);

public interface IDeviceCredentialStore
{
    Task<DeviceCredential?> GetAsync(string reference, CancellationToken ct = default);
    Task<string> SaveAsync(DeviceCredential credential, string? replaceReference = null, CancellationToken ct = default);
    Task DeleteAsync(string reference, CancellationToken ct = default);
}

public interface IDeviceSecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

public enum DeviceCredentialStoreError
{
    InvalidReference,
    ProtectFailed,
    UnprotectFailed,
    CorruptPayload,
    IoFailure,
}

public sealed class DeviceCredentialStoreException : Exception
{
    public DeviceCredentialStoreError Code { get; }

    public DeviceCredentialStoreException(DeviceCredentialStoreError code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}

public sealed class DeviceCredentialStore : IDeviceCredentialStore
{
    private readonly string _directory;
    private readonly IDeviceSecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeviceCredentialStore(string? directory = null, IDeviceSecretProtector? protector = null)
    {
        _directory = directory ?? Path.Combine(IgwPaths.DataDir, "secrets");
        _protector = protector ?? new DpapiDeviceSecretProtector();
    }

    public async Task<DeviceCredential?> GetAsync(string reference, CancellationToken ct = default)
    {
        var path = PathFor(reference);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            SecretEnvelope envelope;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                envelope = await JsonSerializer.DeserializeAsync<SecretEnvelope>(stream, cancellationToken: ct)
                    ?? throw new JsonException("empty envelope");
                if (envelope.Version != 1 || !string.Equals(envelope.Reference, reference, StringComparison.Ordinal))
                    throw new JsonException("invalid envelope metadata");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new DeviceCredentialStoreException(DeviceCredentialStoreError.CorruptPayload, "本机 PLC 凭据文件已损坏。", error);
            }

            byte[] ciphertext;
            byte[] plaintext;
            try
            {
                ciphertext = Convert.FromBase64String(envelope.ProtectedPayload);
                plaintext = _protector.Unprotect(ciphertext);
            }
            catch (Exception error)
            {
                throw new DeviceCredentialStoreException(DeviceCredentialStoreError.UnprotectFailed, "无法在当前计算机解密 PLC 凭据，请重新验证。", error);
            }

            try
            {
                return JsonSerializer.Deserialize<DeviceCredential>(plaintext)
                    ?? throw new JsonException("empty credential");
            }
            catch (Exception error)
            {
                throw new DeviceCredentialStoreException(DeviceCredentialStoreError.CorruptPayload, "PLC 凭据内容已损坏。", error);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> SaveAsync(DeviceCredential credential, string? replaceReference = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Password))
            throw new ArgumentException("PLC 密码不能为空。", nameof(credential));
        var reference = replaceReference is null ? Guid.NewGuid().ToString("N") : NormalizeReference(replaceReference);
        var path = PathFor(reference);
        Directory.CreateDirectory(_directory);

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(credential);
        byte[] protectedPayload;
        try { protectedPayload = _protector.Protect(plaintext); }
        catch (Exception error)
        {
            throw new DeviceCredentialStoreException(DeviceCredentialStoreError.ProtectFailed, "无法加密 PLC 凭据。", error);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }

        var envelope = new SecretEnvelope(1, reference, Convert.ToBase64String(protectedPayload), DateTimeOffset.UtcNow);
        CryptographicOperations.ZeroMemory(protectedPayload);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, envelope, cancellationToken: ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                return reference;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new DeviceCredentialStoreException(DeviceCredentialStoreError.IoFailure, "保存 PLC 凭据失败。", error);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string reference, CancellationToken ct = default)
    {
        var path = PathFor(reference);
        await _gate.WaitAsync(ct);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) { throw new DeviceCredentialStoreException(DeviceCredentialStoreError.IoFailure, "删除 PLC 凭据失败。", error); }
        finally { _gate.Release(); }
    }

    private string PathFor(string reference) => Path.Combine(_directory, NormalizeReference(reference) + ".secret");

    private static string NormalizeReference(string reference)
    {
        if (!Guid.TryParseExact(reference, "N", out var value))
            throw new DeviceCredentialStoreException(DeviceCredentialStoreError.InvalidReference, "PLC 凭据引用无效。");
        return value.ToString("N");
    }

    private sealed record SecretEnvelope(int Version, string Reference, string ProtectedPayload, DateTimeOffset UpdatedAt);
}
