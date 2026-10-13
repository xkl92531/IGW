using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace IgwKeygenGui;

/// <summary>
/// 内嵌在发码工具里的私钥：用管理员密码加密(PBKDF2-SHA256 派生密钥 + AES-GCM)，
/// 双击打开时输对密码才能解出私钥发码；exe 流出去没有密码也拿不到私钥。
/// 格式(整体 Base64)：魔数 "IGWK1" | 迭代次数(int32 LE) | 盐 16 | nonce 12 | tag 16 | 密文。
/// 命令行版 IgwKeygen 的 protect-key 用同一份代码生成，发码工具 publish.ps1 把结果嵌进 exe。
/// </summary>
public static class ProtectedPrivateKey
{
    public const string ResourceName = "IgwKeygenGui.private.key.protected";
    private static readonly byte[] Magic = "IGWK1"u8.ToArray();
    private const int DefaultIterations = 600_000;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;

    public static string Protect(byte[] pkcs8PrivateKey, string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("密码不能为空", nameof(password));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(password, salt, iterations);
        byte[] cipher = new byte[pkcs8PrivateKey.Length];
        byte[] tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, pkcs8PrivateKey, cipher, tag, Magic);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.Write(BitConverter.GetBytes(iterations));
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>密码不对、或内容被改过都返回 false。</summary>
    public static bool TryUnprotect(string protectedText, string password, out byte[] pkcs8PrivateKey)
    {
        pkcs8PrivateKey = [];
        byte[] blob;
        try { blob = Convert.FromBase64String(protectedText.Trim()); }
        catch (FormatException) { return false; }

        int header = Magic.Length + sizeof(int) + SaltSize + NonceSize + TagSize;
        if (blob.Length <= header || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
        int offset = Magic.Length;
        int iterations = BitConverter.ToInt32(blob, offset); offset += sizeof(int);
        if (iterations < 100_000) return false;
        var salt = blob.AsSpan(offset, SaltSize); offset += SaltSize;
        var nonce = blob.AsSpan(offset, NonceSize); offset += NonceSize;
        var tag = blob.AsSpan(offset, TagSize); offset += TagSize;
        var cipher = blob.AsSpan(offset);

        byte[] key = DeriveKey(password, salt.ToArray(), iterations);
        byte[] plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        pkcs8PrivateKey = plain;
        return true;
    }

    /// <summary>编译时嵌进 exe 的加密私钥；开发时直接编译(没嵌)返回 null。</summary>
    public static string? LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
}
