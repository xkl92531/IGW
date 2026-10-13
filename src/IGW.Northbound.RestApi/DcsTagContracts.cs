using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Northbound.RestApi;

internal enum DcsCommunicationType
{
    None = -1,
    OpcUa = 0,
    S1200 = 1,
    S300 = 2,
    S400 = 3,
    S1500 = 4,
    S200Smart = 5,
    S200 = 6,
    FX5U = 7,
    Inovance_H5U = 8,
    Omr_NJ = 9,
    Omr_Fins = 10,
    MemoryCache = 11,
}

internal enum DcsTagDataType
{
    None = -1,
    String = 0,
    Boolean = 1,
    Char = 2,
    Byte = 3,
    Short = 4,
    Word = 5,
    Long = 6,
    DWord = 7,
    Float = 8,
    Double = 9,
    Date = 12,
    StringArray = 20,
    BooleanArray = 21,
    ByteArray = 23,
    ShortArray = 24,
    WordArray = 25,
    LongArray = 26,
    DWordArray = 27,
    FloatArray = 28,
}

internal sealed record DcsReadTagRequest(string Tag);
internal sealed record DcsTagValue(string Key, JsonElement Value);
internal sealed record DcsKeyValue<T>(string Key, T? Value);

/// <summary>
/// ReadTagsWithQuality 的逐点结果(契约见 docs/dcs-read-quality.md，字段与 Reason 取值保持稳定)。
/// StatusCode 为 OPC UA 状态码数值，Status 为其名称；TimestampUtc 为最后一次真实采集的时间。
/// </summary>
internal sealed record DcsTagQuality(
    string Key,
    object? Value,
    bool IsGood,
    uint StatusCode,
    string Status,
    DateTime? TimestampUtc,
    string? Reason);

/// <summary>Bad/Uncertain 的原因分类，供调用方直接分流。</summary>
internal static class DcsQualityReason
{
    public const string SymbolNotFound = "SymbolNotFound";
    public const string CommFailure = "CommFailure";
    public const string Timeout = "Timeout";
    public const string WaitingForData = "WaitingForData";
    public const string NotRegistered = "NotRegistered";
    public const string AccessDenied = "AccessDenied";
    public const string DeviceError = "DeviceError";
}
internal sealed record DcsWriteTagRequest(DcsTagValue Tag);
internal sealed record DcsWriteTagsRequest(List<DcsTagValue> Tags);

internal sealed record DcsTagUpdateModel
{
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public DcsCommunicationType CommunicationType { get; set; }
    public string DbAddress { get; set; } = "";
    public DcsTagDataType DataType { get; set; }
    public string TagName { get; set; } = "";
    public int[]? Dimensions { get; set; }
    public int[]? LowerBounds { get; set; }
    public int? ElementStrideBytes { get; set; }
    public int? ElementStrideBits { get; set; }
    public int? StringLength { get; set; }
    public StructDef? Struct { get; set; }
    /// <summary>鏍囪缁勮矾寰勶紙缁?瀛愮粍锛屾敮鎸佸祵濂楋紱绌?璁惧鏍瑰眰锛夈€傞潪绌烘椂鎸夎矾寰勬煡鎵?鍒涘缓 TagGroupDef 骞舵妸鐐逛綅鏀惧叆缁勩€?/summary>
    public string Group { get; set; } = "";
}

internal sealed record DcsResponse<T>(
    int HttpStatusCode,
    bool IsSuccess,
    T? BackResult,
    string? ErrorMessage,
    string? ExceptionMessage)
{
    public static DcsResponse<T> Success(T? value, int status = 200) =>
        new(status, true, value, null, null);

    public static DcsResponse<T> Failure(string error, int status = 200, string? exception = null) =>
        new(status, false, default, error, exception);
}

internal static class DcsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal static class DcsTagMetadata
{
    public const string DataType = "dcsDataType";
    public const string LogicalDevice = "dcsLogicalDevice";
    public const string CommunicationType = "dcsCommunicationType";
}
