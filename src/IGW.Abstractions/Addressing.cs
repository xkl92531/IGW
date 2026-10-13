namespace IGW.Abstractions;

/// <summary>协议无关的解析后地址:区 + 偏移(协议单位)+ 位偏移 + 长度(单位)。</summary>
public readonly struct TagAddress
{
    /// <summary>协议定义的区域码(如 Modbus 线圈/离散/保持/输入寄存器)。</summary>
    public int Area { get; }
    /// <summary>元素偏移(如寄存器号、字节偏移)。</summary>
    public int Offset { get; }
    /// <summary>位访问时的位号;非位类型为 -1。</summary>
    public int BitOffset { get; }
    /// <summary>占用长度(协议单位,如寄存器数 / 字节数)。</summary>
    public int Length { get; }

    public TagAddress(int area, int offset, int length, int bitOffset = -1)
    {
        Area = area;
        Offset = offset;
        Length = length;
        BitOffset = bitOffset;
    }

    public bool IsBit => BitOffset >= 0;
    /// <summary>结束偏移(不含)。</summary>
    public int End => Offset + Length;
}

/// <summary>把驱动相关的地址串("40001"/"DB1.DBW0"/"D100")解析为 <see cref="TagAddress"/>。</summary>
public interface IAddressParser
{
    bool TryParse(string address, IgwDataType type, out TagAddress result, out string? error);
}

/// <summary>需要点位完整定义来计算地址跨度或协议元数据的地址解析器。</summary>
public interface ITagDefinitionAddressParser : IAddressParser
{
    bool TryParse(TagDef definition, out TagAddress result, out string? error);
}
