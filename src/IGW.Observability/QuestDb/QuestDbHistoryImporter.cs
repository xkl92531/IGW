using System.Text;
using System.Text.Json;

namespace IGW.Observability;

/// <summary>Import legacy segments once. Checkpoints refer to fsynced spool batches; source files are retained.</summary>
public sealed class QuestDbHistoryImporter : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    public bool Completed { get; private set; }
    public string? Error { get; private set; }
    public int TotalFiles { get; private set; }
    public int ImportedFiles { get; private set; }
    public long ImportedRows { get; private set; }
    public long ExpiredRowsSkipped { get; private set; }
    public int FailedFiles { get; private set; }
    public QuestDbHistoryImporter(LogSegmentStore legacy, QuestDbLogStore store, string stateDirectory,
        IEnumerable<string>? previousStorageDirectories = null, string? currentStorageDirectory = null,
        int retentionDays = ObservabilitySettings.DefaultRetentionDays)
    {
        _task = Task.Run(async () =>
        {
            string checkpoint = Path.Combine(stateDirectory, "questdb-history.json");
            try
            {
                var done = File.Exists(checkpoint) ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(checkpoint, _stop.Token))! : [];
                await legacy.Recovery.WaitAsync(_stop.Token).ConfigureAwait(false);
                await store.InitializeSpoolAsync(_stop.Token).ConfigureAwait(false);
                string legacyRoot = legacy.Root;
                var sources = new List<(LogSegmentView Segment, string Root)>();
                sources.AddRange(legacy.Snapshot(long.MinValue, long.MaxValue).Select(segment => (segment, legacyRoot)));
                FailedFiles += legacy.RecoveryErrors.Count;
                Error = legacy.RecoveryErrors.FirstOrDefault();
                string[] previous = (previousStorageDirectories ?? []).Where(Path.IsPathFullyQualified).Distinct().ToArray();
                foreach (string location in previous)
                {
                    if (Path.GetFullPath(LogSegmentStore.RootFor(location)).Equals(Path.GetFullPath(legacyRoot), StringComparison.OrdinalIgnoreCase)) continue;
                    await using var older = new LogSegmentStore(location, 30, readOnly: true);
                    await older.Recovery.WaitAsync(_stop.Token);
                    sources.AddRange(older.Snapshot(long.MinValue, long.MaxValue).Select(segment => (segment, older.Root)));
                    FailedFiles += older.RecoveryErrors.Count;
                    Error ??= older.RecoveryErrors.FirstOrDefault();
                }
                var textDirectories = new[] { Path.Combine(stateDirectory, "logs"), currentStorageDirectory }
                    .Concat(previous).Where(path => path is not null && Path.IsPathFullyQualified(path)).Cast<string>().Distinct();
                string[] texts = textDirectories.SelectMany(path => LogResidueScanner.TextLogs(new DirectoryInfo(path)))
                    .Select(file => file.FullName).Distinct().ToArray();
                TotalFiles = sources.Count + texts.Length + FailedFiles;
                long cutoff = LogRecordCodec.ToMicros(DateTimeOffset.UtcNow.AddDays(-retentionDays));
                var batch = new StringBuilder();
                long batchBytes = 0;
                long targetBytes = Math.Min(2 * 1024 * 1024L, store.MaxHistoryBatchBytes);
                var pendingFiles = new List<string>();
                async Task FlushBatchAsync()
                {
                    if (batch.Length > 0)
                    {
                        try { await store.PersistHistoryAsync(batch.ToString(), _stop.Token); }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                        catch (Exception error) { throw new HistorySpoolException(error); }
                        batch.Clear(); batchBytes = 0;
                    }
                    if (pendingFiles.Count == 0) return;
                    foreach (string key in pendingFiles) done.Add(key);
                    // Only completed files whose bytes are already fsynced can enter the restart checkpoint.
                    PrivateFiles.AtomicWrite(checkpoint, JsonSerializer.SerializeToUtf8Bytes(done));
                    ImportedFiles += pendingFiles.Count; pendingFiles.Clear();
                }
                async Task ImportAsync(string key, IEnumerable<LogRecord> records, bool text = false)
                {
                    if (done.Contains(key)) { ImportedFiles++; return; }
                    long row = 0;
                    foreach (LogRecord record in records)
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        long position = row++;
                        if (record.TimestampMicros < cutoff) { ExpiredRowsSkipped++; continue; }
                        string identity = text ? Path.GetFileName(key) + "/" + QuestDbLogCodec.Raw(record) : key;
                        string line = QuestDbLogCodec.Encode(record, QuestDbLogCodec.HistoricalId(identity, position));
                        int lineBytes = Encoding.UTF8.GetByteCount(line);
                        if (lineBytes > store.MaxHistoryBatchBytes) throw new InvalidDataException("单条旧日志超过历史缓存容量，原文件已保留。");
                        if (batchBytes > 0 && batchBytes + lineBytes > targetBytes) await FlushBatchAsync();
                        batch.Append(line); batchBytes += lineBytes;
                        ImportedRows++;
                        if (batchBytes >= targetBytes) await FlushBatchAsync();
                    }
                    pendingFiles.Add(key);
                }
                IEnumerable<LogRecord> SegmentRecords(LogSegmentView segment)
                {
                    byte[] raw = LogSegmentStore.ReadRaw(segment, _stop.Token, requireExists: true);
                    if (raw.Length == 0 && segment.Index.Count > 0) throw new InvalidDataException("旧日志文件内容为空，未标记导入成功。");
                    using var reader = new StringReader(Encoding.UTF8.GetString(raw));
                    while (reader.ReadLine() is string line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        LogRecord? record = LogRecordCodec.Parse(Encoding.UTF8.GetBytes(line));
                        yield return record ?? throw new InvalidDataException("旧日志有损坏的记录，原文件已保留。");
                    }
                }
                void ReportFailure(string path, Exception error)
                {
                    FailedFiles++;
                    Error ??= $"{Path.GetFileName(path)}：{error.Message}（原文件保留，其余旧日志继续导入）";
                }
                foreach (var (segment, root) in sources.OrderBy(source => source.Segment.Index.FromMicros))
                {
                    string key = Path.ChangeExtension(Path.GetRelativePath(root, segment.Path), null).Replace('\\', '/');
                    try { await ImportAsync(key, SegmentRecords(segment)); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (HistorySpoolException) { throw; }
                    catch (Exception error) { ReportFailure(segment.Path, error); }
                }
                foreach (string file in texts.Order(StringComparer.Ordinal))
                {
                    try
                    {
                        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        await ImportAsync("text/" + file.Replace('\\', '/'), QuestDbLegacyText.Read(reader, _stop.Token), text: true);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (HistorySpoolException) { throw; }
                    catch (Exception error) { ReportFailure(file, error); }
                }
                await FlushBatchAsync();
                while (store.HistoryPending > 0) await Task.Delay(100, _stop.Token);
                if (store.HistoryRejected > 0) throw new InvalidDataException("部分历史日志被数据库拒绝，原文件仍保留，缓存中有 rejected 诊断批次。");
                Completed = FailedFiles == 0;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { Error = error.Message; }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _task.ConfigureAwait(false); _stop.Dispose();
    }
    private sealed class HistorySpoolException(Exception inner) : IOException("历史缓存写入失败，导入已停止，源文件与检查点保留。", inner);
}
