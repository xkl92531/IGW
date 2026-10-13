using System.Collections.Concurrent;
using System.Text;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>
/// PLC 普通 STRING 编解码。自动模式先严格校验 UTF-8，失败后回退 GB18030；
/// 非 ASCII 识别结果按点位缓存，保证连续扫描和回写使用同一种编码。
/// </summary>
internal sealed class PlcTextCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Gb18030;
    private static readonly Encoding StrictGb18030;
    private readonly ConcurrentDictionary<string, PlcTextEncoding> _detected = new(StringComparer.Ordinal);

    static PlcTextCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding(54936);
        StrictGb18030 = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public string Decode(string key, ReadOnlySpan<byte> bytes, PlcTextEncoding? configured)
    {
        var mode = configured.GetValueOrDefault(PlcTextEncoding.Auto);
        if (mode != PlcTextEncoding.Auto)
            return EncodingFor(mode, strict: false).GetString(bytes);
        if (bytes.IsEmpty) return string.Empty;
        if (IsAscii(bytes)) return Encoding.ASCII.GetString(bytes);

        if (_detected.TryGetValue(key, out var cached))
        {
            if (TryDecode(bytes, cached, out var cachedText)) return cachedText;
            _detected.TryRemove(key, out _);
        }

        if (TryDecode(bytes, PlcTextEncoding.Utf8, out var utf8))
        {
            _detected[key] = PlcTextEncoding.Utf8;
            return utf8;
        }
        if (TryDecode(bytes, PlcTextEncoding.Gb18030, out var gb18030))
        {
            _detected[key] = PlcTextEncoding.Gb18030;
            return gb18030;
        }

        // 两种严格解码均失败时保留可见替代符，避免异常拖垮扫描线程。
        return Utf8.GetString(bytes);
    }

    public byte[] Encode(string key, string text, PlcTextEncoding? configured, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(text)) return [];
        var mode = configured.GetValueOrDefault(PlcTextEncoding.Auto);
        if (mode == PlcTextEncoding.Auto)
            mode = _detected.TryGetValue(key, out var cached) ? cached : PlcTextEncoding.Utf8;

        var encoding = EncodingFor(mode, strict: false);
        var buffer = new byte[maxBytes];
        encoding.GetEncoder().Convert(text.AsSpan(), buffer.AsSpan(), true,
            out _, out int bytesUsed, out _);
        return buffer.AsSpan(0, bytesUsed).ToArray();
    }

    public PlcTextEncoding ResolvedEncoding(string key, PlcTextEncoding? configured)
    {
        var mode = configured.GetValueOrDefault(PlcTextEncoding.Auto);
        return mode == PlcTextEncoding.Auto && _detected.TryGetValue(key, out var cached)
            ? cached
            : mode == PlcTextEncoding.Auto ? PlcTextEncoding.Utf8 : mode;
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, PlcTextEncoding mode, out string value)
    {
        try
        {
            value = EncodingFor(mode, strict: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static Encoding EncodingFor(PlcTextEncoding mode, bool strict) => mode switch
    {
        PlcTextEncoding.Gb18030 => strict ? StrictGb18030 : Gb18030,
        _ => strict ? StrictUtf8 : Utf8,
    };

    private static bool IsAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value >= 0x80) return false;
        return true;
    }
}
