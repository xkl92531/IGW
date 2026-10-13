using System.Collections.Concurrent;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Drivers.IoTClient;

namespace IGW.Runtime;

public sealed record SiemensAuthStatus(string Mode, bool HasCredential, string State, string Message);

public sealed class SiemensAuthenticationService
{
    private readonly Func<IgwProject> _project;
    private readonly Func<IgwProject, Task> _reload;
    private readonly IDeviceCredentialStore _credentials;
    private readonly Func<ISiemensPlusTransport> _transportFactory;
    private readonly ConcurrentDictionary<string, FailureWindow> _failures = new(StringComparer.OrdinalIgnoreCase);

    public SiemensAuthenticationService(
        Func<IgwProject> project,
        Func<IgwProject, Task> reload,
        IDeviceCredentialStore credentials,
        Func<ISiemensPlusTransport>? transportFactory = null)
    {
        _project = project;
        _reload = reload;
        _credentials = credentials;
        _transportFactory = transportFactory ?? (() => new SiemensPlusTransport());
    }

    public async Task<SiemensAuthStatus> GetStatusAsync(string deviceName, CancellationToken ct = default)
    {
        var (_, device) = FindDevice(_project(), deviceName);
        var mode = device.Settings.GetValueOrDefault("authMode", "anonymous");
        if (!string.Equals(mode, "credential", StringComparison.OrdinalIgnoreCase))
            return new("anonymous", false, "Anonymous", "默认匿名连接");
        var reference = device.Settings.GetValueOrDefault("credentialRef", "");
        if (string.IsNullOrWhiteSpace(reference))
            return new("credential", false, "NeedsReentry", "凭据引用缺失，请重新验证");
        try
        {
            var credential = await _credentials.GetAsync(reference, ct);
            return credential is null
                ? new("credential", false, "NeedsReentry", "当前机器没有该凭据，请重新验证")
                : new("credential", true, "Stored", "凭据已在本机加密保存");
        }
        catch (DeviceCredentialStoreException)
        {
            return new("credential", false, "NeedsReentry", "凭据无法在当前机器解密，请重新验证");
        }
    }

    public async Task<SiemensAuthStatus> VerifyAndSaveAsync(
        string deviceName,
        string username,
        string password,
        CancellationToken ct = default)
    {
        username ??= string.Empty;
        password ??= string.Empty;
        if (username.Length > 256 || password.Length is 0 or > 1024)
            throw new ArgumentException("用户名或密码长度无效。");
        EnforceRateLimit(deviceName);

        var project = _project();
        var (channel, device) = FindDevice(project, deviceName);
        if (!string.Equals(channel.DriverId, IoTSiemensDriverFactory.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该设备不是 Siemens.S7 设备。");
        var model = device.Settings.GetValueOrDefault("model", "S7-1200").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (model is not ("S7-1200" or "S7-1500"))
            throw new NotSupportedException("S7-200/300/400 使用 Classic 通信，不支持 S7 Plus 身份验证。");
        var (host, port) = ParseEndpoint(device.Endpoint);
        var timeout = Math.Clamp(ParseInt(device.Settings.GetValueOrDefault("timeoutMs"), ParseInt(channel.Settings.GetValueOrDefault("timeoutMs"), 10_000)), 1_000, 120_000);

        try
        {
            await using var transport = _transportFactory();
            await transport.ConnectAsync(host, port, TimeSpan.FromMilliseconds(timeout), legacy: false, ct);
            await transport.AuthenticateAsync(username, password, ct);
            await transport.ProbeAsync(ct);
        }
        catch
        {
            RegisterFailure(deviceName);
            throw;
        }

        var oldReference = device.Settings.GetValueOrDefault("credentialRef", "");
        var newReference = await _credentials.SaveAsync(new DeviceCredential(username, password), ct: ct);
        try
        {
            var clone = Clone(project);
            var (_, clonedDevice) = FindDevice(clone, deviceName);
            clonedDevice.Settings["authMode"] = "credential";
            clonedDevice.Settings["credentialRef"] = newReference;
            await _reload(clone);
        }
        catch
        {
            await _credentials.DeleteAsync(newReference, CancellationToken.None);
            throw;
        }
        _failures.TryRemove(deviceName, out _);
        if (!string.IsNullOrWhiteSpace(oldReference) && !string.Equals(oldReference, newReference, StringComparison.Ordinal))
            await _credentials.DeleteAsync(oldReference, CancellationToken.None);
        return new("credential", true, "Verified", "身份验证成功，凭据已在本机加密保存");
    }

    public async Task DeleteAsync(string deviceName, CancellationToken ct = default)
    {
        var project = _project();
        var (_, device) = FindDevice(project, deviceName);
        var oldReference = device.Settings.GetValueOrDefault("credentialRef", "");
        var clone = Clone(project);
        var (_, clonedDevice) = FindDevice(clone, deviceName);
        clonedDevice.Settings["authMode"] = "anonymous";
        clonedDevice.Settings.Remove("credentialRef");
        await _reload(clone);
        if (!string.IsNullOrWhiteSpace(oldReference))
            await _credentials.DeleteAsync(oldReference, CancellationToken.None);
        _failures.TryRemove(deviceName, out _);
    }

    private static (ChannelDef Channel, DeviceDef Device) FindDevice(IgwProject project, string name)
    {
        foreach (var channel in project.Channels)
        foreach (var device in channel.Devices)
        {
            if (string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"{channel.Name}.{device.Name}", name, StringComparison.OrdinalIgnoreCase))
                return (channel, device);
        }
        throw new KeyNotFoundException($"未找到设备 {name}。");
    }

    private static IgwProject Clone(IgwProject project) =>
        JsonSerializer.Deserialize<IgwProject>(ProjectStore.ToJson(project), ProjectStore.Options)
        ?? throw new InvalidOperationException("无法复制当前工程。");

    private static (string Host, ushort Port) ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new FormatException("设备端点为空。");
        var split = endpoint.LastIndexOf(':');
        var host = split > 0 ? endpoint[..split].Trim() : endpoint.Trim();
        var port = split > 0 && ushort.TryParse(endpoint[(split + 1)..], out var parsed) ? parsed : (ushort)102;
        return (host, port);
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;

    private void EnforceRateLimit(string deviceName)
    {
        if (!_failures.TryGetValue(deviceName, out var window)) return;
        if (DateTimeOffset.UtcNow - window.StartedAt > TimeSpan.FromMinutes(1))
        {
            _failures.TryRemove(deviceName, out _);
            return;
        }
        if (window.Count >= 5)
            throw new InvalidOperationException("身份验证失败次数过多，请一分钟后重试。");
    }

    private void RegisterFailure(string deviceName) => _failures.AddOrUpdate(
        deviceName,
        _ => new FailureWindow(DateTimeOffset.UtcNow, 1),
        (_, old) => DateTimeOffset.UtcNow - old.StartedAt > TimeSpan.FromMinutes(1)
            ? new FailureWindow(DateTimeOffset.UtcNow, 1)
            : old with { Count = old.Count + 1 });

    private sealed record FailureWindow(DateTimeOffset StartedAt, int Count);
}

public sealed class StoreSiemensCredentialResolver : ISiemensCredentialResolver
{
    private readonly IDeviceCredentialStore _store;
    private readonly string _reference;

    public StoreSiemensCredentialResolver(IDeviceCredentialStore store, string reference)
    {
        _store = store;
        _reference = reference;
    }

    public async Task<SiemensCredential?> ResolveAsync(CancellationToken ct)
    {
        var credential = await _store.GetAsync(_reference, ct);
        return credential is null ? null : new SiemensCredential(credential.Username, credential.Password);
    }
}
