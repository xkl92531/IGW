using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectStorageContractTests
{
    [Fact]
    public void Tag_query_rejects_unbounded_pages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectTagQuery("group-1", null, 0, 10_001).Validate());
    }

    [Fact]
    public void Revision_etag_is_stable_and_quoted()
    {
        Assert.Equal("\"project-42\"", new ProjectRevision(42).ETag);
    }
}
