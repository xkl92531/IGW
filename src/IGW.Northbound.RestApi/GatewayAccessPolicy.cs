using System.Text.Json;
using IGW.Core.Security;
using Microsoft.AspNetCore.Http;

namespace IGW.Northbound.RestApi;

/// <summary>接口需要的身份：公开 / 登录即可 / 管理员 / 某个页面的查看或修改权限。</summary>
public enum AccessKind { Public, Session, Admin, Module }

public sealed record AccessRule(AccessKind Kind, string? Module = null, bool Write = false, string? Feature = null)
{
    public static readonly AccessRule Public = new(AccessKind.Public);
    public static readonly AccessRule Session = new(AccessKind.Session);
    public static readonly AccessRule Admin = new(AccessKind.Admin);
    /// <summary>当前账号自己的接口(查看账号、改密码)：还在用初始密码时也能调用，否则改不了密码。</summary>
    public static readonly AccessRule OwnAccount = new(AccessKind.Session) { AllowsInitialPassword = true };
    /// <summary>写 PLC 点位值：第三方系统不用登录(按工程里的接口账号判断)；带了网页登录凭证就要有工程配置的修改权限。</summary>
    public static readonly AccessRule PlcWrite = new(AccessKind.Public, GatewayModules.Project, Write: true);
    /// <summary>
    /// 批量点位注册(POST /api/v1/project/bulk/tags)：HSJM「PLC 程序上传后自动注册 IGW」这类第三方系统调，不登录网页，
    /// 和 /Tag/AddTags 一样按工程里的接口账号鉴权(开启时要有写权限)；带了网页登录凭证就要有工程配置的修改权限。
    /// </summary>
    public static readonly AccessRule BulkTagRegistration = new(AccessKind.Public, GatewayModules.Project, Write: true);

    public bool AllowsInitialPassword { get; init; }
    /// <summary>不带网页登录凭证也能调用的读取(GET 管理接口)；带了凭证的仍按页面权限判断。</summary>
    public bool AnonymousRead { get; init; }
}

/// <summary>
/// 网页账号的接口权限：
/// 第三方系统用的数据接口(读写点位、实时订阅、DCS 兼容接口、实时通道)不需要登录；管理接口的读取(GET)也不需要登录，
/// 改配置、会连设备的浏览/预览、账号与用户管理、带密钥的导出仍要登录；
/// 但网页登录用户写点位值要有工程配置的修改权限(网页上「只能看」的账号不能借数据接口写 PLC)；
/// 其余管理接口必须登录，并按账号在对应页面上的「只能看 / 可修改」判断；账号和授权码激活只有管理员能改。
/// </summary>
public static class GatewayAccessPolicy
{
    public const string AccountItemKey = "IGW.Gateway.Account";
    public const string MutationsPath = "/api/v1/project/mutations";

    // 名字是 POST 但只读数据的接口(预览、浏览、导出、查询)：按「查看」权限判断
    private static readonly HashSet<string> ReadOnlyPosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/project/resolve-paths",
        "/api/v1/project/export",
        "/api/v1/project/mes-payload-preview",
        "/api/v1/project/mes-field-mapping",
        "/api/v1/opcua/browse-branch",
        "/api/v1/opcua/browse-children",
        "/api/v1/opcua/browse-preview",
        "/api/v1/siemens/db-import/preview",
        "/api/v1/validation/jobs",
        "/api/v1/scan-timings/tags",
    };

    // 登录后谁都能看的运行状态(监控、页头、授权状态等都要用)
    private static readonly string[] StatusReads =
    [
        "/api/v1/mes", "/api/v1/mes/log", "/api/v1/mes/timeline",
        "/api/v1/anomalies", "/api/v1/connection-diagnostics", "/api/v1/tag-faults",
        "/api/v1/database/status", "/api/v1/drivers", "/api/v1/network-adapters", "/api/v1/scheduler",
        "/api/v1/project/summary", "/api/v1/project/settings", "/api/v1/project/tag-scope",
    ];

    // 读取但仍要登录：工程/模板导出可带密钥，目录浏览能看网关服务器的文件夹，日志清理的 scan 会在后台扫各个硬盘
    private static readonly string[] LoginRequiredReads =
    [
        "/api/v1/project/export", "/api/v1/runtime-logs/directories", "/api/v1/log-center/cleanup",
    ];

    public static AccessRule Classify(string path, string method, string? mutationKind = null)
    {
        AccessRule rule = ClassifyCore(path, method, mutationKind);
        path = (path ?? "").TrimEnd('/');
        // 管理接口的读取不需要登录：第三方系统直接读工程、状态、日志等。只限 GET——只读的 POST(浏览、预览)会连设备，仍要登录。
        // 账号相关(查看自己、界面语言)不放开：网页靠它们的 401 判断没登录。
        bool get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        bool exportsTemplate = path.EndsWith("/export", StringComparison.OrdinalIgnoreCase)
            && (Under(path, "/api/v1/templates") || Under(path, "/api/v1/mes/flow-templates"));
        return get && !rule.Write && rule.Kind is AccessKind.Session or AccessKind.Module && !rule.AllowsInitialPassword
            && !exportsTemplate && !LoginRequiredReads.Any(item => Is(path, item))
            ? rule with { AnonymousRead = true }
            : rule;
    }

    private static AccessRule ClassifyCore(string path, string method, string? mutationKind)
    {
        path = (path ?? "").TrimEnd('/');
        bool read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

        // 网页本身、DCS 兼容接口(/Tag、/iotgateway)、接口文档、实时通道(/hubs) 都不在 /api 下
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return AccessRule.Public;
        if (Is(path, "/api/v1/auth/login") || Is(path, "/api/v1/health")) return AccessRule.Public;
        if (IsDataInterface(path, read)) return !read && Under(path, "/api/v1/tags") ? AccessRule.PlcWrite : AccessRule.Public;
        if (Is(path, "/api/v1/project/bulk/tags")) return AccessRule.BulkTagRegistration;

        if (Under(path, "/api/v1/auth")) return AccessRule.OwnAccount;
        if (Under(path, "/api/v1/users")) return AccessRule.Admin;
        if (Is(path, "/api/v1/license")) return read ? AccessRule.Session : AccessRule.Admin;
        // 界面语言：登录页(含改初始密码)也要用
        if (Under(path, "/api/v1/ui/preferences")) return AccessRule.OwnAccount;
        if (read && StatusReads.Any(status => Is(path, status))) return AccessRule.Session;

        if (Is(path, MutationsPath))
            return mutationKind switch
            {
                "ReplaceMesTask" or "SaveMesTasks" => new(AccessKind.Module, GatewayModules.Flow, true, "Mes"),
                "ReplaceProjectSections" => new(AccessKind.Module, GatewayModules.Settings, true),
                _ => new(AccessKind.Module, GatewayModules.Project, true),
            };

        bool write = !read && !ReadOnlyPosts.Contains(path);
        if (Under(path, "/api/v1/mes") || Is(path, "/api/v1/project/mes-upload")
            || path.StartsWith("/api/v1/project/mes-", StringComparison.OrdinalIgnoreCase)
            || Is(path, "/api/v1/project/parameter-options"))
            return new(AccessKind.Module, GatewayModules.Flow, write, "Mes");
        if (Under(path, "/api/v1/log-center") || Under(path, "/api/v1/runtime-logs"))
            return new(AccessKind.Module, GatewayModules.Logs, write);
        if (Under(path, "/api/v1/scan-timings") || Under(path, "/api/v1/diagnostics"))
            return new(AccessKind.Module, GatewayModules.Performance, write);
        if (Under(path, "/api/v1/channels"))
            return new(AccessKind.Module, GatewayModules.Monitor, write);
        if (Under(path, "/api/v1/project") || Under(path, "/api/v1/templates") || Under(path, "/api/v1/validation")
            || Under(path, "/api/v1/devices") || Under(path, "/api/v1/opcua") || Under(path, "/api/v1/siemens"))
            return new(AccessKind.Module, GatewayModules.Project,
                write && !(Under(path, "/api/v1/devices") && path.EndsWith("/browse", StringComparison.OrdinalIgnoreCase)));

        // 没列出的接口：看需要登录，改只允许管理员(新接口忘了归类时宁可收紧)
        return read ? AccessRule.Session : AccessRule.Admin;
    }

    /// <summary>检查账号能否调用；返回 null 表示放行，否则返回 HTTP 状态码和错误码。</summary>
    public static (int Status, string Error, string Message)? Check(
        AccessRule rule, GatewayAccount? account, Func<string, bool> featureLicensed)
    {
        if (rule.Kind == AccessKind.Public)
            return rule.Write && account is not null && account.LevelOf(rule.Module!) < AccessLevel.Edit
                ? (403, "edit_forbidden", "没有修改权限")
                : null;
        if (account is null)
            return !rule.AnonymousRead ? (401, "login_required", "请先登录")
                : rule.Feature is { } licensed && !featureLicensed(licensed) ? (403, "feature_unlicensed", "授权码不包含这个功能")
                : null;
        if (account.MustChangePassword && !rule.AllowsInitialPassword) return (403, "password_change_required", "请先修改初始密码");
        if (rule.Feature is { } feature && !featureLicensed(feature))
            return (403, "feature_unlicensed", "授权码不包含这个功能");
        switch (rule.Kind)
        {
            case AccessKind.Admin when !account.IsAdmin:
                return (403, "admin_required", "只有管理员可以操作");
            case AccessKind.Module:
                AccessLevel level = account.LevelOf(rule.Module!);
                if (level == AccessLevel.None) return (403, "module_forbidden", "没有这个页面的权限");
                if (rule.Write && level < AccessLevel.Edit) return (403, "edit_forbidden", "没有修改权限");
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 登录凭证：请求头 Authorization: Bearer。浏览器直接下载(链接、表单提交)带不了请求头，
    /// 所以 GET 和导出工程的表单提交也可以用 ?access_token=。
    /// </summary>
    public static string? ReadToken(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        bool download = HttpMethods.IsGet(request.Method)
            || (HttpMethods.IsPost(request.Method) && Is((request.Path.Value ?? "").TrimEnd('/'), "/api/v1/project/export"));
        return download && request.Query.TryGetValue("access_token", out var token) ? token.ToString() : null;
    }

    /// <summary>工程修改接口按修改类型归到不同页面：只看请求体顶层的 kind，不整体解析(批量点位的请求体可能很大)。</summary>
    public static async Task<string?> PeekMutationKindAsync(HttpRequest request)
    {
        request.EnableBuffering();
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer).ConfigureAwait(false);
            return FindTopLevelKind(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        finally { request.Body.Position = 0; }
    }

    internal static string? FindTopLevelKind(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isKind = reader.ValueTextEquals("kind") || reader.ValueTextEquals("Kind");
            if (!reader.Read()) return null;
            if (isKind)
                return reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number when reader.TryGetInt32(out int value)
                        && Enum.IsDefined(typeof(IGW.Abstractions.ProjectMutationKind), value)
                        => ((IGW.Abstractions.ProjectMutationKind)value).ToString(),
                    _ => null,
                };
            reader.Skip();
        }
        return null;
    }

    // 第三方数据接口：读写点位、批量读值、实时订阅、历史/报警查询、设备在线状态
    private static bool IsDataInterface(string path, bool read) =>
        (read && Is(path, "/api/v1/tags"))
        || Under(path, "/api/v1/tags")
        || (read && Is(path, "/api/v1/values"))
        || Is(path, "/api/v1/values/read")
        || Under(path, "/api/v1/streams")
        || (read && Under(path, "/api/v1/history"))
        || (read && Under(path, "/api/v1/alarms"))
        || (read && Is(path, "/api/v1/devices"));

    private static bool Is(string path, string target) => path.Equals(target, StringComparison.OrdinalIgnoreCase);

    private static bool Under(string path, string prefix) =>
        Is(path, prefix) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
