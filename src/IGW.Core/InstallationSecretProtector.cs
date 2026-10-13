using System.Security.Cryptography;
using System.Text;

namespace IGW.Core;

/// <summary>AES-GCM 可逆密钥保护；主密钥随工程数据目录迁移，但不写入工程 JSON。</summary>
public sealed class InstallationSecretProtector
{
    public const string EnvelopePrefix = "enc:v1:";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("IGW project secret v1");
    private readonly string _keyPath;
    private readonly object _gate = new();
    private byte[]? _key;

    public InstallationSecretProtector(string keyPath) =>
        _keyPath = Path.GetFullPath(keyPath ?? throw new ArgumentNullException(nameof(keyPath)));

    public bool IsProtected(string? value) =>
        value?.StartsWith(EnvelopePrefix, StringComparison.Ordinal) == true;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(GetKey(createIfMissing: true), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);

        byte[] envelope = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, envelope, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, envelope, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, envelope, nonce.Length + tag.Length, cipher.Length);
        return EnvelopePrefix + Convert.ToBase64String(envelope);
    }

    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value)) return value;
        byte[] envelope = Convert.FromBase64String(value[EnvelopePrefix.Length..]);
        if (envelope.Length < 28) throw new CryptographicException("IGW 密钥密文长度无效。");
        ReadOnlySpan<byte> nonce = envelope.AsSpan(0, 12);
        ReadOnlySpan<byte> tag = envelope.AsSpan(12, 16);
        ReadOnlySpan<byte> cipher = envelope.AsSpan(28);
        byte[] plain = new byte[cipher.Length];
        using (var aes = new AesGcm(GetKey(createIfMissing: false), tag.Length))
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] GetKey(bool createIfMissing)
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            if (File.Exists(_keyPath)) return _key = ReadKey();
            if (!createIfMissing)
                throw new FileNotFoundException("缺少 IGW 主密钥，无法解密工程密钥。迁移时请同时复制 igw.masterkey。", _keyPath);

            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            byte[] generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var stream = new FileStream(
                    _keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
                return _key = generated;
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                CryptographicOperations.ZeroMemory(generated);
                return _key = ReadKey();
            }
        }
    }

    private byte[] ReadKey()
    {
        byte[] key = File.ReadAllBytes(_keyPath);
        if (key.Length != 32)
            throw new CryptographicException($"IGW 主密钥长度无效:{key.Length}，期望 32 字节。");
        return key;
    }
}
