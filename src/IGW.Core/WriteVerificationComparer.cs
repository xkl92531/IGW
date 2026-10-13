using IGW.Abstractions;

namespace IGW.Core;

/// <summary>写后回读比较统一走 PLC 声明类型，避免 JSON Double、浮点位宽和字符串截断造成误判。</summary>
public static class WriteVerificationComparer
{
    public static bool IsMatch(TagDef definition, object expected, DataValue actual, out string reason)
    {
        if (!actual.Quality.IsGood)
        {
            reason = $"回读质量为 {actual.Quality}";
            return false;
        }
        if (actual.Value is null)
        {
            reason = "回读值为空";
            return false;
        }

        try
        {
            object normalizedExpected = PlcWriteValueNormalizer.Normalize(definition, expected);
            object normalizedActual = PlcWriteValueNormalizer.Normalize(definition, actual.Value);
            bool matched = definition.DataType switch
            {
                IgwDataType.Float => BitConverter.SingleToInt32Bits((float)normalizedExpected) ==
                                     BitConverter.SingleToInt32Bits((float)normalizedActual),
                IgwDataType.Double => BitConverter.DoubleToInt64Bits((double)normalizedExpected) ==
                                      BitConverter.DoubleToInt64Bits((double)normalizedActual),
                _ => Equals(normalizedExpected, normalizedActual),
            };
            reason = matched ? "" : $"期望 {normalizedExpected}，实际 {normalizedActual}";
            return matched;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            reason = $"回读类型无法按 {definition.DataType} 比较：{ex.Message}";
            return false;
        }
    }
}
