using System.Text;
using IGW.Abstractions;
using IGW.Core;
using Microsoft.AspNetCore.Http;

namespace IGW.Northbound.RestApi;

/// <summary>IGW 对外 PLC 接口的名称、Swagger 范围与安全边界。</summary>
public static class PublicPlcApiPolicy
{
    public const string DocumentTitle = "IGW PLC 接口";
    public const string OperationTag = "PLC 操作";
    public const string RealtimeStreamPath = "/api/v1/streams/realtime";
    public const string RealtimeStreamMetricsPath = "/api/v1/streams/realtime/metrics";
    public const string RealtimeStreamMissingPath = "/api/v1/streams/realtime/missing";
    public const string TagValueStreamPath = RealtimeStreamPath;
    /// <summary>QC 刷新：请求体携带点位列表的只读 POST，读分支由接口按请求体逐点检查。</summary>
    public const string PlcReadNowPath = "/api/v1/values/read";
    public const string AuthenticatedUserItemKey = "IGW.PublicPlc.AuthenticatedUser";
    public const string ProjectRevisionHeader = "X-Igw-Project-Revision";

    public static IResult? RejectStaleProjectWrite(HttpRequest request, string currentProjectRevision)
    {
        string expected = request.Headers[ProjectRevisionHeader].FirstOrDefault() ?? "";
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected, currentProjectRevision, StringComparison.Ordinal)
            ? null
            : Results.Json(new
            {
                error = "project_changed",
                projectRevision = currentProjectRevision,
            }, statusCode: StatusCodes.Status409Conflict);
    }

    public static bool IsProtectedPath(string path) =>
        path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase);

    public static bool IsPublicSwaggerPath(string? path) =>
        path?.StartsWith("Tag/", StringComparison.OrdinalIgnoreCase) == true
        || path?.StartsWith("api/v1/tags/", StringComparison.OrdinalIgnoreCase) == true;

    public static bool RequiresWritePermission(string method, string path) =>
        path.Equals("/api/v1/project", StringComparison.OrdinalIgnoreCase)
        || (!IsBodyAuthorizedRead(path)
            && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method)));

    private static bool IsBodyAuthorizedRead(string path) =>
        path.Equals(RealtimeStreamPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(PlcReadNowPath, StringComparison.OrdinalIgnoreCase);

    public static PublicPlcAuthorizationResult Authorize(HttpContext context, SecuritySettings security)
    {
        string path = context.Request.Path.Value ?? "";
        if (!security.Enabled || !IsProtectedPath(path))
            return PublicPlcAuthorizationResult.Allow(null);

        UserDef? authenticatedUser = Authenticate(context.Request, security);
        if (authenticatedUser is null)
            return PublicPlcAuthorizationResult.Deny(
                StatusCodes.Status401Unauthorized,
                "authentication_required");

        var user = new PublicPlcPrincipal(
            authenticatedUser.Username,
            authenticatedUser.CanWrite,
            authenticatedUser.ReadBranches
                .Where(branch => !string.IsNullOrWhiteSpace(branch))
                .Select(branch => branch.Trim())
                .ToArray());

        if (RequiresWritePermission(context.Request.Method, path) && !user.CanWrite)
            return PublicPlcAuthorizationResult.Deny(
                StatusCodes.Status403Forbidden,
                "write_forbidden");

        bool streamBodyAuthorizedByEndpoint = IsBodyAuthorizedRead(path);
        if (!streamBodyAuthorizedByEndpoint
            && !RequiresWritePermission(context.Request.Method, path)
            && !AreRequestReadBranchesAllowed(context.Request, user))
        {
            return PublicPlcAuthorizationResult.Deny(
                StatusCodes.Status403Forbidden,
                "read_branch_forbidden");
        }

        return PublicPlcAuthorizationResult.Allow(user);
    }

    public static bool AreReadBranchesAllowed(PublicPlcPrincipal? user, IEnumerable<string> tagIds)
    {
        if (user?.ReadBranches is not { Count: > 0 }) return true;

        return TagBranchMatcher.AreAllWithin(tagIds, user.ReadBranches);
    }

    private static UserDef? Authenticate(HttpRequest request, SecuritySettings security)
    {
        string? header = request.Headers.Authorization;
        if (string.IsNullOrEmpty(header)
            || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            string raw = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            int separator = raw.IndexOf(':');
            if (separator < 0) return null;
            string username = raw[..separator];
            string password = raw[(separator + 1)..];
            return security.Users.FirstOrDefault(user =>
                user.Username == username && ProjectSecrets.VerifyPassword(user, password));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool AreRequestReadBranchesAllowed(HttpRequest request, PublicPlcPrincipal user)
    {
        if (user.ReadBranches.Count == 0) return true;

        var ids = new List<string>();
        string path = request.Path.Value ?? "";
        const string tagPathPrefix = "/api/v1/tags/";
        if (path.StartsWith(tagPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = path[tagPathPrefix.Length..];
            int slash = rest.IndexOf('/');
            try
            {
                ids.Add(Uri.UnescapeDataString(slash >= 0 ? rest[..slash] : rest));
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        if (request.Query.TryGetValue("ids", out var values))
            ids.AddRange(values.ToString().Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (request.Query.TryGetValue("id", out var value))
            ids.Add(value.ToString());

        return ids.Count == 0 || AreReadBranchesAllowed(user, ids);
    }
}

public sealed record PublicPlcAuthorizationResult(
    PublicPlcPrincipal? User,
    int? StatusCode,
    string? ErrorCode)
{
    public bool IsAllowed => StatusCode is null;

    public static PublicPlcAuthorizationResult Allow(PublicPlcPrincipal? user) => new(user, null, null);

    public static PublicPlcAuthorizationResult Deny(int statusCode, string errorCode) =>
        new(null, statusCode, errorCode);
}

public sealed record PublicPlcPrincipal(
    string Username,
    bool CanWrite,
    IReadOnlyList<string> ReadBranches);
