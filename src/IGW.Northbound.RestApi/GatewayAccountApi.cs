using System.Collections.Concurrent;
using IGW.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IGW.Northbound.RestApi;

/// <summary>登录、改密码，以及管理员管理用户和页面权限的接口。</summary>
public static class GatewayAccountApi
{
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record PasswordRequest(string? OldPassword, string? NewPassword);
    public sealed record CreateUserRequest(string? Username, string? DisplayName, string? Password, Dictionary<string, string>? Permissions);
    public sealed record UpdateUserRequest(string? DisplayName, Dictionary<string, string>? Permissions, bool? Disabled, string? Password);

    // 同一用户名连续输错 5 次，锁 1 分钟(防止在现场网络里被逐个猜密码)
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapGatewayAccountApi(
        this IEndpointRouteBuilder app, GatewayAccountStore accounts, GatewaySessionTokens tokens)
    {
        var failures = new ConcurrentDictionary<string, (int Count, DateTime LockedUntilUtc)>(StringComparer.OrdinalIgnoreCase);

        app.MapPost("/api/v1/auth/login", (LoginRequest request) =>
        {
            string username = (request.Username ?? "").Trim();
            if (failures.TryGetValue(username, out var state) && state.LockedUntilUtc > DateTime.UtcNow)
                return Error(429, "login_locked", $"密码错误次数过多，请 {Math.Ceiling((state.LockedUntilUtc - DateTime.UtcNow).TotalSeconds)} 秒后再试");
            GatewayAccount? account = accounts.Authenticate(username, request.Password ?? "");
            if (account is null)
            {
                var next = failures.AddOrUpdate(username, _ => (1, DateTime.MinValue), (_, old) => (old.Count + 1, old.LockedUntilUtc));
                if (next.Count >= MaxFailures) failures[username] = (0, DateTime.UtcNow + LockTime);
                return Error(401, "invalid_credentials", "用户名或密码不正确，或账号已停用");
            }
            failures.TryRemove(username, out _);
            return Results.Ok(new { token = tokens.Issue(account), account = Describe(account) });
        });

        app.MapGet("/api/v1/auth/me", (HttpContext context) => Results.Ok(Describe(Current(context))));

        app.MapPost("/api/v1/auth/password", (HttpContext context, PasswordRequest request) =>
        {
            try
            {
                GatewayAccount account = accounts.ChangePassword(Current(context).Username,
                    request.OldPassword ?? "", request.NewPassword ?? "");
                // 改密码会让旧凭证失效：直接换发新凭证，当前页面不用重新登录
                return Results.Ok(new { token = tokens.Issue(account), account = Describe(account) });
            }
            catch (GatewayAccountException ex) { return Error(400, "invalid_request", ex.Message); }
        });

        app.MapGet("/api/v1/users", () => Results.Ok(new
        {
            modules = GatewayModules.All.Select(module => new { id = module.Id, name = module.Name }),
            users = accounts.List().Select(Describe),
        }));

        app.MapPost("/api/v1/users", (CreateUserRequest request) =>
        {
            try
            {
                GatewayAccount account = accounts.Create(request.Username ?? "", request.DisplayName ?? "",
                    request.Password ?? "", ParsePermissions(request.Permissions) ?? new Dictionary<string, AccessLevel>());
                return Results.Ok(Describe(account));
            }
            catch (GatewayAccountException ex) { return Error(400, "invalid_request", ex.Message); }
        });

        app.MapPut("/api/v1/users/{username}", (string username, UpdateUserRequest request) =>
        {
            try
            {
                GatewayAccount account = accounts.Update(username, request.DisplayName,
                    ParsePermissions(request.Permissions), request.Disabled, request.Password);
                return Results.Ok(Describe(account));
            }
            catch (GatewayAccountException ex) { return Error(400, "invalid_request", ex.Message); }
        });

        app.MapDelete("/api/v1/users/{username}", (string username) =>
        {
            try
            {
                accounts.Delete(username);
                return Results.Ok(new { deleted = username });
            }
            catch (GatewayAccountException ex) { return Error(400, "invalid_request", ex.Message); }
        });

        return app;
    }

    private static GatewayAccount Current(HttpContext context) =>
        context.Items[GatewayAccessPolicy.AccountItemKey] as GatewayAccount
        ?? throw new InvalidOperationException("接口权限检查没有放入当前账号。");

    // 给网页的账号信息：不含密码哈希；每个页面都列出级别，管理员全部为 Edit
    private static object Describe(GatewayAccount account) => new
    {
        username = account.Username,
        displayName = account.DisplayName,
        isAdmin = account.IsAdmin,
        mustChangePassword = account.MustChangePassword,
        disabled = account.Disabled,
        createdUtc = account.CreatedUtc,
        lastLoginUtc = account.LastLoginUtc,
        permissions = GatewayModules.All.ToDictionary(module => module.Id, module => account.LevelOf(module.Id).ToString()),
    };

    private static Dictionary<string, AccessLevel>? ParsePermissions(Dictionary<string, string>? permissions)
    {
        if (permissions is null) return null;
        var result = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach ((string module, string level) in permissions)
            if (Enum.TryParse(level, ignoreCase: true, out AccessLevel parsed)) result[module] = parsed;
        return result;
    }

    private static IResult Error(int status, string error, string message) =>
        Results.Json(new { error, message }, statusCode: status);
}
