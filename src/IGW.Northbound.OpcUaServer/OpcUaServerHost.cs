using IGW.Abstractions;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace IGW.Northbound.OpcUaServer;

/// <summary>OPC UA Server 宿主:构建配置、自签证书、启动,并把 DataHub 变化推到地址空间。</summary>
public sealed class OpcUaServerHost : IAsyncDisposable
{
    public void RefreshDescriptions() => _server?.NodeManager?.RefreshDescriptions();

    /// <summary>
    /// 工程局部修改后只同步受影响的地址空间节点(变化设备对齐、删除设备/通道移除、新通道挂到 Objects 下);
    /// 其它设备的会话/订阅不受影响。服务未运行时返回 false。
    /// recreatedHubTagIds:本次 Hub 条目被重建的点(引擎停设备超时回退),用于在新条目上重新注册推送。
    /// 调用方须串行调用(GatewayHost 在其应用闸门内调用)。
    /// </summary>
    public bool SyncDevices(IgwProject project, IReadOnlyCollection<string> changedDevices,
        IReadOnlyCollection<string> removedDevices, IReadOnlyCollection<string>? removedChannels = null,
        IReadOnlyCollection<string>? recreatedHubTagIds = null) =>
        _server?.NodeManager?.SyncDevices(project, changedDevices, removedDevices, removedChannels, recreatedHubTagIds) ?? false;

    /// <summary>只把 <c>通道.设备</c> 这一棵子树与新定义对齐(<see cref="SyncDevices"/> 的单设备形式);服务未运行时 no-op。</summary>
    internal void ReplaceDevice(ChannelDef channel, DeviceDef device, IReadOnlyCollection<string>? recreatedHubTagIds = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(device);
        ChannelDef scoped = channel.ShallowCopy();
        scoped.Devices = [device];
        SyncDevices(new IgwProject { Channels = [scoped] },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{channel.Name}.{device.Name}" },
            Array.Empty<string>(), null, recreatedHubTagIds);
    }

    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });
    private ApplicationInstance? _app;
    private IgwServer? _server;
    private IDisposable? _sub;

    /// <summary>当前 OPC UA 会话数(数据客户端数)。</summary>
    public int SessionCount
    {
        get { try { return _server?.CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0; } catch { return 0; } }
    }

    /// <summary>服务端应用证书到期时间(临期巡检用;取不到返回 null)。</summary>
    public DateTime? CertExpiry
    {
        get { try { return _app?.ApplicationConfiguration?.SecurityConfiguration?.ApplicationCertificate?.Certificate?.NotAfter; } catch { return null; } }
    }

    // 会话数节流缓存(1s):变化推送每条都查 SessionManager.GetSessions() 太贵
    private int _cachedSessions;
    private long _lastSessTick;
    private bool HasSessions
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - _lastSessTick > 1000) { _lastSessTick = now; _cachedSessions = SessionCount; }
            return _cachedSessions > 0;
        }
    }

    public async Task StartAsync(IgwProject project, IDataHub hub, Func<string, object, Task<bool>> writer,
        Func<string, ArraySlice, ArrayRuntimeValue, Task<ArrayWriteResult>> arrayWriter,
        ILogger log, string? pkiDir = null)
    {
        var ua = project.OpcUa ?? new OpcUaSettings();
        int port = ua.Port <= 0 ? 4840 : ua.Port;
        string pki = pkiDir ?? Path.Combine(AppContext.BaseDirectory, "pki");   // 证书目录:落可写数据根(安装目录普通用户没写权限)

        var config = BuildConfig(ua, pki);
        await config.ValidateAsync(ApplicationType.Server, CancellationToken.None);
        config.CertificateValidator.CertificateValidation += (_, e) => { e.Accept = true; };

        _app = new ApplicationInstance(Telemetry)
        {
            ApplicationName = "IGW OPC UA Server",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config,
        };
        await _app.CheckApplicationInstanceCertificatesAsync(true, 240);   // 自签证书 20 年有效期:7×24 跑数年不会"运行中过期"(默认仅 12 个月)

        // 订阅倒转:按"被 MonitoredItem 钉住的点"注册(NodeManager 在监控项建立/删除时 Add/Remove,计数化——
        // 同点多监控项安全)。未钉的点变化不再流经本订阅;普通 UA 读走节点 OnSimpleReadValue 直通缓存,读到的仍是新值。
        var pushSub = hub.SubscribePoints(c =>
        {
            if (!HasSessions) return ValueTask.CompletedTask;   // 推送要抢 NodeManager 全局锁,无会话不推
            _server!.NodeManager?.PushValue(c.TagId, c.Value);
            return ValueTask.CompletedTask;
        }, "opcua-push");
        _sub = pushSub;

        _server = new IgwServer(project, hub, writer, arrayWriter, pushSub);
        await _app.StartAsync(_server);

        _server.NodeManager?.PrimeAll();

        log.LogInformation("IGW 本机 OPC UA Server(北向服务)已启动：本机端点 opc.tcp://localhost:{port} (最大会话={ms},诊断={d})", port, ua.MaxSessions, ua.DiagnosticsEnabled);
    }

    private static ApplicationConfiguration BuildConfig(OpcUaSettings ua, string pki) => new()
    {
        ApplicationName = "IGW OPC UA Server",
        ApplicationUri = "urn:localhost:IGW:OpcUaServer",
        ApplicationType = ApplicationType.Server,
        ServerConfiguration = new ServerConfiguration
        {
            BaseAddresses = { $"opc.tcp://localhost:{ua.Port}" },   // 不加 /IGW 路径:和 KEP 一样、DCS 按配置 URL 直连
            SecurityPolicies = BuildSecurityPolicies(ua),
            UserTokenPolicies = ua.AllowAnonymous
                ? new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) }
                : new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.UserName) },
            DiagnosticsEnabled = ua.DiagnosticsEnabled,
            MaxSessionCount = ua.MaxSessions,
            MinSessionTimeout = ua.MinSessionTimeoutSec * 1000,
            MaxSessionTimeout = ua.MaxSessionTimeoutSec * 1000,
            MaxNotificationsPerPublish = ua.MaxNotificationsPerPublish,
            MaxNotificationQueueSize = ua.MaxDataQueueSize,
            MaxMessageQueueSize = ua.MaxRetransmitQueueSize,
        },
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(pki, "own"),
                SubjectName = "CN=IGW OPC UA Server, O=IGW",
            },
            TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
            TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
            RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
            AutoAcceptUntrustedCertificates = true,
            AddAppCertToTrustedStore = true,
            RejectSHA1SignedCertificates = false,
            MinimumCertificateKeySize = 1024,
        },
        TransportConfigurations = new TransportConfigurationCollection(),
        TransportQuotas = new TransportQuotas { OperationTimeout = 120000 },
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        TraceConfiguration = new TraceConfiguration(),
    };

    // None 始终提供;AllowEncryption 时再提供 Basic256Sha256 的 Sign / SignAndEncrypt 端点
    private static ServerSecurityPolicyCollection BuildSecurityPolicies(OpcUaSettings ua)
    {
        var col = new ServerSecurityPolicyCollection
        {
            new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
        };
        if (ua.AllowEncryption)
        {
            col.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.Sign, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
            col.Add(new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
        }
        return col;
    }

    public async ValueTask DisposeAsync()
    {
        _sub?.Dispose();
        try { if (_server is not null) await _server.StopAsync(CancellationToken.None); } catch { }
        try { _server?.Dispose(); } catch { }   // 释放监听 socket,尽快让 opc.tcp 端口可重绑
        _server = null;
        _app = null;
    }
}
