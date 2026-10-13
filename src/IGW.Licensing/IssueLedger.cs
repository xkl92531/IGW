using System.Text.Json;

namespace IGW.Licensing;

/// <summary>
/// 发码台账:每签发一张授权码记一行 JSONL(与私钥同目录 issued-codes.jsonl)。
/// 仅厂商发码工具(IgwKeygen / IgwKeygenGui)使用;客户端 Runtime/UI 不调用。
/// 台账含客户清单与授权码,和私钥一样只留在厂商机器上(gitignore、不进安装包)。
/// </summary>
public static class IssueLedger
{
    public sealed class Entry
    {
        public DateTime Ts { get; set; }              // 签发时间(本地)
        public string Customer { get; set; } = "";
        public string Machine { get; set; } = "";     // 规范化 16 位机器码
        public string Expiry { get; set; } = "";      // yyyy-MM-dd(本地)
        public int Grace { get; set; }
        public string Note { get; set; } = "";
        public string Code { get; set; } = "";        // 完整授权码(便于补发)
        public string Drivers { get; set; } = "";     // 授权的驱动名称(便于在台账里看)
        public string Features { get; set; } = "";    // 授权的功能模块名称
    }

    private static readonly JsonSerializerOptions J = new() { WriteIndented = false };

    public static string PathFor(string privKeyPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(privKeyPath)) ?? ".", "issued-codes.jsonl");

    /// <summary>追加一条台账(失败静默:记账不能挡发码)。</summary>
    public static void Append(string privKeyPath, Entry e)
    {
        try { File.AppendAllText(PathFor(privKeyPath), JsonSerializer.Serialize(e, J) + Environment.NewLine); }
        catch { }
    }

    /// <summary>读全部台账,最新在前;坏行跳过。</summary>
    public static List<Entry> Load(string privKeyPath)
    {
        var list = new List<Entry>();
        try
        {
            string p = PathFor(privKeyPath);
            if (!File.Exists(p)) return list;
            foreach (var line in File.ReadLines(p))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<Entry>(line, J) is { } e) list.Add(e); } catch { }
            }
        }
        catch { }
        list.Reverse();
        return list;
    }
}
