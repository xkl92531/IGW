using System.Security.Cryptography;
using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class InstallationSecretProtectorTests
{
    [Fact]
    public void Aes_gcm_envelope_round_trips_and_uses_a_fresh_nonce()
    {
        string dir = NewDirectory();
        try
        {
            var protector = new InstallationSecretProtector(Path.Combine(dir, "igw.masterkey"));

            string first = protector.Protect("factory-secret");
            string second = protector.Protect("factory-secret");

            Assert.StartsWith("enc:v1:", first, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
            Assert.Equal("factory-secret", protector.Unprotect(first));
            Assert.Equal("factory-secret", protector.Unprotect(second));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Wrong_key_and_corrupted_ciphertext_are_rejected()
    {
        string firstDir = NewDirectory();
        string secondDir = NewDirectory();
        try
        {
            var first = new InstallationSecretProtector(Path.Combine(firstDir, "igw.masterkey"));
            var second = new InstallationSecretProtector(Path.Combine(secondDir, "igw.masterkey"));
            string encrypted = first.Protect("factory-secret");
            _ = second.Protect("initialize-different-key");

            Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(encrypted));
            Assert.ThrowsAny<Exception>(() => first.Unprotect(encrypted[..^2] + "AA"));
        }
        finally
        {
            Directory.Delete(firstDir, recursive: true);
            Directory.Delete(secondDir, recursive: true);
        }
    }

    [Fact]
    public void Project_store_encrypts_reversible_secrets_but_load_returns_plain_runtime_values()
    {
        string dir = NewDirectory();
        string path = Path.Combine(dir, "project.igwproj");
        try
        {
            var project = new IgwProject
            {
                MqttPublish = new MqttPublishSettings { Password = "mqtt-secret" },
                MesUpload = new MesUploadSettings { AuthBearerToken = "mes-secret" },
                OpcUa = new OpcUaSettings
                {
                    Users = [new OpcUaUser { Username = "opc", Password = "opc-secret" }],
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
                                Settings = new() { ["accessToken"] = "device-secret" },
                            },
                        ],
                    },
                ],
            };

            ProjectStore.Save(project, path);

            string persisted = File.ReadAllText(path);
            Assert.DoesNotContain("mqtt-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("mes-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("opc-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("device-secret", persisted, StringComparison.Ordinal);
            Assert.Contains("enc:v1:", persisted, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "igw.masterkey")));

            IgwProject loaded = ProjectStore.Load(path);
            Assert.Equal("mqtt-secret", loaded.MqttPublish.Password);
            Assert.Equal("mes-secret", loaded.MesUpload.AuthBearerToken);
            Assert.Equal("opc-secret", loaded.OpcUa.Users[0].Password);
            Assert.Equal("device-secret", loaded.Channels[0].Devices[0].Settings["accessToken"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Legacy_plaintext_project_loads_without_creating_a_master_key()
    {
        string dir = NewDirectory();
        string path = Path.Combine(dir, "legacy.igwproj");
        try
        {
            File.WriteAllText(path, ProjectStore.ToJson(new IgwProject
            {
                MqttPublish = new MqttPublishSettings { Password = "legacy-secret" },
            }));

            IgwProject loaded = ProjectStore.Load(path);

            Assert.Equal("legacy-secret", loaded.MqttPublish.Password);
            Assert.False(File.Exists(Path.Combine(dir, "igw.masterkey")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Backup_failure_does_not_replace_the_live_project_or_leave_temporary_files()
    {
        string dir = NewDirectory();
        string path = Path.Combine(dir, "project.igwproj");
        try
        {
            ProjectStore.Save(new IgwProject { Name = "old" }, path);
            Directory.CreateDirectory(path + ".bak");

            Assert.ThrowsAny<IOException>(() =>
                ProjectStore.Save(new IgwProject { Name = "new" }, path));

            Assert.Equal("old", ProjectStore.Load(path).Name);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".bak.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-secret-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
