namespace IGW.DataMover;

/// <summary>数据迁移工具主窗口：选目标盘 → 迁移(停服务、复制核对、原位置建链接、启动服务) → 可选删除旧备份。</summary>
internal sealed class MainForm : Form
{
    private readonly string _dataDirectory;
    private readonly IGatewayService _service;
    private readonly Label _current = new() { AutoSize = true };
    private readonly Label _usage = new() { AutoSize = true, Text = "正在统计…" };
    private readonly TextBox _target = new() { Dock = DockStyle.Fill };
    private readonly Label _targetInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Button _browse = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _start = new() { Text = "开始迁移", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true, Enabled = false };
    private readonly Button _deleteBackup = new() { Text = "删除旧数据备份", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly Label _stage = new() { AutoSize = true };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private CancellationTokenSource? _running;
    private (long Files, long Bytes)? _measured;

    public MainForm(string dataDirectory, IGatewayService service)
    {
        _dataDirectory = dataDirectory;
        _service = service;
        Text = "IGW 数据迁移工具";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(820, 620);
        BuildLayout();
        _browse.Click += (_, _) => Browse();
        _target.TextChanged += (_, _) => UpdateTarget();
        _start.Click += async (_, _) => await StartMoveAsync();
        _cancel.Click += (_, _) => _running?.Cancel();
        _deleteBackup.Click += async (_, _) => await DeleteBackupsAsync();
        Shown += async (_, _) => await InspectAsync();
        FormClosing += (_, e) =>
        {
            if (_running is null) return;
            MessageBox.Show(this, "正在处理中，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 0, 0, 8),
            Text = "把网关的全部数据(工程、授权、日志、接口报文、MES 上报库、历史库等)整体搬到其它盘，" +
                   "原位置留一个目录链接：网关和以后的升级照常使用 C:\\ProgramData\\IGW，实际读写都在新盘上，C 盘不再增长。\r\n" +
                   "迁移期间网关服务会暂停，完成后自动启动；原数据改名保留，确认网关正常后再删除。",
        });

        var current = new GroupBox { Text = "当前数据目录", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var currentRows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        currentRows.Controls.Add(_current);
        currentRows.Controls.Add(_usage);
        current.Controls.Add(currentRows);
        root.Controls.Add(current);

        var target = new GroupBox { Text = "迁移到(选择其它盘上的一个空文件夹，例如 D:\\IGWData)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var targetRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        targetRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRows.Controls.Add(_target, 0, 0);
        targetRows.Controls.Add(_browse, 1, 0);
        targetRows.Controls.Add(_targetInfo, 0, 1);
        targetRows.SetColumnSpan(_targetInfo, 2);
        target.Controls.Add(targetRows);
        root.Controls.Add(target);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_deleteBackup);
        root.Controls.Add(buttons);
        root.Controls.Add(_progress);
        root.Controls.Add(_stage);
        root.Controls.Add(new Label { Text = "过程记录", AutoSize = true, Padding = new Padding(0, 8, 0, 2) });
        root.Controls.Add(_log);
        Controls.Add(root);
    }

    private async Task InspectAsync()
    {
        DataLocation location = DataDirectoryMover.Locate(_dataDirectory);
        _current.Text = location.IsLink
            ? $"{location.LinkPath}   →   实际位置：{location.RealPath}(之前已迁移过)"
            : location.LinkPath;
        RefreshBackups();
        if (!Directory.Exists(location.RealPath))
        {
            _usage.Text = "找不到数据目录，网关可能还没有安装或运行过。";
            return;
        }
        var progress = new Progress<DataMoveProgress>(p => _usage.Text = $"正在统计：{p.Files:N0} 个文件，{SizeText(p.Bytes)}…");
        try
        {
            _measured = await Task.Run(() => DataDirectoryMover.Measure(location.RealPath, progress, CancellationToken.None));
            _usage.Text = $"占用：{_measured.Value.Files:N0} 个文件，{SizeText(_measured.Value.Bytes)}";
        }
        catch (Exception error) { _usage.Text = "统计失败：" + error.Message; }
        UpdateTarget();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择迁移目标(其它盘上的空文件夹，可以新建)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _target.Text = dialog.SelectedPath;
    }

    private void UpdateTarget()
    {
        string path = _target.Text.Trim();
        if (path.Length == 0) { _targetInfo.Text = ""; _start.Enabled = false; return; }
        string? problem = DataDirectoryMover.ValidateTarget(DataDirectoryMover.Locate(_dataDirectory), path);
        string space = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (drive.IsReady)
            {
                space = $"目标盘可用空间 {SizeText(drive.AvailableFreeSpace)}";
                if (problem is null && _measured is { } used && drive.AvailableFreeSpace < used.Bytes + 512L * 1024 * 1024)
                    problem = $"目标盘空间不足：需要约 {SizeText(used.Bytes)}，可用 {SizeText(drive.AvailableFreeSpace)}。";
            }
            else problem ??= "目标盘不可用。";
        }
        catch (Exception) { problem ??= "路径无效。"; }
        _targetInfo.ForeColor = problem is null ? Color.DimGray : Color.Firebrick;
        _targetInfo.Text = problem ?? space;
        _start.Enabled = problem is null && _running is null;
    }

    private async Task StartMoveAsync()
    {
        string target = _target.Text.Trim();
        string estimate = _measured is { } used ? $"共 {used.Files:N0} 个文件、{SizeText(used.Bytes)}，" : "";
        if (MessageBox.Show(this,
                $"{estimate}将迁移到：\r\n{target}\r\n\r\n迁移期间网关服务会暂停(文件多时可能需要较长时间)，完成后自动启动。\r\n确定开始？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        _running = new CancellationTokenSource();
        SetBusy(true);
        var mover = new DataDirectoryMover(_service, message => BeginInvoke(() => Log(message)));
        var progress = new Progress<DataMoveProgress>(ShowProgress);
        try
        {
            DataMoveResult result = await Task.Run(() => mover.Move(_dataDirectory, target, progress, _running.Token));
            Log($"迁移完成：{result.Files:N0} 个文件、{SizeText(result.Bytes)} 已在 {result.Target}");
            Log($"C:\\ProgramData\\IGW 现在是指向新位置的目录链接；原数据保留在 {result.BackupPath}");
            _stage.Text = "迁移完成";
            MessageBox.Show(this,
                $"迁移完成，网关服务已启动。\r\n\r\n新位置：{result.Target}\r\n原数据备份：{result.BackupPath}\r\n\r\n" +
                "请打开网关网页确认工程、授权和日志都正常，再点「删除旧数据备份」释放 C 盘空间。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            Log("已取消，原数据目录未改动。");
            _stage.Text = "已取消";
        }
        catch (Exception error)
        {
            Log("迁移失败：" + error.Message);
            _stage.Text = "迁移失败";
            MessageBox.Show(this, "迁移失败：" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
            await InspectAsync();
        }
    }

    private async Task DeleteBackupsAsync()
    {
        string[] backups = FindBackups();
        if (backups.Length == 0) return;
        if (MessageBox.Show(this, "将永久删除以下迁移前的旧数据(不经过回收站)：\r\n\r\n" + string.Join("\r\n", backups) +
                "\r\n\r\n请先确认网关网页里工程、授权和日志都正常。确定删除？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _running = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<DataMoveProgress>(p => _stage.Text = $"正在删除旧数据：已删除 {p.Files:N0} 个文件");
        try
        {
            foreach (string backup in backups)
            {
                Log("删除 " + backup);
                await Task.Run(() => DataDirectoryMover.DeleteBackup(backup, progress, _running.Token));
            }
            _stage.Text = "旧数据已删除";
            Log("旧数据备份已删除。");
        }
        catch (OperationCanceledException) { Log("已停止删除，剩余的旧数据保留。"); }
        catch (Exception error) { Log("删除失败：" + error.Message); }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
            RefreshBackups();
        }
    }

    /// <summary>迁移留下的「IGW.迁移前-时间戳」备份(数据目录所在文件夹里，以及之前迁移到的位置旁边)。</summary>
    private string[] FindBackups()
    {
        var parents = new[] { Path.GetDirectoryName(Path.GetFullPath(_dataDirectory)) }
            .Concat([Path.GetDirectoryName(DataDirectoryMover.Locate(_dataDirectory).RealPath)])
            .Where(parent => parent is not null && Directory.Exists(parent))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return parents.SelectMany(parent => Directory.EnumerateDirectories(parent!, "*.迁移前-*")).ToArray();
    }

    private void RefreshBackups()
    {
        string[] backups = FindBackups();
        _deleteBackup.Enabled = backups.Length > 0 && _running is null;
        _deleteBackup.Text = backups.Length > 0 ? $"删除旧数据备份({backups.Length} 处)" : "删除旧数据备份";
    }

    private void ShowProgress(DataMoveProgress p)
    {
        if (p.TotalFiles > 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)Math.Clamp(p.Files * 1000 / p.TotalFiles, 0, 1000) / 10;
            _stage.Text = $"{p.Stage}：{p.Files:N0} / {p.TotalFiles:N0} 个文件，{SizeText(p.Bytes)} / {SizeText(p.TotalBytes)}";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _stage.Text = $"{p.Stage}：{p.Files:N0} 个文件，{SizeText(p.Bytes)}";
        }
    }

    private void SetBusy(bool busy)
    {
        _browse.Enabled = !busy;
        _target.Enabled = !busy;
        _cancel.Enabled = busy;
        _start.Enabled = false;
        _deleteBackup.Enabled = false;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!busy) { _progress.Value = 0; UpdateTarget(); RefreshBackups(); }
    }

    private void Log(string message) =>
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");

    private static string SizeText(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.00} GB"
        : $"{bytes / (double)(1L << 20):0.0} MB";
}
