using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Core;

public sealed record ExpandedArrayProposal(
    string BaseName,
    int LowerBound,
    int Count,
    IReadOnlyList<string> SourceTagIds,
    bool CanApply,
    string Reason,
    int ElementStrideBytes = 0,
    int ElementStrideBits = 0);

/// <summary>
/// 旧版逐元素点位到单一逻辑数组的显式迁移工具。只识别同容器内、同类型、连续下标和连续地址的
/// <c>Name[index]</c> 点位；预览本身不改工程，调用 Apply 才会替换。
/// </summary>
public static class ExpandedArrayMigration
{
    private static readonly Regex IndexedName = new(@"^(?<base>.+)\[(?<index>-?\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex S7ByteAddress = new(
        @"^(?<prefix>DB\d+\.DB[XBWD])(?<offset>\d+)(?:\.(?<bit>\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RegisterAddress = new(
        @"^(?<prefix>[A-Za-z]+)(?<offset>[0-9A-Fa-f]+)$", RegexOptions.Compiled);

    public static IReadOnlyList<ExpandedArrayProposal> Preview(IReadOnlyList<TagDef> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var candidates = tags.Select(tag => (Tag: tag, Match: IndexedName.Match(tag.Name ?? "")))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Groups["base"].Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<ExpandedArrayProposal>();
        foreach (var group in candidates)
        {
            var rows = group.Select(x => (x.Tag, Index: int.Parse(x.Match.Groups["index"].Value)))
                .OrderBy(x => x.Index).ToArray();
            var ids = rows.Select(x => x.Tag.Id).ToArray();
            if (rows.Length < 2)
            {
                result.Add(new(group.Key, rows[0].Index, rows.Length, ids, false, "至少需要两个连续元素"));
                continue;
            }
            if (rows.Select(x => x.Index).Distinct().Count() != rows.Length ||
                rows[^1].Index - rows[0].Index + 1 != rows.Length)
            {
                result.Add(new(group.Key, rows[0].Index, rows.Length, ids, false, "数组下标不连续或重复"));
                continue;
            }
            var first = rows[0].Tag;
            if (rows.Any(x => x.Tag.DataType != first.DataType || x.Tag.Array is not null ||
                              x.Tag.Access != first.Access || x.Tag.ScanRateMs != first.ScanRateMs))
            {
                result.Add(new(group.Key, rows[0].Index, rows.Length, ids, false,
                    "元素类型、读写权限或扫描周期不一致"));
                continue;
            }
            if (!TryAddressStride(rows.Select(x => x.Tag.Address).ToArray(), first.DataType,
                    out var strideBytes, out var strideBits, out var reason))
            {
                result.Add(new(group.Key, rows[0].Index, rows.Length, ids, false, reason));
                continue;
            }
            result.Add(new(group.Key, rows[0].Index, rows.Length, ids, true, "可合并",
                strideBytes, strideBits));
        }
        return result;
    }

    public static TagDef Apply(List<TagDef> tags, ExpandedArrayProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.CanApply) throw new InvalidOperationException("该迁移预览不可应用:" + proposal.Reason);
        var current = Preview(tags).FirstOrDefault(x =>
            string.Equals(x.BaseName, proposal.BaseName, StringComparison.OrdinalIgnoreCase));
        if (current is null || !current.CanApply || !current.SourceTagIds.SequenceEqual(proposal.SourceTagIds))
            throw new InvalidOperationException("点位结构已变化，请重新预览后再迁移");
        var source = current.SourceTagIds.Select(id => tags.First(x => x.Id == id)).ToArray();
        var first = source[0];
        var merged = new TagDef
        {
            Id = first.Id,
            Name = current.BaseName,
            Address = first.Address,
            DataType = first.DataType,
            IsArray = true,
            Array = new ArrayDef
            {
                ElementType = first.DataType,
                Dimensions = [current.Count],
                LowerBounds = [current.LowerBound],
                ElementStrideBytes = current.ElementStrideBytes,
                ElementStrideBits = current.ElementStrideBits,
                StringLength = first.StringLength,
                SourceShape = "legacy-expanded-tags",
            },
            Access = first.Access,
            ScanRateMs = first.ScanRateMs,
            Scaling = first.Scaling,
            Deadband = first.Deadband,
            DeadbandType = first.DeadbandType,
            Description = first.Description,
            EngUnit = first.EngUnit,
            Metadata = first.Metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(first.Metadata, StringComparer.OrdinalIgnoreCase),
            Alarm = first.Alarm,
            StringLength = first.StringLength,
            TextEncoding = first.TextEncoding,
        };
        merged.Metadata["legacyElementIds"] = string.Join(",", current.SourceTagIds);
        var insertAt = tags.IndexOf(first);
        tags.RemoveAll(x => current.SourceTagIds.Contains(x.Id, StringComparer.Ordinal));
        tags.Insert(Math.Min(insertAt, tags.Count), merged);
        return merged;
    }

    private static bool TryAddressStride(IReadOnlyList<string> addresses, IgwDataType type,
        out int strideBytes, out int strideBits, out string reason)
    {
        strideBytes = 0;
        strideBits = 0;
        reason = "地址格式不支持自动合并";
        var s7 = addresses.Select(x => S7ByteAddress.Match(x ?? "")).ToArray();
        if (s7.All(x => x.Success) && s7.Select(x => x.Groups["prefix"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            if (type == IgwDataType.Bool && s7.All(x => x.Groups["bit"].Success))
            {
                var offsets = s7.Select(x => int.Parse(x.Groups["offset"].Value) * 8 +
                    int.Parse(x.Groups["bit"].Value)).ToArray();
                strideBits = ConstantPositiveStride(offsets);
                if (strideBits > 0) { reason = ""; return true; }
            }
            else
            {
                var offsets = s7.Select(x => int.Parse(x.Groups["offset"].Value)).ToArray();
                strideBytes = ConstantPositiveStride(offsets);
                if (strideBytes > 0) { reason = ""; return true; }
            }
            reason = "PLC 地址不连续或步长不一致";
            return false;
        }

        var registers = addresses.Select(x => RegisterAddress.Match(x ?? "")).ToArray();
        if (registers.All(x => x.Success) && registers.Select(x => x.Groups["prefix"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            var prefix = registers[0].Groups["prefix"].Value;
            var hex = prefix is "X" or "x" or "Y" or "y" or "B" or "b" or "W" or "w";
            var offsets = registers.Select(x => Convert.ToInt32(x.Groups["offset"].Value, hex ? 16 : 10)).ToArray();
            var stride = ConstantPositiveStride(offsets);
            if (stride > 0)
            {
                if (type == IgwDataType.Bool) strideBits = stride;
                else strideBytes = checked(stride * 2);
                reason = "";
                return true;
            }
            reason = "PLC 地址不连续或步长不一致";
        }
        return false;
    }

    private static int ConstantPositiveStride(IReadOnlyList<int> offsets)
    {
        if (offsets.Count < 2) return 0;
        var stride = offsets[1] - offsets[0];
        if (stride <= 0) return 0;
        for (var i = 2; i < offsets.Count; i++)
            if (offsets[i] - offsets[i - 1] != stride) return 0;
        return stride;
    }
}
