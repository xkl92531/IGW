#nullable enable
using System.Text.RegularExpressions;
using PlcSimulator.Core;

namespace PlcSimulator.Import;

public sealed record SiemensProtocolDetection(
    SiemensSimulationProfile Profile,
    string Evidence,
    int ClassicCount,
    int SymbolicCount);

public static partial class SiemensProtocolClassifier
{
    [GeneratedRegex(@"^DB\d+(?:\.|,)(?:DB)?(?:X|B|W|D|BOOL|BYTE|CHAR|WORD|INT|DWORD|DINT|REAL|STRING)", RegexOptions.IgnoreCase)]
    private static partial Regex DbAbsolutePattern();

    [GeneratedRegex(@"^[MIQ](?:[BWD])?\d+(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerAbsolutePattern();

    [GeneratedRegex(@"^DB\d+\.DB(?:X\d+(?:\.[0-7])?|[BWD]\d+)(?:\.String\[\d+\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex MappedDbAbsolutePattern();

    /// <summary>可由 Classic 模拟器映射的完整 DB 地址；不把 DB1.WordCount 等符号名当成地址。</summary>
    public static bool IsMappedClassicDbAddress(string? address) => MappedDbAbsolutePattern().IsMatch(address?.Trim() ?? string.Empty);

    public static bool IsSiemensDriver(string driver)
    {
        string value = driver?.Trim() ?? string.Empty;
        return value.Contains("siemens", StringComparison.OrdinalIgnoreCase)
            || value.Contains("s7", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClassicAddress(string? address)
    {
        string value = address?.Trim() ?? string.Empty;
        return value.Length > 0 && (DbAbsolutePattern().IsMatch(value) || ControllerAbsolutePattern().IsMatch(value));
    }

    public static SiemensProtocolDetection Detect(string driver, IEnumerable<string> addresses)
    {
        string[] values = addresses
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
        int classic = values.Count(IsClassicAddress);
        int symbolic = values.Length - classic;
        bool explicitPlus = driver.Contains("plus", StringComparison.OrdinalIgnoreCase)
            || driver.Contains("optimized", StringComparison.OrdinalIgnoreCase);

        SiemensSimulationProfile profile = classic > 0 && symbolic > 0
            ? SiemensSimulationProfile.Hybrid
            : symbolic > 0
                ? SiemensSimulationProfile.Plus
                : SiemensSimulationProfile.Classic;
        if (values.Length == 0 && explicitPlus) profile = SiemensSimulationProfile.Plus;

        string evidence = profile switch
        {
            SiemensSimulationProfile.Hybrid => $"检测到 {classic} 个绝对地址和 {symbolic} 个符号地址",
            SiemensSimulationProfile.Plus => explicitPlus
                ? $"驱动标识为 S7 Plus；检测到 {symbolic} 个符号地址"
                : $"检测到 {symbolic} 个符号地址",
            _ => $"检测到 {classic} 个绝对地址",
        };
        return new SiemensProtocolDetection(profile, evidence, classic, symbolic);
    }
}
