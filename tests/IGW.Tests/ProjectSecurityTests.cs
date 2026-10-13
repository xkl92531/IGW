using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectSecurityTests
{
    [Fact]
    public void Api_password_hash_is_versioned_and_verifies_without_retaining_plaintext()
    {
        string hash = ProjectSecrets.HashPassword("Line-Secret-123!");
        var user = new UserDef { Username = "writer", PasswordHash = hash };

        Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("Line-Secret-123!", hash, StringComparison.Ordinal);
        Assert.True(ProjectSecrets.VerifyPassword(user, "Line-Secret-123!"));
        Assert.False(ProjectSecrets.VerifyPassword(user, "wrong"));
    }

    [Fact]
    public void Legacy_plaintext_password_remains_usable_until_a_successful_save_migrates_it()
    {
        var user = new UserDef { Username = "legacy", Password = "old-password" };

        Assert.True(ProjectSecrets.VerifyPassword(user, "old-password"));

        ProjectSecrets.HashPendingApiPasswords(new IgwProject
        {
            Security = new SecuritySettings { Users = [user] },
        });
        Assert.Empty(user.Password);
        Assert.NotEmpty(user.PasswordHash);
        Assert.True(ProjectSecrets.VerifyPassword(user, "old-password"));
    }

    [Fact]
    public void Redacted_project_contains_no_reusable_credentials_or_password_hashes()
    {
        var project = ProjectWithSecrets();

        IgwProject redacted = ProjectConfigurationRedactor.Create(project);
        string json = ProjectStore.ToJson(redacted);

        Assert.DoesNotContain("api-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-password-hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mqtt-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mes-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("opc-server-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("device-access-token", json, StringComparison.Ordinal);
        Assert.Equal(ProjectConfigurationRedactor.UnchangedSecret, redacted.Security.Users[0].Password);
        Assert.Empty(redacted.Security.Users[0].PasswordHash);
    }

    [Fact]
    public void Unchanged_secret_sentinel_restores_live_values_across_all_configuration_sections()
    {
        var live = ProjectWithSecrets();
        IgwProject edited = ProjectConfigurationRedactor.Create(live);
        edited.Name = "edited";

        ProjectConfigurationRedactor.MergeUnchangedSecrets(live, edited);

        Assert.Equal("api-password", edited.Security.Users[0].Password);
        Assert.Equal("stored-password-hash", edited.Security.Users[0].PasswordHash);
        Assert.Equal("mqtt-password", edited.MqttPublish.Password);
        Assert.Equal("mes-token", edited.MesUpload.AuthBearerToken);
        Assert.Equal("opc-server-password", edited.OpcUa.Users[0].Password);
        Assert.Equal("device-access-token", edited.Channels[0].Devices[0].Settings["accessToken"]);
    }

    [Fact]
    public void Project_get_requires_writer_permission_when_security_is_enabled()
    {
        Assert.True(PublicPlcApiPolicy.RequiresWritePermission("GET", "/api/v1/project"));
        Assert.False(PublicPlcApiPolicy.RequiresWritePermission("GET", "/api/v1/project/tag-scope"));
    }

    private static IgwProject ProjectWithSecrets() => new()
    {
        Security = new SecuritySettings
        {
            Enabled = true,
            Users =
            [
                new UserDef
                {
                    Username = "writer",
                    Password = "api-password",
                    PasswordHash = "stored-password-hash",
                },
            ],
        },
        MqttPublish = new MqttPublishSettings { Password = "mqtt-password" },
        MesUpload = new MesUploadSettings { AuthBearerToken = "mes-token" },
        OpcUa = new OpcUaSettings
        {
            Users = [new OpcUaUser { Username = "opc", Password = "opc-server-password" }],
        },
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Name = "Plc",
                        Settings = new() { ["accessToken"] = "device-access-token" },
                    },
                ],
            },
        ],
    };
}
