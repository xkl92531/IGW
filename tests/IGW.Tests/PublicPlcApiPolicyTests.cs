using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class PublicPlcApiPolicyTests
{
    [Fact]
    public void Read_branch_does_not_authorize_a_sibling_with_the_same_text_prefix()
    {
        var user = new PublicPlcPrincipal("reader", false, ["Line.Plc.DB10"]);

        bool allowed = PublicPlcApiPolicy.AreReadBranchesAllowed(user, ["Line.Plc.DB100.Value"]);

        Assert.False(allowed);
    }

    [Fact]
    public void Multiple_read_branches_allow_cross_db_selection()
    {
        var user = new PublicPlcPrincipal("reader", false, ["Line.Plc.DB10.", " Line.Plc.DB20 "]);

        bool allowed = PublicPlcApiPolicy.AreReadBranchesAllowed(user,
            ["Line.Plc.DB10.Group.Value", "Line.Plc.DB20.Other.Value"]);

        Assert.True(allowed);
    }

    [Theory]
    [InlineData("/api/v1/health", true)]
    [InlineData("/api/v1/project", true)]
    [InlineData("/Tag/ReadTag1", false)]
    [InlineData("/tag/WriteTag", false)]
    [InlineData("/swagger", false)]
    [InlineData("/", false)]
    public void Protects_internal_api_but_not_public_tag_routes(string path, bool expected)
    {
        Assert.Equal(expected, PublicPlcApiPolicy.IsProtectedPath(path));
    }

    [Theory]
    [InlineData("Tag/ReadTag1", true)]
    [InlineData("tag/WriteTags", true)]
    [InlineData("api/v1/plc/read", false)]
    [InlineData("api/v1/project", false)]
    [InlineData("TagUnexpected/Read", false)]
    [InlineData(null, false)]
    public void Swagger_contains_only_tag_routes(string? path, bool expected)
    {
        Assert.Equal(expected, PublicPlcApiPolicy.IsPublicSwaggerPath(path));
    }
}
