using System.Security.Cryptography;
using System.Text;

namespace IGW.Observability;

internal static class PrivateFiles
{
    private const string DpapiPrefix = "dpapi:v1:";
    private const string AesPrefix = "aesgcm:v1:";
    private static readonly byte[] Entropy = "IGW.OpenObserve.Credential.v1"u8.ToArray();
    private static readonly byte[] AssociatedData = "IGW OpenObserve credential v1"u8.ToArray();

    public static string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        byte[] clear = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
                try { return DpapiPrefix + Convert.ToBase64String(protectedBytes); }
                finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            }

            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[clear.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(MachineKey(), tag.Length))
                aes.Encrypt(nonce, clear, cipher, tag, AssociatedData);
            byte[] envelope = new byte[nonce.Length + tag.Length + cipher.Length];
            Buffer.BlockCopy(nonce, 0, envelope, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, envelope, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, envelope, nonce.Length + tag.Length, cipher.Length);
            CryptographicOperations.ZeroMemory(cipher);
            return AesPrefix + Convert.ToBase64String(envelope);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);
        byte[] clear;
        if (protectedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows DPAPI 密文不能在其他操作系统解密。");
            byte[] envelope = Convert.FromBase64String(protectedValue[DpapiPrefix.Length..]);
            try { clear = ProtectedData.Unprotect(envelope, Entropy, DataProtectionScope.LocalMachine); }
            finally { CryptographicOperations.ZeroMemory(envelope); }
        }
        else if (protectedValue.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            byte[] envelope = Convert.FromBase64String(protectedValue[AesPrefix.Length..]);
            if (envelope.Length < 28)
                throw new CryptographicException("OpenObserve 凭据密文长度无效。");
            clear = new byte[envelope.Length - 28];
            using (var aes = new AesGcm(MachineKey(), 16))
                aes.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(28), envelope.AsSpan(12, 16), clear, AssociatedData);
            CryptographicOperations.ZeroMemory(envelope);
        }
        else
        {
            throw new CryptographicException("OpenObserve 凭据保护格式无效。");
        }

        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void AtomicWrite(string path, ReadOnlySpan<byte> content)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("文件必须具有父目录。", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            RestrictPermissions(temporary);
            // Windows 上目标文件被打开时(网页轮询迁移进度正在读、杀毒软件在扫)改名覆盖会“拒绝访问”，
            // 读取只占几毫秒，短暂重试即可；否则迁移进度每复制一个文件写一次，随时可能让整次迁移失败
            TransientFileRetry.Run(() => File.Move(temporary, fullPath, overwrite: true));
            RestrictPermissions(fullPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    public static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static byte[] MachineKey()
    {
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData)) commonData = "/var/lib";
        string directory = Path.Combine(commonData, "IGW", "secrets");
        string path = Path.Combine(directory, "observability.masterkey");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            byte[] stored = File.ReadAllBytes(path);
            if (stored.Length != 32) throw new CryptographicException("IGW 观测主密钥长度无效。");
            return stored;
        }

        byte[] generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(generated);
            stream.Flush(flushToDisk: true);
            RestrictPermissions(path);
            return generated;
        }
        catch (IOException) when (File.Exists(path))
        {
            CryptographicOperations.ZeroMemory(generated);
            byte[] stored = File.ReadAllBytes(path);
            if (stored.Length != 32) throw new CryptographicException("IGW 观测主密钥长度无效。");
            return stored;
        }
    }
}
