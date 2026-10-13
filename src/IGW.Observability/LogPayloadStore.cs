using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IGW.Observability;

/// <summary>接口报文拆成“发送内容”和“返回内容”两部分，去掉请求行、Content-Type 等附加信息。</summary>
public sealed record LogPayloadExchange(
    string Id,
    string Request,
    string Response,
    bool RequestTruncated,
    bool ResponseTruncated,
    bool Split);

public sealed record LogPayloadCleanupResult(int Expired, int OverCapacity, long RemainingBytes);
public sealed record LogPayloadReference(string Id, string ContentType, long Length, string Path);
public sealed record LogPayloadPage(
    string Id,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalLength,
    bool HasMore,
    bool Truncated,
    string Content,
    string ContentType);

public sealed class LogPayloadStore
{
    public const int PageSize = 32_768;
    /// <summary>报文放在日志根目录下的这个子目录里，更换日志目录时随日志一起迁移。</summary>
    public const string DirectoryName = "payloads";
    private volatile string _directory;
    /// <summary>
    /// 还没迁完的旧目录：新报文只写当前目录，读取在当前目录找不到时依次回到这些目录找，
    /// 迁移期间在页面上点开旧报文不会报「不存在」。迁完即移除。
    /// </summary>
    private volatile string[] _fallbacks;
    private long _failed;
    private QuestDbPayloadStore? _database;
    internal void UseDatabase(QuestDbPayloadStore database) => _database = database;

    public LogPayloadStore(string directory, IEnumerable<string>? fallbackDirectories = null, bool createDirectory = true)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("载荷目录不能为空。", nameof(directory));
        _directory = Path.GetFullPath(directory);
        if (createDirectory) Directory.CreateDirectory(_directory);
        _fallbacks = (fallbackDirectories ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Path.GetFullPath)
            .Where(item => !SamePath(item, _directory))
            .Distinct(PathComparer)
            .ToArray();
    }

    /// <summary>某个日志存储目录对应的报文目录。</summary>
    public static string DirectoryFor(string storageDirectory) =>
        Path.Combine(LogSegmentStore.RootFor(storageDirectory), DirectoryName);

    public string CurrentDirectory => _directory;
    public IReadOnlyList<string> FallbackDirectories => _fallbacks;
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>改为写入新目录；原目录转为只读回退，直到 <see cref="AdoptFrom"/> 把它收编完。</summary>
    public void SwitchDirectory(string directory)
    {
        string target = Path.GetFullPath(directory);
        Directory.CreateDirectory(target);
        string previous = _directory;
        if (SamePath(previous, target)) return;
        _fallbacks = [previous, .. _fallbacks.Where(item => !SamePath(item, target))];
        _directory = target;
    }

    /// <summary>
    /// 把 <paramref name="source"/> 里当前目录还没有的报文收进当前目录，完成后不再回退到它。
    /// <paramref name="move"/> 为 true 时源文件随后删除(网关自己的旧目录)；false 时保留
    /// (用户选过的旧日志目录，和日志分段一样不替用户删)。文件修改时间原样保留——
    /// 保留期清理按它计算，搬一次不能让报文多活一个周期。
    /// </summary>
    public int AdoptFrom(string source, bool move, CancellationToken cancellationToken = default)
    {
        string from = Path.GetFullPath(source);
        if (SamePath(from, _directory) || !Directory.Exists(from)) { DropFallback(from); return 0; }
        Directory.CreateDirectory(_directory);
        var ids = Directory.EnumerateFiles(from, "*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetExtension(file) is ".payload" or ".json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsPayloadId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        int adopted = 0;
        bool failed = false;
        foreach (string id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fallbacks.Any(item => SamePath(item, from))) return adopted;   // 已被一键清理放弃：停止搬迁，目录交给清理
            try
            {
                // 一份报文的正文和元数据都进了新目录，才删旧目录里的；任何时刻至少有一处完整可读。
                bool copied = false;
                foreach (string name in new[] { id + ".payload", id + ".json" })
                    copied |= AdoptFile(Path.Combine(from, name), Path.Combine(_directory, name));
                if (move)
                {
                    TransientFileRetry.Run(() => File.Delete(Path.Combine(from, id + ".payload")));
                    TransientFileRetry.Run(() => File.Delete(Path.Combine(from, id + ".json")));
                }
                if (copied) adopted++;
            }
            catch (IOException) { failed = true; Interlocked.Increment(ref _failed); }
            catch (UnauthorizedAccessException) { failed = true; Interlocked.Increment(ref _failed); }
        }
        if (!failed) DropFallback(from);
        if (move)
            try { if (!Directory.EnumerateFileSystemEntries(from).Any()) TransientFileRetry.Run(() => Directory.Delete(from)); } catch { }
        return adopted;
    }

    /// <summary>
    /// 先复制到目标目录的临时名，再改名成正式文件：跨盘复制时读者不会看到写了一半的文件。
    /// 搬家也用复制而不是改名——源文件要等整份报文(正文 + 元数据)都到位后才删。
    /// </summary>
    private static bool AdoptFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return false;
        DateTime written = File.GetLastWriteTimeUtc(source);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".migration.tmp";
        try
        {
            File.Copy(source, temporary);
            TransientFileRetry.Run(() => File.SetLastWriteTimeUtc(temporary, written));
            TransientFileRetry.Run(() => File.Move(temporary, destination));
            return true;
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    /// <summary>
    /// 直接读报文正文(导出打包用)：不读元数据、不解码，找不到返回 null。
    /// 导出几十万份报文时，每份少开一个文件就能省下不少时间。
    /// </summary>
    public byte[]? TryReadRaw(string id)
    {
        if (!IsPayloadId(id)) return null;
        if (_database is not null)
        {
            try
            {
                QuestDbPayload? payload = _database.ReadAsync(id, default).GetAwaiter().GetResult();
                if (payload is not null) return Encoding.UTF8.GetBytes(payload.Content);
            }
            catch { Interlocked.Increment(ref _failed); return null; }
        }
        try { return File.ReadAllBytes(LocatePayload(id)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }   // 已清理、被占用：跳过这一份
    }

    public async Task<byte[]?> TryReadRawAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsPayloadId(id)) return null;
        try
        {
            if (_database is not null && await _database.ReadAsync(id, cancellationToken).ConfigureAwait(false) is QuestDbPayload payload)
                return Encoding.UTF8.GetBytes(payload.Content);
            return await File.ReadAllBytesAsync(LocatePayload(id), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal bool DatabaseBacked => _database is not null;

    /// <summary>
    /// 不再从这个目录读取、也不再搬迁它(一键清理要删除它时调用)。正在进行的 <see cref="AdoptFrom"/>
    /// 处理完当前这份报文后停下。
    /// </summary>
    public void AbandonFallback(string directory) => DropFallback(Path.GetFullPath(directory));

    private void DropFallback(string directory) =>
        _fallbacks = _fallbacks.Where(item => !SamePath(item, directory)).ToArray();

    public async Task<LogPayloadReference> SaveTextAsync(
        string contentType,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (_database is not null)
        {
            string body = Redact(content);
            string databaseId = _database.Save(contentType, body);
            return new LogPayloadReference(databaseId, contentType, Encoding.UTF8.GetByteCount(body), "");
        }
        string id = Guid.NewGuid().ToString("N");
        string payloadPath = PayloadPath(id);
        string metadataPath = MetadataPath(id);
        try
        {
            string redacted = Redact(content);
            byte[] bytes = Encoding.UTF8.GetBytes(redacted);
            PrivateFiles.AtomicWrite(payloadPath, bytes);
            var metadata = new PayloadMetadata(2, id, contentType, bytes.LongLength, DateTimeOffset.UtcNow, redacted.Length);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
            PrivateFiles.AtomicWrite(metadataPath, json);
            await Task.CompletedTask;
            return new LogPayloadReference(id, contentType, bytes.LongLength, payloadPath);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Interlocked.Increment(ref _failed);
            try { if (File.Exists(payloadPath)) File.Delete(payloadPath); } catch { }
            try { if (File.Exists(metadataPath)) File.Delete(metadataPath); } catch { }
            throw;
        }
    }

    public async Task<LogPayloadPage> ReadPageAsync(string id, int page, CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (page > int.MaxValue / PageSize) throw new ArgumentOutOfRangeException(nameof(page));
        if (_database is not null && await _database.ReadAsync(id, cancellationToken).ConfigureAwait(false) is QuestDbPayload payload)
        {
            int start = checked(page * PageSize);
            string content = start >= payload.Content.Length ? "" : payload.Content.Substring(start, Math.Min(PageSize, payload.Content.Length - start));
            return new LogPayloadPage(id, page, PageSize, Math.Max(1, (int)Math.Ceiling(payload.Content.Length / (double)PageSize)),
                payload.Content.Length, start + content.Length < payload.Content.Length, content.Length < payload.Content.Length, content, payload.ContentType);
        }
        PayloadMetadata metadata = await ReadMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        int totalLength = metadata.CharacterLength > 0 || metadata.Length == 0
            ? metadata.CharacterLength
            : await CountCharactersAsync(LocatePayload(id), cancellationToken).ConfigureAwait(false);
        int offset = checked(page * PageSize);
        string slice = offset >= totalLength
            ? ""
            : await ReadCharacterPageAsync(LocatePayload(id), offset, Math.Min(PageSize, totalLength - offset), cancellationToken)
                .ConfigureAwait(false);
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalLength / (double)PageSize));
        bool hasMore = offset + slice.Length < totalLength;
        return new LogPayloadPage(id, page, PageSize, totalPages, totalLength, hasMore,
            totalLength > slice.Length, slice, metadata.ContentType);
    }

    public const string RequestMarker = "===== 发送内容 =====";
    public const string ResponseMarker = "===== 返回内容 =====";
    private static readonly string[] RequestMarkers =
        [RequestMarker, "===== IGW MES 实际发送内容 =====", "===== 客户端 → IGW 实际请求 ====="];
    private static readonly string[] ResponseMarkers =
        [ResponseMarker, "===== MES 实际响应内容 =====", "===== IGW → 客户端实际响应 ====="];
    private static readonly Regex HeaderLine = new(
        @"^(?:(?:GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS) \S+.*|HTTP(?:/\d(?:\.\d)?)? \d{3}.*|Content-(?:Type|Length):.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>把报文按“发送内容 / 返回内容”拆开；兼容旧版本带请求行和 Content-Type 的格式。</summary>
    public async Task<LogPayloadExchange> ReadExchangeAsync(string id, int maxCharsPerPart, CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (maxCharsPerPart < 1) throw new ArgumentOutOfRangeException(nameof(maxCharsPerPart));
        if (_database is not null && await _database.ReadAsync(id, cancellationToken).ConfigureAwait(false) is QuestDbPayload payload)
            return SplitExchange(id, payload.Content, maxCharsPerPart);
        await ReadMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        int limit = checked(maxCharsPerPart * 2 + 4096);
        string text = await ReadCharacterPageAsync(LocatePayload(id), 0, limit, cancellationToken).ConfigureAwait(false);
        return SplitExchange(id, text, maxCharsPerPart);
    }

    internal static LogPayloadExchange SplitExchange(string id, string text, int maxCharsPerPart)
    {
        text = text.Replace("\r\n", "\n");
        (int requestAt, int requestLength) = FindMarker(text, RequestMarkers, 0);
        (int responseAt, int responseLength) = FindMarker(text, ResponseMarkers, Math.Max(0, requestAt));
        if (requestAt < 0 || responseAt < 0)
        {
            (string whole, bool cut) = Limit(text.Trim(), maxCharsPerPart);
            return new LogPayloadExchange(id, whole, "", cut, false, Split: false);
        }
        (string request, bool requestCut) = Limit(Clean(text[(requestAt + requestLength)..responseAt]), maxCharsPerPart);
        (string response, bool responseCut) = Limit(Clean(text[(responseAt + responseLength)..]), maxCharsPerPart);
        return new LogPayloadExchange(id, request, response, requestCut, responseCut, Split: true);
    }

    private static (int At, int Length) FindMarker(string text, string[] markers, int start)
    {
        foreach (string marker in markers)
        {
            int at = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (at >= 0) return (at, marker.Length);
        }
        return (-1, 0);
    }

    /// <summary>去掉段首的请求行、状态行、Content-Type/Length 和空行，只留正文。</summary>
    private static string Clean(string section)
    {
        string[] lines = section.Trim('\n').Split('\n');
        int first = 0;
        while (first < lines.Length && (lines[first].Trim().Length == 0 || HeaderLine.IsMatch(lines[first].Trim()))) first++;
        return string.Join('\n', lines[first..]).Trim();
    }

    private static (string Text, bool Truncated) Limit(string text, int max) =>
        text.Length > max ? (text[..max], true) : (text, false);

    public async Task<(Stream Stream, string ContentType, string FileName)> OpenReadAsync(
        string id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (_database is not null && await _database.ReadAsync(id, cancellationToken).ConfigureAwait(false) is QuestDbPayload payload)
            return (new MemoryStream(Encoding.UTF8.GetBytes(payload.Content), writable: false), payload.ContentType, $"igw-payload-{id}.txt");
        PayloadMetadata metadata = await ReadMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        Stream stream = new FileStream(
            LocatePayload(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (stream, metadata.ContentType, $"igw-payload-{id}.txt");
    }

    public void CleanupExpired(TimeSpan retention, DateTimeOffset? now = null) =>
        Cleanup(retention, long.MaxValue, now);

    /// <summary>
    /// 删除超过保留期的载荷；剩余总大小超过 <paramref name="maxBytes"/> 时再从最旧的开始删，
    /// 直到降到上限的 80%。只看文件时间和大小，不逐个解析元数据——现场一个月可达数十万个文件。
    /// </summary>
    public LogPayloadCleanupResult Cleanup(TimeSpan retention, long maxBytes, DateTimeOffset? now = null)
    {
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        DateTime threshold = ((now ?? DateTimeOffset.UtcNow) - retention).UtcDateTime;
        var directory = new DirectoryInfo(_directory);
        var kept = new List<(string Id, DateTime Written, long Bytes)>();
        var sizes = new Dictionary<string, (DateTime Written, long Bytes)>(StringComparer.Ordinal);
        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (file.Extension is not (".payload" or ".json")) continue;
            string id = Path.GetFileNameWithoutExtension(file.Name);
            sizes[id] = sizes.TryGetValue(id, out var seen)
                ? (seen.Written < file.LastWriteTimeUtc ? seen.Written : file.LastWriteTimeUtc, seen.Bytes + file.Length)
                : (file.LastWriteTimeUtc, file.Length);
        }

        int expired = 0, overflow = 0;
        long total = 0;
        foreach (var (id, entry) in sizes)
        {
            if (entry.Written < threshold)
            {
                if (Delete(id)) expired++;
                continue;
            }
            kept.Add((id, entry.Written, entry.Bytes));
            total += entry.Bytes;
        }

        if (total > maxBytes)
        {
            long target = maxBytes / 10 * 8;
            foreach (var item in kept.OrderBy(item => item.Written))
            {
                if (total <= target) break;
                if (!Delete(item.Id)) continue;
                total -= item.Bytes;
                overflow++;
            }
        }
        return new LogPayloadCleanupResult(expired, overflow, total);
    }

    private bool Delete(string id)
    {
        try
        {
            File.Delete(MetadataPath(id));
            File.Delete(PayloadPath(id));
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _failed);
            return false;
        }
    }

    private async Task<PayloadMetadata> ReadMetadataAsync(string id, CancellationToken cancellationToken)
    {
        byte[] json = await File.ReadAllBytesAsync(Locate(id, ".json"), cancellationToken).ConfigureAwait(false);
        PayloadMetadata metadata = JsonSerializer.Deserialize<PayloadMetadata>(json)
            ?? throw new InvalidDataException("日志载荷元数据为空。");
        if (metadata.Version is not (1 or 2) || !metadata.Id.Equals(id, StringComparison.Ordinal))
            throw new InvalidDataException("日志载荷元数据无效。");
        return metadata;
    }

    private static async Task<string> ReadCharacterPageAsync(
        string path,
        int offset,
        int length,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] skip = new char[Math.Min(4096, Math.Max(1, offset))];
        int remaining = offset;
        while (remaining > 0)
        {
            int read = await reader.ReadBlockAsync(skip.AsMemory(0, Math.Min(skip.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return "";
            remaining -= read;
        }
        char[] page = new char[length];
        int count = await reader.ReadBlockAsync(page.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new string(page, 0, count);
    }

    private static async Task<int> CountCharactersAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[8192];
        int total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            total = checked(total + read);
        return total;
    }

    private string PayloadPath(string id) => Path.Combine(_directory, id + ".payload");
    private string MetadataPath(string id) => Path.Combine(_directory, id + ".json");

    /// <summary>
    /// 报文文件所在位置：当前目录优先，没有再看迁移中的旧目录；都没有按当前目录处理(照常报不存在)。
    /// 正文和元数据分开找——迁移是一个文件一个文件搬的，同一份报文可能一半在新目录一半在旧目录。
    /// </summary>
    private string Locate(string id, string extension)
    {
        string current = Path.Combine(_directory, id + extension);
        if (File.Exists(current)) return current;
        foreach (string fallback in _fallbacks)
        {
            string candidate = Path.Combine(fallback, id + extension);
            if (File.Exists(candidate)) return candidate;
        }
        return current;
    }

    private string LocatePayload(string id) => Locate(id, ".payload");

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool SamePath(string left, string right) =>
        PathComparer.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right));

    private static void ValidateId(string id)
    {
        if (!IsPayloadId(id))
            throw new ArgumentException("日志载荷 ID 无效。", nameof(id));
    }

    private static bool IsPayloadId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _);

    private static string Redact(string content)
    {
        string value = Regex.Replace(content, "(?im)^(authorization|proxy-authorization|cookie)\\s*:\\s*.*$", "$1: ***");
        value = Regex.Replace(value,
            "(?i)(\"(?:password|passwd|token|secret|api[_-]?key)\"\\s*:\\s*\")((?:\\\\.|[^\"\\\\])*)(\")",
            "$1***$3");
        value = Regex.Replace(value,
            "(?i)(\\b(?:password|passwd|token|secret|api[_-]?key)\\b\\s*[:=]\\s*)([^&\\s;,\"'}]+)",
            "$1***");
        value = Regex.Replace(value,
            "(?i)([?&](?:access[_-]?token|refresh[_-]?token|id[_-]?token|password|passwd|secret|api[_-]?key)=)([^&#\\s]*)",
            "$1***");
        value = Regex.Replace(value, "(?i)(://[^:/\\s]+:)[^@/\\s]+@", "$1***@");
        return value;
    }

    private sealed record PayloadMetadata(
        int Version,
        string Id,
        string ContentType,
        long Length,
        DateTimeOffset CreatedAt,
        int CharacterLength = 0);
}
