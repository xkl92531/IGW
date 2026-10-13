using IGW.Abstractions;

namespace IGW.Core;

public enum ProjectApplyScope
{
    Full,
    MesOnly,
    SettingsOnly,
}

public sealed record ProjectApplyResult(
    bool Persisted,
    bool RuntimeApplied,
    bool Conflict,
    ProjectRevision Revision,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>批量注册点位的提交结果：Persisted 为仓储结果；RuntimeApplied 表示目标设备运行态是否已应用。</summary>
public sealed record ProjectBulkApplyResult(
    ProjectBulkTagResult Persisted,
    bool RuntimeApplied,
    string? ErrorCode,
    string? ErrorMessage,
    string DeviceFullName,
    /// <summary>写入本机 SQLite 的耗时。</summary>
    long WriteMs = 0,
    /// <summary>写完后把设备读回来的耗时；健康快照对不上时会退化成整台全量加载。</summary>
    long LoadMs = 0,
    /// <summary>由快照构造运行态补丁的耗时。</summary>
    long ProjectMs = 0);

/// <summary>
/// 工程配置提交边界：校验、持久化、重新读取和运行时应用严格按顺序执行。
/// 数据库提交成功但运行态应用失败时保留最后一个健康快照，并允许显式重试。
/// </summary>
public sealed class ProjectConfigurationCoordinator : IAsyncDisposable
{
    private readonly IProjectRepository _repository;
    private readonly Func<IgwProject, ProjectApplyScope, CancellationToken, Task> _applyRuntime;
    private readonly Action<IgwProject> _validate;
    // 局部修改的运行态应用(唯一回调)：界面局部保存(ExecuteAsync 快路径)与批量注册点位(ExecuteBulkAsync)共用
    private readonly Func<ProjectPatch, CancellationToken, Task> _applyPatch;
    private readonly bool _hasPatchApplier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IgwProject _healthyProject;
    private IgwProject? _pendingProject;
    private ProjectApplyScope _pendingScope;
    private int _degraded;
    private string? _degradedMessage;

    public IgwProject HealthyProject => Volatile.Read(ref _healthyProject);
    /// <summary>自启动以来局部应用失败、回退为整体重读 + 整体应用的次数。</summary>
    public long ScopedApplyFallbacks => Interlocked.Read(ref _scopedApplyFallbacks);
    private long _scopedApplyFallbacks;
    public bool Degraded => Volatile.Read(ref _degraded) != 0;
    public string? DegradedMessage => Volatile.Read(ref _degradedMessage);

    public ProjectConfigurationCoordinator(
        IProjectRepository repository,
        IgwProject healthyProject,
        Func<IgwProject, ProjectApplyScope, CancellationToken, Task> applyRuntime,
        Action<IgwProject>? validate = null,
        Func<ProjectPatch, CancellationToken, Task>? applyPatch = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _healthyProject = healthyProject ?? throw new ArgumentNullException(nameof(healthyProject));
        _applyRuntime = applyRuntime ?? throw new ArgumentNullException(nameof(applyRuntime));
        _validate = validate ?? ProjectStore.Validate;
        // 未提供局部应用时：界面保存照旧整体重读；批量注册把投影后的工程整体应用（结果正确，只是更重）
        _hasPatchApplier = applyPatch is not null;
        _applyPatch = applyPatch ?? ((patch, ct) => _applyRuntime(patch.Project, ProjectApplyScope.Full, ct));
    }

    public async Task<ProjectApplyResult> ApplySnapshotAsync(
        IgwProject project,
        ProjectRevision expected,
        CancellationToken cancellationToken) =>
        await ApplySnapshotAsync(project, expected, ProjectApplyScope.Full, cancellationToken).ConfigureAwait(false);

    public async Task<ProjectApplyResult> ApplySnapshotAsync(
        IgwProject project,
        ProjectRevision expected,
        ProjectApplyScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        IgwProject candidate = ProjectStore.Clone(project);
        try
        {
            _validate(candidate);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            ProjectRevision current = await _repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
            return Failure(false, false, current, "validation_failed", ex.Message);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectRevision revision;
            try
            {
                revision = await _repository.ReplaceFromSnapshotAsync(candidate, expected, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProjectRevisionConflictException ex)
            {
                return new ProjectApplyResult(false, false, true, ex.Current, "revision_conflict", ex.Message);
            }
            catch (Exception ex)
            {
                return Failure(false, false, expected, "persistence_failed", ex.Message);
            }

            return await ReloadAndApplyAsync(revision, scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectApplyResult> ExecuteAsync(
        ProjectMutation mutation,
        ProjectRevision expected,
        ProjectApplyScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectMutationResult persisted;
            try
            {
                persisted = await _repository.ExecuteAsync(mutation, expected, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(false, false, expected, "persistence_failed", ex.Message);
            }

            if (persisted.Conflict)
                return new ProjectApplyResult(
                    false, false, true, persisted.Revision, "revision_conflict", persisted.Error);
            if (!persisted.Applied)
                return Failure(false, false, persisted.Revision, "mutation_rejected", persisted.Error);
            // 已提交：此后不再响应请求取消。中途放弃会让数据库领先运行态且不进入降级（运行态与库分叉），
            // 重新读取与应用运行态都是有界操作，必须做完；失败进入降级、可显式重试。
            CancellationToken committed = CancellationToken.None;
            // 快路径：把已提交的修改直接套到内存工程上，只更新受影响的运行态，不整体重读数据库。
            // 已降级 = 之前有已提交但未应用的变更，内存工程不可信：改走整体重读。
            if (_hasPatchApplier && !Degraded
                && ProjectMutationPatcher.TryApply(HealthyProject, mutation) is { } patch)
            {
                ProjectApplyResult applied = await ApplyPatchAsync(patch, persisted.Revision, committed).ConfigureAwait(false);
                return applied;
            }
            // 已降级时之前未应用的变更可能在南向：整体重读后必须整体应用，不能按调用方的范围(如 MES)只应用一部分再记为健康
            ProjectApplyScope reloadScope = Degraded ? ProjectApplyScope.Full : scope;
            return await ReloadAndApplyAsync(persisted.Revision, reloadScope, committed).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量注册点位：仓储单事务写入 → 只读回被改动的一台设备 → 写时复制投影进健康工程 → 只对该设备应用运行态。
    /// 全程持有提交闸门，与其它配置提交严格串行；不做全工程重载。
    /// </summary>
    /// <summary>
    /// 批量注册提交后读回设备：设备已在运行工程里时，只读回请求涉及的根组(groupPath 与 replaceGroups 的首段)
    /// 和(有不带组的项时)根下点，再与内存里的设备合并；大设备上的小增量不再整台设备读回。
    /// </summary>
    private async Task<ProjectDeviceSnapshot> LoadBulkDeviceAsync(
        ProjectBulkTagRequest request, string deviceId, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        bool includeRootTags = false;
        foreach (ProjectBulkTagItem item in request.Items ?? [])
        {
            if (item.GroupPath is { Count: > 0 } path) roots.Add(path[0]);
            else includeRootTags = true;
        }
        foreach (List<string> path in request.ReplaceGroups ?? [])
            if (path.Count > 0) roots.Add(path[0]);
        string[] distinct = [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
        IgwProject healthy = HealthyProject;
        if (healthy.Channels.Any(channel => channel.Devices.Any(device => device.Id == deviceId)))
        {
            ProjectDeviceSnapshot partial = await _repository
                .LoadDeviceGroupsAsync(deviceId, distinct, includeRootTags, cancellationToken).ConfigureAwait(false);
            if (ProjectBulkProjection.MergePartial(healthy, partial, distinct, includeRootTags) is { } merged) return merged;
        }
        return await _repository.LoadDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectBulkApplyResult> ExecuteBulkAsync(
        ProjectBulkTagRequest request,
        ProjectRevision? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 已降级 = 之前有已提交但未应用到运行态的变更；此时不能投影到旧健康快照（会漏掉那次变更），改走全量重载
            bool wasDegraded = Degraded;
            ProjectBulkTagResult persisted;
            long writeMs = 0;
            try
            {
                var writeWatch = System.Diagnostics.Stopwatch.StartNew();
                persisted = await _repository.BulkUpsertTagsAsync(request, expected, cancellationToken).ConfigureAwait(false);
                writeMs = writeWatch.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 提交前取消：事务已回滚、数据库不变，按取消向上传播（不是写库失败）
                throw;
            }
            catch (Exception ex)
            {
                ProjectRevision current = expected ?? await TryGetRevisionAsync(CancellationToken.None).ConfigureAwait(false);
                // 仓储层没归类的异常：只有 IO/超时类视为可重试（接口 503），其余按服务端错误（500）
                persisted = new ProjectBulkTagResult(false, false, current, null, null, 0, 0, 0, "persistence_failed", ex.Message,
                    Retryable: ex is IOException or TimeoutException);
            }
            if (!persisted.Applied || persisted.DeviceId is null)
                return new ProjectBulkApplyResult(persisted, false, persisted.ErrorCode, persisted.Error, "");

            // 已提交：此后不再响应请求取消。中途放弃会让数据库领先运行态（进入降级、需要重试），
            // 读回设备与应用运行态都是有界操作，必须做完。
            CancellationToken committed = CancellationToken.None;

            if (wasDegraded)
            {
                // 全量重载 + 全量应用：一并补上先前未应用的提交；仅全量应用成功才清除降级
                ProjectApplyResult full = await ReloadAndApplyAsync(persisted.Revision, ProjectApplyScope.Full, committed)
                    .ConfigureAwait(false);
                return new ProjectBulkApplyResult(persisted, full.RuntimeApplied, full.ErrorCode, full.ErrorMessage, "");
            }

            ProjectPatch patch;
            string deviceFullName;
            long loadMs, projectMs;
            try
            {
                var loadWatch = System.Diagnostics.Stopwatch.StartNew();
                ProjectDeviceSnapshot snapshot = await LoadBulkDeviceAsync(request, persisted.DeviceId, committed)
                    .ConfigureAwait(false);
                loadMs = loadWatch.ElapsedMilliseconds;
                var projectWatch = System.Diagnostics.Stopwatch.StartNew();
                patch = ProjectBulkProjection.ToPatch(HealthyProject, snapshot, out deviceFullName);
                projectMs = projectWatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                // 数据库已提交但读回失败：无法构造投影，保留健康快照，重试时走全量重载
                SetDegraded(null, ProjectApplyScope.Full, "数据库已提交，但重新读取设备失败：" + ex.Message);
                return new ProjectBulkApplyResult(persisted, false, "runtime_apply_failed", DegradedMessage, "");
            }

            ProjectApplyResult applied = await ApplyPatchAsync(patch, persisted.Revision, committed).ConfigureAwait(false);
            return new ProjectBulkApplyResult(persisted, applied.RuntimeApplied, applied.ErrorCode, applied.ErrorMessage,
                deviceFullName, writeMs, loadMs, projectMs);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectApplyResult> RetryRuntimeApplyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectRevision revision = await _repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
            IgwProject project = _pendingProject
                ?? await _repository.LoadRuntimeProjectAsync(cancellationToken).ConfigureAwait(false);
            ProjectApplyScope scope = _pendingProject is null ? ProjectApplyScope.Full : _pendingScope;
            return await ApplyRuntimeAsync(project, revision, scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProjectApplyResult> ReloadAndApplyAsync(
        ProjectRevision revision,
        ProjectApplyScope scope,
        CancellationToken cancellationToken)
    {
        IgwProject persisted;
        try
        {
            persisted = await _repository.LoadRuntimeProjectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetDegraded(null, scope, "数据库已提交，但重新读取运行快照失败：" + ex.Message);
            return Failure(true, false, revision, "runtime_apply_failed", DegradedMessage);
        }
        return await ApplyRuntimeAsync(persisted, revision, scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectApplyResult> ApplyRuntimeAsync(
        IgwProject project,
        ProjectRevision revision,
        ProjectApplyScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            await _applyRuntime(project, scope, cancellationToken).ConfigureAwait(false);
            MarkHealthy(project);
            return new ProjectApplyResult(true, true, false, revision, null, null);
        }
        catch (Exception ex)
        {
            SetDegraded(project, scope, ex.Message);
            return Failure(true, false, revision, "runtime_apply_failed", ex.Message);
        }
    }

    /// <summary>
    /// 应用一个局部修改（界面局部保存与批量注册点位共用）。成功才把修改后的工程记为健康。
    /// 局部应用失败时在同一次调用内回退为整体重读 + 整体应用（与 master 的界面保存体验一致，调用方看到成功）；
    /// 只有整体应用也失败才进入降级（待办 = 整体应用），显式重试或下一次保存时整体重读补上。
    /// 仅在仓储提交之后调用：cancellationToken 应为 CancellationToken.None。
    /// </summary>
    private async Task<ProjectApplyResult> ApplyPatchAsync(
        ProjectPatch patch,
        ProjectRevision revision,
        CancellationToken cancellationToken)
    {
        try
        {
            await _applyPatch(patch, cancellationToken).ConfigureAwait(false);
            MarkHealthy(patch.Project);
            return new ProjectApplyResult(true, true, false, revision, null, null);
        }
        catch (Exception)
        {
            // 局部应用失败：运行态可能停在半途，按数据库整体重读并整体应用，保证运行态与数据库一致
            // (失败原因由局部应用回调自己记日志；这里只计数)
            Interlocked.Increment(ref _scopedApplyFallbacks);
            return await ReloadAndApplyAsync(revision, ProjectApplyScope.Full, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void MarkHealthy(IgwProject project)
    {
        Volatile.Write(ref _healthyProject, project);
        _pendingProject = null;
        Volatile.Write(ref _degradedMessage, null);
        Volatile.Write(ref _degraded, 0);
    }

    private async Task<ProjectRevision> TryGetRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _repository.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private void SetDegraded(IgwProject? project, ProjectApplyScope scope, string message)
    {
        _pendingProject = project;
        _pendingScope = scope;
        Volatile.Write(ref _degradedMessage, message);
        Volatile.Write(ref _degraded, 1);
    }

    private static ProjectApplyResult Failure(
        bool persisted,
        bool runtimeApplied,
        ProjectRevision revision,
        string code,
        string? message) =>
        new(persisted, runtimeApplied, false, revision, code, message);

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _repository.DisposeAsync().ConfigureAwait(false);
    }
}
