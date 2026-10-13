namespace IGW.Core;

/// <summary>
/// 按设备应用(<see cref="RuntimeEngine.ApplyDevicesAsync"/>)的结果。
/// Restarted:实际重建的设备全名;RemovedTagIds:从 Hub 注销的点;AddedTagIds:新登记的点。
/// </summary>
public sealed record ScopedDeviceApplyResult(
    IReadOnlyList<string> Restarted,
    IReadOnlyList<string> RemovedTagIds,
    IReadOnlyList<string> AddedTagIds)
{
    /// <summary>未应用任何设备(局部修改不涉及设备时)。</summary>
    public static ScopedDeviceApplyResult Empty { get; } = new([], [], []);
}
