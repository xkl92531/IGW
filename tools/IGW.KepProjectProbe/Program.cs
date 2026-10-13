using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IGW.KepProjectProbe;

Console.OutputEncoding = Encoding.UTF8;
ProbeOptions options;
try { options = ProbeOptions.Parse(args); }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    ProbeOptions.PrintUsage();
    return 64;
}
if (options.Help)
{
    ProbeOptions.PrintUsage();
    return 0;
}

string inputJson = await File.ReadAllTextAsync(options.Input);
using JsonDocument input = JsonDocument.Parse(inputJson, new JsonDocumentOptions { MaxDepth = 128 });
if (!input.RootElement.TryGetProperty("project", out _))
    throw new InvalidDataException("input_project_missing");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Backup))!);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);

string? password = string.IsNullOrWhiteSpace(options.PasswordEnvironment)
    ? null : Environment.GetEnvironmentVariable(options.PasswordEnvironment);
DateTimeOffset started = DateTimeOffset.UtcNow;
string inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson))).ToLowerInvariant();
using var client = new KepConfigurationClient(new Uri(EnsureSlash(options.BaseUrl)), options.Username, password);

try
{
    KepProjectResponse backup = await client.ExportAsync(CancellationToken.None);
    using JsonDocument backupDocument = JsonDocument.Parse(backup.Json, new JsonDocumentOptions { MaxDepth = 128 });
    await File.WriteAllTextAsync(options.Backup, backup.Json);
    if (!options.AllowProjectReplace)
    {
        await WriteReportAsync(options.Output + ".report.json", new
        {
            accepted = false,
            dryRun = true,
            reason = "allow_project_replace_required",
            startedAtUtc = started,
            completedAtUtc = DateTimeOffset.UtcNow,
            inputHash,
            kepServer = backup.Server,
            backup = Path.GetFullPath(options.Backup),
        });
        Console.WriteLine("连接和输入校验完成；未提供 --allow-project-replace，未改动 KEP 工程。");
        return 2;
    }

    KepLoadResponse loaded = await client.LoadAsync(inputJson, CancellationToken.None);
    if (!loaded.Success)
    {
        await WriteReportAsync(options.Output + ".report.json", new
        {
            accepted = false,
            dryRun = false,
            reason = "kep_project_load_failed",
            loaded.StatusCode,
            response = loaded.Body,
            startedAtUtc = started,
            completedAtUtc = DateTimeOffset.UtcNow,
            inputHash,
            backup = Path.GetFullPath(options.Backup),
        });
        Console.Error.WriteLine($"KEP 导入失败，HTTP {loaded.StatusCode}；原工程备份在 {options.Backup}");
        return 1;
    }

    KepProjectResponse exported = await client.ExportAsync(CancellationToken.None);
    await File.WriteAllTextAsync(options.Output, exported.Json);
    using JsonDocument actual = JsonDocument.Parse(exported.Json, new JsonDocumentOptions { MaxDepth = 128 });
    KepComparisonReport comparison = KepProjectComparer.Compare(input.RootElement, actual.RootElement);
    await WriteReportAsync(options.Output + ".report.json", new
    {
        accepted = comparison.Equivalent,
        dryRun = false,
        startedAtUtc = started,
        completedAtUtc = DateTimeOffset.UtcNow,
        inputHash,
        kepServer = exported.Server,
        loadStatus = loaded.StatusCode,
        backup = Path.GetFullPath(options.Backup),
        reExported = Path.GetFullPath(options.Output),
        differences = comparison.Differences,
    });
    Console.WriteLine(comparison.Equivalent
        ? "KEP 导入并重新导出比较一致。"
        : $"KEP 往返发现 {comparison.Differences.Count} 项差异，详情见 {options.Output}.report.json");
    return comparison.Equivalent ? 0 : 1;
}
catch (Exception error)
{
    await WriteReportAsync(options.Output + ".report.json", new
    {
        accepted = false,
        startedAtUtc = started,
        completedAtUtc = DateTimeOffset.UtcNow,
        inputHash,
        error = error.GetType().Name,
        message = Redact(error.Message, password),
        backup = Path.GetFullPath(options.Backup),
    });
    Console.Error.WriteLine(Redact(error.Message, password));
    return 1;
}

static async Task WriteReportAsync(string path, object report) =>
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
static string EnsureSlash(string value) => value.EndsWith('/') ? value : value + "/";
static string Redact(string value, string? secret) => string.IsNullOrEmpty(secret)
    ? value : value.Replace(secret, "***", StringComparison.Ordinal);

internal sealed record ProbeOptions(
    string Input,
    string BaseUrl,
    string Backup,
    string Output,
    string? Username,
    string? PasswordEnvironment,
    bool AllowProjectReplace,
    bool Help)
{
    public static ProbeOptions Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
            return new("", "", "", "", null, null, false, true);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool allow = false;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--allow-project-replace") { allow = true; continue; }
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"无效参数：{args[index]}");
            values[args[index]] = args[++index];
        }
        string Required(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"缺少必填参数 {name}");
        return new(
            Path.GetFullPath(Required("--input")),
            Required("--base-url"),
            Path.GetFullPath(Required("--backup")),
            Path.GetFullPath(Required("--output")),
            values.GetValueOrDefault("--username"),
            values.GetValueOrDefault("--password-env"),
            allow,
            false);
    }

    public static void PrintUsage() => Console.WriteLine(
        "IGW.KepProjectProbe --input project.json --base-url http://127.0.0.1:57412/ " +
        "--backup kep-backup.json --output kep-reexport.json [--username user --password-env KEP_PASSWORD] " +
        "[--allow-project-replace]");
}
