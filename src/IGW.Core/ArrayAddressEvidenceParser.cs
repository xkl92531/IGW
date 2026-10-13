using System.Globalization;
using System.Text;

namespace IGW.Core;

/// <summary>
/// 从 OPC UA NodeId 或符号地址中提取显式的数组下标。它只相信真实地址里的
/// 方括号证据，不根据工程树显示名称推断数组。
/// </summary>
public static class ArrayAddressEvidenceParser
{
    public static bool TryParse(string? address, out ArrayAddressEvidence evidence)
    {
        evidence = default!;
        if (string.IsNullOrWhiteSpace(address)) return false;

        var pattern = new StringBuilder(address.Length + 16);
        var vectors = new List<int[]>();
        var flattened = new List<int>();
        var segments = new List<ArrayIndexedSegment>();
        int placeholder = 0;
        int firstIndexStart = -1;
        int firstIndexEnd = -1;
        int previousIndexEnd = -2;
        bool quoted = false;

        for (int i = 0; i < address.Length; i++)
        {
            char current = address[i];
            if (current == '"' && !IsEscaped(address, i))
            {
                quoted = !quoted;
                pattern.Append(current);
                continue;
            }

            if (current != '[' || quoted)
            {
                pattern.Append(current);
                continue;
            }

            int close = FindCloseBracket(address, i + 1);
            if (close < 0 || !TryParseVector(address.AsSpan(i + 1, close - i - 1), out var vector))
                return false;

            vectors.Add(vector);
            flattened.AddRange(vector);
            if (i == previousIndexEnd + 1 && segments.Count > 0)
            {
                var prior = segments[^1];
                segments[^1] = prior with { Indices = [.. prior.Indices, .. vector] };
            }
            else
            {
                string segmentName = ExtractIndexedName(address, i);
                if (string.IsNullOrWhiteSpace(segmentName)) return false;
                segments.Add(new ArrayIndexedSegment(segmentName, [.. vector]));
            }
            pattern.Append('[');
            for (int value = 0; value < vector.Length; value++)
            {
                if (value > 0) pattern.Append(',');
                pattern.Append('{').Append(placeholder++).Append('}');
            }
            pattern.Append(']');
            if (firstIndexStart < 0)
            {
                firstIndexStart = i;
                firstIndexEnd = close;
            }
            previousIndexEnd = close;
            i = close;
        }

        if (quoted || vectors.Count == 0) return false;

        evidence = new ArrayAddressEvidence(
            SourceFormat(address),
            address,
            address[..firstIndexStart].Trim(),
            pattern.ToString(),
            vectors,
            flattened,
            segments,
            ExtractMemberPath(address, firstIndexEnd + 1));
        return true;
    }

    private static bool TryParseVector(ReadOnlySpan<char> source, out int[] vector)
    {
        vector = [];
        if (source.IsEmpty) return false;

        var values = new List<int>();
        int start = 0;
        while (start <= source.Length)
        {
            int comma = source[start..].IndexOf(',');
            int length = comma < 0 ? source.Length - start : comma;
            var item = source.Slice(start, length).Trim();
            if (item.IsEmpty || !int.TryParse(item, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int value))
                return false;
            values.Add(value);
            if (comma < 0) break;
            start += comma + 1;
            if (start >= source.Length) return false;
        }

        vector = values.ToArray();
        return vector.Length > 0;
    }

    private static string ExtractMemberPath(string address, int start)
    {
        if (start <= 0 || start >= address.Length) return "";
        var parts = new List<string>();

        for (int i = start; i < address.Length;)
        {
            char current = address[i];
            if (current == '[')
            {
                int close = FindCloseBracket(address, i + 1);
                if (close < 0) break;
                i = close + 1;
                continue;
            }
            if (current is '.' or '/' or '\\' || char.IsWhiteSpace(current))
            {
                i++;
                continue;
            }
            if (current == '"')
            {
                int end = FindQuoteEnd(address, i + 1);
                if (end < 0) break;
                if (end > i + 1) parts.Add(address[(i + 1)..end].Replace("\\\"", "\""));
                i = end + 1;
                continue;
            }

            int tokenStart = i;
            while (i < address.Length && address[i] is not '.' and not '/' and not '\\' and not '['
                   && !char.IsWhiteSpace(address[i]))
                i++;
            if (i > tokenStart) parts.Add(address[tokenStart..i]);
        }

        return string.Join('.', parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string ExtractIndexedName(string address, int bracket)
    {
        int end = bracket - 1;
        while (end >= 0 && char.IsWhiteSpace(address[end])) end--;
        if (end < 0) return "";

        if (address[end] == '"')
        {
            int close = end;
            for (int i = close - 1; i >= 0; i--)
            {
                if (address[i] == '"' && !IsEscaped(address, i))
                    return address[(i + 1)..close].Replace("\\\"", "\"");
            }
            return "";
        }

        int start = end;
        while (start >= 0 && address[start] is not '.' and not '/' and not '\\' and not ';' and not '=')
            start--;
        return address[(start + 1)..(end + 1)].Trim();
    }

    private static int FindCloseBracket(string source, int start)
    {
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == ']') return i;
            if (source[i] == '[' || source[i] == '"') return -1;
        }
        return -1;
    }

    private static int FindQuoteEnd(string source, int start)
    {
        for (int i = start; i < source.Length; i++)
            if (source[i] == '"' && !IsEscaped(source, i)) return i;
        return -1;
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashes = 0;
        for (int i = index - 1; i >= 0 && source[i] == '\\'; i--) slashes++;
        return (slashes & 1) == 1;
    }

    private static string SourceFormat(string address) =>
        address.StartsWith("ns=", StringComparison.OrdinalIgnoreCase) && address.Contains(';')
            ? "OpcUaNodeId"
            : "Symbolic";
}
