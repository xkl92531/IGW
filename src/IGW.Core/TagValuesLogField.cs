using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 采集记录、质量变化记录附带的点位值(日志字段 igw_tag_values)。现场排查要知道「那一刻 PLC 里是什么值」，
/// 只有批次耗时和坏点数不够。
///
/// 存成一段紧凑 JSON 字符串：日志存储的字段只认字符串/数字/布尔；关键词和工程路径筛选按字段文本查找，
/// 点位全路径原样出现在文本里，右键点位「查看日志」就能命中。格式：
/// <c>{"total":点数,"omitted":省略数,"badOnly":true,"points":[["通道.设备.组.点",值,"质量(非 Good 才有)"],...]}</c>
/// 值按原类型写 JSON 数字/布尔/null，字符串截到 120 字符，数组只记形状和前几个元素。
///
/// 体积有上限：每条最多 <see cref="MaxPoints"/> 点，超出时坏点优先、其余记省略数。
/// 惰性生成：采集循环每批只创建这个小对象(引用本批样本，不复制)，写日志时(ToString)才序列化——
/// 汇总记录只用窗口里最后一批，100ms 周期的其余批次没有序列化开销。序列化出错只丢这个字段，不影响采集。
/// </summary>
internal sealed class TagValuesLogField
{
    /// <summary>采集字段名，经 GatewayTelemetry 规范为 igw_tag_values。</summary>
    public const string FieldName = "tagValues";
    public const int MaxPoints = 200;
    public const int MaxTextLength = 120;
    public const int ArrayPreviewCount = 5;
    private const int MaxElementTextLength = 24;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 中文点位名不转义：存储和关键词预筛都在 UTF-8 原文上找
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IReadOnlyList<TagBinding> _tags;
    private readonly IReadOnlyList<TagSample> _samples;
    private readonly StatusCode _missingStatus;
    private readonly bool _badOnly;
    private string? _json;

    /// <param name="tags">本批请求的点位(决定顺序；没返回样本的点记 <paramref name="missingStatus"/>)。</param>
    /// <param name="samples">本批读到的样本；请求整体失败时为空。</param>
    /// <param name="badOnly">只列质量异常的点(异常批次、质量变坏时只关心哪些点坏了、为什么)。</param>
    public TagValuesLogField(IReadOnlyList<TagBinding> tags, IReadOnlyList<TagSample>? samples,
        StatusCode missingStatus, bool badOnly = false)
    {
        _tags = tags;
        _samples = samples ?? [];
        _missingStatus = missingStatus;
        _badOnly = badOnly;
    }

    /// <summary>日志存储把非基础类型的字段按 ToString 落盘；同一条日志多个输出端只序列化一次。</summary>
    public override string ToString() => _json ??= Render();

    private string Render()
    {
        try
        {
            List<Point> points = Collect();
            if (_badOnly) points.RemoveAll(point => point.Quality.IsGood);
            if (points.Count == 0) return "";
            bool[]? keep = points.Count > MaxPoints ? SelectBadFirst(points) : null;
            var buffer = new ArrayBufferWriter<byte>(Math.Min(points.Count, MaxPoints) * 48 + 64);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteNumber("total", points.Count);
                if (keep is not null) writer.WriteNumber("omitted", points.Count - MaxPoints);
                if (_badOnly) writer.WriteBoolean("badOnly", true);
                writer.WriteStartArray("points");
                for (int i = 0; i < points.Count; i++)
                {
                    if (keep is not null && !keep[i]) continue;
                    Point point = points[i];
                    writer.WriteStartArray();
                    writer.WriteStringValue(point.Id);
                    if (point.Returned) WriteValue(writer, point.Value.Value);
                    else writer.WriteNullValue();
                    if (point.Quality != StatusCode.Good) writer.WriteStringValue(QualityText(point.Quality));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (Exception)
        {
            return "";   // 日志附带信息，出错宁可不记，也不能影响采集
        }
    }

    /// <summary>按请求顺序对上样本(同一点多个样本以最后一个为准)；没返回的点记缺失状态，和读取明细的口径一致。</summary>
    private List<Point> Collect()
    {
        var points = new List<Point>(Math.Max(_tags.Count, _samples.Count));
        if (_tags.Count == 0)
        {
            foreach (TagSample sample in _samples) points.Add(Point.Of(sample.TagId, sample.Value));
            return points;
        }
        var byId = new Dictionary<string, int>(_samples.Count, StringComparer.Ordinal);
        for (int i = 0; i < _samples.Count; i++) byId[_samples[i].TagId] = i;
        foreach (TagBinding tag in _tags)
            points.Add(byId.TryGetValue(tag.TagId, out int at)
                ? Point.Of(tag.TagId, _samples[at].Value)
                : new Point(tag.TagId, default, _missingStatus, Returned: false));
        return points;
    }

    /// <summary>点数超限时先保证坏点都在(排障最需要)，再按原顺序补正常点，输出仍保持原顺序。</summary>
    private static bool[] SelectBadFirst(List<Point> points)
    {
        var keep = new bool[points.Count];
        int kept = 0;
        for (int i = 0; i < points.Count && kept < MaxPoints; i++)
            if (!points[i].Quality.IsGood) { keep[i] = true; kept++; }
        for (int i = 0; i < points.Count && kept < MaxPoints; i++)
            if (!keep[i]) { keep[i] = true; kept++; }
        return keep;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case sbyte or short or int or long: writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case byte or ushort or uint or ulong: writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            // 非规范浮点 JSON 写不了数字，按文本记
            case float single when float.IsFinite(single): writer.WriteNumberValue(single); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case string text: writer.WriteStringValue(Text(text)); break;
            case ArrayRuntimeValue array: writer.WriteStringValue(Text(DescribeArray(array))); break;
            case Array array: writer.WriteStringValue(Text(DescribeArray(array))); break;
            default: writer.WriteStringValue(Text(Scalar(value, MaxTextLength))); break;
        }
    }

    /// <summary>数组只记形状和前几个元素：一个 1000 元素的数组整段落进每条汇总日志没有阅读价值。</summary>
    private static string DescribeArray(ArrayRuntimeValue array)
    {
        string shape = string.Join("×", array.Shape.Dimensions);
        if (array is StructArrayValue) return $"结构数组[{shape}]";
        var text = new StringBuilder($"数组[{shape}]");
        int count = Math.Min(array.Count, ArrayPreviewCount);
        for (int i = 0; i < count; i++)
            text.Append(i == 0 ? ": " : ", ").Append(Scalar(array.GetBoxedValue(i), MaxElementTextLength));
        if (array.Count > count) text.Append(", …");
        return text.ToString();
    }

    private static string DescribeArray(Array array)
    {
        var text = new StringBuilder($"数组[{array.Length}]");
        int count = Math.Min(array.Length, ArrayPreviewCount);
        for (int i = 0; i < count; i++)
            text.Append(i == 0 ? ": " : ", ").Append(Scalar(array.GetValue(i), MaxElementTextLength));
        if (array.Length > count) text.Append(", …");
        return text.ToString();
    }

    private static string Scalar(object? value, int maxLength) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        string text => Truncate(text, maxLength),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IDictionary members => DescribeMembers(members, maxLength),
        _ => Truncate(value.ToString() ?? "", maxLength)
    };

    /// <summary>结构体值记成员摘要 {a=1, b=2, …}，过长截断。</summary>
    private static string DescribeMembers(IDictionary members, int maxLength)
    {
        var text = new StringBuilder("{");
        foreach (DictionaryEntry member in members)
        {
            if (text.Length > 1) text.Append(", ");
            if (text.Length >= maxLength) { text.Append('…'); break; }
            text.Append(member.Key).Append('=').Append(Scalar(member.Value, MaxElementTextLength));
        }
        return Truncate(text.Append('}').ToString(), maxLength);
    }

    /// <summary>PLC 字符串也可能夹带口令一类的文本，和其他日志字段一样脱敏。</summary>
    private static string Text(string text) => GatewayTelemetry.RedactText(Truncate(text, MaxTextLength));

    internal static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        int cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;   // 不劈开代理对
        return string.Concat(text.AsSpan(0, cut), "…");
    }

    /// <summary>已知状态记符号名(BadCommFailure)；未知码带上十六进制，避免只剩一个笼统的 Bad。</summary>
    private static string QualityText(StatusCode quality)
    {
        string name = quality.SymbolicName;
        bool generic = name == quality.Class.ToString()
            && quality != StatusCode.Bad && quality != StatusCode.Uncertain;
        return generic ? $"{name}({quality.Hex})" : name;
    }

    private readonly record struct Point(string Id, DataValue Value, StatusCode Quality, bool Returned)
    {
        /// <summary>数组整体质量为坏(部分元素坏)时按坏点算，和批次坏点数的口径一致。</summary>
        public static Point Of(string id, DataValue value)
        {
            StatusCode quality = value.Quality;
            if (quality.IsGood && value.Kind == ValueKind.Ref && value.Value is ArrayRuntimeValue array && !array.OverallQuality.IsGood)
                quality = array.OverallQuality;
            return new Point(id, value, quality, Returned: true);
        }
    }
}
