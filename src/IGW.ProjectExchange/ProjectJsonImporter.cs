using System.Security.Cryptography;
using IGW.Abstractions;
using IGW.Core;
using IGW.ProjectExchange.Igw;

namespace IGW.ProjectExchange;

public sealed class ProjectJsonImporter : IProjectExchangeService, IAsyncDisposable
{
    private const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private readonly IProjectRepository _repository;
    private readonly ProjectImportSessionStore _sessions;
    private readonly ProjectJsonInspector _inspector;
    private readonly ProjectJsonDocumentReader _reader;
    private readonly ProjectJsonExporter _exporter;
    private readonly Func<IgwProject, ProjectRevision, CancellationToken, Task<ProjectRevision>>? _apply;

    public ProjectJsonImporter(
        IProjectRepository repository,
        IEnumerable<IKepDriverCodec> codecs,
        ProjectImportSessionStore sessions,
        Func<IgwProject, ProjectRevision, CancellationToken, Task<ProjectRevision>>? apply = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        IKepDriverCodec[] codecArray = codecs?.ToArray() ?? throw new ArgumentNullException(nameof(codecs));
        _inspector = new ProjectJsonInspector(codecArray, sessions, MaximumBytes);
        _reader = new ProjectJsonDocumentReader(codecArray);
        _exporter = new ProjectJsonExporter(repository, codecArray);
        _apply = apply;
    }

    public Task<ProjectImportPreview> InspectAsync(Stream input, CancellationToken cancellationToken) =>
        _inspector.InspectAsync(input, cancellationToken);

    public async Task<ProjectImportResult> ImportAsync(
        Stream input,
        ProjectImportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ProjectRevision current = await _repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryGet(options.Token, out ProjectImportSession session))
            return Failure(current, "import_session_not_found", KepCompatibilityReport.Compatible);

        string actualHash;
        try
        {
            actualHash = await ProjectImportHash.ComputeAsync(input, MaximumBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            return Failure(current, ErrorCode(error), session.Preview.Compatibility);
        }
        if (!FixedEquals(options.FileHash, session.FileHash) || !FixedEquals(actualHash, session.FileHash))
            return Failure(current, "import_file_changed", session.Preview.Compatibility);

        ProjectJsonReadResult parsed;
        try
        {
            parsed = await _reader.ReadAsync(
                session.FilePath, options.MigrationPassword, restoreSecrets: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CryptographicException error)
        {
            return Failure(current, ErrorCode(error), session.Preview.Compatibility);
        }
        catch (IgwExtensionMergeException error)
        {
            return Failure(current, error.Code, session.Preview.Compatibility);
        }
        catch (ProjectImportException error)
        {
            return Failure(current, error.Code, session.Preview.Compatibility);
        }
        catch (InvalidDataException error)
        {
            return Failure(current, ErrorCode(error), session.Preview.Compatibility);
        }

        RetiredProjectContent.RemoveOpenProtocolChannels(parsed.Project);
        try
        {
            ProjectRevision revision = _apply is null
                ? await _repository.ReplaceFromSnapshotAsync(
                    parsed.Project, current, cancellationToken).ConfigureAwait(false)
                : await _apply(parsed.Project, current, cancellationToken).ConfigureAwait(false);
            _sessions.Complete(session.Token);
            return new ProjectImportResult(true, revision, null, parsed.Compatibility);
        }
        catch (ProjectRevisionConflictException error)
        {
            return Failure(error.Current, "revision_conflict", parsed.Compatibility);
        }
    }

    public Task ExportAsync(
        Stream output,
        ProjectExportOptions options,
        CancellationToken cancellationToken) => _exporter.ExportAsync(output, options, cancellationToken);

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();

    private static ProjectImportResult Failure(
        ProjectRevision revision,
        string code,
        KepCompatibilityReport compatibility) =>
        new(false, revision, code, compatibility);

    private static string ErrorCode(Exception error)
    {
        string message = error.Message;
        if (message.Contains("portable_secret_authentication_failed", StringComparison.Ordinal))
            return "portable_secret_authentication_failed";
        if (message.Contains("migration_password_required", StringComparison.Ordinal))
            return "migration_password_required";
        int separator = message.IndexOfAny([':', ' ', '\r', '\n']);
        return separator > 0 ? message[..separator] : message;
    }

    private static bool FixedEquals(string left, string right)
    {
        byte[] leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? "");
        byte[] rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? "");
        try
        {
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
