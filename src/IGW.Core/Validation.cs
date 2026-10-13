using IGW.Abstractions;

namespace IGW.Core;

/// <summary>套用验证的单点结果类型。</summary>
public enum ValidationResultKind
{
    Matched,   // 可浏览:模板点在真机地址空间已对上
    Missing,   // 可浏览:模板有、真机无
    Extra,     // 可浏览:真机有、模板无(可收编为实例追加点)
    ReadOk,    // 不可浏览:试读读通
    ReadFail,  // 不可浏览:试读超时/异常
}

/// <summary>统一验证报告的单点结构。</summary>
public sealed record PointResult(string Path, string Address, ValidationResultKind Result, string Detail);

/// <summary>
/// 套用验证的纯匹配逻辑(不依赖运行引擎,便于单测):
/// 可浏览驱动按 点名/地址 与真机地址空间匹配;不可浏览驱动按展开点集逐点试读(结果由调用方经缓存查得的委托提供)。
/// </summary>
public static class ValidationMatch
{
    /// <summary>可浏览驱动:展开点集 vs 真机浏览结果,产出 matched/missing/extra。按 地址 或 点名 任一相等即视为对上。</summary>
    public static List<PointResult> MatchBrowse(
        IReadOnlyList<(string Path, string Name, string Address)> effective,
        IReadOnlyList<BrowsedTag> browsed)
    {
        var browsedAddr = new HashSet<string>(browsed.Select(b => b.Address), StringComparer.OrdinalIgnoreCase);
        var browsedName = new HashSet<string>(browsed.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var effAddr = new HashSet<string>(effective.Select(e => e.Address), StringComparer.OrdinalIgnoreCase);
        var effName = new HashSet<string>(effective.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

        var result = new List<PointResult>(effective.Count + browsed.Count);
        foreach (var e in effective)
        {
            bool ok = (!string.IsNullOrEmpty(e.Address) && browsedAddr.Contains(e.Address)) || browsedName.Contains(e.Name);
            result.Add(new PointResult(e.Path, e.Address, ok ? ValidationResultKind.Matched : ValidationResultKind.Missing,
                ok ? "" : "真机地址空间未发现该点"));
        }
        foreach (var b in browsed)
        {
            bool inTmpl = (!string.IsNullOrEmpty(b.Address) && effAddr.Contains(b.Address)) || effName.Contains(b.Name);
            if (!inTmpl)
                result.Add(new PointResult(b.Name, b.Address, ValidationResultKind.Extra, "真机存在、模板未含(可收编)"));
        }
        return result;
    }

    /// <summary>不可浏览驱动:对展开点集逐点试读。<paramref name="read"/> 按相对路径返回 (是否读通, 错误摘要)。</summary>
    public static List<PointResult> TrialRead(
        IReadOnlyList<(string Path, string Name, string Address)> effective,
        Func<string, (bool Ok, string Detail)> read)
    {
        var result = new List<PointResult>(effective.Count);
        foreach (var e in effective)
        {
            var (ok, detail) = read(e.Path);
            result.Add(new PointResult(e.Path, e.Address, ok ? ValidationResultKind.ReadOk : ValidationResultKind.ReadFail, ok ? "" : detail));
        }
        return result;
    }
}
