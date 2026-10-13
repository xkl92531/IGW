using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.Simulator.Kep;
using IGW.Persistence.Sqlite;
using IGW.ProjectExchange;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectJsonStreamingExportTests
{
    [Fact]
    public async Task Export_is_snapshot_consistent_bounded_and_never_loads_runtime_project()
    {
        string directory = Path.Combine(Path.GetTempPath(), "igw-stream-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inner = new SqliteProjectRepository(
            new ProjectDatabase(Path.Combine(directory, "project.db")),
            new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
        try
        {
            await inner.InitializeAsync(default);
            await inner.ReplaceFromSnapshotAsync(
                ProjectStorageSmokePerformanceTests.BuildProject(25_000), new ProjectRevision(0), default);
            var repository = new NoRuntimeLoadRepository(inner);
            var exporter = new ProjectJsonExporter(repository, [new SimulatorKepDriverCodec()]);
            using var bytes = new MemoryStream();
            await using var bounded = new MaximumWriteStream(bytes, 128 * 1024);

            await exporter.ExportAsync(bounded, new ProjectExportOptions(false, null), default);

            Assert.Equal(0, repository.RuntimeLoadCalls);
            Assert.InRange(bounded.MaximumObservedWrite, 1, 128 * 1024);
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            Assert.True(document.RootElement.TryGetProperty("project", out JsonElement project));
            Assert.True(document.RootElement.TryGetProperty("igw", out JsonElement igw));
            Assert.Equal(25_000, project.GetProperty("channels")[0].GetProperty("devices")[0]
                .GetProperty("tag_groups")[0].GetProperty("tags").GetArrayLength());
            Assert.Equal(25_000, igw.GetProperty("entities").GetProperty("tags").GetArrayLength());
            Assert.Equal(1, igw.GetProperty("revision").GetInt64());
        }
        finally
        {
            await inner.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoRuntimeLoadRepository(IProjectRepository inner) : IProjectRepository
    {
        public int RuntimeLoadCalls { get; private set; }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<ProjectRevision> GetRevisionAsync(CancellationToken ct) => inner.GetRevisionAsync(ct);
        public Task<ProjectTreeNode> GetRootAsync(CancellationToken ct) => inner.GetRootAsync(ct);
        public Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(ProjectNodeRef parent, CancellationToken ct) => inner.GetChildrenAsync(parent, ct);
        public Task<ProjectPage<ProjectTagRow>> GetTagsAsync(ProjectTagQuery query, CancellationToken ct) => inner.GetTagsAsync(query, ct);
        public Task<ProjectPage<ProjectMesTaskRow>> GetMesTasksAsync(ProjectMesTaskQuery query, CancellationToken ct) => inner.GetMesTasksAsync(query, ct);
        public Task<ProjectPage<ProjectNodeSearchRow>> SearchNodesAsync(ProjectNodeSearchQuery query, CancellationToken ct) => inner.SearchNodesAsync(query, ct);
        public Task<string> GetEntityDocumentAsync(ProjectNodeRef entity, CancellationToken ct) => inner.GetEntityDocumentAsync(entity, ct);
        public Task<ProjectMutationResult> ExecuteAsync(ProjectMutation mutation, ProjectRevision expected, CancellationToken ct) => inner.ExecuteAsync(mutation, expected, ct);
        public Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken ct)
        {
            RuntimeLoadCalls++;
            throw new Xunit.Sdk.XunitException("Streaming export must not load the complete runtime project.");
        }
        public Task<ProjectRevision> ReplaceFromSnapshotAsync(IgwProject project, ProjectRevision expected, CancellationToken ct) =>
            inner.ReplaceFromSnapshotAsync(project, expected, ct);
        public Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken ct) => inner.OpenReadSnapshotAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MaximumWriteStream(Stream inner, int maximumWrite) : Stream
    {
        public int MaximumObservedWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Observe(buffer.Length);
            inner.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Observe(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }
        private void Observe(int count)
        {
            MaximumObservedWrite = Math.Max(MaximumObservedWrite, count);
            if (count > maximumWrite) throw new IOException($"write_too_large:{count}");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
