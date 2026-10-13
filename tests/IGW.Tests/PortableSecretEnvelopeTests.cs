using System.Security.Cryptography;
using System.Text;
using IGW.Abstractions;
using IGW.ProjectExchange.Security;
using IGW.ProjectExchange.Igw;
using System.Text.Json;
using Xunit;

namespace IGW.Tests;

public sealed class PortableSecretEnvelopeTests
{
    [Fact]
    public void Correct_password_round_trips_secrets_without_plaintext()
    {
        var secrets = new Dictionary<string, string>
        {
            ["mqttPublish.password"] = "mqtt-plain-marker",
            ["mesUpload.authBearerToken"] = "mes-token-marker",
        };

        byte[] encrypted = PortableSecretEnvelope.Encrypt(secrets, "migration-pass");
        IReadOnlyDictionary<string, string> restored =
            PortableSecretEnvelope.Decrypt(encrypted, "migration-pass");

        Assert.Equal(secrets, restored);
        string envelopeText = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("mqtt-plain-marker", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain("mes-token-marker", envelopeText, StringComparison.Ordinal);
        Assert.Contains("pbkdf2-sha256", envelopeText, StringComparison.Ordinal);
        Assert.Contains("aes-256-gcm", envelopeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_export_uses_unique_salt_and_nonce()
    {
        var secrets = new Dictionary<string, string> { ["secret"] = "value" };

        byte[] first = PortableSecretEnvelope.Encrypt(secrets, "migration-pass");
        byte[] second = PortableSecretEnvelope.Encrypt(secrets, "migration-pass");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Wrong_password_fails_authentication()
    {
        byte[] encrypted = PortableSecretEnvelope.Encrypt(
            new Dictionary<string, string> { ["secret"] = "value" }, "right-password");

        Assert.Throws<CryptographicException>(() =>
            PortableSecretEnvelope.Decrypt(encrypted, "wrong-password"));
    }

    [Fact]
    public void Collector_redacts_and_restores_every_supported_reversible_secret()
    {
        var project = new IgwProject
        {
            MqttPublish = new MqttPublishSettings { Password = "mqtt-plain-marker" },
            MesUpload = new MesUploadSettings { AuthBearerToken = "mes" },
            OpcUa = new OpcUaSettings { Users = [new OpcUaUser { Username = "operator", Password = "opc" }] },
            Channels =
            [
                new ChannelDef
                {
                    Id = "c1", Settings = new() { ["password"] = "channel" },
                    Devices = [new DeviceDef { Id = "d1", Settings = new() { ["apiToken"] = "device" } }],
                },
            ],
            Templates = [new DeviceTemplate { Id = "tpl1", DefaultSettings = new() { ["secret"] = "template" } }],
        };

        IReadOnlyDictionary<string, string> secrets = PortableSecretCollector.CollectAndRedact(project);
        Assert.Equal(6, secrets.Count);
        Assert.DoesNotContain("mqtt-plain-marker", IGW.Core.ProjectStore.ToJson(project), StringComparison.Ordinal);

        PortableSecretCollector.Restore(project, secrets);
        Assert.Equal("mqtt-plain-marker", project.MqttPublish.Password);
        Assert.Equal("mes", project.MesUpload.AuthBearerToken);
        Assert.Equal("opc", project.OpcUa.Users[0].Password);
        Assert.Equal("channel", project.Channels[0].Settings["password"]);
        Assert.Equal("device", project.Channels[0].Devices[0].Settings["apiToken"]);
        Assert.Equal("template", project.Templates[0].DefaultSettings["secret"]);
    }

    [Fact]
    public void Igw_extension_encrypts_and_restores_portable_secrets()
    {
        var project = new IgwProject
        {
            Name = "P",
            MqttPublish = new MqttPublishSettings { Password = "export-secret-marker" },
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new IgwExtensionWriter().Write(writer, project, KepCompatibilityReport.Compatible,
                new ProjectExportOptions(true, "migration-pass"));
        string json = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("export-secret-marker", json, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(json);
        IgwProject restored = new IgwExtensionReader().Merge(
            document.RootElement, new IgwProject { Name = "P" }, "migration-pass");

        Assert.Equal("export-secret-marker", restored.MqttPublish.Password);
    }
}
