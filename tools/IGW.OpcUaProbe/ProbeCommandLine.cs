namespace IGW.OpcUaProbe;

public sealed record ProbeCommandLineResult(
    ProbeOptions Options,
    string OutputDirectory,
    bool ShowHelp,
    string SafeDescription);

public static class ProbeCommandLine
{
    public static ProbeCommandLineResult Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"无法识别参数：{key}");
            if (key is "--help" or "--include-ns0" or "--strict-cert")
            {
                switches.Add(key);
                continue;
            }
            if (++index >= args.Length)
                throw new ArgumentException($"参数 {key} 缺少值。");
            values[key] = args[index];
        }

        string url = Get(values, "--url", string.Empty);
        string username = Get(values, "--username", string.Empty);
        string password = Get(values, "--password", string.Empty);
        bool anonymous = username.Length == 0 && Bool(Get(values, "--anonymous", "true"), "--anonymous");
        var options = new ProbeOptions(
            url,
            Get(values, "--security", "None"),
            anonymous,
            username,
            password,
            Number(Get(values, "--timeout", "15000"), "--timeout"),
            Number(Get(values, "--max-nodes", "200000"), "--max-nodes"),
            Number(Get(values, "--max-depth", "128"), "--max-depth"),
            switches.Contains("--include-ns0"),
            !switches.Contains("--strict-cert"),
            LocalIpv4(Get(values, "--local-ip", string.Empty)));
        string output = Path.GetFullPath(Get(values, "--output", Environment.CurrentDirectory));
        string safe = $"Url={url}; Security={options.SecurityPolicy}; Identity={(anonymous ? "Anonymous" : $"UserName:{username}")}; " +
                      $"TimeoutMs={options.TimeoutMs}; MaxNodes={options.MaxNodes}; MaxDepth={options.MaxDepth}; " +
                      $"IncludeNamespaceZero={options.IncludeNamespaceZero}; LocalIp={Display(options.LocalIp)}; Output={output}";
        return new ProbeCommandLineResult(options, output, switches.Contains("--help"), safe);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? value) ? value : fallback;

    private static int Number(string value, string name) =>
        int.TryParse(value, out int result)
            ? result
            : throw new ArgumentException($"参数 {name} 必须是整数。");

    private static bool Bool(string value, string name) =>
        bool.TryParse(value, out bool result)
            ? result
            : throw new ArgumentException($"参数 {name} 必须是 true 或 false。");

    private static string LocalIpv4(string value)
    {
        string candidate = value.Trim();
        if (candidate.Length == 0) return string.Empty;
        if (!System.Net.IPAddress.TryParse(candidate, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("参数 --local-ip 必须是有效的 IPv4 地址。");
        return address.ToString();
    }

    private static string Display(string value) => value.Length == 0 ? "SystemDefault" : value;
}
