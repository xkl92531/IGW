namespace IGW.Northbound.RestApi;

/// <summary>
/// 按完整点位路径段判断读取范围。分支可以跨任意 PLC/DB 配置，
/// 但 DB10 只匹配自身和 DB10.*，不会误匹配 DB100。
/// </summary>
public static class TagBranchMatcher
{
    public static bool IsWithin(string? tagId, string? branch)
    {
        string tag = Normalize(tagId);
        string root = Normalize(branch);
        if (tag.Length == 0 || root.Length == 0) return false;

        return string.Equals(tag, root, StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AreAllWithin(IEnumerable<string> tagIds, IEnumerable<string> branches)
    {
        string[] roots = branches
            .Select(Normalize)
            .Where(static branch => branch.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return false;

        return tagIds.All(tagId => roots.Any(branch => IsWithin(tagId, branch)));
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.');
}
