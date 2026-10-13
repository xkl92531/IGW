namespace IGW.Abstractions;

/// <summary>Configured classic PLC storage span, including S7 string headers and array layout.</summary>
public static class ProtocolStorageSize
{
    public static int Estimate(TagDef definition, TagAddress address)
    {
        if (definition.Array is { } array)
        {
            long count = array.Shape.ElementCount;
            if (array.ElementStrideBits > 0)
                return checked((int)((Math.Max(0, address.BitOffset) + count * array.ElementStrideBits + 7) / 8));
            int stride = array.ElementStrideBytes > 0 ? array.ElementStrideBytes :
                array.ElementType == IgwDataType.Struct ? array.Struct?.SizeBytes ?? 0 : 0;
            // An unconfirmed layout must not be inferred from the logical element type.
            return stride > 0 ? checked((int)(count * stride)) : Math.Max(1, address.Length);
        }

        return definition.DataType switch
        {
            IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char or IgwDataType.SInt or IgwDataType.S7Char => 1,
            IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd or IgwDataType.Word or
                IgwDataType.WChar or IgwDataType.Date or IgwDataType.S5Time or IgwDataType.Timer or IgwDataType.Counter => 2,
            IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd or
                IgwDataType.DWord or IgwDataType.Tod or IgwDataType.Time => 4,
            IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double or IgwDataType.LWord or
                IgwDataType.Ltod or IgwDataType.Ltime or IgwDataType.Dt or IgwDataType.Ldt or IgwDataType.DateTime => 8,
            IgwDataType.Dtl => 12,
            IgwDataType.String => checked(Capacity(definition) + 2),
            IgwDataType.WString => checked(Capacity(definition) * 2 + 4),
            IgwDataType.ByteArray => Math.Max(1, definition.StringLength ?? address.Length),
            _ => Math.Max(1, address.Length),
        };
    }

    private static int Capacity(TagDef definition) => definition.StringLength is < 0
        ? throw new FormatException("字符串容量不能为负数。") : definition.StringLength ?? 254;
}
