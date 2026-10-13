using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 旧工程里已下线协议的内容。Open Protocol(螺丝枪)已删除，旧工程文件、旧数据库或导入的工程里
/// 残留的这类通道直接忽略：读入时移除，不再出现在工程树、监控中心和运行时里。
/// </summary>
public static class RetiredProjectContent
{
    private const string OpenProtocolDriverId = "OpenProtocol.Tcp";

    /// <summary>移除 Open Protocol 通道，返回被移除的通道名；没有则返回空列表且不改动工程。</summary>
    public static IReadOnlyList<string> RemoveOpenProtocolChannels(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Channels is null) return [];
        var removed = project.Channels
            .Where(channel => string.Equals(channel.DriverId, OpenProtocolDriverId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (removed.Count == 0) return [];
        project.Channels.RemoveAll(removed.Contains);
        return removed.Select(channel => channel.Name).ToArray();
    }
}
