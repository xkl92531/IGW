namespace IGW.Abstractions;

/// <summary>网关支持的点位数据类型(协议无关)。</summary>
public enum IgwDataType
{
    Bool, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String, ByteArray,
    WString,// 西门子 WString(UTF-16,2字节/字符)
    Char,   // 有符号 8 位(KEP「字符」)
    Bcd,    // 16 位 BCD(4 位十进制,0–9999)
    LBcd,   // 32 位 BCD(8 位十进制)
    Struct, // 结构体；成员布局由 StructDef 描述
    // Append only: persisted projects may use the numeric enum values.
    Dtl = 17, Date, Tod, Ltod, Time, Ltime, Dt, Ldt, S5Time,
    S7Char, WChar, Word, DWord, LWord, Timer, Counter, SInt, DateTime,
}

/// <summary>PLC 普通字符串的字节编码。null/Auto 表示运行时自动识别。</summary>
public enum PlcTextEncoding { Auto, Utf8, Gb18030 }

/// <summary>点位读写权限。</summary>
public enum TagAccess { Read, Write, ReadWrite }

/// <summary>死区类型。</summary>
public enum DeadbandType { None, Absolute, PercentOfRange }

/// <summary>通道/设备连接状态。</summary>
public enum ConnectionState { Disconnected, Connecting, Connected, Faulted }

/// <summary>质量大类(由 OPC UA StatusCode 高 2 位推导)。</summary>
public enum QualityClass : byte { Bad = 0, Uncertain = 1, Good = 2 }
