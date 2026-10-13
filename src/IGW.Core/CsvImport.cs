using System.Text;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>CSV 行级错误(行号 1-based + 原因)。</summary>
public sealed record CsvRowError(int Line, string Message);

/// <summary>批量导入结果:成功时 <see cref="Project"/> 为已套用全部行的新工程(原子)、<see cref="Devices"/> 为新建设备全名;任一行错则 Project=null 且 Errors 逐行列出。</summary>
public sealed record CsvImportResult(IgwProject? Project, IReadOnlyList<CsvRowError> Errors, int Count, IReadOnlyList<string> Devices)
{
    public bool Ok => Errors.Count == 0 && Project != null;
}

/// <summary>
/// CSV 设备清单批量实例化(纯逻辑):解析(UTF-8,容 BOM,行级定位)→ 全量校验(复用 <see cref="TemplateOps.Apply"/>)→ 原子提交。
/// 列:设备名 / 通道 / 模板名 / 端点(+可选设备设置列,列名=设置键)。任一行错 → 整体不写,返回逐行错误报告。
/// </summary>
public static class CsvImport
{
    private static readonly string[] NameAliases = { "设备名", "设备名称", "device", "devicename", "name" };
    private static readonly string[] ChannelAliases = { "通道", "通道名", "channel" };
    private static readonly string[] TemplateAliases = { "模板名", "模板", "template", "templatename" };
    private static readonly string[] EndpointAliases = { "端点", "地址", "endpoint", "ip" };

    /// <summary>可下载的样例模板文件内容(UTF-8)。</summary>
    public static string SampleCsv() =>
        "设备名,通道,模板名,端点\n" +
        "注塑机01,Modbus通道,注塑机-A,192.168.1.11:502\n" +
        "注塑机02,Modbus通道,注塑机-A,192.168.1.12:502\n";

    public static CsvImportResult Build(IgwProject current, string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
            return new(null, new[] { new CsvRowError(0, "CSV 内容为空。") }, 0, Array.Empty<string>());

        csvText = csvText.TrimStart('﻿');   // 容错 BOM
        var lines = csvText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        int hi = 0;
        while (hi < lines.Length && string.IsNullOrWhiteSpace(lines[hi])) hi++;
        if (hi >= lines.Length)
            return new(null, new[] { new CsvRowError(0, "CSV 无表头。") }, 0, Array.Empty<string>());

        var headers = SplitLine(lines[hi]).Select(h => h.Trim()).ToList();
        int ciName = FindCol(headers, NameAliases), ciCh = FindCol(headers, ChannelAliases),
            ciTpl = FindCol(headers, TemplateAliases), ciEp = FindCol(headers, EndpointAliases);
        var missing = new List<string>();
        if (ciName < 0) missing.Add("设备名");
        if (ciCh < 0) missing.Add("通道");
        if (ciTpl < 0) missing.Add("模板名");
        if (ciEp < 0) missing.Add("端点");
        if (missing.Count > 0)
            return new(null, new[] { new CsvRowError(hi + 1, "缺少必需列:" + string.Join("/", missing) + "。") }, 0, Array.Empty<string>());

        // 其余非空列名 → 设备设置键
        var required = new HashSet<int> { ciName, ciCh, ciTpl, ciEp };
        var settingCols = new List<(string key, int idx)>();
        for (int i = 0; i < headers.Count; i++)
            if (!required.Contains(i) && !string.IsNullOrWhiteSpace(headers[i]))
                settingCols.Add((headers[i], i));

        var errors = new List<CsvRowError>();
        var devices = new List<string>();
        var work = ProjectStore.Clone(current);   // 全程改动落在副本上;有错则整体丢弃(原子)

        for (int li = hi + 1; li < lines.Length; li++)
        {
            if (string.IsNullOrWhiteSpace(lines[li])) continue;
            int lineNo = li + 1;   // 1-based(与用户在编辑器里看到的行号一致)
            var cells = SplitLine(lines[li]);
            string name = Cell(cells, ciName), channel = Cell(cells, ciCh), tpl = Cell(cells, ciTpl), ep = Cell(cells, ciEp);

            if (string.IsNullOrWhiteSpace(ep)) { errors.Add(new(lineNo, $"设备 {name}:端点为空。")); continue; }

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, idx) in settingCols)
            {
                var v = Cell(cells, idx);
                if (!string.IsNullOrWhiteSpace(v)) settings[key] = v;
            }

            var err = TemplateOps.Apply(work, tpl, channel, name, ep, settings);   // 复用套用逻辑:模板存在/驱动匹配/重名/名称非空
            if (err != null) errors.Add(new(lineNo, err));
            else devices.Add($"{channel}.{name}");
        }

        if (errors.Count > 0) return new(null, errors, 0, Array.Empty<string>());   // 任一行错 → 整体不导
        return new(work, Array.Empty<CsvRowError>(), devices.Count, devices);
    }

    private static int FindCol(List<string> headers, string[] aliases)
    {
        for (int i = 0; i < headers.Count; i++)
            if (aliases.Contains(headers[i], StringComparer.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Cell(List<string> cells, int idx) => idx >= 0 && idx < cells.Count ? cells[idx].Trim() : "";

    // 支持双引号字段(内含逗号)与转义引号("")。不处理引号内换行(逐行解析)。
    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
