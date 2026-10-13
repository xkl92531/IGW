using System.Security.Cryptography;
using IGW.Abstractions;

namespace IGW.ProjectExchange;

public sealed class ProjectJsonInspector
{
    private readonly ProjectImportSessionStore _sessions;
    private readonly ProjectJsonDocumentReader _reader;
    private readonly long _maximumBytes;

    public ProjectJsonInspector(
        IEnumerable<IKepDriverCodec> codecs,
        ProjectImportSessionStore sessions,
        long maximumBytes = 4L * 1024 * 1024 * 1024)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _reader = new ProjectJsonDocumentReader(codecs);
        _maximumBytes = maximumBytes;
    }

    public async Task<ProjectImportPreview> InspectAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        string path = Path.Combine(_sessions.DirectoryPath, $"{Guid.NewGuid():N}.json");
        try
        {
            string hash = await ProjectImportHash.CopyAndHashAsync(
                input, path, _maximumBytes, cancellationToken).ConfigureAwait(false);
            ProjectJsonReadResult parsed = await _reader.ReadAsync(
                path, null, restoreSecrets: false, cancellationToken).ConfigureAwait(false);
            var preview = new ProjectImportPreview(
                "", hash, parsed.Channels, parsed.Devices, parsed.Groups, parsed.Tags,
                parsed.MesTasks, parsed.HasPortableSecrets, parsed.Compatibility);
            return _sessions.Add(path, hash, preview).Preview;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }
}

internal static class ProjectImportHash
{
    public static async Task<string> CopyAndHashAsync(
        Stream input,
        string outputPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("project_import_file_too_large");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<string> ComputeAsync(Stream input, long maximumBytes, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("project_import_file_too_large");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
