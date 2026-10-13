namespace IGW.Abstractions;

/// <summary>工程数据库的单调递增修订号，用作配置端并发控制。</summary>
public readonly record struct ProjectRevision(long Value)
{
    public string ETag => $"\"project-{Value}\"";

    public static bool TryParse(string? value, out ProjectRevision revision)
    {
        revision = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..].Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
            normalized = normalized[1..^1];
        const string prefix = "project-";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(normalized[prefix.Length..], out long parsed)
            && parsed >= 0
            && Set(parsed, out revision);
    }

    private static bool Set(long value, out ProjectRevision revision)
    {
        revision = new ProjectRevision(value);
        return true;
    }
}

public sealed class ProjectRevisionConflictException : InvalidOperationException
{
    public ProjectRevision Current { get; }

    public ProjectRevisionConflictException(ProjectRevision current)
        : base($"工程修订号冲突，当前为 {current.Value}。") => Current = current;
}

public enum ProjectNodeKind
{
    Project,
    Channel,
    Device,
    Group,
    Tag,
}

public sealed record ProjectNodeRef(ProjectNodeKind Kind, string Id);

public sealed record ProjectTreeNode(
    string Id,
    ProjectNodeKind Kind,
    string Name,
    string? Description,
    string ActualPath,
    bool HasChildren,
    int DirectTagCount,
    long RowVersion,
    int SchemaVersion = 1);

public sealed record ProjectTagRow(
    string Id,
    string Name,
    string ActualPath,
    string Address,
    IgwDataType DataType,
    TagAccess Access,
    int ScanRateMs,
    string? Description,
    string? EngUnit,
    int? StringLength,
    ArrayDef? Array,
    IReadOnlyDictionary<string, string>? Metadata,
    long RowVersion);

public sealed record ProjectNodeSearchQuery(
    string? ScopePath,
    string? Search,
    ProjectNodeKind Kind,
    TagAccess? RequiredAccess,
    bool DirectOnly,
    int Offset,
    int Limit,
    bool CountTotal = true)
{
    public ProjectNodeSearchQuery Validate()
    {
        if (Kind is not (ProjectNodeKind.Group or ProjectNodeKind.Tag))
            throw new ArgumentException("搜索节点类型只能是 Group 或 Tag。", nameof(Kind));
        if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Limit));
        return this;
    }
}

public sealed record ProjectNodeSearchRow(
    string Id,
    ProjectNodeKind Kind,
    string Name,
    string ActualPath,
    string ParentPath,
    string DevicePath,
    string? Description,
    string? Address,
    IgwDataType? DataType,
    TagAccess? Access,
    string? EngUnit,
    int? StringLength,
    ArrayDef? Array,
    IReadOnlyDictionary<string, string>? Metadata,
    long RowVersion);

public sealed record ProjectTagQuery(
    string? GroupId,
    string? Search,
    int Offset,
    int Limit,
    string? DeviceId = null)
{
    public ProjectTagQuery Validate()
    {
        if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Limit));
        // 组点位与设备直挂点位互斥：同时给会让过滤条件悄悄变成交集，返回空页还查不出错。
        if (GroupId is not null && DeviceId is not null)
            throw new ArgumentException("groupId 与 deviceId 只能提供一个。");
        return this;
    }
}

public sealed record ProjectPage<T>(
    IReadOnlyList<T> Items,
    long Total,
    int Offset,
    int Limit);

public enum ProjectMesTaskScope
{
    All,
    Device,
    Channel,
}

public sealed record ProjectMesTaskQuery(
    ProjectMesTaskScope Scope,
    string? DeviceId,
    string? ChannelId,
    string? Search,
    int Offset,
    int Limit,
    bool? Enabled = null)
{
    public ProjectMesTaskQuery Validate()
    {
        if (Offset < 0) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Limit));
        if (Scope == ProjectMesTaskScope.Device && string.IsNullOrWhiteSpace(DeviceId))
            throw new ArgumentException("按设备筛选 MES 任务时必须提供 deviceId。", nameof(DeviceId));
        if (Scope == ProjectMesTaskScope.Channel && string.IsNullOrWhiteSpace(ChannelId))
            throw new ArgumentException("按通道筛选 MES 任务时必须提供 channelId。", nameof(ChannelId));
        return this;
    }
}

public sealed record ProjectMesTaskRow(
    string Id,
    string Name,
    string? GroupId,
    string? GroupRef,
    string? DeviceRef,
    string? DeviceId,
    string? ChannelId,
    string? Description,
    bool Enabled,
    bool FlowAvailable,
    long RowVersion,
    int CollectIntervalMs = 1000);

public sealed record ProjectEntityDocument(
    ProjectNodeRef Entity,
    string? ParentId,
    int SortOrder,
    string DocumentJson);

public enum ProjectDocumentKind
{
    ProjectSection,
    MesTask,
    FlowTemplate,
    DeviceTemplate,
}

public sealed record ProjectDocument(
    ProjectDocumentKind Kind,
    string Key,
    string DocumentJson,
    long RowVersion);

public sealed record ProjectPortableSecret(
    string OwnerType,
    string OwnerId,
    string SecretName,
    string Value);

public enum ProjectMutationKind
{
    UpsertEntity,
    DeleteEntities,
    MoveEntities,
    CopyEntities,
    SetEnabled,
    ReplaceMesTask,
    ReplaceProjectSections,
    SaveMesTasks,
    /// <summary>DocumentJson 是一组修改(不可嵌套)，在同一事务里依次执行，全部成功才提交。</summary>
    Batch,
}

public sealed class MesTaskBatchSave
{
    public List<MesTask> Tasks { get; set; } = new();
    public List<string> PeriodTaskIds { get; set; } = new();
    public int? CollectIntervalMs { get; set; }
}

public sealed record ProjectMutation(
    ProjectMutationKind Kind,
    ProjectNodeKind EntityKind,
    IReadOnlyList<string> EntityIds,
    string? TargetId,
    string? DocumentJson);

public sealed record ProjectMutationResult(
    bool Applied,
    bool Conflict,
    ProjectRevision Revision,
    string? Error);

public interface IProjectReadSnapshot : IAsyncDisposable
{
    ProjectRevision Revision { get; }

    IAsyncEnumerable<ProjectEntityDocument> StreamEntitiesAsync(
        ProjectNodeKind kind,
        string? parentId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProjectDocument> StreamDocumentsAsync(
        ProjectDocumentKind kind,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProjectPortableSecret> StreamPortableSecretsAsync(
        CancellationToken cancellationToken);
}

public interface IProjectRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ProjectRevision> GetRevisionAsync(CancellationToken cancellationToken);
    Task<ProjectTreeNode> GetRootAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(
        ProjectNodeRef parent,
        CancellationToken cancellationToken);
    Task<ProjectPage<ProjectTagRow>> GetTagsAsync(
        ProjectTagQuery query,
        CancellationToken cancellationToken);
    async Task<long> GetTagCountAsync(CancellationToken cancellationToken) =>
        (await GetTagsAsync(new ProjectTagQuery(null, null, 0, 1), cancellationToken).ConfigureAwait(false)).Total;
    Task<ProjectPage<ProjectMesTaskRow>> GetMesTasksAsync(
        ProjectMesTaskQuery query,
        CancellationToken cancellationToken);
    Task<ProjectPage<ProjectNodeSearchRow>> SearchNodesAsync(
        ProjectNodeSearchQuery query,
        CancellationToken cancellationToken);
    Task<string> GetEntityDocumentAsync(
        ProjectNodeRef entity,
        CancellationToken cancellationToken);
    Task<ProjectDocument> GetDocumentAsync(
        ProjectDocumentKind kind,
        string key,
        CancellationToken cancellationToken) =>
        Task.FromException<ProjectDocument>(new NotSupportedException("当前工程仓储不支持按键读取文档。"));
    Task<ProjectMutationResult> ExecuteAsync(
        ProjectMutation mutation,
        ProjectRevision expected,
        CancellationToken cancellationToken);
    Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken cancellationToken);
    Task<ProjectRevision> ReplaceFromSnapshotAsync(
        IgwProject project,
        ProjectRevision expected,
        CancellationToken cancellationToken);
    Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken cancellationToken);
    Task<ProjectBulkTagResult> BulkUpsertTagsAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken) =>
        Task.FromException<ProjectBulkTagResult>(new NotSupportedException("当前工程仓储不支持批量注册点位。"));
    Task<ProjectDeviceSnapshot> LoadDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromException<ProjectDeviceSnapshot>(new NotSupportedException("当前工程仓储不支持单设备加载。"));
    /// <summary>
    /// 只读回设备的部分内容：设备本身、名称在 <paramref name="rootGroupNames"/> 中的根组(含整棵子树与其中的点)，
    /// 以及 <paramref name="includeRootTags"/> 为 true 时设备根下的点；其它根组不读。
    /// 批量注册在大设备上改少量组时用它代替整台设备读回。不支持的实现退回整台设备(合并结果相同)。
    /// </summary>
    Task<ProjectDeviceSnapshot> LoadDeviceGroupsAsync(
        string deviceId,
        IReadOnlyCollection<string> rootGroupNames,
        bool includeRootTags,
        CancellationToken cancellationToken) =>
        LoadDeviceAsync(deviceId, cancellationToken);
}
