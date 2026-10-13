using IGW.DataMover;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 数据迁移工具：把整个网关数据目录搬到其它盘，原位置留目录链接。
/// 现场 C 盘被日志、报文、MES 上报库撑满，而网关和安装包都固定使用 C:\ProgramData\IGW。
/// 搬走后程序照旧认原路径，实际读写落在新盘上；原数据改名保留，确认无误后再删。
/// </summary>
public sealed class DataDirectoryMoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igw-mover", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Moving_copies_everything_keeps_timestamps_and_leaves_a_link_at_the_old_path()
    {
        string data = Seed(Path.Combine(_root, "C", "IGW"));
        string target = Path.Combine(_root, "D", "IGWData");
        var service = new FakeService { Exists = true, IsRunning = true };
        var log = new List<string>();

        DataMoveResult result = new DataDirectoryMover(service, log.Add).Move(data, target, null, CancellationToken.None);

        DataLocation location = DataDirectoryMover.Locate(data);
        Assert.True(location.IsLink);
        Assert.Equal(Path.GetFullPath(target), location.RealPath, ignoreCase: true);
        Assert.Equal("工程", File.ReadAllText(Path.Combine(data, "project.db")));            // 经由链接读到新位置
        Assert.Equal("报文", File.ReadAllText(Path.Combine(target, "log-payloads", "a.payload")));
        Assert.Equal(Written, File.GetLastWriteTimeUtc(Path.Combine(target, "log-payloads", "a.payload")));
        Assert.Equal(4, result.Files);
        Assert.True(File.Exists(Path.Combine(result.BackupPath, "project.db")), "原数据改名保留");
        Assert.Equal(["stop", "start"], service.Calls);

        File.WriteAllText(Path.Combine(data, "new.txt"), "新写入");                          // 网关之后写的内容落在新盘
        Assert.True(File.Exists(Path.Combine(target, "new.txt")));
    }

    [Fact]
    public void A_data_directory_that_was_already_moved_can_be_moved_again()
    {
        string data = Seed(Path.Combine(_root, "C", "IGW"));
        var service = new FakeService();
        new DataDirectoryMover(service).Move(data, Path.Combine(_root, "D", "IGWData"), null, CancellationToken.None);

        DataMoveResult second = new DataDirectoryMover(service).Move(data, Path.Combine(_root, "E", "IGWData"), null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "E", "IGWData")), DataDirectoryMover.Locate(data).RealPath, ignoreCase: true);
        Assert.Equal("工程", File.ReadAllText(Path.Combine(data, "project.db")));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "D", "IGWData")), second.BackupPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("not-empty")]
    [InlineData("same")]
    public void An_invalid_target_is_rejected_before_anything_is_touched(string kind)
    {
        string data = Seed(Path.Combine(_root, "C", "IGW"));
        string target = kind switch
        {
            "inside" => Path.Combine(data, "sub"),
            "same" => data,
            _ => Path.Combine(_root, "D", "Busy"),
        };
        if (kind == "not-empty") { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "x.txt"), "x"); }
        var service = new FakeService { Exists = true, IsRunning = true };

        Assert.Throws<InvalidOperationException>(() => new DataDirectoryMover(service).Move(data, target, null, CancellationToken.None));

        Assert.False(DataDirectoryMover.Locate(data).IsLink);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public void Deleting_the_backup_removes_the_old_copy()
    {
        string data = Seed(Path.Combine(_root, "C", "IGW"));
        DataMoveResult result = new DataDirectoryMover(new FakeService()).Move(data, Path.Combine(_root, "D", "IGWData"), null, CancellationToken.None);

        DataDirectoryMover.DeleteBackup(result.BackupPath, null, CancellationToken.None);

        Assert.False(Directory.Exists(result.BackupPath));
        Assert.Equal("工程", File.ReadAllText(Path.Combine(data, "project.db")));
    }

    private static readonly DateTime Written = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string Seed(string data)
    {
        Directory.CreateDirectory(Path.Combine(data, "log-payloads"));
        Directory.CreateDirectory(Path.Combine(data, "pki", "own"));
        File.WriteAllText(Path.Combine(data, "project.db"), "工程");
        File.WriteAllText(Path.Combine(data, "license.key"), "授权");
        File.WriteAllText(Path.Combine(data, "log-payloads", "a.payload"), "报文");
        File.SetLastWriteTimeUtc(Path.Combine(data, "log-payloads", "a.payload"), Written);
        File.WriteAllText(Path.Combine(data, "pki", "own", "cert.der"), "证书");
        return Path.GetFullPath(data);
    }

    private sealed class FakeService : IGatewayService
    {
        public bool Exists { get; init; }
        public bool IsRunning { get; set; }
        public List<string> Calls { get; } = [];
        public void Stop(TimeSpan timeout) { Calls.Add("stop"); IsRunning = false; }
        public void Start(TimeSpan timeout) { Calls.Add("start"); IsRunning = true; }
    }

    public void Dispose()
    {
        try
        {
            // 先拆链接，避免递归删除时经由链接删到别处
            foreach (string link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(path => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray())
                Directory.Delete(link);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
