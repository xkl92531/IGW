using IGW.Observability;
using Xunit;

namespace IGW.Tests;

public sealed class ObservabilitySettingsTests
{
    /// <summary>
    /// 改保留天数、改日志目录都会生成新的设置对象；报文容量上限原先没带过去，
    /// 页面上改一次设置就悄悄回到默认 2048 MB，现场手工调小的上限随之失效。
    /// </summary>
    [Fact]
    public void Changing_retention_or_storage_keeps_the_payload_capacity_limit()
    {
        string data = Path.Combine(Path.GetTempPath(), "igw-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ObservabilitySettings { StorageDirectory = Path.Combine(data, "logs"), PayloadMaxMegabytes = 100 };
            Assert.Equal(100, settings.WithRetentionDays(7).PayloadMaxMegabytes);
            Assert.Equal(100, settings.WithStorageDirectory(Path.Combine(data, "other")).PayloadMaxMegabytes);
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
    }

    [Fact]
    public void Storage_preflight_creates_writes_and_checks_the_selected_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "igw-log-preflight", Guid.NewGuid().ToString("N"));
        string? checkedDirectory = null;
        try
        {
            LogStoragePreflight.Validate(root, directory =>
            {
                checkedDirectory = directory;
                return LogStoragePreflight.MinimumFreeBytes;
            });

            Assert.Equal(Path.GetFullPath(root), checkedDirectory);
            Assert.True(Directory.Exists(root));
            Assert.Empty(Directory.EnumerateFiles(root, ".igw-write-test-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Storage_preflight_rejects_less_than_one_GiB_without_leaving_a_probe()
    {
        string root = Path.Combine(Path.GetTempPath(), "igw-log-preflight-low", Guid.NewGuid().ToString("N"));
        try
        {
            IOException error = Assert.Throws<IOException>(() =>
                LogStoragePreflight.Validate(root, _ => LogStoragePreflight.MinimumFreeBytes - 1));

            Assert.Contains("1 GiB", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(root, ".igw-write-test-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectAvailablePortPair_uses_the_preferred_pair_when_both_ports_are_free()
    {
        int selected = ObservabilitySettings.SelectAvailablePortPair(28020, _ => true);

        Assert.Equal(28020, selected);
    }

    [Theory]
    [InlineData(28020)]
    [InlineData(28021)]
    public void SelectAvailablePortPair_skips_a_pair_when_either_port_is_occupied(int occupiedPort)
    {
        int selected = ObservabilitySettings.SelectAvailablePortPair(
            28020,
            candidate => candidate != occupiedPort);

        Assert.Equal(28022, selected);
    }

    [Fact]
    public void Initialize_uses_reserved_IGW_log_ports_by_default()
    {
        string root = NewDirectory("igw-observability-default-port");
        try
        {
            ObservabilitySettings settings = ObservabilitySettings.Initialize(
                root,
                storageDirectory: null,
                port: null,
                isPortAvailable: _ => true);
            IReadOnlyDictionary<string, string> environment = settings.OpenObserveEnvironment();

            Assert.Equal(28020, settings.Port);
            Assert.Equal("28020", environment["ZO_HTTP_PORT"]);
            Assert.Equal("28021", environment["ZO_GRPC_PORT"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Initialize_migrates_the_previous_generated_default_port()
    {
        string root = NewDirectory("igw-observability-default-port-upgrade");
        try
        {
            ObservabilitySettings previous = ObservabilitySettings.Initialize(
                root,
                Path.Combine(root, "data"),
                5088);

            ObservabilitySettings upgraded = ObservabilitySettings.Initialize(
                root,
                storageDirectory: null,
                port: null,
                isPortAvailable: _ => true);

            Assert.Equal(28020, upgraded.Port);
            Assert.Equal(previous.InstanceId, upgraded.InstanceId);
            Assert.Equal(previous.Password, upgraded.Password);
            Assert.Equal(previous.StorageDirectory, upgraded.StorageDirectory);
            Assert.Contains("28020", File.ReadAllText(Path.Combine(root, ObservabilitySettings.FileName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Initialize_persists_unicode_storage_loopback_and_protected_credentials()
    {
        string root = NewDirectory("IGW 日志 空格");
        try
        {
            string storage = Path.Combine(root, "观测 数据");

            ObservabilitySettings created = ObservabilitySettings.Initialize(root, storage, 5088);
            ObservabilitySettings loaded = ObservabilitySettings.Load(root)!;
            IReadOnlyDictionary<string, string> environment = loaded.OpenObserveEnvironment();

            Assert.Equal(created.InstanceId, loaded.InstanceId);
            Assert.Equal(created.Username, loaded.Username);
            Assert.EndsWith("@igw.local", loaded.Username, StringComparison.Ordinal);
            Assert.Equal(created.Password, loaded.Password);
            Assert.Equal(Path.GetFullPath(storage), loaded.StorageDirectory);
            Assert.Equal("127.0.0.1", environment["ZO_HTTP_ADDR"]);
            Assert.Equal("5088", environment["ZO_HTTP_PORT"]);
            Assert.Equal("127.0.0.1", environment["ZO_GRPC_ADDR"]);
            Assert.Equal("5089", environment["ZO_GRPC_PORT"]);
            Assert.Equal(Path.GetFullPath(storage), environment["ZO_DATA_DIR"]);
            Assert.StartsWith(Path.GetFullPath(storage), environment["ZO_WAL_DIR"]);
            Assert.DoesNotContain(loaded.Password, File.ReadAllText(Path.Combine(root, "observability.json")));
            Assert.NotEqual(loaded.Password, loaded.ProtectedPassword);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Initialize_upgrades_legacy_localhost_identity_and_preserves_password()
    {
        string root = NewDirectory("igw-observability-identity-upgrade");
        try
        {
            ObservabilitySettings original = ObservabilitySettings.Initialize(
                root,
                Path.Combine(root, "data"),
                5093);
            string settingsPath = Path.Combine(root, ObservabilitySettings.FileName);
            string legacyUsername = $"igw-{original.InstanceId[..12]}@localhost";
            File.WriteAllText(
                settingsPath,
                File.ReadAllText(settingsPath).Replace(original.Username, legacyUsername, StringComparison.Ordinal));

            ObservabilitySettings upgraded = ObservabilitySettings.Initialize(root);

            Assert.Equal($"igw-{original.InstanceId[..12]}@igw.local", upgraded.Username);
            Assert.Equal(original.Password, upgraded.Password);
            Assert.DoesNotContain(legacyUsername, File.ReadAllText(settingsPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Initialize_is_upgrade_safe_and_preserves_existing_identity_and_location()
    {
        string root = NewDirectory("igw-observability-upgrade");
        try
        {
            string originalStorage = Path.Combine(root, "original");
            ObservabilitySettings first = ObservabilitySettings.Initialize(root, originalStorage, 5091);

            ObservabilitySettings second = ObservabilitySettings.Initialize(
                root,
                Path.Combine(root, "installer-default-changed"),
                5092);

            Assert.Equal(first.InstanceId, second.InstanceId);
            Assert.Equal(first.Username, second.Username);
            Assert.Equal(first.Password, second.Password);
            Assert.Equal(first.StorageDirectory, second.StorageDirectory);
            Assert.Equal(5091, second.Port);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(65536)]
    public void Initialize_rejects_invalid_or_privileged_ports(int port)
    {
        string root = NewDirectory("igw-observability-port");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ObservabilitySettings.Initialize(root, Path.Combine(root, "data"), port));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Initialize_rejects_relative_directories()
    {
        Assert.Throws<ArgumentException>(() => ObservabilitySettings.Initialize("relative", null, 5088));
        string root = NewDirectory("igw-observability-path");
        try
        {
            Assert.Throws<ArgumentException>(() => ObservabilitySettings.Initialize(root, "relative", 5088));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
