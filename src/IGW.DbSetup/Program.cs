using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IGW.Core;
using IGW.Persistence.Replication;

// 安装向导调用的数据库设置工具。参数(含密码)通过临时 JSON 文件传入，不出现在命令行里；结果写回 JSON 文件。
//   IGW.DbSetup test      --request <file> --result <file>
//   IGW.DbSetup configure --request <file> --result <file> --data-dir <dir>
// 退出码：0 成功，1 失败(原因见结果文件)，2 参数错误。
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

string? Arg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string command = args.FirstOrDefault() ?? "";
string? requestPath = Arg("--request");
string? resultPath = Arg("--result");
if (command is not ("test" or "configure") || requestPath is null || resultPath is null)
{
    Console.Error.WriteLine("用法: IGW.DbSetup test|configure --request <file> --result <file> [--data-dir <dir>]");
    return 2;
}

void WriteResult(bool ok, string status, string message)
{
    // Inno Setup 按 ANSI/UTF-8 读取；写 UTF-8 带 BOM，保证中文不乱码。
    File.WriteAllText(resultPath, JsonSerializer.Serialize(new { ok, status, message }, options), new UTF8Encoding(true));
}

try
{
    var request = JsonSerializer.Deserialize<SetupRequest>(File.ReadAllText(requestPath), options)
        ?? throw new InvalidDataException("请求为空。");
    try { File.Delete(requestPath); } catch { /* 含密码的临时文件用完即删 */ }
    var settings = new DatabaseSettings(request.Kind, request.Host?.Trim() ?? "", request.Port,
        string.IsNullOrWhiteSpace(request.Database) ? DatabaseSettings.DefaultDatabase : request.Database.Trim(),
        request.Username?.Trim() ?? "", request.Password ?? "");

    if (command == "test")
    {
        if (!settings.IsExternal) { WriteResult(true, "ok", "SQLite 为本机内置数据库，无需测试。"); return 0; }
        DatabaseTestResult result = await DatabaseConnector.TestAsync(settings);
        WriteResult(result.Ok, JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString()), result.Message);
        return result.Ok ? 0 : 1;
    }

    string dataDir = Arg("--data-dir") ?? throw new InvalidDataException("缺少 --data-dir。");
    Directory.CreateDirectory(dataDir);
    var protector = new InstallationSecretProtector(Path.Combine(dataDir, "igw.masterkey"));
    if (!settings.IsExternal)
    {
        // 选 SQLite：保留旧的外部库配置会让网关继续连远端，这里明确写成 SQLite。
        DatabaseSettingsFile.Save(dataDir, settings, protector);
        WriteResult(true, "ok", "使用本机 SQLite 数据库。");
        return 0;
    }
    DatabaseTestResult test = await DatabaseConnector.TestAsync(settings);
    if (!test.Ok)
    {
        WriteResult(false, JsonNamingPolicy.CamelCase.ConvertName(test.Status.ToString()), test.Message);
        return 1;
    }
    await DatabaseConnector.EnsureDatabaseAsync(settings);
    DatabaseSettingsFile.Save(dataDir, settings, protector);
    WriteResult(true, "ok", $"已创建/确认数据库 {settings.Database}，数据表将在网关首次启动时自动创建。");
    return 0;
}
catch (Exception ex)
{
    WriteResult(false, "failed", ex.Message);
    return 1;
}

internal sealed record SetupRequest(DatabaseKind Kind, string? Host, int Port, string? Database, string? Username, string? Password);
