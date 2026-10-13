using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 保存工程前的驱动授权检查：不能新建、复制或改成未授权驱动的通道。
/// 已有的未授权通道(例如换了一张驱动更少的授权码)照样可以保存其它修改、删除，只是运行时不采集。
/// </summary>
public static class DriverLicenseGuard
{
    /// <summary>整包保存：候选工程里每个未授权驱动的通道，必须在当前工程里就已存在且驱动没变。</summary>
    public static string? CheckSnapshot(IgwProject current, IgwProject candidate, Func<string, bool> allows)
    {
        foreach (ChannelDef channel in candidate.Channels)
        {
            if (allows(channel.DriverId) || ExistsWithSameDriver(current, channel.Id, channel.DriverId)) continue;
            return Message(channel.Name, channel.DriverId);
        }
        return null;
    }

    /// <summary>局部修改：通道的新建/修改、复制通道，批量修改逐项检查。</summary>
    public static string? CheckMutation(IgwProject current, ProjectMutation mutation, Func<string, bool> allows)
    {
        if (mutation.Kind == ProjectMutationKind.Batch)
        {
            List<ProjectMutation> items;
            try { items = JsonSerializer.Deserialize<List<ProjectMutation>>(mutation.DocumentJson ?? "[]", ProjectStore.Options) ?? []; }
            catch (JsonException) { return null; }   // 格式错误交给仓储报错
            foreach (ProjectMutation item in items)
                if (CheckMutation(current, item, allows) is { } error) return error;
            return null;
        }
        if (mutation.EntityKind != ProjectNodeKind.Channel) return null;

        if (mutation.Kind == ProjectMutationKind.UpsertEntity)
        {
            ChannelDef? channel;
            try { channel = JsonSerializer.Deserialize<ChannelDef>(mutation.DocumentJson ?? "", ProjectStore.Options); }
            catch (JsonException) { return null; }
            if (channel is null || allows(channel.DriverId)) return null;
            string id = string.IsNullOrEmpty(channel.Id) && mutation.EntityIds.Count > 0 ? mutation.EntityIds[0] : channel.Id;
            return ExistsWithSameDriver(current, id, channel.DriverId) ? null : Message(channel.Name, channel.DriverId);
        }
        if (mutation.Kind == ProjectMutationKind.CopyEntities)
        {
            foreach (string id in mutation.EntityIds)
                if (current.Channels.FirstOrDefault(channel => channel.Id == id) is { } source && !allows(source.DriverId))
                    return Message(source.Name, source.DriverId);
        }
        return null;
    }

    private static bool ExistsWithSameDriver(IgwProject project, string channelId, string driverId) =>
        project.Channels.Any(channel => channel.Id == channelId
            && string.Equals(channel.DriverId, driverId, StringComparison.OrdinalIgnoreCase));

    private static string Message(string channelName, string driverId) =>
        $"驱动 {driverId} 未授权，不能新建或改用这个驱动(通道 {channelName})。请联系供应商更换包含该驱动的授权码。";
}
