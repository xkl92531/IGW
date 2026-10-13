using IGW.Observability;
using Xunit;

namespace IGW.Tests;

/// <summary>杀毒软件、索引服务短暂占用刚写完的文件时，改名/删除偶发拒绝访问：短暂重试，找不到文件则不重试。</summary>
public sealed class TransientFileRetryTests
{
    [Fact]
    public void Access_denied_is_retried_until_the_file_is_released()
    {
        int calls = 0;
        TransientFileRetry.Run(() => { if (++calls < 3) throw new UnauthorizedAccessException("scanning"); });
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Missing_file_is_not_retried()
    {
        int calls = 0;
        Assert.Throws<FileNotFoundException>(() => TransientFileRetry.Run(() => { calls++; throw new FileNotFoundException(); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Persistent_conflict_gives_up_after_a_bounded_number_of_attempts()
    {
        int calls = 0;
        Assert.Throws<IOException>(() => TransientFileRetry.Run(() => { calls++; throw new IOException("in use"); }));
        Assert.Equal(6, calls);
    }
}
