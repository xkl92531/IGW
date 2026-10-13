using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public enum SiemensAddressKind { Classic, Symbolic }

/// <summary>
/// Classic 绝对地址的「区域」编码，复用 DbNumber 这一个字段传递：
/// 非负数就是 DB 号，负数是过程映像区。这样块读规划、分片、传输三层都不用改签名，
/// 而 <see cref="ToAddress"/> 负责还原成 IoTClient 认的地址串——两者用的是同一个
/// Read(address, length, isBit) 入口，DB 区与 I/Q/M/V 区只差这个前缀。
/// </summary>
public static class SiemensAreaCode
{
    public const int Input = -1;      // I(英)/E(德) 输入过程映像
    public const int Output = -2;     // Q(英)/A(德) 输出过程映像
    public const int Memory = -3;     // M 位存储器
    public const int Variable = -4;   // V 变量区(S7-200)

    public static bool IsProcessImage(int area) => area < 0;

    public static int FromPrefix(char prefix) => char.ToUpperInvariant(prefix) switch
    {
        'I' or 'E' => Input,
        'Q' or 'A' => Output,
        'M' => Memory,
        'V' => Variable,
        _ => throw new FormatException($"未知的 Siemens 数据区前缀:{prefix}"),
    };

    /// <summary>还原成协议寻址用的地址串:DB 区 "DB436.DBB0",过程映像区 "Q204"。</summary>
    public static string ToAddress(int area, int byteOffset) => area switch
    {
        >= 0 => $"DB{area}.DBB{byteOffset}",
        Input => $"I{byteOffset}",
        Output => $"Q{byteOffset}",
        Memory => $"M{byteOffset}",
        Variable => $"V{byteOffset}",
        _ => throw new FormatException($"未知的 Siemens 数据区编码:{area}"),
    };
}

public sealed record SiemensAddressDescriptor(
    SiemensAddressKind Kind,
    string Raw,
    int? DbNumber,
    int? ByteOffset,
    int? BitOffset,
    string? SymbolPath)
{
    private static readonly Regex DbAddress = new(
        @"^DB\s*(?<db>\d+)\.(?<area>DBX|DBB|DBW|DBD)(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AreaAddress = new(
        @"^(?<area>V|M|I|Q|E|A)(?:X|B|W|D)?(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static SiemensAddressDescriptor Parse(string address)
    {
        string raw = address?.Trim() ?? "";
        if (raw.Length == 0) throw new FormatException("Siemens 地址不能为空。");

        var match = DbAddress.Match(raw);
        if (match.Success)
        {
            int? bit = match.Groups["bit"].Success ? int.Parse(match.Groups["bit"].Value) : null;
            return new SiemensAddressDescriptor(
                SiemensAddressKind.Classic,
                raw,
                int.Parse(match.Groups["db"].Value),
                int.Parse(match.Groups["offset"].Value),
                bit,
                null);
        }

        // 过程映像区(I/Q/M/V)和 DB 区一样是绝对地址,只是区域码不同:这里同样给出字节/位偏移,
        // 读取规划器才能把它们合并成块读。缺了偏移它们会被整批过滤掉、退化成逐点读——
        // 现场 1000 个 I/Q 点全读不到就是这么来的。
        var area = AreaAddress.Match(raw);
        if (area.Success)
            return new SiemensAddressDescriptor(
                SiemensAddressKind.Classic,
                raw,
                SiemensAreaCode.FromPrefix(area.Groups["area"].Value[0]),
                int.Parse(area.Groups["offset"].Value),
                area.Groups["bit"].Success ? int.Parse(area.Groups["bit"].Value) : null,
                null);

        if (!raw.Contains('.', StringComparison.Ordinal) || raw.Any(char.IsWhiteSpace))
            throw new FormatException($"无法识别 Siemens 地址:{raw}");

        return new SiemensAddressDescriptor(SiemensAddressKind.Symbolic, raw, null, null, null, raw);
    }
}

/// <summary>
/// 西门子扫描地址解析器。Area 表示独立的数据区/DB，确保一个 DB 的地址错误不会和
/// 同一设备下其它 DB 合并到同一个读取块；真正的协议寻址仍使用 TagDef.Address。
/// </summary>
public sealed class SiemensScanAddressParser : ITagDefinitionAddressParser
{
    private const int DbAreaBase = 100_000;
    private static readonly Regex SymbolDb = new(
        @"(?:^|\.)DB(?<db>\d+)(?=[_.\[]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error)
        => TryParse(new TagDef { Address = address, DataType = type }, out result, out error);

    public bool TryParse(TagDef definition, out TagAddress result, out string? error)
    {
        if (!TryParseCore(definition.Address, definition.DataType, out result, out error)) return false;
        try
        {
            result = new TagAddress(result.Area, result.Offset,
                ProtocolStorageSize.Estimate(definition, result), result.BitOffset);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseCore(string address, IgwDataType type, out TagAddress result, out string? error)
    {
        result = default;
        error = null;
        try
        {
            var descriptor = SiemensAddressDescriptor.Parse(address);
            int length = LengthOf(type);
            if (descriptor.DbNumber is int classicDb)
            {
                result = new TagAddress(DbAreaBase + classicDb, descriptor.ByteOffset ?? 0, length,
                    descriptor.BitOffset ?? -1);
                return true;
            }

            string raw = descriptor.Raw;
            var symbolDb = SymbolDb.Match(raw);
            if (symbolDb.Success && int.TryParse(symbolDb.Groups["db"].Value, out int db))
            {
                result = new TagAddress(DbAreaBase + db, 0, length);
                return true;
            }

            string scope = descriptor.Kind == SiemensAddressKind.Symbolic
                ? SymbolRoot(raw)
                : raw[..1].ToUpperInvariant();
            result = new TagAddress(StableArea(scope), 0, length);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SymbolRoot(string address)
    {
        int separator = address.IndexOf('.');
        return separator > 0 ? address[..separator] : address;
    }

    private static int StableArea(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in value.ToUpperInvariant())
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return -1 - (int)(hash & 0x3FFF_FFFF);
        }
    }

    private static int LengthOf(IgwDataType type) => type switch
    {
        IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
        _ => 1,
    };
}
