using System.Diagnostics;

namespace IGW.Observability;

/// <summary>Owns only the child it starts. No global Java process discovery/termination.</summary>
public sealed class QuestDbProcess : IAsyncDisposable
{
    private readonly string _runtimeDirectory;
    private readonly ObservabilitySettings _settings;
    private Process? _process;
    private FileStream? _ownership;
    private QuestDbWindowsJob? _job;
    private bool _ready;
    public bool Ready => _ready && Running;
    public string Root { get; private set; }
    public bool Running => _process is { HasExited: false };
    public string? LastError { get; private set; }
    public string? LastOutput { get; private set; }
    public static string RootFor(string storageDirectory) => Path.Combine(LogSegmentStore.RootFor(storageDirectory), "questdb");

    public QuestDbProcess(ObservabilitySettings settings, string? runtimeDirectory = null)
    {
        _settings = settings;
        _runtimeDirectory = runtimeDirectory ?? Path.Combine(AppContext.BaseDirectory, "questdb");
        Root = RootFor(settings.StorageDirectory);
    }

    public void Start(string? storageDirectory = null)
    {
        if (Running) return;
        if (storageDirectory is not null) Root = RootFor(storageDirectory);
        Directory.CreateDirectory(Root);
        _ownership ??= new FileStream(Path.Combine(Root, ".igw-owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(Path.Combine(Root, "conf"));
        string config = $"http.bind.to=127.0.0.1:{_settings.Port}\nhttp.user={_settings.Username}\nhttp.password={_settings.Password}\n" +
            "http.min.enabled=false\nline.tcp.enabled=false\npg.enabled=false\nhttp.net.connection.limit=12\n" +
            "shared.worker.count=2\nhttp.worker.count=2\nwal.apply.worker.count=2\ncairo.commit.mode=sync\n" +
            "query.timeout=8s\nline.http.max.recv.buffer.size=64M\nconfig.validation.strict=true\n" +
            "cairo.writer.data.append.page.size=1M\ncairo.wal.writer.data.append.page.size=1M\ncairo.symbol.table.append.page.size=64K\n" +
            "line.auto.create.new.tables=false\nline.auto.create.new.columns=false\nhttp.json.query.connection.check.frequency=100000\n";
        PrivateFiles.AtomicWrite(Path.Combine(Root, "conf", "server.conf"), System.Text.Encoding.UTF8.GetBytes(config));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(_runtimeDirectory, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _runtimeDirectory
        };
        start.Environment["JAVA_TOOL_OPTIONS"] = "-Xmx512m";
            foreach (string argument in new[] { "--enable-native-access=io.questdb", "--sun-misc-unsafe-memory-access=allow",
                "--add-opens=java.base/java.lang=io.questdb", "--add-opens=java.base/java.lang.reflect=io.questdb",
                "--add-opens=java.base/java.nio=io.questdb", "--add-opens=java.base/java.time.zone=io.questdb",
                "--add-exports=java.base/jdk.internal.vm=io.questdb", "-m", "io.questdb/io.questdb.ServerMain" }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(Root);
        try
        {
            _process?.Dispose(); _job?.Dispose(); _job = null;
            _process = Process.Start(start) ?? throw new IOException("QuestDB 未能启动。");
            if (OperatingSystem.IsWindows()) _job = new QuestDbWindowsJob(_process);
            _process.OutputDataReceived += (_, line) => { if (line.Data is { Length: > 0 } text) LastOutput = text.Length > 512 ? text[..512] : text; };
            _process.ErrorDataReceived += (_, line) => { if (line.Data is { Length: > 0 } text) LastOutput = text.Length > 512 ? text[..512] : text; };
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            LastError = null;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _ownership.Dispose(); _ownership = null;
            throw;
        }
    }

    public async Task EnsureReadyAsync(QuestDbClient client, int retentionDays, CancellationToken cancellationToken)
    {
        if (Ready) return;
        _ready = false;
        if (!Running) Start();
        await client.InitializeAsync(retentionDays, cancellationToken).ConfigureAwait(false);
        _ready = true;
    }

    public async Task StopAsync()
    {
        _ready = false;
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                // Matches official Windows service shutdown. Commit mode sync + WAL recovery is required.
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            _process.Dispose(); _process = null;
        }
        _ownership?.Dispose(); _ownership = null;
        _job?.Dispose(); _job = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
