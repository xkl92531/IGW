using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <param name="Classic">绝对地址(DB1.DBW0)走 S7 Classic。</param>
/// <param name="Plus">符号地址(DB名.成员)走 S7 Plus。</param>
/// <param name="Unroutable">这台设备读不了的点位，例如 200/300/400 上配了符号地址。</param>
public sealed record SiemensRoutePlan(
    IReadOnlyList<TagBinding> Classic,
    IReadOnlyList<TagBinding> Plus,
    IReadOnlyList<(TagBinding Binding, string Reason)> Unroutable);

public static class SiemensRoutePlanner
{
    /// <summary>
    /// 按地址形式把一批点位分到两条协议通道。地址形式不被这台设备支持的点位单独列出，
    /// 由调用方标成坏值——它们不能让整批读取失败：一个配错协议的点位若掀翻整批，
    /// 同批的好点会跟着变坏值，读取失败还会被判成连接问题而反复重连。
    /// </summary>
    public static SiemensRoutePlan Plan(IReadOnlyList<TagBinding> bindings, bool supportsPlus)
    {
        var classic = new List<TagBinding>(bindings.Count);
        var plus = new List<TagBinding>();
        List<(TagBinding, string)>? unroutable = null;
        foreach (var binding in bindings)
        {
            SiemensAddressDescriptor descriptor;
            try
            {
                descriptor = SiemensAddressDescriptor.Parse(binding.Def.Address);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                (unroutable ??= []).Add((binding, $"地址 {binding.Def.Address} 无法解析：{ex.Message}"));
                continue;
            }

            if (descriptor.Kind == SiemensAddressKind.Classic)
            {
                classic.Add(binding);
                continue;
            }

            if (!supportsPlus)
            {
                (unroutable ??= []).Add((binding,
                    $"点位使用 S7 Plus 符号地址，但当前 PLC 型号仅支持 S7 Classic：{binding.Def.Address}"));
                continue;
            }
            plus.Add(binding);
        }
        return new SiemensRoutePlan(classic, plus, (IReadOnlyList<(TagBinding, string)>?)unroutable ?? []);
    }
}
