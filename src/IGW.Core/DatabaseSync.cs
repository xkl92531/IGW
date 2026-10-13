namespace IGW.Core;

/// <summary>
/// 主数据库同步(本机 SQLite 为工作库和缓存)的运行状态。选择 SQLite 时不存在该对象。
/// 网关据此在主库断开时暂停修改配置，并在保存成功后立即触发一次同步。
/// </summary>
public interface IDatabaseSync
{
    /// <summary>sqlite / syncing / online / offline</summary>
    string State { get; }

    /// <summary>非空时禁止修改配置(主库断开或主库数据已被别处修改)，内容为给用户看的原因。</summary>
    string? ConfigurationWriteBlockedReason { get; }

    object Snapshot();

    void Nudge();
}
