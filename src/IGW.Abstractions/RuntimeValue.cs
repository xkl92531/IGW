namespace IGW.Abstractions;

/// <summary>
/// 对齐 OPC UA StatusCode:高 2 位表示严重级别(0=Good,1=Uncertain,2/3=Bad)。
/// 直接采用 OPC UA 标准码值,北向 OPC UA Server 可零转换映射。
/// </summary>
public readonly struct StatusCode : IEquatable<StatusCode>
{
    public uint Code { get; }
    public StatusCode(uint code) => Code = code;

    public QualityClass Class => (Code >> 30) switch
    {
        0 => QualityClass.Good,
        1 => QualityClass.Uncertain,
        _ => QualityClass.Bad,
    };

    public bool IsGood => (Code >> 30) == 0;
    public bool IsBad => (Code >> 30) >= 2;

    /// <summary>已知 IGW/OPC UA 状态的稳定符号名；未知码保留质量级别。</summary>
    public string SymbolicName => Code switch
    {
        0x00000000 => nameof(Good),
        0x00B80000 => nameof(GoodUnverified),
        0x40000000 => nameof(Uncertain),
        0x80000000 => nameof(Bad),
        0x808A0000 => nameof(BadNotConnected),
        0x800A0000 => nameof(BadTimeout),
        0x80050000 => nameof(BadCommFailure),
        0x80070000 => nameof(BadDecodeError),
        0x808B0000 => nameof(BadDeviceFailure),
        0x803A0000 => nameof(BadNotReadable),
        0x803B0000 => nameof(BadNotWritable),
        0x801F0000 => "BadUserAccessDenied",
        0x80340000 => "BadNodeIdUnknown",
        0x80350000 => "BadAttributeIdInvalid",
        0x80360000 => "BadIndexRangeInvalid",
        0x80370000 => "BadIndexRangeNoData",
        0x80380000 => "BadDataEncodingInvalid",
        0x80390000 => "BadDataEncodingUnsupported",
        0x803C0000 => "BadOutOfRange",
        0x803D0000 => "BadNotSupported",
        0x80740000 => "BadTypeMismatch",
        0x80B80000 => nameof(BadNotVerified),
        0x80100000 => nameof(BadTooManyOperations),
        0x80840000 => nameof(BadRequestInterrupted),
        0x802C0000 => nameof(BadRequestCancelled),
        0x80B90000 => nameof(BadWriteSuperseded),
        _ => Class.ToString(),
    };

    public string Hex => $"0x{Code:X8}";

    // 常用 OPC UA 标准码
    public static readonly StatusCode Good             = new(0x00000000);
    public static readonly StatusCode GoodUnverified   = new(0x00B80000); // IGW:协议确认成功，未回读 PLC 实际值
    public static readonly StatusCode Uncertain        = new(0x40000000);
    public static readonly StatusCode Bad              = new(0x80000000);
    public static readonly StatusCode BadNotConnected  = new(0x808A0000); // Bad_NotConnected
    public static readonly StatusCode BadTimeout       = new(0x800A0000); // Bad_Timeout
    public static readonly StatusCode BadCommFailure   = new(0x80050000); // Bad_CommunicationError
    public static readonly StatusCode BadDecodeError   = new(0x80070000); // Bad_DecodingError
    public static readonly StatusCode BadDeviceFailure = new(0x808B0000); // Bad_DeviceFailure
    public static readonly StatusCode BadNotReadable   = new(0x803A0000); // Bad_NotReadable
    public static readonly StatusCode BadNodeIdUnknown = new(0x80340000); // Bad_NodeIdUnknown：地址/符号在设备中不存在
    public static readonly StatusCode BadNotWritable   = new(0x803B0000); // Bad_NotWritable
    public static readonly StatusCode BadNotVerified   = new(0x80B80000); // IGW:写入后回读未确认
    public static readonly StatusCode BadTooManyOperations = new(0x80100000); // Bad_TooManyOperations
    public static readonly StatusCode BadRequestInterrupted = new(0x80840000); // Bad_RequestInterrupted
    public static readonly StatusCode BadRequestCancelled  = new(0x802C0000); // Bad_RequestCancelledByClient
    public static readonly StatusCode BadWriteSuperseded   = new(0x80B90000); // IGW:旧写入代次已被新值取代
    public static readonly StatusCode BadWaitingForInitialData = new(0x80320000); // Bad_WaitingForInitialData

    public bool Equals(StatusCode other) => Code == other.Code;
    public override bool Equals(object? obj) => obj is StatusCode s && Equals(s);
    public override int GetHashCode() => (int)Code;
    public override string ToString() => $"{SymbolicName}({Hex})";
    public static bool operator ==(StatusCode a, StatusCode b) => a.Code == b.Code;
    public static bool operator !=(StatusCode a, StatusCode b) => a.Code != b.Code;
}

/// <summary>变体承载的值类别。数值类保留原始 CLR 宽度,<see cref="DataValue.Value"/> 按原类型还原(北向类型不漂移)。</summary>
public enum ValueKind : byte
{
    Empty = 0, Bool, SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double,
    Ref,   // 字符串/数组等引用型:原样持有引用
}

/// <summary>
/// 运行态点位值:值 + 质量 + 时间戳(对齐 OPC UA DataValue)。
/// 变体承载(1M 点热路径):数值内联在 <c>_bits</c>(保留原始宽度),引用型走 <c>_ref</c>——
/// 点表常驻、总线队列、订阅队列全程零装箱;<see cref="Value"/> 仅在边界(REST/OPC UA/历史)按原 CLR 类型现装。
/// </summary>
public readonly struct DataValue
{
    private readonly long _bits;     // Bool:0/1;整型:原值;Float:SingleToInt32Bits;Double:DoubleToInt64Bits
    private readonly object? _ref;   // Ref 专用(字符串/数组);其余为 null
    public ValueKind Kind { get; }
    public StatusCode Quality { get; }
    public DateTime SourceTimestamp { get; }
    public DateTime ServerTimestamp { get; }

    /// <summary>兼容构造:把 object 规范化进变体(驱动产出的短命箱在此拆掉,之后全链零箱)。</summary>
    public DataValue(object? value, StatusCode quality, DateTime sourceTs, DateTime serverTs)
    {
        Quality = quality; SourceTimestamp = sourceTs; ServerTimestamp = serverTs;
        switch (value)
        {
            case null:     Kind = ValueKind.Empty;  _bits = 0; _ref = null; break;
            case bool v:   Kind = ValueKind.Bool;   _bits = v ? 1 : 0; _ref = null; break;
            case sbyte v:  Kind = ValueKind.SByte;  _bits = v; _ref = null; break;
            case byte v:   Kind = ValueKind.Byte;   _bits = v; _ref = null; break;
            case short v:  Kind = ValueKind.Int16;  _bits = v; _ref = null; break;
            case ushort v: Kind = ValueKind.UInt16; _bits = v; _ref = null; break;
            case int v:    Kind = ValueKind.Int32;  _bits = v; _ref = null; break;
            case uint v:   Kind = ValueKind.UInt32; _bits = v; _ref = null; break;
            case long v:   Kind = ValueKind.Int64;  _bits = v; _ref = null; break;
            case ulong v:  Kind = ValueKind.UInt64; _bits = unchecked((long)v); _ref = null; break;
            case float v:  Kind = ValueKind.Float;  _bits = BitConverter.SingleToInt32Bits(v); _ref = null; break;
            case double v: Kind = ValueKind.Double; _bits = BitConverter.DoubleToInt64Bits(v); _ref = null; break;
            default:       Kind = ValueKind.Ref;    _bits = 0; _ref = value; break;
        }
    }

    private DataValue(ValueKind kind, long bits, object? r, StatusCode quality, DateTime sourceTs, DateTime serverTs)
    {
        Kind = kind; _bits = bits; _ref = r;
        Quality = quality; SourceTimestamp = sourceTs; ServerTimestamp = serverTs;
    }

    /// <summary>热路径构造:double 直进变体,零装箱(缩放后的 eng 值)。</summary>
    public static DataValue FromDouble(double v, StatusCode quality, DateTime sourceTs, DateTime serverTs) =>
        new(ValueKind.Double, BitConverter.DoubleToInt64Bits(v), null, quality, sourceTs, serverTs);

    /// <summary>复制并重打 ServerTimestamp(发布路径:同一样本进缓存时统一服务器时戳)。</summary>
    public DataValue WithServer(DateTime serverTs) => new(Kind, _bits, _ref, Quality, SourceTimestamp, serverTs);

    private static readonly object BoxTrue = true, BoxFalse = false;   // bool 共享箱:边界取值也零分配

    /// <summary>边界取值:按原始 CLR 类型还原(数值装箱仅发生在此,REST/OPC UA/历史等边界调用)。</summary>
    public object? Value => Kind switch
    {
        ValueKind.Empty  => null,
        ValueKind.Bool   => _bits != 0 ? BoxTrue : BoxFalse,
        ValueKind.SByte  => (sbyte)_bits,
        ValueKind.Byte   => (byte)_bits,
        ValueKind.Int16  => (short)_bits,
        ValueKind.UInt16 => (ushort)_bits,
        ValueKind.Int32  => (int)_bits,
        ValueKind.UInt32 => (uint)_bits,
        ValueKind.Int64  => _bits,
        ValueKind.UInt64 => unchecked((ulong)_bits),
        ValueKind.Float  => BitConverter.Int32BitsToSingle((int)_bits),
        ValueKind.Double => BitConverter.Int64BitsToDouble(_bits),
        _ => _ref,
    };

    /// <summary>数值(含 bool,含可解析的字符串)→ double,热路径零装箱。语义对齐 ValueOps.TryToDouble。</summary>
    public bool TryGetNumeric(out double d)
    {
        switch (Kind)
        {
            case ValueKind.Bool:   d = _bits != 0 ? 1 : 0; return true;
            case ValueKind.SByte: case ValueKind.Int16: case ValueKind.Int32: case ValueKind.Int64:
                d = _bits; return true;
            case ValueKind.Byte: case ValueKind.UInt16: case ValueKind.UInt32:
                d = _bits; return true;
            case ValueKind.UInt64: d = unchecked((ulong)_bits); return true;
            case ValueKind.Float:  d = BitConverter.Int32BitsToSingle((int)_bits); return true;
            case ValueKind.Double: d = BitConverter.Int64BitsToDouble(_bits); return true;
            case ValueKind.Ref when _ref is string s: return double.TryParse(s, out d);
            default: d = 0; return false;
        }
    }

    /// <summary>值等判断(替代旧 Equals(object,object) 的装箱比较):类别与位形一致才等——(short)5 ≠ (int)5,与装箱语义一致。</summary>
    public bool ValueEquals(in DataValue other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ValueKind.Empty => true,
            ValueKind.Ref when _ref is ArrayRuntimeValue left && other._ref is ArrayRuntimeValue right =>
                left.HasSameContent(right),
            ValueKind.Ref => Equals(_ref, other._ref),
            _ => _bits == other._bits,
        };
    }

    /// <summary>非规范浮点(NaN/±Inf)→ 同类别零(通道「非规范浮点→替换为零」);其余原样。</summary>
    public DataValue NonNormalToZero()
    {
        if (Kind == ValueKind.Double && !double.IsFinite(BitConverter.Int64BitsToDouble(_bits)))
            return new(ValueKind.Double, 0, null, Quality, SourceTimestamp, ServerTimestamp);
        if (Kind == ValueKind.Float && !float.IsFinite(BitConverter.Int32BitsToSingle((int)_bits)))
            return new(ValueKind.Float, 0, null, Quality, SourceTimestamp, ServerTimestamp);
        return this;
    }

    public bool IsGood => Quality.IsGood;

    public static DataValue GoodAt(object? value, DateTime ts) => new(value, StatusCode.Good, ts, ts);
    public static DataValue BadAt(StatusCode code, DateTime ts) => new(null, code, ts, ts);
}
