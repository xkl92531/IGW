using IGW.Licensing;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace IGW.Tests;

public sealed class LicenseIssueScopeTests
{
    [Fact]
    public void New_code_records_explicit_driver_and_feature_grants()
    {
        var payload = new LicensePayload();

        LicenseIssueScope.Apply(payload, ["Siemens.S7", "Modbus.Tcp"], [LicenseCatalog.Mes]);

        Assert.Equal(2, payload.Version);
        Assert.Equal(["Siemens.S7", "Modbus.Tcp"], payload.Drivers);
        Assert.Equal([LicenseCatalog.Mes], payload.Features);
    }

    [Fact]
    public void Empty_or_unknown_driver_and_unknown_feature_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => LicenseIssueScope.Apply(new LicensePayload(), [], []));
        Assert.Throws<ArgumentException>(() => LicenseIssueScope.Apply(new LicensePayload(), ["Unknown"], []));
        Assert.Throws<ArgumentException>(() => LicenseIssueScope.Apply(new LicensePayload(), ["Siemens.S7"], ["Unknown"]));
    }

    [Fact]
    public void Signed_code_preserves_selected_grants_after_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = new LicensePayload
        {
            Customer = "Factory A",
            Machine = "ABCD1234EFAB5678",
            IssuedUtc = DateTime.UtcNow,
            ExpiryUtc = DateTime.UtcNow.AddYears(1),
        };
        LicenseIssueScope.Apply(payload, ["Siemens.S7"], [LicenseCatalog.Mes]);

        string code = LicenseCode.Sign(payload, key);
        string[] parts = code.Split('.');
        byte[] body = B64Url.Decode(parts[1]);
        Assert.True(key.VerifyData(body, B64Url.Decode(parts[2]), HashAlgorithmName.SHA256));
        LicensePayload? decoded = JsonSerializer.Deserialize<LicensePayload>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(["Siemens.S7"], decoded!.Drivers);
        Assert.Equal([LicenseCatalog.Mes], decoded.Features);
    }
}
