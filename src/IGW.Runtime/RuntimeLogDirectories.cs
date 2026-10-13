using IGW.Observability;

namespace IGW.Runtime;

/// <summary>运行日志目录规则与 Web 端目录选择器共用 <see cref="GatewayDirectoryBrowser"/>，避免两份实现各自漂移。</summary>
internal static class RuntimeLogDirectories
{
    internal static string Normalize(string path) => GatewayDirectoryBrowser.Normalize(path);
}
