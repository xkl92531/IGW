using System.Text.Json;
using IGW.Abstractions;
using Microsoft.Extensions.Logging;

namespace IGW.Northbound.RestApi;

internal sealed record DcsApiOutcome<T>(int StatusCode, DcsResponse<T> Body)
{
    public static DcsApiOutcome<T> Ok(T? value) => new(200, DcsResponse<T>.Success(value));
    public static DcsApiOutcome<T> BadRequest(string error) =>
        new(400, DcsResponse<T>.Failure(error, 400));
    public static DcsApiOutcome<T> OperationFailure(string error) =>
        new(200, DcsResponse<T>.Failure(error));
    public static DcsApiOutcome<T> Unavailable(string error) =>
        new(503, DcsResponse<T>.Failure(error, 503));
}

internal sealed class DcsTagOperations
{
    private const int MaxBatchSize = 1000;

    private sealed record ValidatedWrite(
        int Index,
        string RequestedTag,
        string PhysicalTag,
        string Device,
        object Value,
        TagDef Definition);

    /// <summary>解析器 + 其所属工程对象的不可变快照，整体一次性发布，读者不会读到"新解析器配旧工程"或反之的撕裂组合。</summary>
    private sealed record ResolverSnapshot(DcsTagAliasResolver Resolver, IgwProject Project);

    /// <summary>
    /// 后台重建失败后的退避窗口：窗口内不再发起新的重建尝试，继续用旧解析器提供服务。
    /// 内部可写（非 readonly），供测试用小窗口验证退避行为，生产环境不应修改。
    /// </summary>
    internal static TimeSpan ResolverRebuildFailureBackoff = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 旧快照未命中（读）或写入前，等待进行中的后台重建的上限；超时仍未落地则由调用方同步构建。
    /// </summary>
    internal static readonly TimeSpan ResolverCatchUpTimeout = TimeSpan.FromSeconds(2);

    /// <summary>本实例的追赶等待上限（默认 <see cref="ResolverCatchUpTimeout"/>；测试可调小）。</summary>
    internal TimeSpan CatchUpTimeout { get; set; } = ResolverCatchUpTimeout;

    /// <summary>调用方同步兜底构建解析器的次数（诊断/测试用）。</summary>
    internal int SynchronousBuildCount => Volatile.Read(ref _synchronousBuildCount);
    private int _synchronousBuildCount;
    private (IgwProject Project, Task<DcsTagAliasResolver?> Build)? _fallbackBuild;

    private readonly IDcsTagRuntime _runtime;
    private readonly ILogger? _logger;
    private readonly object _resolverLock = new();
    private ResolverSnapshot? _resolverSnapshot;
    private Task? _resolverBuild;
    private DateTime _resolverBuildFailedAtUtc = DateTime.MinValue;

    public DcsTagOperations(IDcsTagRuntime runtime, ILogger? logger = null)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public DcsApiOutcome<object?> ReadConditionCache(string? groupName) =>
        DcsApiOutcome<object?>.Ok(null);

    /// <summary>
    /// 读取前准备：把请求的点位解析为物理点，未在采集的先从 PLC 读一次。
    /// 解析失败或无权限的点不处理，由随后的读取给出原有的错误。
    /// </summary>
    public Task PrepareReadAsync(IEnumerable<string?>? requestedTags, UserDef? user, CancellationToken cancellationToken,
        bool activate = false)
    {
        if (_runtime.Reloading || requestedTags is null) return Task.CompletedTask;
        var resolver = ReadResolver();
        var physical = new List<string>();
        foreach (string? requested in requestedTags.Take(MaxBatchSize))
        {
            if (string.IsNullOrWhiteSpace(requested)) continue;
            if (resolver.TryResolve(requested, out string tag, out _) && CanRead(user, tag)) physical.Add(tag);
        }
        return physical.Count == 0 ? Task.CompletedTask : _runtime.RefreshIdleAsync(physical, cancellationToken, activate);
    }

    /// <param name="activate">
    /// true = 顺带把这批点拉进活动窗口(持续盯屏的调用方才需要)。
    /// 默认 false：取一次当前值不该让点位被白白轮询 activeWindowSec——
    /// 一次诊断读上千个点、读完就走，没必要进扫描计划。
    /// </param>
    public DcsApiOutcome<object?> ReadOne(string? requestedTag, UserDef? user, bool activate = false)
    {
        if (_runtime.Reloading) return DcsApiOutcome<object?>.Unavailable("网关正在热重载，请稍后重试");
        if (string.IsNullOrWhiteSpace(requestedTag))
            return DcsApiOutcome<object?>.BadRequest("参数为空");

        var resolver = ReadResolver();
        if (!resolver.TryResolve(requestedTag, out string physical, out string error))
            return DcsApiOutcome<object?>.OperationFailure(error);
        if (!CanRead(user, physical))
            return DcsApiOutcome<object?>.OperationFailure($"无该点位读取权限:{requestedTag.Trim()}");

        if (activate) _runtime.Touch(physical);
        if (!_runtime.TryGet(physical, out DataValue data))
            return DcsApiOutcome<object?>.OperationFailure($"点位不存在:{requestedTag.Trim()}");
        if (!data.Quality.IsGood)
            return DcsApiOutcome<object?>.OperationFailure(
                $"点位质量异常:{requestedTag.Trim()} {data.Quality}");
        return DcsApiOutcome<object?>.Ok(DcsTagValueConverter.ToWireValue(data));
    }

    public DcsApiOutcome<List<string>> ReadAsJsonStrings(IReadOnlyList<string>? tags, UserDef? user, bool activate = false)
    {
        var read = ReadMany(tags, user, activate);
        if (read.Error is not null) return Failure<List<string>>(read.StatusCode, read.Error);
        return DcsApiOutcome<List<string>>.Ok(
            read.Values.Select(item => JsonSerializer.Serialize(item.Value, DcsJson.Options)).ToList());
    }

    public DcsApiOutcome<List<DcsKeyValue<object?>>> ReadAsList(IReadOnlyList<string>? tags, UserDef? user, bool activate = false)
    {
        var read = ReadMany(tags, user, activate);
        if (read.Error is not null) return Failure<List<DcsKeyValue<object?>>>(read.StatusCode, read.Error);
        return DcsApiOutcome<List<DcsKeyValue<object?>>>.Ok(
            read.Values.Select(item => new DcsKeyValue<object?>(item.Requested, item.Value)).ToList());
    }

    /// <summary>
    /// 逐点带质量读取：单个点不存在/无权限/质量不好只体现在该点的 IsGood/StatusCode/Reason 上，
    /// 整个请求只在参数错误(400)或热重载(503)时失败。顺序与请求一致，重复别名按出现次数返回。
    /// </summary>
    public DcsApiOutcome<List<DcsTagQuality>> ReadWithQuality(IReadOnlyList<string>? tags, UserDef? user, bool activate = false)
    {
        if (_runtime.Reloading) return DcsApiOutcome<List<DcsTagQuality>>.Unavailable("网关正在热重载，请稍后重试");
        if (tags is not { Count: > 0 }) return DcsApiOutcome<List<DcsTagQuality>>.BadRequest("参数为空");
        if (tags.Count > MaxBatchSize) return DcsApiOutcome<List<DcsTagQuality>>.BadRequest($"单次最多处理 {MaxBatchSize} 个点位");
        if (tags.Any(tag => string.IsNullOrWhiteSpace(tag)))
            return DcsApiOutcome<List<DcsTagQuality>>.BadRequest("点位名称不能为空");

        var resolver = ReadResolver();
        var rows = new List<DcsTagQuality>(tags.Count);
        foreach (string requestedRaw in tags)
        {
            string requested = requestedRaw.Trim();
            if (!resolver.TryResolve(requested, out string physical, out _) || !_runtime.TryGet(physical, out DataValue data))
            {
                rows.Add(Missing(requested, StatusCode.BadNodeIdUnknown, DcsQualityReason.NotRegistered));
                continue;
            }
            if (!CanRead(user, physical))
            {
                rows.Add(Missing(requested, new StatusCode(0x801F0000), DcsQualityReason.AccessDenied));
                continue;
            }
            if (activate) _runtime.Touch(physical);
            DateTime? timestamp = data.SourceTimestamp == default ? null : DateTime.SpecifyKind(data.SourceTimestamp, DateTimeKind.Utc);
            rows.Add(new DcsTagQuality(requested,
                data.Quality.IsGood ? DcsTagValueConverter.ToWireValue(data) : null,
                data.Quality.IsGood, data.Quality.Code, data.Quality.SymbolicName, timestamp,
                data.Quality.IsGood ? null : QualityReason(data.Quality, _runtime.DeviceStateOf(physical))));
        }
        return DcsApiOutcome<List<DcsTagQuality>>.Ok(rows);

        static DcsTagQuality Missing(string key, StatusCode status, string reason) =>
            new(key, null, false, status.Code, status.SymbolicName, null, reason);
    }

    /// <summary>
    /// 质量 → 原因分类(规则写在 docs/dcs-read-quality.md，保持稳定)。
    /// BadNotConnected 是点注册后尚未采到首值时的初始质量，也是设备断开时的质量：
    /// 设备当前已连上 → WaitingForData(连上后首轮采集/符号解析期间)，否则 → CommFailure。
    /// </summary>
    internal static string QualityReason(StatusCode quality, ConnectionState? deviceState)
    {
        uint code = quality.Code & 0xFFFF0000u;
        return code switch
        {
            0x80340000u => DcsQualityReason.SymbolNotFound,
            0x800A0000u => DcsQualityReason.Timeout,
            0x80320000u => DcsQualityReason.WaitingForData,   // BadWaitingForInitialData(点定义变更后等首值)
            0x80050000u => DcsQualityReason.CommFailure,
            0x808A0000u => deviceState == ConnectionState.Connected ? DcsQualityReason.WaitingForData : DcsQualityReason.CommFailure,
            0x801F0000u => DcsQualityReason.AccessDenied,
            _ => DcsQualityReason.DeviceError,
        };
    }

    public DcsApiOutcome<Dictionary<string, object?>> ReadAsDictionary(IReadOnlyList<string>? tags, UserDef? user, bool activate = false)
    {
        if (tags is { Count: > 0 }
            && tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .GroupBy(tag => tag.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return DcsApiOutcome<Dictionary<string, object?>>.OperationFailure("请求包含重复点位，无法生成字典");

        var read = ReadMany(tags, user, activate);
        if (read.Error is not null) return Failure<Dictionary<string, object?>>(read.StatusCode, read.Error);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in read.Values) result.Add(item.Requested, item.Value);
        return DcsApiOutcome<Dictionary<string, object?>>.Ok(result);
    }

    public DcsApiOutcome<Dictionary<string, string>> GetTypes(IReadOnlyList<string>? tags, UserDef? user)
    {
        if (_runtime.Reloading)
            return DcsApiOutcome<Dictionary<string, string>>.Unavailable("网关正在热重载，请稍后重试");
        if (tags is not { Count: > 0 })
            return DcsApiOutcome<Dictionary<string, string>>.BadRequest("参数为空");
        if (tags.Count > MaxBatchSize)
            return DcsApiOutcome<Dictionary<string, string>>.BadRequest($"单次最多处理 {MaxBatchSize} 个点位");

        var resolver = ReadResolver();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string requestedRaw in tags)
        {
            string requested = requestedRaw?.Trim() ?? "";
            if (requested.Length == 0)
                return DcsApiOutcome<Dictionary<string, string>>.BadRequest("点位名称不能为空");
            if (result.ContainsKey(requested))
                return DcsApiOutcome<Dictionary<string, string>>.OperationFailure("请求包含重复点位，无法生成字典");
            if (!resolver.TryResolve(requested, out string physical, out string error))
                return DcsApiOutcome<Dictionary<string, string>>.OperationFailure(error);
            if (!CanRead(user, physical))
                return DcsApiOutcome<Dictionary<string, string>>.OperationFailure($"无该点位读取权限:{requested}");
            if (!_runtime.TryGetDef(physical, out TagDef definition))
                return DcsApiOutcome<Dictionary<string, string>>.OperationFailure($"点位不存在:{requested}");
            result.Add(requested, DcsTagValueConverter.GetDcsTypeName(definition));
        }
        return DcsApiOutcome<Dictionary<string, string>>.Ok(result);
    }

    public DcsApiOutcome<bool> CheckValidity(IReadOnlyList<DcsTagValue>? tags, UserDef? user)
    {
        var validation = ValidateWrites(tags, user, requireWritePermission: false, requireWritableTag: false);
        return validation.Error is null
            ? DcsApiOutcome<bool>.Ok(true)
            : Failure<bool>(validation.StatusCode, validation.Error);
    }

    public Task<DcsApiOutcome<bool>> WriteOneAsync(
        DcsTagValue? tag, UserDef? user, CancellationToken cancellationToken) =>
        tag is null
            ? Task.FromResult(DcsApiOutcome<bool>.BadRequest("参数为空"))
            : WriteManyAsync([tag], user, cancellationToken);

    public async Task<DcsApiOutcome<bool>> WriteManyAsync(
        IReadOnlyList<DcsTagValue>? tags, UserDef? user, CancellationToken cancellationToken)
    {
        var validation = ValidateWrites(tags, user, requireWritePermission: true, requireWritableTag: true);
        if (validation.Error is not null) return Failure<bool>(validation.StatusCode, validation.Error);

        var groupTasks = validation.Items
            .GroupBy(item => item.Device, StringComparer.OrdinalIgnoreCase)
            .Select(group => WriteDeviceGroupAsync(group.OrderBy(item => item.Index).ToList(), cancellationToken))
            .ToArray();
        string?[] errors = await Task.WhenAll(groupTasks);
        string? error = errors.FirstOrDefault(item => item is not null);
        return error is null
            ? DcsApiOutcome<bool>.Ok(true)
            : DcsApiOutcome<bool>.OperationFailure(error);
    }

    public async Task<DcsApiOutcome<string>> AddTagsAsync(
        IReadOnlyList<DcsTagUpdateModel>? tags, UserDef? user, CancellationToken cancellationToken)
    {
        if (_runtime.Reloading) return DcsApiOutcome<string>.Unavailable("网关正在热重载，请稍后重试");
        if (tags is not { Count: > 0 }) return DcsApiOutcome<string>.BadRequest("参数为空");
        if (user is { CanWrite: false }) return DcsApiOutcome<string>.OperationFailure("当前用户没有工程写权限");
        if (!DcsTagProjectUpdater.TryBuild(_runtime.Project, tags, out IgwProject updated, out string error))
            return DcsApiOutcome<string>.OperationFailure(error);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runtime.ReloadAsync(updated);
            return DcsApiOutcome<string>.Ok(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DcsApiOutcome<string>.OperationFailure($"工程应用失败:{ex.Message}");
        }
    }

    private (int StatusCode, string? Error, List<(string Requested, object? Value)> Values) ReadMany(
        IReadOnlyList<string>? tags, UserDef? user, bool activate = false)
    {
        if (_runtime.Reloading) return (503, "网关正在热重载，请稍后重试", []);
        if (tags is not { Count: > 0 }) return (400, "参数为空", []);
        if (tags.Count > MaxBatchSize) return (400, $"单次最多处理 {MaxBatchSize} 个点位", []);

        var resolver = ReadResolver();
        var values = new List<(string Requested, object? Value)>(tags.Count);
        foreach (string requestedRaw in tags)
        {
            string requested = requestedRaw?.Trim() ?? "";
            if (requested.Length == 0) return (400, "点位名称不能为空", []);
            if (!resolver.TryResolve(requested, out string physical, out string error)) return (200, error, []);
            if (!CanRead(user, physical)) return (200, $"无该点位读取权限:{requested}", []);
            if (activate) _runtime.Touch(physical);
            if (!_runtime.TryGet(physical, out DataValue data)) return (200, $"点位不存在:{requested}", []);
            if (!data.Quality.IsGood) return (200, $"点位质量异常:{requested} {data.Quality}", []);
            values.Add((requested, DcsTagValueConverter.ToWireValue(data)));
        }
        return (200, null, values);
    }

    /// <summary>
    /// 解析器懒构建 + 后台重建：首次访问（尚无旧快照）只能同步构建，会阻塞本次调用；
    /// 此后一旦发现 <c>_runtime.Project</c> 已变化（如批量注册点位后整体换对象），
    /// 改为在后台线程重建，本次调用立即返回旧快照（不阻塞读取）；构建完成后在锁内
    /// 整体发布新的 <see cref="ResolverSnapshot"/>（解析器与其所属工程一起换），
    /// 读者不会读到"新解析器配旧工程"这种撕裂组合。
    /// 读取：旧快照命中直接返回（共有点位不等待）；未命中时经 <see cref="ReadResolver"/> 追一次当前工程
    /// （等待进行中的重建或同步构建）再查，新设备的别名不会误报「不存在」。
    /// 写入：一律经 <see cref="CurrentResolver"/> 按当前工程解析，绝不经旧快照写到改挂前的 PLC。
    /// </summary>
    private ResolverSnapshot Snapshot()
    {
        IgwProject project = _runtime.Project;
        var snapshot = Volatile.Read(ref _resolverSnapshot);
        if (snapshot is not null && ReferenceEquals(snapshot.Project, project)) return snapshot;

        lock (_resolverLock)
        {
            if (_resolverSnapshot is null)
            {
                // 首次构建：没有旧快照可退，只能同步构建（会短暂阻塞本次调用）。
                var first = new ResolverSnapshot(BuildResolver(project), project);
                Volatile.Write(ref _resolverSnapshot, first);
                return first;
            }

            // 同一时刻只允许一次后台重建在飞行中（用 _resolverBuild 的完成状态做互斥），
            // 避免同一工程被并发重复解析多次。若重建期间工程又变了，重建任务内部会追到最新，
            // 不会把过期结果原子替换回去（见 RebuildResolverInBackground 的循环校验）。
            // 上一次重建失败后，在退避窗口内不再发起新的尝试，避免对着持续失败的工程反复重试。
            bool projectChanged = !ReferenceEquals(_resolverSnapshot.Project, project);
            bool buildIdle = _resolverBuild is null || _resolverBuild.IsCompleted;
            bool pastBackoff = DateTime.UtcNow - _resolverBuildFailedAtUtc >= ResolverRebuildFailureBackoff;
            if (projectChanged && buildIdle && pastBackoff)
            {
                _resolverBuild = Task.Run(RebuildResolverInBackground);
            }
            return _resolverSnapshot;
        }
    }

    private DcsTagAliasResolver BuildResolver(IgwProject project) =>
        new(project, tagId => _runtime.TryGetDef(tagId, out _));

    /// <summary>读取用解析器：先用（可能滞后的）快照，命中即返回；未命中且快照滞后时追一次当前工程再查。</summary>
    private ReadAliasResolver ReadResolver()
    {
        ResolverSnapshot snapshot = Snapshot();
        return new ReadAliasResolver(this, snapshot.Resolver, ReferenceEquals(snapshot.Project, _runtime.Project));
    }

    /// <summary>
    /// 为当前工程构建的解析器：快照已是当前工程直接返回；否则最多等待 <see cref="ResolverCatchUpTimeout"/>
    /// 让进行中的后台重建落地，仍未落地则在本线程同步构建并发布。
    /// 失败退避窗口内（<paramref name="allowBuildDuringBackoff"/>=false）不同步构建，返回 null。构建失败返回 null。
    /// </summary>
    private DcsTagAliasResolver? CurrentResolver(bool allowBuildDuringBackoff)
    {
        ResolverSnapshot snapshot = Snapshot();
        IgwProject project = _runtime.Project;
        if (ReferenceEquals(snapshot.Project, project)) return snapshot.Resolver;

        Task? build;
        lock (_resolverLock) build = _resolverBuild;
        if (build is { IsCompleted: false })
            build.Wait(CatchUpTimeout);   // 后台任务自行捕获异常，不会抛出
        snapshot = Volatile.Read(ref _resolverSnapshot)!;
        project = _runtime.Project;
        if (ReferenceEquals(snapshot.Project, project)) return snapshot.Resolver;

        // 同步兜底构建按工程单飞：同一工程上等不到后台重建的并发调用方共享一次构建，而不是各建一份
        TaskCompletionSource<DcsTagAliasResolver?>? owned = null;
        Task<DcsTagAliasResolver?> shared;
        lock (_resolverLock)
        {
            bool inBackoff = DateTime.UtcNow - _resolverBuildFailedAtUtc < ResolverRebuildFailureBackoff;
            if (_fallbackBuild is { } running && ReferenceEquals(running.Project, project))
                shared = running.Build;
            else if (inBackoff && !allowBuildDuringBackoff)
                return null;
            else
            {
                owned = new TaskCompletionSource<DcsTagAliasResolver?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _fallbackBuild = (project, owned.Task);
                shared = owned.Task;
            }
        }
        if (owned is null) return shared.GetAwaiter().GetResult();

        DcsTagAliasResolver? built = null;
        try
        {
            Interlocked.Increment(ref _synchronousBuildCount);
            built = BuildResolver(project);
            lock (_resolverLock)
            {
                if (ReferenceEquals(_runtime.Project, project)
                    && !ReferenceEquals(_resolverSnapshot?.Project, project))
                    Volatile.Write(ref _resolverSnapshot, new ResolverSnapshot(built, project));
            }
        }
        catch (Exception ex)
        {
            lock (_resolverLock) { _resolverBuildFailedAtUtc = DateTime.UtcNow; }
            LogRebuildFailure(ex);
        }
        finally
        {
            lock (_resolverLock)
                if (_fallbackBuild is { } current && ReferenceEquals(current.Build, owned.Task)) _fallbackBuild = null;
            owned.SetResult(built);
        }
        return built;
    }

    /// <summary>一次请求内的读取解析：旧快照命中直接用；未命中时至多追一次当前工程（之后都用追上的解析器）。</summary>
    private sealed class ReadAliasResolver(DcsTagOperations owner, DcsTagAliasResolver resolver, bool current)
    {
        private DcsTagAliasResolver _resolver = resolver;
        private bool _current = current;

        public bool TryResolve(string requested, out string physical, out string error)
        {
            if (_resolver.TryResolve(requested, out physical, out error)) return true;
            if (_current) return false;
            _current = true;
            DcsTagAliasResolver? caughtUp = owner.CurrentResolver(allowBuildDuringBackoff: false);
            if (caughtUp is null) return false;
            _resolver = caughtUp;
            return _resolver.TryResolve(requested, out physical, out error);
        }
    }

    /// <summary>
    /// 后台线程重建别名解析器。循环追最新工程对象直到落地时仍是最新，
    /// 保证"重建期间工程又变了"时不丢更新，也不会用旧构建覆盖更新的构建。
    /// </summary>
    private void RebuildResolverInBackground()
    {
        try
        {
            while (true)
            {
                IgwProject target = _runtime.Project;
                var built = new DcsTagAliasResolver(target, tagId => _runtime.TryGetDef(tagId, out _));
                lock (_resolverLock)
                {
                    // 写入/读取路径可能已为当前工程同步构建并发布：不要用本次（可能更旧的）结果覆盖它
                    if (ReferenceEquals(_resolverSnapshot?.Project, _runtime.Project)) return;
                    Volatile.Write(ref _resolverSnapshot, new ResolverSnapshot(built, target));
                    if (ReferenceEquals(_runtime.Project, target)) return;
                    // 构建这段时间里工程又变了：不退出，继续在本次后台任务内为最新工程重建。
                }
            }
        }
        catch (Exception ex)
        {
            lock (_resolverLock) { _resolverBuildFailedAtUtc = DateTime.UtcNow; }
            // 旧快照原样保留；退避窗口过后下一次读取发现工程仍不匹配时会自动重试重建。
            LogRebuildFailure(ex);
        }
    }

    private void LogRebuildFailure(Exception ex)
    {
        if (_logger is not null)
            _logger.LogError(ex, "[DcsTagOperations] 重建 DCS 别名解析器失败");
        else
            // 本类未注入日志器时兜底写 stderr，避免异常静默丢失，
            // 参考 IGW.Runtime/Program.cs、IGW.LogService/Program.cs 对顶层失败的既有处理方式。
            Console.Error.WriteLine($"[DcsTagOperations] 重建 DCS 别名解析器失败：{ex}");
    }

    private static bool CanRead(UserDef? user, string physicalTag) =>
        user?.ReadBranches is not { Count: > 0 }
        || TagBranchMatcher.AreAllWithin([physicalTag], user.ReadBranches);

    private static DcsApiOutcome<T> Failure<T>(int statusCode, string error) => statusCode switch
    {
        400 => DcsApiOutcome<T>.BadRequest(error),
        503 => DcsApiOutcome<T>.Unavailable(error),
        _ => DcsApiOutcome<T>.OperationFailure(error),
    };

    private (int StatusCode, string? Error, List<ValidatedWrite> Items) ValidateWrites(
        IReadOnlyList<DcsTagValue>? tags,
        UserDef? user,
        bool requireWritePermission,
        bool requireWritableTag)
    {
        if (_runtime.Reloading) return (503, "网关正在热重载，请稍后重试", []);
        if (tags is not { Count: > 0 }) return (400, "参数为空", []);
        if (tags.Count > MaxBatchSize) return (400, $"单次最多处理 {MaxBatchSize} 个点位", []);
        if (requireWritePermission && user is { CanWrite: false }) return (200, "当前用户没有 PLC 写权限", []);

        // 写入必须按当前工程解析：逻辑设备改挂后，旧快照会把写入发到旧 PLC
        DcsTagAliasResolver? resolver = CurrentResolver(allowBuildDuringBackoff: true);
        if (resolver is null) return (503, "点位别名解析器正在重建，请稍后重试", []);
        var items = new List<ValidatedWrite>(tags.Count);
        for (int index = 0; index < tags.Count; index++)
        {
            DcsTagValue? tag = tags[index];
            if (tag is null || string.IsNullOrWhiteSpace(tag.Key)) return (400, $"索引 {index} 的点位名称不能为空", []);
            string requested = tag.Key.Trim();
            if (!resolver.TryResolve(requested, out string physical, out string resolveError))
                return (200, resolveError, []);
            if (!CanRead(user, physical)) return (200, $"无该点位读取权限:{requested}", []);
            if (!_runtime.TryGetDef(physical, out TagDef definition)) return (200, $"点位不存在:{requested}", []);
            if (requireWritableTag && definition.Access == TagAccess.Read) return (200, $"点位为只读:{requested}", []);
            if (!DcsTagValueConverter.TryConvertWriteValue(tag.Value, definition, out object? value, out string convertError))
                return (200, $"{requested}变量的值格式不正确:{convertError}", []);
            items.Add(new ValidatedWrite(index, requested, physical, DeviceOf(physical), value!, definition));
        }
        return (200, null, items);
    }

    private async Task<string?> WriteDeviceGroupAsync(
        IReadOnlyList<ValidatedWrite> items, CancellationToken cancellationToken)
    {
        foreach (ValidatedWrite item in items)
        {
            string? error = await WriteConfirmedAsync(item, cancellationToken);
            if (error is not null) return error;
        }
        return null;
    }

    private async Task<string?> WriteConfirmedAsync(ValidatedWrite item, CancellationToken cancellationToken)
    {
        StatusCode lastStatus = StatusCode.Bad;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _runtime.Touch(item.PhysicalTag);
            lastStatus = item.Value is ArrayRuntimeValue array
                ? (await _runtime.WriteArrayAsync(item.PhysicalTag, array, cancellationToken)).Status
                : await _runtime.WriteAsync(item.PhysicalTag, item.Value, cancellationToken);
            if (lastStatus.IsGood && ReadBackMatches(item)) return null;
            if (attempt < 2)
            {
                int delayMs = Math.Clamp(item.Definition.ScanRateMs, 20, 1000);
                await Task.Delay(delayMs, cancellationToken);
            }
        }
        return $"PLC 写入或回读确认失败:{item.RequestedTag} {lastStatus}";
    }

    private bool ReadBackMatches(ValidatedWrite item)
    {
        if (!_runtime.TryGet(item.PhysicalTag, out DataValue data) || !data.Quality.IsGood) return false;
        object? actual = data.Value;
        object expected = item.Value;
        if (expected is float expectedFloat && actual is float actualFloat)
            return NearlyEqual(expectedFloat, actualFloat);
        if (expected is double expectedDouble && actual is double actualDouble)
            return NearlyEqual(expectedDouble, actualDouble);
        if (expected is ArrayRuntimeValue expectedRuntime && actual is ArrayRuntimeValue actualRuntime)
            return expectedRuntime.HasSameContent(actualRuntime);
        if (expected is Array expectedArray && actual is Array actualArray)
            return ArraysEqual(expectedArray, actualArray);
        return Equals(expected, actual);
    }

    private static bool ArraysEqual(Array left, Array right)
    {
        if (left.Length != right.Length) return false;
        var l = left.GetEnumerator();
        var r = right.GetEnumerator();
        while (l.MoveNext() && r.MoveNext())
            if (!Equals(l.Current, r.Current)) return false;
        return true;
    }

    private static bool NearlyEqual(double expected, double actual)
    {
        double scale = Math.Max(1d, Math.Max(Math.Abs(expected), Math.Abs(actual)));
        return Math.Abs(expected - actual) <= 1e-6d * scale;
    }

    private static string DeviceOf(string physicalTag)
    {
        int first = physicalTag.IndexOf('.');
        int second = first < 0 ? -1 : physicalTag.IndexOf('.', first + 1);
        return second > 0 ? physicalTag[..second] : physicalTag;
    }
}
