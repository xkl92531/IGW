using IGW.Abstractions;
using IGW.Drivers.IoTClient;

namespace IGW.Runtime;

/// <summary>
/// 西门子地址模式守卫：拒绝同一设备内混用 S7 Classic 绝对地址（如 DB1.DBX0.0）
/// 和 S7 Plus 符号地址（如 DB490.Type.Error1）——驱动在构造时会因此抛异常，
/// 整台设备无法启动（见 <c>SiemensHybridDeviceDriver.cs</c>）。此处在写入前提前拦截。
/// </summary>
public static class SiemensAddressModeGuard
{
    /// <summary>
    /// 检查设备下所有点位（根点 + 所有组/子组）的地址模式是否一致。
    /// 非 Siemens.S7 通道直接放行；无法解析的地址忽略（交给驱动报错）。
    /// </summary>
    /// <returns>存在混用时返回中文错误信息；否则返回 null。</returns>
    public static string? Check(ChannelDef channel, DeviceDef device)
    {
        if (!string.Equals(channel.DriverId, IoTSiemensDriverFactory.Id, StringComparison.OrdinalIgnoreCase))
            return null;

        string? classicExample = null;
        string? symbolicExample = null;

        foreach (var tag in EnumerateTags(device))
        {
            SiemensAddressDescriptor descriptor;
            try
            {
                descriptor = SiemensAddressDescriptor.Parse(tag.Address);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                continue; // 无法解析（含超长 DB 号/偏移量导致 int.Parse 溢出）的地址交给驱动报错，此处忽略
            }

            if (descriptor.Kind == SiemensAddressKind.Classic)
                classicExample ??= descriptor.Raw;
            else
                symbolicExample ??= descriptor.Raw;

            if (classicExample is not null && symbolicExample is not null)
            {
                return $"设备 {device.Name} 同时包含 S7 绝对地址（如 {classicExample}）和 S7 Plus 符号地址（如 {symbolicExample}），请分到两台设备";
            }
        }

        return null;
    }

    private static IEnumerable<TagDef> EnumerateTags(DeviceDef device)
    {
        foreach (var tag in device.Tags)
            yield return tag;

        foreach (var tag in EnumerateGroupTags(device.Groups))
            yield return tag;
    }

    private static IEnumerable<TagDef> EnumerateGroupTags(List<TagGroupDef> groups)
    {
        foreach (var group in groups)
        {
            foreach (var tag in group.Tags)
                yield return tag;

            foreach (var tag in EnumerateGroupTags(group.SubGroups))
                yield return tag;
        }
    }
}
