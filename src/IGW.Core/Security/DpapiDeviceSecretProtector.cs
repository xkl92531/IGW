using System.Security.Cryptography;

namespace IGW.Core.Security;

public sealed class DpapiDeviceSecretProtector : IDeviceSecretProtector
{
    private static readonly byte[] Entropy = "IGW.DeviceCredential.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PLC 凭据仅支持 Windows DPAPI 本机加密。");
        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PLC 凭据仅支持 Windows DPAPI 本机加密。");
        return ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
    }
}
