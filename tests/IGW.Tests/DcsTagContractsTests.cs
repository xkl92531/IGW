using System.Text.Json;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class DcsTagContractsTests
{
    [Fact]
    public void Communication_type_numbers_match_dcs()
    {
        Assert.Equal(0, (int)DcsCommunicationType.OpcUa);
        Assert.Equal(4, (int)DcsCommunicationType.S1500);
        Assert.Equal(8, (int)DcsCommunicationType.Inovance_H5U);
        Assert.Equal(11, (int)DcsCommunicationType.MemoryCache);
    }

    [Fact]
    public void Data_type_numbers_match_dcs()
    {
        Assert.Equal(-1, (int)DcsTagDataType.None);
        Assert.Equal(0, (int)DcsTagDataType.String);
        Assert.Equal(12, (int)DcsTagDataType.Date);
        Assert.Equal(20, (int)DcsTagDataType.StringArray);
        Assert.Equal(28, (int)DcsTagDataType.FloatArray);
    }

    [Fact]
    public void Response_envelope_is_camel_case_and_complete()
    {
        string json = JsonSerializer.Serialize(
            DcsResponse<bool>.Success(true, 200), DcsJson.Options);

        Assert.Equal(
            "{\"httpStatusCode\":200,\"isSuccess\":true,\"backResult\":true,\"errorMessage\":null,\"exceptionMessage\":null}",
            json);
    }

    [Fact]
    public void Write_request_accepts_original_dcs_shape()
    {
        var model = JsonSerializer.Deserialize<DcsWriteTagRequest>(
            "{\"tag\":{\"key\":\"通用.S1500_10.0.0.1.DB1.Value\",\"value\":\"12\"}}",
            DcsJson.Options);

        Assert.NotNull(model);
        Assert.Equal("通用.S1500_10.0.0.1.DB1.Value", model.Tag.Key);
        Assert.Equal("12", model.Tag.Value.GetString());
    }
}
