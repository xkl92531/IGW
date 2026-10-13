using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace IGW.Northbound.RestApi;

internal static class IgwRealtimeAuthorization
{
    public static PublicPlcPrincipal? Authenticate(string? auth, SecuritySettings security)
    {
        if (auth?.Length > 16_384) throw Error(401, "authentication_required");
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/values";
        context.Request.Method = HttpMethods.Get;
        if (!string.IsNullOrEmpty(auth)) context.Request.Headers.Authorization = "Basic " + auth;
        var result = PublicPlcApiPolicy.Authorize(context, security);
        if (!result.IsAllowed) throw Error(result.StatusCode!.Value, result.ErrorCode!);
        return result.User;
    }

    public static HubException Error(int status, string code, byte[]? payload = null) =>
        new($"igw:{status}:{code}" + (payload is { Length: > 0 } ? ":" + Convert.ToBase64String(payload) : ""));

    public static (int Status, string Code) Describe(HubException error)
    {
        string[] parts = error.Message.Split(':', 4);
        return parts.Length >= 3
            && parts[0].Equals("igw", StringComparison.Ordinal)
            && int.TryParse(parts[1], out int status)
            ? (status, parts[2])
            : (500, "signalr_failure");
    }
}

/// <summary>
/// Bounded in-process adapter to the existing handlers. There is no loopback HTTP request:
/// existing minimal-API binding, PLC normalization, write verification and JSON status DTOs
/// run once, using a fresh invocation scope. Only the named runtime operations below are reachable.
/// </summary>
internal sealed class IgwRealtimeRequestDispatcher(IGatewayHost host, IEndpointRouteBuilder endpoints)
{
    internal const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, RequestDelegate> _handlers = new(StringComparer.Ordinal);
    private readonly object _handlerGate = new();

    public async Task<JsonElement> InvokeAsync(string auth, string operation, JsonElement args, string revision,
        Func<GatewayAccount?> webAccount, CancellationToken ct)
    {
        try
        {
            var principal = IgwRealtimeAuthorization.Authenticate(auth, host.Project.Security);
            if (args.ValueKind != JsonValueKind.Object) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
            var spec = Describe(operation, args);
            if (spec.Write && principal is { CanWrite: false }) throw IgwRealtimeAuthorization.Error(403, "write_forbidden");
            if (spec.Write && GatewayAccessPolicy.Check(AccessRule.PlcWrite, webAccount(), _ => true) is { } denied)
                throw IgwRealtimeAuthorization.Error(denied.Status, denied.Error);
            if (!PublicPlcApiPolicy.AreReadBranchesAllowed(principal, spec.TagIds)
                || (spec.Unrestricted && principal?.ReadBranches is { Count: > 0 }))
                throw IgwRealtimeAuthorization.Error(403, "read_branch_forbidden");
            if (spec.Write && !string.IsNullOrEmpty(revision) && !string.Equals(revision, host.ProjectRevision, StringComparison.Ordinal))
                throw IgwRealtimeAuthorization.Error(409, "project_changed", JsonSerializer.SerializeToUtf8Bytes(new { error = "project_changed", projectRevision = host.ProjectRevision }));

            await using var scope = endpoints.ServiceProvider.CreateAsyncScope();
            await using var body = new MemoryStream(spec.Body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(spec.Body, TagValueStreamJson.Options));
            await using var response = new BoundedResponseStream(MaxResponseBytes);
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = ct };
            context.Request.Method = spec.Method;
            context.Request.Path = spec.Path;
            context.Request.QueryString = QueryString.Create(spec.Query);
            context.Request.RouteValues = spec.Id is null ? [] : new RouteValueDictionary { ["id"] = spec.Id };
            context.Request.Headers[PublicPlcApiPolicy.ProjectRevisionHeader] = revision;
            if (principal is not null) context.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] = principal;
            context.Request.Body = body;
            context.Request.ContentLength = body.Length;
            if (spec.Body is not null)
            {
                context.Request.ContentType = "application/json";
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
            }
            context.Response.Body = response;
            await Handler(spec.Method, spec.Pattern)(context);
            await context.Response.BodyWriter.FlushAsync(ct);
            byte[] bytes = response.ToArray();
            if (context.Response.StatusCode >= 400)
            {
                string code = "request_failed";
                try
                {
                    using var error = JsonDocument.Parse(bytes);
                    if (error.RootElement.TryGetProperty("error", out var property) && property.ValueKind == JsonValueKind.String)
                    {
                        string text = property.GetString()!;
                        if (text.Length is > 0 and <= 128 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) code = text;
                    }
                }
                catch (JsonException) { }
                // Error DTOs are bounded separately because SignalR completion messages carry strings.
                throw IgwRealtimeAuthorization.Error(context.Response.StatusCode, code, bytes.Length <= 128 * 1024 ? bytes : null);
            }
            if (bytes.Length == 0) return JsonSerializer.SerializeToElement<object?>(null);
            using var result = JsonDocument.Parse(bytes);
            if (operation == "tags" && principal?.ReadBranches is { Count: > 0 })
                return JsonSerializer.SerializeToElement(result.RootElement.EnumerateArray().Where(id =>
                    PublicPlcApiPolicy.AreReadBranchesAllowed(principal, [id.GetString()!])).ToArray());
            return result.RootElement.Clone();
        }
        catch (HubException) { throw; }
        catch (ResponseTooLargeException) { throw IgwRealtimeAuthorization.Error(413, "response_too_large"); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException or OverflowException or BadHttpRequestException or KeyNotFoundException)
        {
            throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
        }
    }

    private RequestDelegate Handler(string method, string pattern)
    {
        string key = method + " " + pattern;
        lock (_handlerGate)
        {
            if (_handlers.TryGetValue(key, out var cached)) return cached;
            var endpoint = endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .SingleOrDefault(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == pattern.TrimEnd('/')
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
            return _handlers[key] = endpoint?.RequestDelegate ?? throw IgwRealtimeAuthorization.Error(503, "operation_unavailable");
        }
    }

    private static RequestSpec Describe(string operation, JsonElement args)
    {
        string Text(string name, bool required = false)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (required) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
                return "";
            }
            if (value.ValueKind != JsonValueKind.String) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
            string result = value.GetString()!;
            if (result.Length > 4096 || required && string.IsNullOrWhiteSpace(result)) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
            return result;
        }
        string[] Ids(string name)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 10_000)
                throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
            return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 512 } id && !id.Contains(',')
                ? id : throw IgwRealtimeAuthorization.Error(400, "invalid_arguments")).ToArray();
        }
        string Number(string name, int defaultValue) => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Vector(string name)
        {
            var value = args.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
            return string.Join(',', value.EnumerateArray().Select(item => item.GetInt32()));
        }
        RequestSpec Read(string path, bool unrestricted = true, Dictionary<string, string?>? query = null) => new("GET", path, path, null, null, query ?? [], [], false, unrestricted);
        switch (operation)
        {
            case "health": return Read("/api/v1/health");
            case "devices": return Read("/api/v1/devices");
            case "mes": return Read("/api/v1/mes");
            case "mesLog": return Read("/api/v1/mes/log", query: new() { ["limit"] = Number("limit", 80) });
            case "mesTimeline": return Read("/api/v1/mes/timeline", query: new() { ["task"] = Text("task") });
            case "anomalies": return Read("/api/v1/anomalies", query: new() { ["q"] = Text("q") });
            case "connectionDiagnostics": return Read("/api/v1/connection-diagnostics");
            case "tagFaults": return Read("/api/v1/tag-faults");
            case "tags": return Read("/api/v1/tags", false);
            case "scanTimings": return Read("/api/v1/scan-timings", query: new() { ["device"] = Text("device"), ["tag"] = Text("tag"), ["limit"] = Number("limit", 100) });
            case "tagScanTimings":
                var tagIds = Ids("tagIds");
                return new("POST", "/api/v1/scan-timings/tags", "/api/v1/scan-timings/tags", null, new { tagIds }, [], tagIds, false, false);
            case "readValues":
                var readIds = Ids("ids");
                return new("POST", "/api/v1/values/read", "/api/v1/values/read", null, new { ids = readIds }, [], readIds, false, false);
            case "values":
                var ids = Ids("ids");
                return Read("/api/v1/values", false, new() { ["ids"] = string.Join(',', ids) }) with { TagIds = ids };
            case "write":
            case "arrayInfo":
            case "arraySlice":
            case "writeArraySlice":
                string id = Text("id", true);
                if (id.Length > 512) throw IgwRealtimeAuthorization.Error(400, "invalid_arguments");
                string suffix = operation == "write" ? "/value" : operation == "arrayInfo" ? "/array" : "/array/slice";
                string pattern = "/api/v1/tags/{id}" + suffix;
                string path = "/api/v1/tags/" + Uri.EscapeDataString(id) + suffix;
                bool write = operation is "write" or "writeArraySlice";
                object? body = operation == "write" ? new { value = args.GetProperty("value") }
                    : operation == "writeArraySlice" ? new { start = args.GetProperty("start"), length = args.GetProperty("length"), values = args.GetProperty("values") } : null;
                Dictionary<string, string?> query = operation == "arraySlice" ? new() { ["start"] = Vector("start"), ["length"] = Vector("length") } : [];
                return new(operation == "write" ? "POST" : operation == "writeArraySlice" ? "PUT" : "GET", pattern, path, id, body, query, [id], write, false);
            default: throw IgwRealtimeAuthorization.Error(400, "unknown_operation");
        }
    }

    private sealed record RequestSpec(string Method, string Pattern, string Path, string? Id, object? Body,
        Dictionary<string, string?> Query, string[] TagIds, bool Write, bool Unrestricted);
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class ResponseTooLargeException : Exception;
    private sealed class BoundedResponseStream(int maxBytes) : MemoryStream
    {
        private void Check(int count) { if (count > maxBytes - Position) throw new ResponseTooLargeException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
    }
}
