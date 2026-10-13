using System.Globalization;

namespace IGW.Core;

public sealed record ArrayElementAlias(
    string ParentTagId,
    IReadOnlyList<int> Indices,
    IReadOnlyList<string> MemberPath);

/// <summary>解析 Tag[1,2].Member 或 Tag[1][2].Nested.Member 虚拟路径，不注册额外点位。</summary>
public static class ArrayAliasResolver
{
    public static bool TryParse(string path, out ArrayElementAlias alias)
    {
        alias = null!;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var firstBracket = path.IndexOf('[');
        if (firstBracket <= 0) return false;

        var parent = path[..firstBracket];
        var indices = new List<int>();
        var cursor = firstBracket;
        while (cursor < path.Length && path[cursor] == '[')
        {
            var close = path.IndexOf(']', cursor + 1);
            if (close < 0) return false;
            var content = path.AsSpan(cursor + 1, close - cursor - 1);
            foreach (var item in content.ToString().Split(',', StringSplitOptions.TrimEntries))
            {
                if (item.Length == 0 || !int.TryParse(item, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var index))
                    return false;
                indices.Add(index);
            }
            cursor = close + 1;
        }

        if (indices.Count == 0) return false;
        IReadOnlyList<string> memberPath = Array.Empty<string>();
        if (cursor < path.Length)
        {
            if (path[cursor] != '.') return false;
            var memberText = path[(cursor + 1)..];
            var members = memberText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (members.Length == 0 || members.Any(static member => member.Length == 0)) return false;
            memberPath = members;
        }

        alias = new ArrayElementAlias(parent, indices, memberPath);
        return true;
    }
}
