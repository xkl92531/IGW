using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;

namespace IGW.Northbound.RestApi;

/// <summary>
/// 普通 JSON 应答与前端静态资源的 gzip 压缩。接近 5 万点的完整工程 JSON 约 27 MB，gzip 后约 1 MB；
/// 浏览器远程访问网关时，不压缩的传输时间会直接超过前端请求超时。
/// SSE、SignalR 等推送流必须逐帧立即送达，不在压缩范围内。
/// </summary>
public static class IgwResponseCompression
{
    internal static readonly string[] CompressedMimeTypes =
    [
        "application/json",
        "application/problem+json",
        "text/csv",
        // 前端静态资源：远程首次打开页面时 element-plus 等脚本约 800 KB → 250 KB。
        "text/html",
        "text/css",
        "text/javascript",
        "application/javascript",
        "image/svg+xml",
    ];

    public static IServiceCollection AddIgwResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            // 网关自身以 HTTP 提供服务；HTTPS 反代场景由反代负责压缩，避免压缩与认证信息混合带来的旁路风险。
            options.EnableForHttps = false;
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = CompressedMimeTypes;
        });
        // 网关 CPU 优先保证采集，压缩取最快档：27 MB 工程仍可压到约 1 MB。
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }

    public static IApplicationBuilder UseIgwResponseCompression(this IApplicationBuilder app) =>
        app.UseResponseCompression();
}
