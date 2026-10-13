using System.Collections;
using System.Globalization;

namespace IGW.Northbound.RestApi;

/// <summary>JSON numbers outside JavaScript's exact integer range are decimal strings in monitor APIs.</summary>
internal static class BrowserValueJson
{
    public static object? Normalize(object? value) => value switch
    {
        long number when number is < -9_007_199_254_740_991 or > 9_007_199_254_740_991 => number.ToString(CultureInfo.InvariantCulture),
        ulong number when number > 9_007_199_254_740_991 => number.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => bytes,
        Array array => array.Cast<object?>().Select(Normalize).ToArray(),
        IDictionary dictionary => dictionary.Keys.Cast<object>().ToDictionary(key => key.ToString()!, key => Normalize(dictionary[key])),
        _ => value,
    };

    public static object?[] ArrayValues(IGW.Abstractions.ArrayRuntimeValue value)
        => IGW.Core.ArrayValueCodec.ToWireValues(value).Select(Normalize).ToArray();
}
