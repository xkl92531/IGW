using System.Text;
using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.Mes;

internal static class MesArrayConditionEvaluator
{
    public static bool TrySelect(IDataHub hub, MesCondition condition, out ArrayRuntimeValue selected)
    {
        selected = null!;
        if (string.IsNullOrWhiteSpace(condition.TagId)) return false;

        try
        {
            if (hub.TryGet(condition.TagId, out var direct) && direct.Value is ArrayRuntimeValue directArray)
            {
                var slice = new ArraySlice(
                    condition.ArrayStart is { Length: > 0 } ? condition.ArrayStart : directArray.Shape.LowerBounds,
                    condition.ArrayLengths is { Length: > 0 } ? condition.ArrayLengths : directArray.Shape.Dimensions);
                selected = MesArrayProjection.Select(directArray, slice, Split(condition.ArrayMemberPath));
                return true;
            }

            if (!ArrayAliasResolver.TryParse(condition.TagId, out var alias) ||
                !hub.TryGet(alias.ParentTagId, out var parent) || parent.Value is not ArrayRuntimeValue parentArray)
                return false;
            var memberPath = alias.MemberPath.Concat(Split(condition.ArrayMemberPath)).ToArray();
            var aliasSlice = new ArraySlice(alias.Indices, Enumerable.Repeat(1, alias.Indices.Count).ToArray());
            selected = MesArrayProjection.Select(parentArray, aliasSlice, memberPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return false;
        }
    }

    public static bool Evaluate(ArrayRuntimeValue selected, string? quantifier, Func<object?, bool> predicate)
    {
        bool all = string.Equals(quantifier, "All", StringComparison.OrdinalIgnoreCase);
        if (selected.Count == 0) return false;
        for (var i = 0; i < selected.Count; i++)
        {
            bool match = !selected.ElementQuality.Span[i].IsBad && predicate(selected.GetBoxedValue(i));
            if (all && !match) return false;
            if (!all && match) return true;
        }
        return all;
    }

    public static string Fingerprint(ArrayRuntimeValue selected)
    {
        var text = new StringBuilder();
        text.Append((int)selected.ElementType).Append('|');
        for (var i = 0; i < selected.Shape.Rank; i++)
            text.Append(selected.Shape.LowerBounds[i]).Append(':').Append(selected.Shape.Dimensions[i]).Append(';');
        text.Append('|');
        foreach (var hash in selected.SegmentHashes.Span) text.Append(hash.ToString("X16")).Append(',');
        return text.ToString();
    }

    public static string Describe(ArrayRuntimeValue selected) =>
        $"数组 {selected.Count} 项(Good {selected.GoodCount}/Uncertain {selected.UncertainCount}/Bad {selected.BadCount})";

    private static IReadOnlyList<string> Split(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
