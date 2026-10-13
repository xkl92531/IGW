#nullable enable
using System.Text;

namespace PlcSimulator.Core;

public enum SimulationTextEncoding { Auto, Utf8, Gb18030 }

internal static class SimulationStringCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Gb;
    static SimulationStringCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public static byte[] Encode(SimulationTag tag, string text)
    {
        var mode = tag.TextEncoding == SimulationTextEncoding.Auto ? tag.DetectedTextEncoding ?? SimulationTextEncoding.Utf8 : tag.TextEncoding;
        try { return (mode == SimulationTextEncoding.Gb18030 ? Gb : Utf8).GetBytes(text); }
        catch (EncoderFallbackException ex) { throw new SimulationValueException($"{tag.SymbolPath} 包含无法编码的字符", ex); }
    }
    public static string Decode(SimulationTag tag, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        if (tag.TextEncoding != SimulationTextEncoding.Auto)
            return (tag.TextEncoding == SimulationTextEncoding.Gb18030 ? Gb : Utf8).GetString(bytes);
        bool ascii = true;
        foreach (byte value in bytes) if (value >= 128) { ascii = false; break; }
        if (ascii) return Encoding.ASCII.GetString(bytes);
        try { string value = Utf8.GetString(bytes); tag.DetectedTextEncoding = SimulationTextEncoding.Utf8; return value; }
        catch (DecoderFallbackException) { string value = Gb.GetString(bytes); tag.DetectedTextEncoding = SimulationTextEncoding.Gb18030; return value; }
    }
}
