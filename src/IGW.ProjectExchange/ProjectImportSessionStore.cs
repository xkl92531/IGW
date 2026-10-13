using System.Collections.Concurrent;
using IGW.Abstractions;

namespace IGW.ProjectExchange;

public sealed class ProjectImportSessionStore : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ProjectImportSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;

    public string DirectoryPath { get; }

    public ProjectImportSessionStore(string directoryPath, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(DirectoryPath);
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    }

    internal ProjectImportSession Add(
        string filePath,
        string fileHash,
        ProjectImportPreview preview)
    {
        CleanupExpired();
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .ToLowerInvariant();
        ProjectImportSession session = new(
            token, fileHash, filePath, preview with { Token = token, FileHash = fileHash },
            DateTimeOffset.UtcNow.Add(_lifetime));
        if (!_sessions.TryAdd(token, session))
            throw new InvalidOperationException("import_session_token_collision");
        return session;
    }

    internal bool TryGet(string token, out ProjectImportSession session)
    {
        CleanupExpired();
        if (_sessions.TryGetValue(token, out ProjectImportSession? current)
            && current.ExpiresAt > DateTimeOffset.UtcNow)
        {
            session = current;
            return true;
        }
        session = null!;
        return false;
    }

    internal void Complete(string token)
    {
        if (_sessions.TryRemove(token, out ProjectImportSession? session)) Delete(session.FilePath);
    }

    private void CleanupExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string token, ProjectImportSession session) in _sessions)
            if (session.ExpiresAt <= now && _sessions.TryRemove(token, out ProjectImportSession? removed))
                Delete(removed.FilePath);
    }

    public ValueTask DisposeAsync()
    {
        foreach ((string token, _) in _sessions)
            Complete(token);
        return ValueTask.CompletedTask;
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 下次启动的临时目录清理会再次处理。 */ }
    }
}

internal sealed record ProjectImportSession(
    string Token,
    string FileHash,
    string FilePath,
    ProjectImportPreview Preview,
    DateTimeOffset ExpiresAt);
