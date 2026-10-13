using System.Net.Http.Headers;
using System.Text;
using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IGW.Tests;

public sealed class TagValueStreamAuthorizationTests
{
    [Fact]
    public void Read_only_stream_post_does_not_require_write_permission()
    {
        var context = Request(
            HttpMethods.Post,
            PublicPlcApiPolicy.TagValueStreamPath,
            username: "reader",
            password: "secret");

        var result = PublicPlcApiPolicy.Authorize(context, Security());

        Assert.True(result.IsAllowed);
        Assert.Equal("reader", result.User!.Username);
    }

    [Fact]
    public void Plc_read_now_post_is_a_read_and_does_not_require_write_permission()
    {
        var context = Request(HttpMethods.Post, PublicPlcApiPolicy.PlcReadNowPath, "reader", "secret");

        var result = PublicPlcApiPolicy.Authorize(context, Security());

        Assert.True(result.IsAllowed);
        Assert.Equal("reader", result.User!.Username);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Other_mutating_api_requests_still_require_write_permission(string method)
    {
        var context = Request(method, "/api/v1/project", "reader", "secret");

        var result = PublicPlcApiPolicy.Authorize(context, Security());

        Assert.False(result.IsAllowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("write_forbidden", result.ErrorCode);
    }

    [Fact]
    public void Missing_or_wrong_basic_credentials_are_unauthorized()
    {
        var missing = Request(HttpMethods.Post, PublicPlcApiPolicy.TagValueStreamPath);
        var wrong = Request(HttpMethods.Post, PublicPlcApiPolicy.TagValueStreamPath, "reader", "wrong");

        var missingResult = PublicPlcApiPolicy.Authorize(missing, Security());
        var wrongResult = PublicPlcApiPolicy.Authorize(wrong, Security());

        Assert.Equal(StatusCodes.Status401Unauthorized, missingResult.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, wrongResult.StatusCode);
        Assert.Equal("authentication_required", missingResult.ErrorCode);
        Assert.Equal("authentication_required", wrongResult.ErrorCode);
    }

    [Fact]
    public void Query_tag_reads_still_enforce_all_branches()
    {
        var allowed = Request(HttpMethods.Get, "/api/v1/values?ids=Line.Plc.Tag1,Line.Plc.Tag2", "reader", "secret");
        var denied = Request(HttpMethods.Get, "/api/v1/values?ids=Line.Plc.Tag1,Other.Plc.Tag2", "reader", "secret");

        var allowedResult = PublicPlcApiPolicy.Authorize(allowed, Security());
        var deniedResult = PublicPlcApiPolicy.Authorize(denied, Security());

        Assert.True(allowedResult.IsAllowed);
        Assert.Equal(StatusCodes.Status403Forbidden, deniedResult.StatusCode);
        Assert.Equal("read_branch_forbidden", deniedResult.ErrorCode);
    }

    [Fact]
    public void Stream_body_authorization_requires_every_tag_without_returning_denied_names()
    {
        var authorization = PublicPlcApiPolicy.Authorize(
            Request(HttpMethods.Post, PublicPlcApiPolicy.TagValueStreamPath, "reader", "secret"),
            Security());
        var user = authorization.User!;

        Assert.True(PublicPlcApiPolicy.AreReadBranchesAllowed(
            user,
            ["Line.Plc.Tag1", "line.plc.Group.Tag2"]));
        Assert.False(PublicPlcApiPolicy.AreReadBranchesAllowed(
            user,
            ["Line.Plc.Tag1", "Secret.Plc.Tag2"]));
        Assert.DoesNotContain(
            user.GetType().GetProperties(),
            property => property.Name.Equals("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stream_path_is_not_treated_as_write_but_regular_post_is()
    {
        Assert.False(PublicPlcApiPolicy.RequiresWritePermission(
            HttpMethods.Post,
            PublicPlcApiPolicy.TagValueStreamPath));
        Assert.True(PublicPlcApiPolicy.RequiresWritePermission(
            HttpMethods.Post,
            "/api/v1/project"));
    }

    [Fact]
    public void Disabled_security_and_unprotected_routes_remain_allowed()
    {
        var disabled = Security();
        disabled.Enabled = false;

        Assert.True(PublicPlcApiPolicy.Authorize(
            Request(HttpMethods.Post, "/api/v1/project"),
            disabled).IsAllowed);
        Assert.True(PublicPlcApiPolicy.Authorize(
            Request(HttpMethods.Get, "/swagger"),
            Security()).IsAllowed);
    }

    private static SecuritySettings Security() => new()
    {
        Enabled = true,
        Users =
        [
            new UserDef
            {
                Username = "reader",
                Password = "secret",
                CanWrite = false,
                ReadBranches = ["Line.Plc"],
            },
        ],
    };

    private static DefaultHttpContext Request(
        string method,
        string pathAndQuery,
        string? username = null,
        string? password = null)
    {
        var context = new DefaultHttpContext();
        var uri = new Uri($"http://localhost{pathAndQuery}");
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        if (username is not null)
        {
            string raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            context.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw).ToString();
        }
        return context;
    }
}
