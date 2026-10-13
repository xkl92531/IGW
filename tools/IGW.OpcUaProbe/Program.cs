using System.Text;
using IGW.OpcUaProbe;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

ProbeCommandLineResult command;
try
{
    command = ProbeCommandLine.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"参数错误：{ex.Message}");
    PrintHelp();
    return 2;
}

if (command.ShowHelp)
{
    PrintHelp();
    return 0;
}

ProbeOptions options = command.Options;
string outputDirectory = command.OutputDirectory;
if (string.IsNullOrWhiteSpace(options.Url))
{
    Console.Write("OPC UA 地址（例如 opc.tcp://10.217.152.80:4840）：");
    string url = Console.ReadLine()?.Trim() ?? string.Empty;
    Console.Write("匿名登录？[Y/n]：");
    string anonymousAnswer = Console.ReadLine()?.Trim() ?? string.Empty;
    bool anonymous = anonymousAnswer.Length == 0 || anonymousAnswer.Equals("y", StringComparison.OrdinalIgnoreCase)
        || anonymousAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    string username = string.Empty;
    string password = string.Empty;
    if (!anonymous)
    {
        Console.Write("用户名：");
        username = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.Write("密码（不会保存）：");
        password = ReadPassword();
        Console.WriteLine();
    }
    string localIp = SelectLocalIpv4(options.LocalIp);
    Console.Write($"输出目录（回车使用 {outputDirectory}）：");
    string selectedOutput = Console.ReadLine()?.Trim() ?? string.Empty;
    if (selectedOutput.Length > 0) outputDirectory = Path.GetFullPath(selectedOutput);
    options = options with
    {
        Url = url,
        Anonymous = anonymous,
        Username = username,
        Password = password,
        LocalIp = localIp,
    };
}
else if (!options.Anonymous && string.IsNullOrEmpty(options.Password))
{
    Console.Write("密码（不会保存）：");
    options = options with { Password = ReadPassword() };
    Console.WriteLine();
}

Directory.CreateDirectory(outputDirectory);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
var logs = new List<string>();
void Log(string line)
{
    string safe = string.IsNullOrEmpty(options.Password)
        ? line
        : line.Replace(options.Password, "***", StringComparison.Ordinal);
    logs.Add(safe);
    Console.WriteLine(safe);
}

string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
string host = Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
    ? Sanitize(uri.Host)
    : "unknown";
string zipPath = Path.Combine(outputDirectory, $"IGW-OPCUA-Probe-{host}-{stamp}.zip");

try
{
    Log("IGW OPC UA 只读信息采集器 1.0.1");
    Log($"参数：Url={options.Url}; Security={options.SecurityPolicy}; Identity={(options.Anonymous ? "Anonymous" : $"UserName:{options.Username}")}; " +
        $"TimeoutMs={options.TimeoutMs}; MaxNodes={options.MaxNodes}; MaxDepth={options.MaxDepth}; IncludeNamespaceZero={options.IncludeNamespaceZero}; " +
        $"LocalIp={(string.IsNullOrWhiteSpace(options.LocalIp) ? "SystemDefault" : options.LocalIp)}");
    Log("安全边界：不读取业务变量值；不订阅；不调用 Method；不执行 Write。仅浏览、读取节点属性和 OPC UA Server 标准能力值。");
    var report = await new OpcUaMetadataCollector().CollectAsync(options, Log, cancellation.Token);
    string runtimeWriteErrors = RuntimeWriteLogCollector.Collect(RuntimeWriteLogCollector.DefaultLogDirectory());
    ProbeArchiveWriter.Create(
        zipPath,
        ProbeReportFormatter.ToJson(report),
        ProbeReportFormatter.ToWriteDiagnosticsJson(report.WriteDiagnostics),
        ProbeReportFormatter.ToSummary(report),
        string.Join(Environment.NewLine, logs),
        runtimeWriteErrors);
    Console.WriteLine();
    Console.WriteLine($"采集完成：{zipPath}");
    return 0;
}
catch (OperationCanceledException)
{
    Log("采集已由用户取消。");
    return 3;
}
catch (Exception ex)
{
    Log($"采集失败：{ex.GetType().Name}: {ex.Message}");
    Log(ex.ToString());
    string failureJson = System.Text.Json.JsonSerializer.Serialize(new
    {
        formatVersion = "1.0",
        collectedAtUtc = DateTimeOffset.UtcNow,
        requestedEndpoint = options.Url,
        localSourceIp = string.IsNullOrWhiteSpace(options.LocalIp) ? null : options.LocalIp,
        businessVariableValuesRead = false,
        serverCapabilityValuesRead = false,
        plcWritesPerformed = false,
        errorType = ex.GetType().FullName,
        error = ex.Message,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    string runtimeWriteErrors = RuntimeWriteLogCollector.Collect(RuntimeWriteLogCollector.DefaultLogDirectory());
    ProbeArchiveWriter.Create(
        zipPath,
        failureJson,
        "{\"items\":[],\"limitation\":\"连接或浏览失败，未生成写入诊断。\"}",
        $"采集失败：{ex.GetType().Name}: {ex.Message}",
        string.Join(Environment.NewLine, logs),
        runtimeWriteErrors);
    Console.Error.WriteLine($"失败信息也已保存：{zipPath}");
    return 1;
}

static string ReadPassword()
{
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0) text.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    }
    return text.ToString();
}

static string Sanitize(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
}

static string SelectLocalIpv4(string current)
{
    var adapters = ProbeNetworkAdapterCatalog.Available();
    Console.WriteLine("本机网络源地址（现场多网卡时请选择 PLC 所在网卡）：");
    Console.WriteLine("  0. 由 Windows 自动选择");
    for (int index = 0; index < adapters.Count; index++)
        Console.WriteLine($"  {index + 1}. {adapters[index].Label}");
    if (!string.IsNullOrWhiteSpace(current))
        Console.WriteLine($"当前参数：{current}");
    Console.Write("选择序号（回车使用 Windows 默认）：");
    string answer = Console.ReadLine()?.Trim() ?? string.Empty;
    if (answer.Length == 0 || answer == "0") return string.Empty;
    if (int.TryParse(answer, out int selected) && selected >= 1 && selected <= adapters.Count)
        return adapters[selected - 1].Ipv4;
    if (System.Net.IPAddress.TryParse(answer, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        return address.ToString();
    throw new ArgumentException("网卡选择无效，请输入列表序号或本机 IPv4 地址。");
}

static void PrintHelp()
{
    Console.WriteLine("""
        IGW OPC UA 只读信息采集器

        双击运行后按提示输入，或使用：
          IGW.OpcUaProbe.exe --url opc.tcp://10.217.152.80:4840 --output C:\Probe

        可选参数：
          --security None              安全端点选择，默认 None
          --anonymous true|false       是否匿名，默认 true
          --username USER              用户名；密码未传时会安全提示输入
          --password PASSWORD          可用但不推荐，命令行历史可能暴露密码
          --timeout 15000              超时毫秒，范围 1000..120000
          --local-ip 10.217.152.221    指定连接现场 OPC UA 的本机 IPv4/网卡
          --max-nodes 200000           最大节点数
          --max-depth 128              最大层级
          --include-ns0                同时递归 OPC UA 标准 namespace 0
          --strict-cert                不自动接受现场未受信任证书
          --help                       显示帮助

        输出 ZIP 包含完整节点属性、实际路径、数据类型定义、服务器写入能力、
        写权限诊断和日志。工具不读取业务变量值，也不会向 PLC 写入。
        """);
}
