using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaCapabilityAndTrustTests
{
    [Fact]
    public async Task Learned_batch_windows_are_restored_for_the_same_server()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-capability-tests", Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "opcua-capabilities.json");
        try
        {
            var cache = new OpcUaCapabilityCache(file);
            await cache.SaveAsync("server-a", 4096, 2048, CancellationToken.None);

            var restored = await new OpcUaCapabilityCache(file).LoadAsync("server-a", CancellationToken.None);

            Assert.NotNull(restored);
            Assert.Equal(4096, restored!.Read);
            Assert.Equal(2048, restored.Write);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Trust_on_first_use_accepts_only_the_first_untrusted_thumbprint()
    {
        Assert.True(OpcUaCertificatePolicy.ShouldAccept(
            OpcUaCertificateTrustMode.TrustOnFirstUse,
            StatusCodes.BadCertificateUntrusted,
            "AA", null, out bool remember));
        Assert.True(remember);

        Assert.False(OpcUaCertificatePolicy.ShouldAccept(
            OpcUaCertificateTrustMode.TrustOnFirstUse,
            StatusCodes.BadCertificateUntrusted,
            "BB", "AA", out remember));
        Assert.False(remember);
    }

    [Fact]
    public void Strict_mode_never_bypasses_certificate_validation()
    {
        Assert.False(OpcUaCertificatePolicy.ShouldAccept(
            OpcUaCertificateTrustMode.Strict,
            StatusCodes.BadCertificateUntrusted,
            "AA", null, out _));
    }
}
