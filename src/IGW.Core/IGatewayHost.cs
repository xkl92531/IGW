using IGW.Abstractions;

namespace IGW.Core;

/// <summary>网关运行宿主抽象:北向 API 通过它访问当前引擎/缓存,并下发新配置(热重载)。</summary>
public interface IGatewayHost
{
    /// <summary>外部主数据库同步状态；SQLite 时为 null。</summary>
    IDatabaseSync? DatabaseSync => null;

    DataHub Hub { get; }
    RuntimeEngine Engine { get; }
    AlarmEngine Alarms { get; }
    DataLogger? Logger { get; }
    ComputedEngine Computed { get; }
    ScheduleEngine Scheduler { get; }
    IgwProject Project { get; }
    IProjectRepository? ProjectRepository => null;
    bool ConfigurationDegraded => false;
    string? ConfigurationDegradedMessage => null;
    /// <summary>局部应用失败、回退为整体应用的累计次数(诊断用)。</summary>
    long ScopedApplyFallbacks => 0;
    string ProjectRevision => JsonHash.Of(Project);
    string RealtimeRevision => RealtimeProjectRevision.Compute(Project);
    RealtimeStreamCoordinator? RealtimeStreams => null;
    IReadOnlyList<DriverDescriptor> Drivers { get; }
    INetworkAdapterCatalog NetworkAdapters { get; }

    Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct);
    Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(
        BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct);
    Task<BrowseResult> BrowseOpcUaBranchAsync(
        BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct);

    Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct);
    Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct);
    Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct);

    /// <summary>只重建指定通道的设备循环，清除连接退避/熔断而不重载整个工程。</summary>
    Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct);

    /// <summary>当前 OPC UA 会话数(数据客户端);未启用 OPC UA 时为 0。</summary>
    int OpcUaSessions { get; }

    /// <summary>最近一次保存/应用工程的分段耗时，如“保存数据库 120ms；PLC 采集 800ms；…”，供界面和日志定位慢在哪一步。</summary>
    string? LastApplyTiming => null;

    /// <summary>是否正在热重载(此窗口内 Hub/Engine 正在重建,数据端点应返回 503)。</summary>
    bool Reloading { get; }

    /// <summary>各北向组件运行状态(off/running/failed),供 /health 暴露"哪个面下线了"。</summary>
    object ComponentsSnapshot();

    /// <summary>授权状态快照(status/blocked/customer/expiry/daysLeft/machineId/message)。</summary>
    object LicenseSnapshot();

    /// <summary>激活授权码(验签+本机机器码校验通过则写盘并即时生效);返回同 LicenseSnapshot 结构。</summary>
    Task<object> ActivateLicenseAsync(string code);

    /// <summary>应用新工程:校验 → 原子保存 → 有界停止旧组件 → 起新引擎/OPC UA。</summary>
    Task ReloadAsync(IgwProject newProject);

    /// <summary>带版本前置条件应用完整工程；版本检查与保存必须处于同一宿主锁内。</summary>
    async Task<ProjectUpdateResult> ApplyProjectAsync(
        IgwProject newProject, string? expectedRevision, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(expectedRevision)
            && !string.Equals(expectedRevision, ProjectRevision, StringComparison.Ordinal))
            return ProjectUpdateResult.RevisionConflict(ProjectRevision);
        ct.ThrowIfCancellationRequested();
        await ReloadAsync(newProject);
        return ProjectUpdateResult.Success(ProjectRevision);
    }

    /// <summary>以仓储修订号为前置条件执行一个局部工程命令，并只重建受影响的运行组件。</summary>
    Task<ProjectUpdateResult> ApplyProjectMutationAsync(
        ProjectMutation mutation,
        string expectedRevision,
        CancellationToken ct) =>
        throw new NotSupportedException("当前宿主不支持局部工程修改。");

    /// <summary>
    /// 批量注册点位（单通道单设备）：以仓储修订号为前置条件（空=不检查）单事务写库，
    /// 只对被改动的一台设备应用运行态；其它设备、实时流与北向订阅保持不动。
    /// </summary>
    Task<ProjectBulkTagResponse> ApplyBulkTagsAsync(
        ProjectBulkTagRequest request,
        string? expectedRevision,
        CancellationToken ct) =>
        throw new NotSupportedException("当前宿主不支持批量注册点位。");

    /// <summary>导入工程使用仓储修订号作为前置条件，并在一次协调操作中持久化和应用运行态。</summary>
    async Task<ProjectRevision> ApplyImportedProjectAsync(
        IgwProject newProject,
        ProjectRevision expected,
        CancellationToken ct)
    {
        ProjectUpdateResult result = await ApplyProjectAsync(newProject, expected.ETag, ct).ConfigureAwait(false);
        if (result.Conflict)
        {
            IGW.Abstractions.ProjectRevision.TryParse(result.Revision, out ProjectRevision current);
            throw new ProjectRevisionConflictException(current);
        }
        if (!result.Applied) throw new InvalidOperationException(result.Error ?? "project_import_runtime_apply_failed");
        if (!IGW.Abstractions.ProjectRevision.TryParse(result.Revision, out ProjectRevision revision))
            throw new InvalidOperationException("project_import_revision_invalid");
        return revision;
    }

    /// <summary>只替换并重启 MES 配置；PLC、SSE、OPC UA、MQTT、历史等运行组件保持不动。</summary>
    Task UpdateMesUploadAsync(MesUploadSettings mesUpload, CancellationToken ct) =>
        ReloadAsync(ProjectStore.WithMesUpload(Project, mesUpload));

    /// <summary>带版本前置条件定向应用 MES 配置。</summary>
    async Task<ProjectUpdateResult> ApplyMesUploadAsync(
        MesUploadSettings mesUpload, string? expectedRevision, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(expectedRevision)
            && !string.Equals(expectedRevision, ProjectRevision, StringComparison.Ordinal))
            return ProjectUpdateResult.RevisionConflict(ProjectRevision);
        await UpdateMesUploadAsync(mesUpload, ct);
        return ProjectUpdateResult.Success(ProjectRevision);
    }

    /// <summary>MES 采集上报运行状态快照(供 REST/界面;未启用 enabled=false)。</summary>
    object MesStatusSnapshot();

    /// <summary>MES 上报日志(SQLite,最近 limit 条;最新在前)。</summary>
    object MesLogSnapshot(int limit);

    /// <summary>MES 任务状态(类型化;未启用返回空)。异常中心聚合用。</summary>
    IReadOnlyList<MesTaskStatus> MesTaskStatuses();

    /// <summary>某任务的节点流转事件(内存环形缓冲,时间正序;未启用/无此任务返回空)。</summary>
    IReadOnlyList<MesFlowEvent> MesTimeline(string taskName);

    /// <summary>测试上报:用传入的任务定义发一次(读当前缓存值),返回请求/响应/可用响应字段;不回写、不改统计。</summary>
    Task<object> MesTestAsync(MesTask task, CancellationToken ct);
}
