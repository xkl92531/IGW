namespace IGW.Observability;

/// <summary>日志中心后端：托管 QuestDB 与旧分段兼容模式的查询、状态、保留期和存储迁移。</summary>
public sealed class LogCenterBackend
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private ObservabilitySettings _settings;
    private Task _storageChange = Task.CompletedTask;
    private bool _closing;
    internal Task LegacyPayloadMigration { get; set; } = Task.CompletedTask;
    public async Task FinishStorageChangeAsync()
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try { _closing = true; }
        finally { _settingsGate.Release(); }
        await _storageChange.ConfigureAwait(false);
    }

    public LogCenterBackend(
        string dataDirectory,
        ObservabilitySettings settings,
        LogSegmentStore store,
        LogPayloadStore payloads,
        QuestDbDatabase? database = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("观测配置目录必须是绝对路径。", nameof(dataDirectory));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(payloads);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _settings = settings;
        Store = store;
        Payloads = payloads;
        Database = database;
        Cleanup = new LogResidueCleaner(_dataDirectory, () => Settings, payloads, blockedReason: () =>
            !_storageChange.IsCompleted ? "日志目录正在迁移，旧目录暂时不能清理。"
            : Database is not null && !Database.History.Completed ? "旧日志尚未导入完成，源文件暂时不能清理。"
            : !LegacyPayloadMigration.IsCompleted ? "旧接口报文正在迁移，源目录暂时不能清理。" : null);
        Cleanup.ResumePending();   // 上次一键清理没删完(网关中途重启)的，接着删
    }

    /// <summary>一键清理不再写入的日志残留(旧版报文、换目录前的旧日志等)，后台进行，不停服务。</summary>
    public LogResidueCleaner Cleanup { get; }

    public LogSegmentStore Store { get; }
    public LogPayloadStore Payloads { get; }
    public QuestDbDatabase? Database { get; }
    public ObservabilitySettings Settings => RefreshSettings();
    public string DefaultStorageDirectory => Path.Combine(_dataDirectory, "observability");

    public async Task<LogCenterResponse> QueryAsync(LogCenterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LogFilter.Create(request);   // 参数错误立即返回，不占用线程池
        if (Database is not null)
        {
            LogCenterResponse response = await Database.Query.QueryAsync(request, cancellationToken).ConfigureAwait(false);
            return response with { Partial = response.Partial || !Database.Process.Ready || Database.Suspended };
        }
        // 请求被放弃(浏览器超时、页面刷新/关闭)时扫描随之停止，不让自动刷新的查询在后台越堆越多
        return await Task.Run(() => LogQueryEngine.Query(Store, request, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按时间正序输出匹配的日志，供打包导出。</summary>
    public IAsyncEnumerable<LogCenterEntry> StreamAsync(LogCenterRequest request, CancellationToken cancellationToken)
    {
        if (Database is not null && (!Database.History.Completed || !Database.Process.Ready || Database.Suspended))
            throw new InvalidOperationException("日志数据库或历史导入尚未就绪，请稍后导出。");
        return Database is null ? LogQueryEngine.StreamAscending(Store, request, cancellationToken) : Database.Query.StreamAsync(request, cancellationToken);
    }

    public async Task<LogCenterStatus> StatusAsync(CancellationToken cancellationToken)
    {
        ObservabilitySettings settings = RefreshSettings();
        LogStorageChange? change = LogStorageChange.Load(_dataDirectory);
        bool healthy = Store.Pending < 100_000;
        if (Database is not null)
        {
            if (_storageChange.IsCompleted) await Database.CheckWalAsync(cancellationToken).ConfigureAwait(false);
            bool backgroundImport = !Database.History.Completed && Database.History.Error is null;
            int livePending = Math.Max(0, Database.Store.Pending - Database.Store.HistoryPending);
            healthy = _storageChange.IsCompleted && Database.Process.Ready && !Database.Suspended
                && (Database.WalLag < 10 || backgroundImport) && livePending < 100_000;
        }
        return new LogCenterStatus(
            healthy,
            healthy ? "ready" : "degraded",
            Database is null ? 0 : settings.Port,
            settings.RetentionDays,
            settings.StorageDirectory,
            Database is null ? new TelemetrySpoolStatus(Store.Pending, Store.WriteFailures, Store.Dropped)
                : new TelemetrySpoolStatus(Database.Store.Pending, Database.Store.WriteFailures, Database.Store.Dropped),
            change?.Status,
            change?.TotalBytes ?? 0,
            change?.CopiedBytes ?? 0,
            change?.FailureReason,
            Store.TotalBytes() + (Database?.StorageBytes() ?? 0))
        {
            Engine = Database is null ? "segments" : "questdb",
            HistoryImported = Database?.History.Completed ?? true,
            HistoryError = Database?.History.Error,
            WalPendingTransactions = Database?.WalLag ?? 0,
            DatabaseError = healthy ? null : Database?.Store.LastError ?? Database?.Process.LastError,
            HistoryTotalFiles = Database?.History.TotalFiles ?? 0,
            HistoryImportedFiles = Database?.History.ImportedFiles ?? 0,
            HistoryImportedRows = Database?.History.ImportedRows ?? 0,
            HistoryFailedFiles = Database?.History.FailedFiles ?? 0
        };
    }

    public async Task<ObservabilitySettings> UpdateRetentionAsync(int retentionDays, CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObservabilitySettings previous = RefreshSettings();
            ObservabilitySettings updated = previous.WithRetentionDays(retentionDays);
            // Persist first: an unwritable settings file must not shorten DB retention before returning failure.
            updated.Persist(_dataDirectory);
            try
            {
                if (Database is not null) await Database.Client.SetRetentionAsync(updated.RetentionDays, cancellationToken, previous.RetentionDays).ConfigureAwait(false);
            }
            catch { previous.Persist(_dataDirectory); _settings = previous; throw; }
            _settings = updated;
            Store.RetentionDays = updated.RetentionDays;   // 立即生效，不再需要重启日志服务
            return updated;
        }
        finally { _settingsGate.Release(); }
    }

    /// <summary>更换日志目录：后台复制已有日志后切换，期间日志照常写入和查询；旧目录保留不删。</summary>
    public async Task<LogStorageChange> BeginStorageChangeAsync(
        string storageDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory) || !Path.IsPathFullyQualified(storageDirectory))
            throw new ArgumentException("日志存储目录必须是绝对路径。", nameof(storageDirectory));
        string target = Path.GetFullPath(storageDirectory);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing) throw new InvalidOperationException("日志中心正在关闭。");
            if (!_storageChange.IsCompleted)
                throw new InvalidOperationException("已有日志目录迁移正在进行。");
            if (Cleanup.Status.State == LogCleanupState.Running) throw new InvalidOperationException("旧日志正在清理，请清理完成后再切换目录。");
            Directory.CreateDirectory(target);
            string probePath = Path.Combine(target, ".igw-observability-write-probe-" + Guid.NewGuid().ToString("N"));
            using (var probe = new FileStream(
                probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                probe.WriteByte(0);
                probe.Flush(flushToDisk: true);
            }
            ObservabilitySettings current = RefreshSettings();
            if (Database is not null && Directory.Exists(QuestDbProcess.RootFor(target)))
                throw new InvalidOperationException("目标目录已有 QuestDB 数据，请选择空的新日志目录。");
            LogStorageChange change = LogStorageChange.Begin(_dataDirectory,
                LogSegmentStore.RootFor(current.StorageDirectory), LogSegmentStore.RootFor(target));
            _storageChange = Task.Run(() => ApplyStorageChangeAsync(change, target));
            return change;
        }
        finally { _settingsGate.Release(); }
    }

    private async Task ApplyStorageChangeAsync(LogStorageChange change, string target)
    {
        if (Database is not null) { await ApplyDatabaseStorageChangeAsync(change, target).ConfigureAwait(false); return; }
        try
        {
            await Store.SwitchDirectoryAsync(target, change, CancellationToken.None).ConfigureAwait(false);
            // 报文在日志根目录的 payloads 子目录里，上面两遍复制已经带过去了；这里切换写入位置，
            // 再补上复制期间新写的报文。旧目录和日志分段一样保留不删。
            string previousPayloads = Path.Combine(change.SourceDirectory, LogPayloadStore.DirectoryName);
            Payloads.SwitchDirectory(LogPayloadStore.DirectoryFor(target));
            Payloads.AdoptFrom(previousPayloads, move: false);
            await _settingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObservabilitySettings updated = RefreshSettings().WithStorageDirectory(target);
                updated.Persist(_dataDirectory);
                _settings = updated;
            }
            finally { _settingsGate.Release(); }
            change.Complete();
        }
        catch (Exception error) { change.Fail(error.Message); }
    }

    private async Task ApplyDatabaseStorageChangeAsync(LogStorageChange change, string target)
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObservabilitySettings current = RefreshSettings();
            await Database!.Store.DatabaseGate.WaitAsync().ConfigureAwait(false);
            using IDisposable pause = await Database.Client.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            bool settingsChanged = false;
            try
            {
                await LegacyPayloadMigration.ConfigureAwait(false);
                await Database.Process.StopAsync().ConfigureAwait(false);
                await Store.SwitchDirectoryAsync(target, change, CancellationToken.None).ConfigureAwait(false);
                ObservabilitySettings updated = current.WithStorageDirectory(target);
                updated.Persist(_dataDirectory);
                settingsChanged = true;
                _settings = updated;
                Database.Process.Start(target);
                await VerifyDatabaseAsync(updated).ConfigureAwait(false);
                Payloads.SwitchDirectory(LogPayloadStore.DirectoryFor(target));
                Payloads.AdoptFrom(Path.Combine(change.SourceDirectory, LogPayloadStore.DirectoryName), move: false);
                change.Complete();
            }
            catch (Exception error)
            {
                try
                {
                    await Database.Process.StopAsync().ConfigureAwait(false);
                    if (settingsChanged) current.Persist(_dataDirectory);
                    _settings = current;
                    Store.UseExistingDirectory(current.StorageDirectory);
                    Database.Process.Start(current.StorageDirectory);
                    await VerifyDatabaseAsync(current).ConfigureAwait(false);
                }
                catch (Exception rollback) { error = new IOException(error.Message + "; 原目录恢复失败：" + rollback.Message); }
                change.Fail(error.Message);
            }
            finally { Database.Store.DatabaseGate.Release(); }
        }
        finally { _settingsGate.Release(); }
    }

    private async Task VerifyDatabaseAsync(ObservabilitySettings settings)
    {
        // Independent client bypasses the migration pause; normal query/write requests stay parked.
        using var verification = new QuestDbClient(new Uri($"http://127.0.0.1:{settings.Port}"), settings.Username, settings.Password);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { await Database!.Process.EnsureReadyAsync(verification, settings.RetentionDays, timeout.Token).ConfigureAwait(false); return; }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !timeout.IsCancellationRequested)
            { await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
        }
    }

    private ObservabilitySettings RefreshSettings()
    {
        ObservabilitySettings? loaded = ObservabilitySettings.Load(_dataDirectory);
        if (loaded is not null) _settings = loaded;
        return _settings;
    }
}
