using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectConfigurationCoordinatorTests
{
    [Fact]
    public async Task Persistence_commits_before_runtime_apply_and_reports_apply_failure()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events, new ProjectRevision(1));
        var coordinator = new ProjectConfigurationCoordinator(
            repository,
            new IgwProject { Name = "healthy" },
            (_, _, _) =>
            {
                events.Add("apply");
                throw new IOException("driver restart");
            },
            project =>
            {
                events.Add("validate");
                ProjectStore.Validate(project);
            });

        ProjectApplyResult result = await coordinator.ApplySnapshotAsync(
            new IgwProject { Name = "persisted" }, new ProjectRevision(1), default);

        Assert.Equal(["validate", "persist", "load", "apply"], events);
        Assert.True(result.Persisted);
        Assert.False(result.RuntimeApplied);
        Assert.False(result.Conflict);
        Assert.Equal("runtime_apply_failed", result.ErrorCode);
        Assert.True(coordinator.Degraded);
        Assert.Equal("healthy", coordinator.HealthyProject.Name);
        Assert.Equal("persisted", repository.Stored.Name);
    }

    [Fact]
    public async Task Revision_conflict_performs_neither_persistence_nor_runtime_apply()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events, new ProjectRevision(4));
        var coordinator = new ProjectConfigurationCoordinator(
            repository,
            new IgwProject { Name = "healthy" },
            (_, _, _) =>
            {
                events.Add("apply");
                return Task.CompletedTask;
            });

        ProjectApplyResult result = await coordinator.ApplySnapshotAsync(
            new IgwProject { Name = "stale" }, new ProjectRevision(3), default);

        Assert.False(result.Persisted);
        Assert.False(result.RuntimeApplied);
        Assert.True(result.Conflict);
        Assert.Equal(new ProjectRevision(4), result.Revision);
        Assert.DoesNotContain("persist", events);
        Assert.DoesNotContain("apply", events);
    }

    [Fact]
    public async Task Mes_only_mutation_applies_only_mes_runtime_scope()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events, new ProjectRevision(2));
        var scopes = new List<ProjectApplyScope>();
        var coordinator = new ProjectConfigurationCoordinator(
            repository,
            new IgwProject { Name = "healthy" },
            (_, scope, _) =>
            {
                events.Add("apply");
                scopes.Add(scope);
                return Task.CompletedTask;
            });

        ProjectApplyResult result = await coordinator.ExecuteAsync(
            new ProjectMutation(
                ProjectMutationKind.ReplaceMesTask,
                ProjectNodeKind.Group,
                ["task-1"],
                null,
                "{}"),
            new ProjectRevision(2),
            ProjectApplyScope.MesOnly,
            default);

        Assert.True(result.Persisted);
        Assert.True(result.RuntimeApplied);
        Assert.Equal([ProjectApplyScope.MesOnly], scopes);
        Assert.Equal(["persist-mutation", "load", "apply"], events);
    }

    [Fact]
    public async Task Bulk_execution_applies_only_the_target_device_without_full_reload()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 3, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        IReadOnlyList<string>? appliedDevices = null;
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => { fullApplies++; return Task.CompletedTask; },
            validate: null,
            applyPatch: (patch, _) => { appliedDevices = patch.Summary.ChangedDevices.ToList(); return Task.CompletedTask; });

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(result.RuntimeApplied);
        Assert.Equal(["A.D1"], appliedDevices);
        Assert.Equal(0, fullApplies);
        Assert.Equal(0, repository.LoadRuntimeProjectCalls);
        Assert.Equal("D1", Assert.Single(Assert.Single(coordinator.HealthyProject.Channels).Devices).Name);
    }

    [Fact]
    public async Task Failed_scoped_bulk_apply_falls_back_to_full_reload_in_the_same_call()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        int patches = 0;
        var fullScopes = new List<ProjectApplyScope>();
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, scope, ct) => { ct.ThrowIfCancellationRequested(); fullScopes.Add(scope); return Task.CompletedTask; }, validate: null,
            applyPatch: (_, _) => ++patches == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        Assert.Null(result.ErrorCode);
        Assert.False(coordinator.Degraded);
        Assert.Equal(1, patches);
        Assert.Equal([ProjectApplyScope.Full], fullScopes);
        Assert.Equal(1, repository.LoadRuntimeProjectCalls);
        Assert.Equal("stored", coordinator.HealthyProject.Name);   // 整体重读后的数据库版本成为健康快照
    }

    [Fact]
    public async Task Failed_scoped_bulk_apply_and_failed_full_apply_degrades_and_retry_recovers()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        int fullApplies = 0;
        var healthy = new IgwProject { Name = "healthy" };
        var coordinator = new ProjectConfigurationCoordinator(repository, healthy,
            (_, _, _) => ++fullApplies == 1 ? throw new IOException("driver restart") : Task.CompletedTask, validate: null,
            applyPatch: (_, _) => throw new InvalidOperationException("boom"));

        var failed = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);
        Assert.False(failed.RuntimeApplied);
        Assert.Equal("runtime_apply_failed", failed.ErrorCode);
        Assert.True(coordinator.Degraded);
        Assert.Same(healthy, coordinator.HealthyProject);

        var retried = await coordinator.RetryRuntimeApplyAsync(default);
        Assert.True(retried.RuntimeApplied, retried.ErrorMessage);
        Assert.False(coordinator.Degraded);
        Assert.Equal(2, fullApplies);
        Assert.Equal("stored", coordinator.HealthyProject.Name);
    }

    [Fact]
    public async Task Persist_failure_or_conflict_does_not_touch_runtime()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(false, true, new ProjectRevision(5), null, null, 0, 0, 0, "revision_conflict", "x");
        bool applied = false;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => Task.CompletedTask, validate: null,
            applyPatch: (_, _) => { applied = true; return Task.CompletedTask; });
        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), new ProjectRevision(4), default);
        Assert.True(result.Persisted.Conflict);
        Assert.False(applied);
    }

    [Fact]
    public async Task Persisted_but_device_reload_failure_degrades_and_keeps_healthy_project()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceLoadError = new IOException("db gone");
        bool applied = false;
        var healthy = new IgwProject { Name = "healthy" };
        var coordinator = new ProjectConfigurationCoordinator(repository, healthy,
            (_, _, _) => { applied = true; return Task.CompletedTask; }, validate: null,
            applyPatch: (_, _) => { applied = true; return Task.CompletedTask; });

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(result.Persisted.Applied);
        Assert.False(result.RuntimeApplied);
        Assert.Equal("runtime_apply_failed", result.ErrorCode);
        Assert.True(coordinator.Degraded);
        Assert.Same(healthy, coordinator.HealthyProject);
        Assert.False(applied);
    }

    [Fact]
    public async Task Repository_exception_maps_to_persistence_failed()
    {
        var repository = new RecordingRepository();
        repository.BulkError = new IOException("disk full");
        bool applied = false;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => { applied = true; return Task.CompletedTask; }, validate: null,
            applyPatch: (_, _) => { applied = true; return Task.CompletedTask; });

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.False(result.Persisted.Applied);
        Assert.False(result.RuntimeApplied);
        Assert.Equal("persistence_failed", result.ErrorCode);
        Assert.False(coordinator.Degraded);
        Assert.False(applied);
    }

    [Fact]
    public async Task Bulk_while_degraded_falls_back_to_full_apply_and_clears_degraded_on_success()
    {
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        int deviceApplies = 0;
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => ++fullApplies == 1 ? throw new IOException("driver restart") : Task.CompletedTask, validate: null,
            applyPatch: (_, _) => ++deviceApplies == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        var first = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);
        Assert.False(first.RuntimeApplied);                          // 局部失败 + 回退的整体应用也失败
        Assert.True(coordinator.Degraded);

        var second = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(second.RuntimeApplied);
        Assert.Null(second.ErrorCode);
        Assert.Equal(1, deviceApplies);                              // 降级期间不走按设备应用
        Assert.Equal(2, fullApplies);
        Assert.Equal(2, repository.LoadRuntimeProjectCalls);
        Assert.False(coordinator.Degraded);
        Assert.Equal("stored", coordinator.HealthyProject.Name);     // 全量重载后的工程成为健康快照
    }

    [Fact]
    public async Task Bulk_while_degraded_full_apply_failure_stays_degraded()
    {
        var events = new List<string>();
        var repository = new RecordingRepository(events, new ProjectRevision(1));
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(3), "c1", "d1", 1, 0, 0, null, null);
        bool deviceApplied = false;
        var healthy = new IgwProject { Name = "healthy" };
        var coordinator = new ProjectConfigurationCoordinator(repository, healthy,
            (_, _, _) => throw new IOException("driver restart"), validate: null,
            applyPatch: (_, _) => { deviceApplied = true; return Task.CompletedTask; });

        ProjectApplyResult snapshot = await coordinator.ApplySnapshotAsync(
            new IgwProject { Name = "persisted" }, new ProjectRevision(1), default);
        Assert.False(snapshot.RuntimeApplied);
        Assert.True(coordinator.Degraded);

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default);

        Assert.True(result.Persisted.Applied);
        Assert.False(result.RuntimeApplied);
        Assert.Equal("runtime_apply_failed", result.ErrorCode);
        Assert.True(coordinator.Degraded);
        Assert.False(deviceApplied);
        Assert.Equal(2, repository.LoadRuntimeProjectCalls);
        Assert.Same(healthy, coordinator.HealthyProject);
    }

    [Fact]
    public async Task Cancellation_after_commit_still_applies_the_device_and_does_not_degrade()
    {
        using var cts = new CancellationTokenSource();
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        repository.AfterBulkCommit = cts.Cancel;
        IReadOnlyList<string>? appliedDevices = null;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => Task.CompletedTask, validate: null,
            applyPatch: (patch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                appliedDevices = patch.Summary.ChangedDevices.ToList();
                return Task.CompletedTask;
            });

        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        Assert.Equal(["A.D1"], appliedDevices);
        Assert.False(coordinator.Degraded);
        Assert.Equal("D1", Assert.Single(Assert.Single(coordinator.HealthyProject.Channels).Devices).Name);
    }

    [Fact]
    public async Task Cancellation_after_commit_while_degraded_still_runs_the_full_reload()
    {
        using var cts = new CancellationTokenSource();
        var repository = new RecordingRepository();
        repository.BulkResult = new ProjectBulkTagResult(true, false, new ProjectRevision(2), "c1", "d1", 1, 0, 0, null, null);
        repository.DeviceSnapshot = new ProjectDeviceSnapshot(
            new ChannelDef { Id = "c1", Name = "A", DriverId = "Siemens.S7" }, new DeviceDef { Id = "d1", Name = "D1" });
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ++fullApplies == 1 ? throw new IOException("driver restart") : Task.CompletedTask;
            }, validate: null,
            applyPatch: (_, _) => throw new InvalidOperationException("boom"));
        Assert.False((await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, default)).RuntimeApplied);
        Assert.True(coordinator.Degraded);

        repository.AfterBulkCommit = cts.Cancel;
        var result = await coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, cts.Token);

        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        Assert.False(coordinator.Degraded);
    }

    [Fact]
    public async Task Cancellation_before_commit_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var repository = new RecordingRepository();
        repository.BulkError = new OperationCanceledException(cts.Token);
        bool applied = false;
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject(),
            (_, _, _) => { applied = true; return Task.CompletedTask; }, validate: null,
            applyPatch: (_, _) => { applied = true; return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteBulkAsync(new ProjectBulkTagRequest(), null, cts.Token));
        Assert.False(applied);
        Assert.False(coordinator.Degraded);
    }

    // ---- 界面局部保存(ExecuteAsync 快路径)与批量注册共用同一个 applyPatch 回调 ----

    private static IgwProject PatchSample() => new()
    {
        Name = "healthy",
        Channels =
        [
            new ChannelDef
            {
                Id = "c1", Name = "C1", DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef
                    {
                        Id = "d1", Name = "D1",
                        Groups = [new TagGroupDef { Id = "g1", Name = "G", Tags = [new TagDef { Id = "t1", Name = "A", Address = "SINE" }] }],
                    },
                ],
            },
        ],
    };

    private static ProjectMutation TagUpsert(string description) => new(
        ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null,
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProjectMutation(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Tag, [], "g1",
                System.Text.Json.JsonSerializer.Serialize(
                    new TagDef { Id = "t1", Name = "A", Address = "SINE", Description = description }, ProjectStore.Options)),
        }, ProjectStore.Options));

    [Fact]
    public async Task Cancellation_after_mutation_commit_still_applies_the_patch()
    {
        using var cts = new CancellationTokenSource();
        var repository = new RecordingRepository(new List<string>(), new ProjectRevision(1));
        repository.AfterMutationCommit = cts.Cancel;
        ProjectPatch? applied = null;
        var coordinator = new ProjectConfigurationCoordinator(repository, PatchSample(),
            (_, _, _) => throw new InvalidOperationException("must not fully reload"), validate: null,
            applyPatch: (patch, ct) =>
            {
                ct.ThrowIfCancellationRequested();   // 已提交后协调器不能再把请求取消传下来
                applied = patch;
                return Task.CompletedTask;
            });

        ProjectApplyResult result = await coordinator.ExecuteAsync(
            TagUpsert("new"), new ProjectRevision(1), ProjectApplyScope.Full, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        Assert.False(coordinator.Degraded);
        Assert.NotNull(applied);
        Assert.Equal(["C1.D1"], applied!.Summary.ChangedDevices);
        Assert.Same(applied.Project, coordinator.HealthyProject);
        Assert.Equal(0, repository.LoadRuntimeProjectCalls);
    }

    [Fact]
    public async Task Failed_mutation_patch_falls_back_to_full_reload_and_reports_success()
    {
        var repository = new RecordingRepository(new List<string>(), new ProjectRevision(1));
        int patches = 0;
        var fullApplied = new List<IgwProject>();
        var coordinator = new ProjectConfigurationCoordinator(repository, PatchSample(),
            (project, _, ct) => { ct.ThrowIfCancellationRequested(); fullApplied.Add(project); return Task.CompletedTask; },
            validate: null,
            applyPatch: (_, _) => ++patches == 1 ? throw new IOException("opc ua sync failed") : Task.CompletedTask);

        ProjectApplyResult result = await coordinator.ExecuteAsync(
            TagUpsert("new"), new ProjectRevision(1), ProjectApplyScope.Full, default);

        Assert.True(result.Persisted);
        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        Assert.Null(result.ErrorCode);
        Assert.False(coordinator.Degraded);
        Assert.Equal(1, patches);
        Assert.Equal(1, coordinator.ScopedApplyFallbacks);
        Assert.Equal(1, repository.LoadRuntimeProjectCalls);          // 按数据库整体重读
        var applied = Assert.Single(fullApplied);
        Assert.Same(applied, coordinator.HealthyProject);              // 运行态与数据库一致
        Assert.Equal("stored", applied.Name);
    }

    [Fact]
    public async Task Failed_mutation_patch_and_failed_full_apply_degrades_and_retry_recovers()
    {
        var repository = new RecordingRepository(new List<string>(), new ProjectRevision(1));
        IgwProject healthy = PatchSample();
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, healthy,
            (_, _, _) => ++fullApplies == 1 ? throw new IOException("driver restart") : Task.CompletedTask, validate: null,
            applyPatch: (_, _) => throw new IOException("opc ua sync failed"));

        ProjectApplyResult failed = await coordinator.ExecuteAsync(
            TagUpsert("new"), new ProjectRevision(1), ProjectApplyScope.Full, default);

        Assert.True(failed.Persisted);
        Assert.False(failed.RuntimeApplied);
        Assert.Equal("runtime_apply_failed", failed.ErrorCode);
        Assert.True(coordinator.Degraded);
        Assert.Same(healthy, coordinator.HealthyProject);

        ProjectApplyResult retried = await coordinator.RetryRuntimeApplyAsync(default);

        Assert.True(retried.RuntimeApplied, retried.ErrorMessage);
        Assert.False(coordinator.Degraded);
        Assert.Equal(2, fullApplies);
        Assert.Equal("stored", coordinator.HealthyProject.Name);
    }

    [Fact]
    public async Task Mutation_while_degraded_uses_the_full_reload_not_the_patch()
    {
        var repository = new RecordingRepository(new List<string>(), new ProjectRevision(1));
        int patchApplies = 0;
        int fullApplies = 0;
        var coordinator = new ProjectConfigurationCoordinator(repository, PatchSample(),
            (_, _, _) => ++fullApplies == 1 ? throw new IOException("driver restart") : Task.CompletedTask, validate: null,
            applyPatch: (_, _) => { patchApplies++; throw new IOException("boom"); });

        Assert.False((await coordinator.ExecuteAsync(TagUpsert("x"), new ProjectRevision(1), ProjectApplyScope.Full, default))
            .RuntimeApplied);
        Assert.True(coordinator.Degraded);

        ProjectApplyResult second = await coordinator.ExecuteAsync(
            TagUpsert("y"), new ProjectRevision(2), ProjectApplyScope.Full, default);

        Assert.True(second.RuntimeApplied, second.ErrorMessage);
        Assert.Equal(1, patchApplies);
        Assert.Equal(2, fullApplies);
        Assert.Equal(2, repository.LoadRuntimeProjectCalls);
        Assert.False(coordinator.Degraded);
    }

    [Fact]
    public async Task Mes_only_mutation_while_degraded_reloads_and_applies_the_full_scope()
    {
        var repository = new RecordingRepository(new List<string>(), new ProjectRevision(1));
        var scopes = new List<ProjectApplyScope>();
        var coordinator = new ProjectConfigurationCoordinator(repository, new IgwProject { Name = "healthy" },
            (_, scope, _) =>
            {
                scopes.Add(scope);
                return scopes.Count == 1 ? throw new IOException("driver restart") : Task.CompletedTask;
            });

        Assert.False((await coordinator.ApplySnapshotAsync(new IgwProject { Name = "persisted" }, new ProjectRevision(1), default))
            .RuntimeApplied);
        Assert.True(coordinator.Degraded);

        ProjectApplyResult result = await coordinator.ExecuteAsync(
            new ProjectMutation(ProjectMutationKind.ReplaceMesTask, ProjectNodeKind.Group, ["task-1"], null, "{}"),
            new ProjectRevision(2), ProjectApplyScope.MesOnly, default);

        Assert.True(result.RuntimeApplied, result.ErrorMessage);
        // 降级期间未应用的南向变更必须一起补上：整体应用，而不是只按 MES 范围应用后记为健康
        Assert.Equal([ProjectApplyScope.Full, ProjectApplyScope.Full], scopes);
        Assert.False(coordinator.Degraded);
    }

    private sealed class RecordingRepository : IProjectRepository
    {
        private readonly List<string> _events;
        private ProjectRevision _revision;
        public IgwProject Stored { get; private set; } = new() { Name = "stored" };
        public ProjectBulkTagResult? BulkResult { get; set; }
        public Exception? BulkError { get; set; }
        public ProjectDeviceSnapshot? DeviceSnapshot { get; set; }
        public Exception? DeviceLoadError { get; set; }
        public int LoadRuntimeProjectCalls { get; private set; }
        /// <summary>仓储提交成功之后、返回之前调用（模拟请求在提交后被取消）。</summary>
        public Action? AfterBulkCommit { get; set; }
        public Action? AfterMutationCommit { get; set; }

        public RecordingRepository()
            : this([], new ProjectRevision(1))
        {
        }

        public RecordingRepository(List<string> events, ProjectRevision revision)
        {
            _events = events;
            _revision = revision;
        }

        public Task<ProjectRevision> ReplaceFromSnapshotAsync(
            IgwProject project,
            ProjectRevision expected,
            CancellationToken cancellationToken)
        {
            if (expected != _revision) throw new ProjectRevisionConflictException(_revision);
            _events.Add("persist");
            Stored = ProjectStore.Clone(project);
            _revision = new ProjectRevision(_revision.Value + 1);
            return Task.FromResult(_revision);
        }

        public Task<ProjectMutationResult> ExecuteAsync(
            ProjectMutation mutation,
            ProjectRevision expected,
            CancellationToken cancellationToken)
        {
            if (expected != _revision)
                return Task.FromResult(new ProjectMutationResult(false, true, _revision, "conflict"));
            _events.Add("persist-mutation");
            _revision = new ProjectRevision(_revision.Value + 1);
            AfterMutationCommit?.Invoke();
            return Task.FromResult(new ProjectMutationResult(true, false, _revision, null));
        }

        public Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadRuntimeProjectCalls++;
            _events.Add("load");
            return Task.FromResult(ProjectStore.Clone(Stored));
        }

        public Task<ProjectBulkTagResult> BulkUpsertTagsAsync(
            ProjectBulkTagRequest request,
            ProjectRevision? expected,
            CancellationToken cancellationToken)
        {
            if (BulkError is not null) throw BulkError;
            var result = BulkResult ?? throw new InvalidOperationException("BulkResult 未设置");
            AfterBulkCommit?.Invoke();
            return Task.FromResult(result);
        }

        public Task<ProjectDeviceSnapshot> LoadDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeviceLoadError is not null) throw DeviceLoadError;
            return Task.FromResult(DeviceSnapshot ?? throw new InvalidOperationException("DeviceSnapshot 未设置"));
        }

        public Task<ProjectRevision> GetRevisionAsync(CancellationToken cancellationToken) => Task.FromResult(_revision);
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProjectTreeNode> GetRootAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(ProjectNodeRef parent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectPage<ProjectTagRow>> GetTagsAsync(ProjectTagQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectPage<ProjectMesTaskRow>> GetMesTasksAsync(ProjectMesTaskQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectPage<ProjectNodeSearchRow>> SearchNodesAsync(ProjectNodeSearchQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetEntityDocumentAsync(ProjectNodeRef entity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
