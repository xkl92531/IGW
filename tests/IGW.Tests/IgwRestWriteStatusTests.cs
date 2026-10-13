using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IGW.Tests;

public sealed class IgwRestWriteStatusTests
{
    [Fact]
    public void Failed_write_payload_preserves_symbolic_and_numeric_status()
    {
        WriteFailureDto payload = IgwRestApi.WriteFailure("Line.Plc.Value", StatusCode.BadNotWritable);

        Assert.Equal("BadNotWritable", payload.Quality);
        Assert.Equal(StatusCode.BadNotWritable.Code, payload.StatusCode);
        Assert.Equal("0x803B0000", payload.StatusHex);
    }

    [Fact]
    public void Known_status_names_are_not_collapsed_to_quality_class()
    {
        Assert.Equal("BadTimeout", StatusCode.BadTimeout.SymbolicName);
        Assert.Equal("BadNotVerified", StatusCode.BadNotVerified.SymbolicName);
    }

    [Fact]
    public void Project_revision_precondition_is_optional_but_rejects_a_stale_UI_write()
    {
        var legacy = new DefaultHttpContext();
        var stale = new DefaultHttpContext();
        stale.Request.Headers[PublicPlcApiPolicy.ProjectRevisionHeader] = "rev-old";
        var current = new DefaultHttpContext();
        current.Request.Headers[PublicPlcApiPolicy.ProjectRevisionHeader] = "rev-new";

        Assert.Null(PublicPlcApiPolicy.RejectStaleProjectWrite(legacy.Request, "rev-new"));
        Assert.NotNull(PublicPlcApiPolicy.RejectStaleProjectWrite(stale.Request, "rev-new"));
        Assert.Null(PublicPlcApiPolicy.RejectStaleProjectWrite(current.Request, "rev-new"));
    }
}
