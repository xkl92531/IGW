using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace IGW.Licensing;

/// <summary>
/// 本机硬件指纹(授权绑机用)。
/// Windows:注册表 MachineGuid(重装系统才变) + 系统盘卷序列号;
/// Linux:/etc/machine-id(systemd 标准,重装系统才变。容器里可能是随机值,授权版建议裸机/虚机部署)。
/// 哈希后取 16 位十六进制,按 4 位分组显示,如 A1B2-C3D4-E5F6-0718。
/// 两平台同一格式与算法框架,发码工具无差别(码只对指纹,不关心平台)。
/// </summary>
public static class MachineId
{
    private static string? _cached;

    /// <summary>本机机器码(分组格式 XXXX-XXXX-XXXX-XXXX)。</summary>
    public static string Current => _cached ??= Compute();

    /// <summary>去掉分隔符的纯 16 位机器码(用于比对/签发)。</summary>
    public static string Normalize(string? code) =>
        new string((code ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static string Compute()
    {
        string raw = OperatingSystem.IsWindows()
            ? MachineGuid() + "|" + VolumeSerial()
            : LinuxId();
        using var sha = SHA256.Create();
        byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        // 取前 8 字节 → 16 hex,分 4 组
        string hex = Convert.ToHexString(h, 0, 8);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    // Linux 机器标识:systemd 的 /etc/machine-id(安装系统时生成,重装才变);老系统退 dbus 同义文件
    private static string LinuxId()
    {
        foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(p))
                {
                    var s = File.ReadAllText(p).Trim();
                    if (s.Length >= 8) return "mid:" + s;
                }
            }
            catch { }
        }
        return "host:" + Environment.MachineName;   // 兜底(极少见;注意改主机名会变码)
    }

    private static string MachineGuid()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return k?.GetValue("MachineGuid") as string ?? "";
        }
        catch { return ""; }
    }

    private static string VolumeSerial()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
                return serial.ToString("X8");
        }
        catch { }
        return "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
}
