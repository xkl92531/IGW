using IGW.Observability;
using Xunit;

namespace IGW.Tests;

public sealed class LogStorageChangeTests
{
    [Fact]
    public async Task Migration_copies_nested_history_without_deleting_source_and_records_progress()
    {
        string state = NewDirectory("state");
        string source = NewDirectory("旧 日志");
        string target = NewDirectory("新 日志");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "stream", "中文"));
            string sourceFile = Path.Combine(source, "stream", "中文", "record.bin");
            await File.WriteAllTextAsync(sourceFile, new string('x', 2048));
            LogStorageChange change = LogStorageChange.Begin(state, source, target);

            await change.CopyHistoryAsync(CancellationToken.None);

            Assert.True(File.Exists(sourceFile));
            Assert.Equal(
                await File.ReadAllBytesAsync(sourceFile),
                await File.ReadAllBytesAsync(Path.Combine(target, "stream", "中文", "record.bin")));
            Assert.Equal(change.TotalBytes, change.CopiedBytes);
            Assert.Equal(LogStorageChangeStatus.Copied, change.Status);
            LogStorageChange loaded = LogStorageChange.Load(state)!;
            Assert.Equal(change.CopiedBytes, loaded.CopiedBytes);
            Assert.Equal(LogStorageChangeStatus.Copied, loaded.Status);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Failed_migration_persists_a_single_line_redacted_reason()
    {
        string state = NewDirectory("state");
        string source = NewDirectory("source");
        string target = NewDirectory("target");
        try
        {
            LogStorageChange change = LogStorageChange.Begin(state, source, target);
            change.Fail("Authorization: Basic abc123\r\npassword=very-secret token=hidden");

            LogStorageChange loaded = LogStorageChange.Load(state)!;
            Assert.Equal(LogStorageChangeStatus.Failed, loaded.Status);
            string reason = Assert.IsType<string>(loaded.FailureReason);
            Assert.DoesNotContain("abc123", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("very-secret", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", reason, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', reason);
            Assert.DoesNotContain('\r', reason);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Migration_rejects_same_or_nested_directories()
    {
        string state = NewDirectory("state");
        string source = NewDirectory("source");
        try
        {
            Assert.Throws<ArgumentException>(() => LogStorageChange.Begin(state, source, source));
            Assert.Throws<ArgumentException>(() =>
                LogStorageChange.Begin(state, source, Path.Combine(source, "nested")));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void Complete_is_persisted_without_removing_the_migration_record()
    {
        string state = NewDirectory("state");
        string source = NewDirectory("source");
        string target = NewDirectory("target");
        try
        {
            LogStorageChange change = LogStorageChange.Begin(state, source, target);
            change.Complete();

            LogStorageChange loaded = LogStorageChange.Load(state)!;
            Assert.Equal(LogStorageChangeStatus.Completed, loaded.Status);
            Assert.NotNull(loaded.CompletedAt);
        }
        finally
        {
            Directory.Delete(state, recursive: true);
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Segment_compressed_between_listing_and_copy_is_copied_as_its_compressed_files()
    {
        string source = NewDirectory("source");
        string target = NewDirectory("target");
        try
        {
            // 列出文件时还是 .jsonl，复制前后台压缩已换成 .seg + .idx 并删掉了 .jsonl
            await File.WriteAllTextAsync(Path.Combine(source, "a.seg"), "compressed");
            await File.WriteAllTextAsync(Path.Combine(source, "a.idx"), "{}");

            await LogStorageChange.CopyFileOrReplacementAsync(
                Path.Combine(source, "a.jsonl"), Path.Combine(target, "a.jsonl"), CancellationToken.None);

            Assert.Equal("compressed", await File.ReadAllTextAsync(Path.Combine(target, "a.seg")));
            Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(target, "a.idx")));
            Assert.False(File.Exists(Path.Combine(target, "a.jsonl")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task File_removed_by_retention_between_listing_and_copy_is_skipped()
    {
        string source = NewDirectory("source");
        string target = NewDirectory("target");
        try
        {
            await LogStorageChange.CopyFileOrReplacementAsync(
                Path.Combine(source, "old.seg"), Path.Combine(target, "old.seg"), CancellationToken.None);

            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    private static string NewDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), "igw-log-migration", prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
