using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 数组写后回读差异计算。差异按最后一维的连续区间压缩，避免一个坏元素导致整个大数组重写。
/// </summary>
internal static class ArrayWriteVerification
{
    public static IReadOnlyList<ArrayWriteRequest> BuildRetryRequests(
        ArrayWriteRequest original,
        ArrayRuntimeValue actualSlice)
    {
        var expected = original.Value;
        if (!SameDimensions(expected.Shape, actualSlice.Shape) || expected.ElementType != actualSlice.ElementType)
            return [original];

        var mismatches = new List<int>();
        for (var offset = 0; offset < expected.Count; offset++)
        {
            if (!ElementMatches(expected, offset, actualSlice, offset))
                mismatches.Add(offset);
        }
        if (mismatches.Count == 0) return [];

        var retries = new List<ArrayWriteRequest>();
        var expectedShape = expected.Shape;
        var targetShape = original.Slice.ValidateAndCreateShape(original.Binding.Def.Array!.Shape);
        for (var cursor = 0; cursor < mismatches.Count;)
        {
            int firstOffset = mismatches[cursor];
            int lastOffset = firstOffset;
            var firstIndices = expectedShape.GetIndices(firstOffset);
            var prefix = firstIndices.AsSpan(0, firstIndices.Length - 1).ToArray();
            cursor++;
            while (cursor < mismatches.Count)
            {
                int nextOffset = mismatches[cursor];
                if (nextOffset != lastOffset + 1) break;
                var nextIndices = expectedShape.GetIndices(nextOffset);
                if (!nextIndices.AsSpan(0, nextIndices.Length - 1).SequenceEqual(prefix)) break;
                lastOffset = nextOffset;
                cursor++;
            }

            var expectedStart = expectedShape.GetIndices(firstOffset);
            var targetStart = targetShape.GetIndices(firstOffset);
            var lengths = Enumerable.Repeat(1, expectedShape.Rank).ToArray();
            lengths[^1] = lastOffset - firstOffset + 1;
            var value = expected.Slice(new ArraySlice(expectedStart, lengths));
            retries.Add(new ArrayWriteRequest(original.Binding, new ArraySlice(targetStart, lengths), value));
        }
        return retries;
    }

    private static bool ElementMatches(
        ArrayRuntimeValue expected,
        int expectedOffset,
        ArrayRuntimeValue actual,
        int actualOffset)
    {
        if (!actual.ElementQuality.Span[actualOffset].IsGood || expected.ElementType != actual.ElementType)
            return false;

        if (expected is StructArrayValue expectedStruct)
        {
            if (actual is not StructArrayValue actualStruct) return false;
            foreach (var expectedField in expectedStruct.Schema.Fields)
            {
                var actualField = actualStruct.Schema.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, expectedField.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(expectedField.JsonName) &&
                     string.Equals(candidate.JsonName, expectedField.JsonName, StringComparison.OrdinalIgnoreCase)));
                if (actualField is null ||
                    !expectedStruct.Members.TryGetValue(expectedField.Name, out var expectedMember) ||
                    !actualStruct.Members.TryGetValue(actualField.Name, out var actualMember))
                    return false;

                if (expectedField.Array is null)
                {
                    if (!ElementMatches(expectedMember, expectedOffset, actualMember, actualOffset)) return false;
                }
                else
                {
                    var expectedNested = expectedStruct.GetArrayMemberSlice(expectedField, expectedOffset);
                    var actualNested = actualStruct.GetArrayMemberSlice(actualField, actualOffset);
                    if (!expectedNested.HasSameContent(actualNested)) return false;
                }
            }
            return true;
        }

        object? left = expected.GetBoxedValue(expectedOffset);
        object? right = actual.GetBoxedValue(actualOffset);
        return expected.ElementType switch
        {
            IgwDataType.Float => left is float a && right is float b &&
                                 BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b),
            IgwDataType.Double => left is double a && right is double b &&
                                  BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            _ => Equals(left, right),
        };
    }

    private static bool SameDimensions(ArrayShape left, ArrayShape right)
    {
        if (left.Rank != right.Rank) return false;
        for (var i = 0; i < left.Rank; i++)
            if (left.Dimensions[i] != right.Dimensions[i]) return false;
        return true;
    }
}
