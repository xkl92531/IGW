using IGW.Abstractions;

namespace IGW.Core;

/// <summary>
/// 实时点值协议的版本只包含会改变点位身份、句柄来源、准入限制或读取权限的配置。
/// MES 画布、日志保留和其它北向配置变化不能让所有浏览器实时流无条件断线。
/// </summary>
public static class RealtimeProjectRevision
{
    private sealed record Fingerprint(
        string Channels,
        IReadOnlyList<DeviceTemplate> Templates,
        IReadOnlyList<ComputedTagDef> ComputedTags,
        StreamFingerprint Stream,
        SecuritySettings Security);

    private sealed record StreamFingerprint(
        int MaxTagsPerStream,
        int MaxStreams,
        int HeartbeatSeconds,
        int SnapshotIntervalSeconds,
        int MaxConcurrentSnapshots,
        int DeltaBatchWindowMs,
        int CoordinatorQueueCapacity)
    {
        public static StreamFingerprint Of(TagStreamSettings settings) => new(
            settings.MaxTagsPerStream,
            settings.MaxStreams,
            settings.HeartbeatSeconds,
            settings.SnapshotIntervalSeconds,
            settings.MaxConcurrentSnapshots,
            settings.DeltaBatchWindowMs,
            settings.CoordinatorQueueCapacity);
    }

    public static string Compute(IgwProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        TagStreamSettings stream = project.Settings?.TagStream ?? new TagStreamSettings();
        return JsonHash.OfRuntime(new Fingerprint(
            JsonHash.OfRuntimeChannels(project.Channels ?? []),
            project.Templates ?? [],
            project.ComputedTags ?? [],
            StreamFingerprint.Of(stream),
            project.Security ?? new SecuritySettings()));
    }
}
