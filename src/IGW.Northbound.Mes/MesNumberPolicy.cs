using System.Collections;
using System.Globalization;
using IGW.Abstractions;

namespace IGW.Northbound.Mes;

/// <summary>
/// 报文里的小数统一处理。后台接口不认 NaN 和无穷大(直接写出去还不是合法 JSON，常见的是被
/// [Range] 校验拦下)：无穷大写成 99999999，无穷小写成 -99999999，NaN 写成 null；
/// 小数再按上报节点设置的位数和取法截取。整数、字符串、布尔原样输出。
/// </summary>
public static class MesNumberPolicy
{
    public const long Limit = 99999999;
    public const int MaxDecimals = 10;

    /// <summary>处理单个值或数组/结构体投影出来的值(object?[]、字典)，返回新的值，不改原对象。</summary>
    public static object? Apply(object? value, MesNumberFormat? format)
    {
        switch (value)
        {
            case float f: return Real(f, f, f.ToString(CultureInfo.InvariantCulture), format);
            case double d: return Real(d, d, d.ToString("R", CultureInfo.InvariantCulture), format);
            case decimal m: return DecimalsOf(format) is int n ? RoundTo(m, n, format!.Rounding) : m;
            case IDictionary<string, object?> map:
            {
                var result = new Dictionary<string, object?>(map.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var item in map) result[item.Key] = Apply(item.Value, format);
                return result;
            }
            case string: return value;
            case IList list:
            {
                var items = new object?[list.Count];
                for (int i = 0; i < items.Length; i++) items[i] = Apply(list[i], format);
                return items;
            }
            default: return value;
        }
    }

    private static object? Real(double value, object original, string text, MesNumberFormat? format)
    {
        if (double.IsNaN(value)) return null;
        if (double.IsPositiveInfinity(value)) return Limit;
        if (double.IsNegativeInfinity(value)) return -Limit;
        if (DecimalsOf(format) is not int n) return original;
        // 按显示出来的最短写法转十进制再截取，避免 float 0.1 变成 0.100000001490116
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact)
            ? RoundTo(exact, n, format!.Rounding)
            : original;   // 超出十进制范围的极大值不截取
    }

    private static int? DecimalsOf(MesNumberFormat? format) =>
        format?.Decimals is int n ? Math.Clamp(n, 0, MaxDecimals) : null;

    private static decimal RoundTo(decimal value, int decimals, string? rounding) => rounding switch
    {
        MesNumberRounding.Truncate => Math.Round(value, decimals, MidpointRounding.ToZero),
        MesNumberRounding.Ceiling => Math.Round(value, decimals,
            value >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity),
        _ => Math.Round(value, decimals, MidpointRounding.AwayFromZero),
    };
}
