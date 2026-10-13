using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using IGW.Runtime;
using IGW.Core.Security;
using IGW.Persistence.Sqlite;
using IGW.Persistence.Replication;
using IGW.ProjectExchange.Kep;
using IGW.ProjectExchange;
using IGW.Observability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;

if (args.Contains("--probe")) { await ModbusProbe.RunAsync(); return; }
// 安装器调用：按用户在安装向导里选的目录写入日志配置后立即退出(已有配置时保留原目录)。
if (args.Contains("--initialize-logs")) { Environment.Exit(RuntimeLogging.InitializeStorage(args)); return; }

// 首次运行把旧版写在安装目录里的用户数据迁到 %ProgramData%\IGW(工程/授权/历史/证书无缝续上)
IgwPaths.MigrateFromLegacy(AppContext.BaseDirectory);
string dataDir = IgwPaths.DataDir;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // 作为 Windows 服务运行时,工作目录默认是 System32;固定到程序目录才能找到 appsettings
    ContentRootPath = AppContext.BaseDirectory,
});
// 控制台运行时均为空操作;被 Windows SCM / Linux systemd 拉起时自动切服务模式(Type=notify 就绪通知)
builder.Host.UseWindowsService(o => o.ServiceName = "IGW Gateway");
builder.Host.UseSystemd();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddIgwRealtimeSignalR();
builder.Services.AddIgwResponseCompression();
// 工程 JSON 可达数 GB(Kepware 全量导出 296MB 起)：multipart 表单限制必须与导入上限一致，
// 否则 Kestrel 30MB/表单 128MB 先把 /api/v1/project/import/* 拦下，接口自己的 4GB 校验形同虚设。
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ProjectExchangeApi.MaximumUploadBytes;
});
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = PublicPlcApiPolicy.DocumentTitle,
        Version = "v1",
        Description = "IGW PLC 点位查询、读取、写入、校验和配置接口。读取使用实时采集缓存并激活对应点位，写入进入 PLC 设备写队列。",
    });
    options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name);
    // Swagger 是对外 PLC 接口文件，不公开工程配置、授权、MES 等内部管理路由。
    options.DocInclusionPredicate((_, api) => PublicPlcApiPolicy.IsPublicSwaggerPath(api.RelativePath));
});

string legacyProjectPath = ResolveLegacyProjectPath(args, builder.Configuration);
var storagePaths = new ProjectStoragePaths(
    Path.Combine(dataDir, "project.db"),
    legacyProjectPath,
    Path.Combine(dataDir, "igw.masterkey"));
ProjectBootstrapResult storage = await new ProjectDatabaseBootstrapper()
    .OpenOrMigrateAsync(storagePaths, CancellationToken.None);
IProjectRepository projectRepository = storage.Repository;

// 数据存储：安装时选择的外部主库(SQL Server / PostgreSQL / MySQL)。本机 SQLite 始终是工作库和断线缓存；
// 读取工程前先与主库握手(换电脑/重装时以主库为准拉回配置)，主库连不上则用本机缓存照常运行。
ReplicationEngine? replication = null;
DatabaseSettings databaseSettings;
try
{
    databaseSettings = DatabaseSettingsFile.Load(dataDir, new InstallationSecretProtector(storagePaths.MasterKeyPath));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[IGW] 数据库配置文件无法读取，暂按本机 SQLite 运行：{ex.Message}");
    databaseSettings = new DatabaseSettings(DatabaseKind.Sqlite);
}
if (databaseSettings.IsExternal)
{
    using var bootLogs = LoggerFactory.Create(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
    string MesLogPath(string? configured) => string.IsNullOrWhiteSpace(configured) ? Path.Combine(dataDir, "mesupload.db")
        : Path.IsPathRooted(configured) ? configured : Path.Combine(dataDir, configured);
    replication = new ReplicationEngine(databaseSettings,
    [
        new ReplicationSource("project", storagePaths.DatabasePath, Authoritative: true),
        new ReplicationSource("ui", Path.Combine(dataDir, "ui-preferences.db"), Authoritative: false),
        new ReplicationSource("mes_interface_inbox", Path.Combine(dataDir, "mes-interface-inbox.db"), Authoritative: false),
        new ReplicationSource("mes_upload_log", MesLogPath(null), Authoritative: false),
        new ReplicationSource("history", Path.Combine(dataDir, "history.db"), Authoritative: false),
    ], bootLogs.CreateLogger("DatabaseSync"));
    using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    bool connected = await replication.InitializeAsync(handshakeTimeout.Token);
    Console.WriteLine(connected
        ? $"[IGW] 已连接主数据库 {databaseSettings}{(replication.WasPulled("project") ? "，已按主数据库回灌工程配置" : "")}"
        : $"[IGW] 主数据库 {databaseSettings} 暂时无法连接，使用本机缓存运行(暂停修改配置)");
}

IgwProject project = await projectRepository.LoadRuntimeProjectAsync(CancellationToken.None);
ProjectRevision projectRevision = await projectRepository.GetRevisionAsync(CancellationToken.None);
// Open Protocol 已下线：旧数据库里残留的通道直接移除并写回，工程树和监控中心里不再出现。
IReadOnlyList<string> retiredChannels = RetiredProjectContent.RemoveOpenProtocolChannels(project);
if (retiredChannels.Count > 0)
{
    projectRevision = await projectRepository.ReplaceFromSnapshotAsync(project, projectRevision, CancellationToken.None);
    Console.WriteLine($"[IGW] 已忽略旧工程中的 Open Protocol 通道：{string.Join("、", retiredChannels)}");
}
// 安装向导里填写的端口(首次安装或卸载后重装)，网关启动时写入工程设置后删除该文件。
string firstRunPath = Path.Combine(dataDir, "first-run.json");
if (File.Exists(firstRunPath))
{
    try
    {
        // 首次安装或卸载后重装时在向导里填写的端口；从主库拉回的工程保持主库里的端口。
        if (replication?.WasPulled("project") != true)
        {
            using var firstRun = System.Text.Json.JsonDocument.Parse(File.ReadAllText(firstRunPath));
            if (firstRun.RootElement.TryGetProperty("restPort", out var restPortValue) && restPortValue.TryGetInt32(out int firstRest)
                && firstRest is > 0 and <= 65535)
                project.Settings.RestPort = firstRest;
            if (firstRun.RootElement.TryGetProperty("opcUaPort", out var opcUaPortValue) && opcUaPortValue.TryGetInt32(out int firstUa)
                && firstUa is > 0 and <= 65535)
            {
                project.OpcUa.Port = firstUa;
                project.Settings.OpcUaPort = firstUa;
            }
            projectRevision = await projectRepository.ReplaceFromSnapshotAsync(project, projectRevision, CancellationToken.None);
        }
        File.Delete(firstRunPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[IGW] 首次安装端口设置未能应用：{ex.Message}");
    }
}
Console.WriteLine(storage.Source switch
{
    ProjectStorageSource.MigratedLegacy => $"[IGW] 旧 JSON 工程已安全迁移到 SQLite，备份:{storage.LegacyBackupPath}",
    ProjectStorageSource.InitialProject => $"[IGW] 已生成初始 SQLite 工程:{storagePaths.DatabasePath}",
    _ => $"[IGW] 已打开 SQLite 工程:{storagePaths.DatabasePath}",
});
bool hadObservabilitySettings = File.Exists(Path.Combine(dataDir, ObservabilitySettings.FileName));
int? observabilityPort = int.TryParse(Environment.GetEnvironmentVariable("IGW_OPENOBSERVE_PORT"), out int configuredObservabilityPort)
    && configuredObservabilityPort is >= 1024 and <= 65535
    ? configuredObservabilityPort
    : null;
string? initialObservabilityDirectory = string.IsNullOrWhiteSpace(project.Settings.LogStorageDirectory)
    ? null
    : project.Settings.LogStorageDirectory;
ObservabilitySettings observabilitySettings = ObservabilitySettings.Initialize(
    dataDir, initialObservabilityDirectory, observabilityPort);
if (!hadObservabilitySettings
    && project.Settings.LogRetentionDays is >= 1 and <= 3650
    && project.Settings.LogRetentionDays != observabilitySettings.RetentionDays)
{
    observabilitySettings = observabilitySettings.WithRetentionDays(project.Settings.LogRetentionDays);
    observabilitySettings.Persist(dataDir);
}
await using RuntimeTelemetry runtimeTelemetry = RuntimeTelemetry.Create(observabilitySettings, dataDir);
RuntimeLogging.Configure(builder.Logging, runtimeTelemetry);
builder.Services.AddSingleton(runtimeTelemetry);
builder.Services.AddSingleton(runtimeTelemetry.Backend);
// 旧正式版曾把开发种子标题写成 Demo。升级时只纠正标题，通道、设备、点位和流程全部原样保留。
if (string.Equals(project.Name, "Demo", StringComparison.OrdinalIgnoreCase))
{
    project.Name = "项目";
    projectRevision = await projectRepository.ReplaceFromSnapshotAsync(
        project, projectRevision, CancellationToken.None);
}
int restPort = int.TryParse(Environment.GetEnvironmentVariable("IGW_REST_PORT"), out int developmentPort)
    && developmentPort is > 0 and <= 65535
    ? developmentPort
    : project.Settings.RestPort;
builder.WebHost.UseUrls($"http://0.0.0.0:{restPort}");
// 记录实际监听的端口：安装程序升级时据此生成快捷方式、等待网关启动。
try
{
    File.WriteAllText(Path.Combine(dataDir, "runtime-endpoint.json"),
        $"{{\"restPort\":{restPort},\"opcUaPort\":{project.OpcUa?.Port ?? 4840}}}");
}
catch { /* 只影响安装程序的端口推断，不影响运行 */ }

var app = builder.Build();
// 最外层：JSON 应答 gzip 压缩(完整工程 27 MB → 约 1 MB)；实时推送流不压缩。
app.UseIgwResponseCompression();

var buildIdentity = RuntimeBuildIdentity.Capture(typeof(GatewayHost).Assembly);
app.Logger.LogInformation(
    "IGW 进程身份：程序集版本={assemblyVersion}，构建标识={buildIdentifier}，模块标识={moduleVersionId}，程序集路径={assemblyPath}，程序路径={processPath}，PID={processId}，本次启动ID={startupId}",
    buildIdentity.AssemblyVersion, buildIdentity.BuildIdentifier, buildIdentity.ModuleVersionId,
    buildIdentity.AssemblyPath, buildIdentity.ProcessPath, buildIdentity.ProcessId, buildIdentity.StartupId);

bool useSim = args.Contains("--sim") || builder.Configuration.GetValue<bool>("Simulator:Enabled");
ModbusSimulator? simulator = null;
if (useSim)
{
    // 模拟器是辅助功能:端口 1502 被占(如另一实例已开模拟器)绝不能拖死整个网关
    try { simulator = new ModbusSimulator(app.Logger); simulator.Start(); }
    catch (Exception ex)
    {
        app.Logger.LogError("内置 Modbus 模拟器启动失败(端口 1502 被占用?),已跳过,不影响采集与北向:{m}", ex.Message);
        simulator = null;
    }
}

var registry = new DriverRegistry();
var deviceCredentials = new DeviceCredentialStore(Path.Combine(dataDir, "secrets"));
registry.Register(new IGW.Drivers.Simulator.SimulatorDriverFactory());
registry.Register(new IGW.Drivers.IoTClient.IoTModbusDriverFactory());   // Modbus.Tcp 改用 IoTClient 实现
registry.Register(new IGW.Drivers.OpcUaClient.OpcUaClientDriverFactory());
registry.Register(new IGW.Drivers.IoTClient.IoTSiemensDriverFactory(dev =>
{
    if (!string.Equals(dev.Settings.GetValueOrDefault("authMode"), "credential", StringComparison.OrdinalIgnoreCase)) return null;
    var reference = dev.Settings.GetValueOrDefault("credentialRef", "");
    return string.IsNullOrWhiteSpace(reference) ? null : new StoreSiemensCredentialResolver(deviceCredentials, reference);
}));      // 西门子 → Classic/Plus 自动路由，凭据从本机 DPAPI 凭据库解析
registry.Register(new IGW.Drivers.IoTClient.IoTOmronFinsDriverFactory());    // 欧姆龙 FINS → IoTClient
registry.Register(new IGW.Drivers.IoTClient.IoTAbDriverFactory());           // AB → IoTClient
registry.Register(new IGW.Drivers.IoTClient.IoTMitsubishiDriverFactory());   // 三菱 → IoTClient
registry.Register(new IGW.Drivers.EtherNetIP.OmronCipDriverFactory());       // 欧姆龙 NJ/NX EtherNet/IP 保留 libplctag(IoTClient 不支持)
registry.Register(new IGW.Drivers.Mqtt.MqttDriverFactory());

var kepCodecRegistry = new KepDriverCodecRegistry([
    new IGW.Drivers.Simulator.Kep.SimulatorKepDriverCodec(),
    new IGW.Drivers.IoTClient.Kep.SiemensS7KepDriverCodec(),
    new IGW.Drivers.IoTClient.Kep.ModbusTcpKepDriverCodec(),
    new IGW.Drivers.IoTClient.Kep.OmronFinsKepDriverCodec(),
    new IGW.Drivers.IoTClient.Kep.AllenBradleyKepDriverCodec(),
    new IGW.Drivers.IoTClient.Kep.MitsubishiKepDriverCodec(),
    new IGW.Drivers.EtherNetIP.Kep.OmronCipKepDriverCodec(),
    new IGW.Drivers.OpcUaClient.Kep.OpcUaClientKepDriverCodec(),
    new IGW.Drivers.Mqtt.Kep.MqttKepDriverCodec(),
]);

var host = new GatewayHost(
    project,
    projectRepository,
    projectRevision,
    registry,
    app.Services.GetRequiredService<ILoggerFactory>(),
    dataDir,
    deviceCredentials,
    async (content, cancellationToken) =>
        (await runtimeTelemetry.Payloads.SaveTextAsync(
            "text/plain; charset=utf-8", content, cancellationToken).ConfigureAwait(false)).Id);
var projectExchangeSessions = new ProjectImportSessionStore(Path.Combine(dataDir, "project-import"));
var projectExchange = new ProjectJsonImporter(
    projectRepository,
    kepCodecRegistry.Codecs,
    projectExchangeSessions,
    host.ApplyImportedProjectAsync);
var uiPreferences = new UiPreferenceStore(Path.Combine(dataDir, "ui-preferences.db"));
if (replication is not null)
{
    replication.UseLogger(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSync"));
    host.DatabaseSync = replication;
    replication.Start();
}
await host.StartAsync();

// 网页账号与页面权限：第三方数据接口不需要登录，管理接口必须登录并按页面权限判断(见 GatewayAccessPolicy)
var accounts = new GatewayAccountStore(dataDir);
var sessions = new GatewaySessionTokens(dataDir);
// 实时通道(网页 QC 写值走这里)：连接带的网页登录凭证每次写值都重新验证
app.Services.GetRequiredService<IgwRealtimeRuntime>().WebAccount =
    ctx => sessions.Validate(GatewayAccessPolicy.ReadToken(ctx.Request), accounts);
app.Use(async (ctx, next) =>
{
    string path = ctx.Request.Path.Value ?? "";
    string? mutationKind = HttpMethods.IsPost(ctx.Request.Method)
        && path.Equals(GatewayAccessPolicy.MutationsPath, StringComparison.OrdinalIgnoreCase)
            ? await GatewayAccessPolicy.PeekMutationKindAsync(ctx.Request)
            : null;
    AccessRule rule = GatewayAccessPolicy.Classify(path, ctx.Request.Method, mutationKind);
    GatewayAccount? account = sessions.Validate(GatewayAccessPolicy.ReadToken(ctx.Request), accounts);
    if (account is not null) ctx.Items[GatewayAccessPolicy.AccountItemKey] = account;
    if (GatewayAccessPolicy.Check(rule, account, host.FeatureLicensed) is { } denied)
    {
        ctx.Response.StatusCode = denied.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = denied.Error, message = denied.Message });
        return;
    }
    await next();
});

// 工程里的接口账号(安全设置，HTTP Basic)：启用后第三方调数据接口按它鉴权；已用网页账号登录的请求不再检查
app.Use(async (ctx, next) =>
{
    if (ctx.Items.ContainsKey(GatewayAccessPolicy.AccountItemKey)) { await next(); return; }
    var sec = host.Project.Security;
    var authorization = PublicPlcApiPolicy.Authorize(ctx, sec);
    if (!authorization.IsAllowed)
    {
        ctx.Response.StatusCode = authorization.StatusCode!.Value;
        if (authorization.StatusCode == StatusCodes.Status401Unauthorized)
            ctx.Response.Headers.WWWAuthenticate = "Basic";
        string message = authorization.ErrorCode == "write_forbidden"
            ? "无写权限"
            : authorization.ErrorCode == "read_branch_forbidden"
                ? "无该分支读权限"
                : "未授权:需要用户名/密码";
        await ctx.Response.WriteAsync(message);
        return;
    }

    if (authorization.User is not null)
        ctx.Items[PublicPlcApiPolicy.AuthenticatedUserItemKey] = authorization.User;
    await next();
});

app.UseIgwInterfaceTelemetry(app.Logger, async (content, cancellationToken) =>
    (await runtimeTelemetry.Payloads.SaveTextAsync(
        "text/plain; charset=utf-8", content, cancellationToken).ConfigureAwait(false)).Id);

app.MapIgwApi(host);
app.MapProjectExchangeApi(projectExchange, () => host.Project.Name);
app.MapIgwRealtimeSignalR(host);
app.MapRealtimeStreamDiagnostics(() => host.RealtimeStreams);
app.MapUiPreferencesApi(uiPreferences);
app.MapGatewayAccountApi(accounts, sessions);
app.MapDcsTagCompatibilityApi(host);
app.MapLogCenterApi(runtimeTelemetry.Backend, () => host.Project.MesUpload.Tasks.Select(task =>
    new LogTaskBinding(task.Id, string.IsNullOrWhiteSpace(task.GroupRef) ? task.DeviceRef : task.GroupRef)));

// 对外接口文档：页面 /swagger，机器可读接口文件 /swagger/v1/swagger.json。
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", $"{PublicPlcApiPolicy.DocumentTitle} v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = PublicPlcApiPolicy.DocumentTitle;
    options.DisplayRequestDuration();
});

// 内嵌 Web 配置界面(wwwroot,Vue 构建产物):浏览器访问 http://网关IP:端口/ 即用,Windows/Linux 同一套。
// 静态资源存在才启用;/api、/iotgateway 不受影响(它们已在上面 Map)。
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    app.UseDefaultFiles();
    // index.html 禁缓存(assets 带内容哈希可长缓存):否则升级后浏览器用旧 index 引旧 js,"修了还是老样子"
    var sfo = new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        },
    };
    app.UseStaticFiles(sfo);
    // 未注册的 API 必须返回真实 404，不能被 SPA 回退伪装成 index.html 200。
    app.Map("/api/{**path}", () => Results.NotFound());
    app.Map("/Tag/{**path}", () => Results.NotFound());
    // SPA 回退:非 /api、/iotgateway 的未命中路由都回 index.html(前端 hash 路由,基本用不到,兜底)
    app.MapFallbackToFile("index.html", sfo);
}
else
{
    app.MapGet("/", () => Results.Redirect("/api/v1/health"));   // 没打包 Web UI 时退回健康页
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    try { replication?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    try { projectExchange.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    try { uiPreferences.Dispose(); } catch { }
    simulator?.Stop();
});

app.Logger.LogInformation("IGW Runtime 启动:工程 {name},REST :{rp},本机 OPC UA Server(北向) :{op}",
    project.Name, restPort, project.OpcUa?.Port ?? 4840);   // 用真正监听的 OpcUa.Port,别用 Settings.OpcUaPort

// 明确打印「网页配置界面」访问地址(本机 + 各网卡局域网 IP):无界面/Linux 环境靠日志或 `igwctl url` 知道往哪连
LogAccessUrls(app.Logger, restPort);

try
{
    await app.RunAsync();
}
catch (IOException ex) when (IsAddressInUse(ex))
{
    // Windows 下未处理的绑定异常会弹出 0xe0434352 系统崩溃框；端口冲突属于可说明的启动失败。
    string message = $"IGW Runtime 启动失败：REST 端口 {restPort} 已被其他程序占用。请关闭重复实例或修改工程端口。";
    app.Logger.LogCritical(ex, "{message}", message);
    Console.Error.WriteLine(message);
    Environment.ExitCode = 98;
}

static string ResolveLegacyProjectPath(string[] args, IConfiguration cfg)
{
    var fromArg = args.FirstOrDefault(a => a.EndsWith(".igwproj", StringComparison.OrdinalIgnoreCase));
    if (fromArg is not null) return Path.GetFullPath(fromArg);
    var fromCfg = cfg["ProjectFile"];
    if (!string.IsNullOrWhiteSpace(fromCfg)) return Path.GetFullPath(fromCfg);
    return IgwPaths.ProjectFile;   // %ProgramData%\IGW\project.igwproj(可写、重装不丢)
}

static bool IsAddressInUse(Exception error)
{
    for (Exception? current = error; current is not null; current = current.InnerException)
        if (current is System.Net.Sockets.SocketException socket
            && socket.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
            return true;
    return false;
}

// 打印网页配置界面可访问的 URL:本机 localhost + 各活动网卡的 IPv4(局域网其它机器/HMI 用得上)
static void LogAccessUrls(Microsoft.Extensions.Logging.ILogger logger, int port)
{
    logger.LogInformation("──────── IGW 网页配置界面:浏览器打开以下任一地址即用 ────────");
    logger.LogInformation("    本机:   http://localhost:{p}/", port);
    try
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    logger.LogInformation("    局域网: http://{ip}:{p}/", ua.Address, port);
        }
    }
    catch { /* 枚举网卡失败不影响启动 */ }
    logger.LogInformation("────────────────────────────────────────────────────────────");
}
