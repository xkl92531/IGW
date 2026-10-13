using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IGW.Abstractions;

/// <summary>Siemens scalar semantics, independent of transport. Calendar strings retain nanoseconds.</summary>
public static class SiemensValueCodec
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTime Epoch = new(1970, 1, 1);
    private static readonly DateTime DateEpoch = new(1990, 1, 1);
    private static readonly DateTime LastSecond = new(2262, 4, 11, 23, 47, 16);
    private const uint LastNanosecond = 854775807;
    private const ulong DayNanoseconds = 86400_000_000_000;
    private static readonly Regex Calendar = new(@"^(\d{4}-\d{2}-\d{2})[ T-](\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?$", RegexOptions.CultureInvariant);
    private static readonly Regex Clock = new(@"^(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?$", RegexOptions.CultureInvariant);
    private static readonly Regex DurationPart = new(@"\G(\d+(?:\.\d+)?)(ms|us|ns|d|h|m|s)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Encoding Windows1252;

    static SiemensValueCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static bool IsSemantic(IgwDataType type) => type is >= IgwDataType.Dtl and <= IgwDataType.DateTime;
    public static bool IsText(IgwDataType type) => type is >= IgwDataType.Dtl and <= IgwDataType.WChar || type == IgwDataType.DateTime;

    public static IgwDataType? FromTypeName(string name) => name.ToUpperInvariant() switch
    {
        "BOOL" or "BOOLEAN" => IgwDataType.Bool,
        "BYTE" or "USINT" => IgwDataType.Byte,
        "SINT" or "SBYTE" => IgwDataType.SInt,
        "INT" or "INT16" => IgwDataType.Int16, "UINT" or "UINT16" => IgwDataType.UInt16,
        "DINT" or "INT32" => IgwDataType.Int32, "UDINT" or "UINT32" => IgwDataType.UInt32,
        "LINT" or "INT64" => IgwDataType.Int64, "ULINT" or "UINT64" => IgwDataType.UInt64,
        "REAL" or "FLOAT" => IgwDataType.Float, "LREAL" or "DOUBLE" => IgwDataType.Double,
        "WORD" => IgwDataType.Word, "DWORD" => IgwDataType.DWord, "LWORD" => IgwDataType.LWord,
        "CHAR" => IgwDataType.S7Char, "WCHAR" => IgwDataType.WChar,
        "STRING" => IgwDataType.String, "WSTRING" => IgwDataType.WString,
        "DTL" => IgwDataType.Dtl, "DATE" => IgwDataType.Date,
        "TOD" or "TIME_OF_DAY" => IgwDataType.Tod, "LTOD" or "LTIME_OF_DAY" => IgwDataType.Ltod,
        "TIME" => IgwDataType.Time, "LTIME" => IgwDataType.Ltime, "S5TIME" => IgwDataType.S5Time,
        "DT" or "DATE_AND_TIME" => IgwDataType.Dt, "LDT" or "DATE_AND_LTIME" => IgwDataType.Ldt,
        "TIMER" => IgwDataType.Timer, "COUNTER" => IgwDataType.Counter,
        "DATETIME" => IgwDataType.DateTime,
        _ => null,
    };

    public static object Normalize(IgwDataType type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is JsonElement json)
            value = json.ValueKind switch {
                JsonValueKind.String => json.GetString()!, JsonValueKind.Number => json.GetDecimal(),
                _ => throw new FormatException("值必须为字符串或数值。") };
        return type switch
        {
            IgwDataType.Dtl => value is byte[] bytes ? DecodeDtl(bytes) : FormatCalendar(ParseCalendar(value, type), 9),
            IgwDataType.Dt => value is byte[] bytes ? DecodeDt(bytes) : FormatCalendar(ParseCalendar(value, type), 3),
            IgwDataType.Ldt => FormatCalendar(ParseCalendar(value, type), 9),
            IgwDataType.Date => NormalizeDate(value),
            IgwDataType.Tod => FormatClock(ParseClock(value, false), 3),
            IgwDataType.Ltod => FormatClock(ParseClock(value, true), 9),
            IgwDataType.Time => $"T#{checked((int)ParseDuration(value, false))}ms",
            IgwDataType.Ltime => $"LT#{ParseDuration(value, true)}ns",
            IgwDataType.S5Time => NormalizeS5Time(value),
            IgwDataType.S7Char => Character(value, false), IgwDataType.WChar => Character(value, true),
            IgwDataType.Word or IgwDataType.Timer or IgwDataType.Counter => checked((ushort)Integer(value)),
            IgwDataType.DWord => checked((uint)Integer(value)), IgwDataType.LWord => checked((ulong)Integer(value)),
            IgwDataType.SInt => checked((sbyte)Integer(value)),
            IgwDataType.DateTime => UaDateTime(value).ToString("O", Invariant),
            _ => value,
        };
    }

    /// <summary>Convert a validated runtime value to its OPC UA built-in wire representation.</summary>
    public static object ToWire(IgwDataType type, object value)
    {
        var normalized = Normalize(type, value);
        string text = Convert.ToString(normalized, Invariant)!;
        return type switch
        {
            IgwDataType.Dtl => EncodeDtl(normalized),
            IgwDataType.Dt => EncodeDt(normalized),
            IgwDataType.Ldt => CalendarDateTime(ParseCalendar(normalized, type)),
            IgwDataType.Date => checked((ushort)(DateTime.ParseExact(text, "yyyy-MM-dd", Invariant) - DateEpoch).Days),
            IgwDataType.Tod => checked((uint)(ParseClock(text, false) / 1_000_000)),
            IgwDataType.Ltod => ParseClock(text, true),
            IgwDataType.Time => checked((int)ParseDuration(text, false)),
            IgwDataType.Ltime => ParseDuration(text, true),
            IgwDataType.S5Time => EncodeS5(ParseDuration(text, false)),
            IgwDataType.S7Char => Windows1252.GetBytes(text)[0],
            IgwDataType.WChar => (ushort)text[0],
            IgwDataType.DateTime => UaDateTime(normalized),
            _ => normalized,
        };
    }

    public static string DecodeDtl(ReadOnlySpan<byte> bytes, bool littleEndian = false)
    {
        if (bytes.Length != 12) throw new FormatException("DTL 必须为 12 字节。");
        int year = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        uint nanos = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
        if (bytes[4] is < 1 or > 7) throw new FormatException("DTL 星期必须为 1 到 7。");
        var date = new DateTime(year, bytes[2], bytes[3], bytes[5], bytes[6], bytes[7]);
        ValidateCalendar(date, nanos, IgwDataType.Dtl);
        return FormatCalendar((date, nanos), 9);
    }

    public static byte[] EncodeDtl(object value, bool littleEndian = false)
    {
        var (date, nanos) = ParseCalendar(value is byte[] bytes ? DecodeDtl(bytes) : value, IgwDataType.Dtl);
        var result = new byte[12];
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)date.Year);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), nanos);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)date.Year);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), nanos);
        }
        result[2] = (byte)date.Month; result[3] = (byte)date.Day;
        result[4] = (byte)((int)date.DayOfWeek + 1);
        result[5] = (byte)date.Hour; result[6] = (byte)date.Minute; result[7] = (byte)date.Second;
        return result;
    }

    private static (DateTime Date, uint Nanos) ParseCalendar(object value, IgwDataType type)
    {
        DateTime date;
        uint nanos;
        if (value is DateTime dt)
        {
            // Siemens calendar components do not acquire the gateway computer's timezone.
            nanos = (uint)(dt.Ticks % TimeSpan.TicksPerSecond * 100);
            date = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);
        }
        else
        {
            string text = StripPrefix(value, type switch {
                IgwDataType.Dtl => ["DTL"], IgwDataType.Dt => ["DT", "DATE_AND_TIME"],
                _ => ["LDT", "DATE_AND_LTIME"] });
            var match = Calendar.Match(text);
            if (!match.Success) throw new FormatException($"{type} 格式应为 yyyy-MM-dd HH:mm:ss.fffffffff，不附加时区。");
            date = DateTime.ParseExact(match.Groups[1].Value + " " + match.Groups[2].Value,
                "yyyy-MM-dd HH:mm:ss", Invariant, DateTimeStyles.None);
            nanos = Fraction(match.Groups[3].Value);
        }
        ValidateCalendar(date, nanos, type);
        return (date, nanos);
    }

    private static void ValidateCalendar(DateTime date, uint nanos, IgwDataType type)
    {
        if (nanos > 999_999_999) throw new FormatException("纳秒必须为 0 到 999999999。");
        if (type == IgwDataType.Dt)
        {
            if (date < DateEpoch || date.Year > 2089 || nanos % 1_000_000 != 0)
                throw new FormatException("DT 范围为 1990 到 2089 年，精度为毫秒。");
        }
        else if (date < Epoch || date > LastSecond || date == LastSecond && nanos > LastNanosecond)
            throw new FormatException($"{type} 范围为 1970-01-01 至 2262-04-11 23:47:16.854775807。");
    }

    private static DateTime CalendarDateTime((DateTime Date, uint Nanos) value)
    {
        if (value.Nanos % 100 != 0)
            throw new FormatException("OPC UA DateTime 的精度为 100 纳秒；此 LDT 值无法无损写入。");
        return DateTime.SpecifyKind(value.Date.AddTicks(value.Nanos / 100), DateTimeKind.Utc);
    }

    private static string FormatCalendar((DateTime Date, uint Nanos) value, int precision)
        => value.Date.ToString("yyyy-MM-dd HH:mm:ss", Invariant) + "." + value.Nanos.ToString("D9", Invariant)[..precision];

    private static string NormalizeDate(object value)
    {
        DateTime date;
        var text = StripPrefix(value, ["D", "DATE"]);
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$"))
            date = DateTime.ParseExact(text, "yyyy-MM-dd", Invariant);
        else date = DateEpoch.AddDays(checked((ushort)Integer(value)));
        // DATE is an unsigned count of days since 1990-01-01 (65535 = 2169-06-06).
        if (date < DateEpoch || (date - DateEpoch).TotalDays > ushort.MaxValue)
            throw new FormatException("DATE 范围为 1990-01-01 至 2169-06-06。");
        return date.ToString("yyyy-MM-dd", Invariant);
    }

    private static ulong ParseClock(object value, bool nanoseconds)
    {
        var text = StripPrefix(value, nanoseconds ? ["LTOD", "LTIME_OF_DAY"] : ["TOD", "TIME_OF_DAY"]);
        var match = Clock.Match(text);
        ulong result;
        if (!match.Success) result = checked((ulong)Integer(text) * (nanoseconds ? 1UL : 1_000_000UL));
        else
        {
            int hour = int.Parse(match.Groups[1].Value, Invariant), minute = int.Parse(match.Groups[2].Value, Invariant), second = int.Parse(match.Groups[3].Value, Invariant);
            if (hour > 23 || minute > 59 || second > 59) throw new FormatException("时分秒超出有效范围。");
            uint nanos = Fraction(match.Groups[4].Value);
            if (!nanoseconds && nanos % 1_000_000 != 0) throw new FormatException("TOD 只能精确到毫秒。");
            result = (ulong)(hour * 3600 + minute * 60 + second) * 1_000_000_000 + nanos;
        }
        if (result >= DayNanoseconds) throw new FormatException("当天时间必须小于 24 小时。");
        return result;
    }

    private static string FormatClock(ulong nanos, int precision)
    {
        ulong seconds = nanos / 1_000_000_000;
        return $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}." + (nanos % 1_000_000_000).ToString("D9", Invariant)[..precision];
    }

    private static long ParseDuration(object value, bool nanoseconds)
    {
        string text = StripPrefix(value, nanoseconds ? ["LT", "LTIME"] : ["T", "TIME", "S5T", "S5TIME"]);
        if (Regex.IsMatch(text, @"^[+-]?\d+$")) return checked((long)Integer(text));
        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        int offset = 0;
        decimal total = 0;
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (offset < text.Length)
        {
            var match = DurationPart.Match(text, offset);
            if (!match.Success || !units.Add(match.Groups[2].Value)) throw new FormatException("时间间隔格式无效，例如 T#1h2m3s 或 LT#123ns。");
            decimal multiplier = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => 86400_000_000_000m, "h" => 3600_000_000_000m, "m" => 60_000_000_000m,
                "s" => 1_000_000_000m, "ms" => 1_000_000m, "us" => 1000m, _ => 1m,
            };
            total = checked(total + decimal.Parse(match.Groups[1].Value, Invariant) * multiplier);
            offset += match.Length;
        }
        if (offset == 0) throw new FormatException("时间间隔不能为空。");
        if (!nanoseconds) total /= 1_000_000;
        if (negative) total = -total;
        if (decimal.Truncate(total) != total) throw new FormatException("时间间隔超出目标精度。");
        return checked((long)total);
    }

    private static string NormalizeS5Time(object value)
    {
        string text = Convert.ToString(value, Invariant)!;
        long milliseconds;
        if (text.Contains('#') || Regex.IsMatch(text, "[a-zA-Z]")) milliseconds = ParseDuration(text, false);
        else
        {
            ushort code = checked((ushort)Integer(value));
            if ((code & 0xc000) != 0) throw new FormatException("S5TIME 编码保留位必须为零。");
            int digits = BcdDigit((code >> 8) & 15) * 100 + BcdDigit((code >> 4) & 15) * 10 + BcdDigit(code & 15);
            milliseconds = digits * new[] { 10, 100, 1000, 10000 }[(code >> 12) & 3];
        }
        _ = EncodeS5(milliseconds);
        return $"S5T#{milliseconds}ms";
    }

    private static ushort EncodeS5(long milliseconds)
    {
        int[] bases = [10, 100, 1000, 10000];
        for (int i = 0; i < bases.Length; i++)
        {
            long digits = milliseconds / bases[i];
            if (milliseconds >= 0 && milliseconds % bases[i] == 0 && digits <= 999)
                return (ushort)((i << 12) | ((int)digits / 100 << 8) | ((int)digits / 10 % 10 << 4) | (int)digits % 10);
        }
        throw new FormatException("S5TIME 必须能由 0–999 与 10/100/1000/10000 毫秒时间基数精确表示。");
    }

    private static string DecodeDt(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8) throw new FormatException("DT 必须为 8 字节。");
        int year = FromBcd(bytes[0]); year += year >= 90 ? 1900 : 2000;
        if ((bytes[7] & 15) is < 1 or > 7) throw new FormatException("DT 星期必须为 1 到 7。");
        var date = new DateTime(year, FromBcd(bytes[1]), FromBcd(bytes[2]), FromBcd(bytes[3]), FromBcd(bytes[4]), FromBcd(bytes[5]));
        uint nanos = (uint)(FromBcd(bytes[6]) * 10 + BcdDigit(bytes[7] >> 4)) * 1_000_000;
        ValidateCalendar(date, nanos, IgwDataType.Dt);
        return FormatCalendar((date, nanos), 3);
    }

    private static byte[] EncodeDt(object value)
    {
        var (date, nanos) = ParseCalendar(value, IgwDataType.Dt);
        int milliseconds = (int)(nanos / 1_000_000);
        return [ToBcd(date.Year % 100), ToBcd(date.Month), ToBcd(date.Day), ToBcd(date.Hour),
            ToBcd(date.Minute), ToBcd(date.Second), ToBcd(milliseconds / 10),
            (byte)((milliseconds % 10 << 4) | ((int)date.DayOfWeek + 1))];
    }

    private static int BcdDigit(int digit) => digit <= 9 ? digit : throw new FormatException("无效的 BCD 数字。");
    private static int FromBcd(byte value) => BcdDigit(value >> 4) * 10 + BcdDigit(value & 15);
    private static byte ToBcd(int value) => (byte)((value / 10 << 4) | value % 10);
    private static uint Fraction(string value) => value.Length == 0 ? 0 : uint.Parse(value.PadRight(9, '0'), Invariant);

    private static string Character(object value, bool wide)
    {
        if (value is not string text)
            text = wide ? ((char)checked((ushort)Integer(value))).ToString() : Windows1252.GetString([checked((byte)Integer(value))]);
        if (text.Length != 1 || char.IsSurrogate(text[0])) throw new FormatException("CHAR/WCHAR 必须为一个可编码字符。");
        if (!wide && Windows1252.GetByteCount(text) != 1) throw new FormatException("CHAR 必须为 Windows-1252 单字节字符。");
        return text;
    }

    private static string StripPrefix(object value, string[] prefixes)
    {
        string text = (Convert.ToString(value, Invariant) ?? "").Trim();
        int hash = text.IndexOf('#');
        if (hash >= 0)
        {
            if (!prefixes.Contains(text[..hash], StringComparer.OrdinalIgnoreCase)) throw new FormatException("类型前缀与点位类型不匹配。");
            text = text[(hash + 1)..];
        }
        return text.Replace("_", "", StringComparison.Ordinal);
    }

    private static decimal Integer(object value)
    {
        if (value is double d && Math.Abs(d) > 9007199254740991d || value is float f && Math.Abs(f) > 16777215f)
            throw new FormatException("大整数请使用十进制字符串，避免浮点数丢失精度。");
        string text = (Convert.ToString(value, Invariant) ?? "").Replace("_", "", StringComparison.Ordinal);
        // Siemens bit-string literals, e.g. W#16#FFFF or LWORD#16#FFFF_FFFF_FFFF_FFFF.
        if (text.Contains('#'))
        {
            var parts = text.Split('#');
            int start = parts.Length == 3 ? 1 : 0;
            if (parts.Length - start != 2 || !int.TryParse(parts[start], out int radix) || radix is not (2 or 8 or 16))
                throw new FormatException("整数进制格式无效。");
            return Convert.ToUInt64(parts[start + 1], radix);
        }
        decimal number = decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, Invariant);
        if (decimal.Truncate(number) != number) throw new FormatException("整数类型不能写入小数。");
        return number;
    }

    private static DateTime UaDateTime(object value)
    {
        if (value is DateTime date) return date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
        string text = Convert.ToString(value, Invariant)!.Trim();
        if (!Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase))
            throw new FormatException("OPC UA DateTime 需要带 Z 或时区偏移的 ISO 时间。");
        var fraction = Regex.Match(text, @"\.(\d+)");
        if (fraction.Success && fraction.Groups[1].Length > 7 && fraction.Groups[1].Value[7..].Any(c => c != '0'))
            throw new FormatException("OPC UA DateTime 的精度为 100 纳秒。");
        return DateTimeOffset.Parse(text, Invariant, DateTimeStyles.None).UtcDateTime;
    }
}
