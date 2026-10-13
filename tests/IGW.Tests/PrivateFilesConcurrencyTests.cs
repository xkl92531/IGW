using System.Text;
using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 日志目录迁移每复制一个文件就覆盖写一次进度文件，网页同时在轮询读取它。
/// Windows 上目标文件正被打开时改名覆盖会“拒绝访问”，不能因此让整次迁移失败。
/// </summary>
public sealed class PrivateFilesConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igw-private-files", Guid.NewGuid().ToString("N"));

    public PrivateFilesConcurrencyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Overwrite_waits_out_a_status_read_or_antivirus_scan_holding_the_file()
    {
        string path = Path.Combine(_root, "state.json");
        PrivateFiles.AtomicWrite(path, "{\"v\":1}"u8);
        var scanner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () => { await Task.Delay(150); scanner.Dispose(); });

        PrivateFiles.AtomicWrite(path, "{\"v\":2}"u8);

        await release;
        Assert.Equal("{\"v\":2}", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
