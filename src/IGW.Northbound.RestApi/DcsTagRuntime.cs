using IGW.Abstractions;
using IGW.Core;

namespace IGW.Northbound.RestApi;

internal interface IDcsTagRuntime
{
    IgwProject Project { get; }
    bool Reloading { get; }
    bool TryGet(string tagId, out DataValue value);
    bool TryGetDef(string tagId, out TagDef definition);
    void Touch(string tagId);
    /// <summary>点所属设备的连接状态(设备不存在或未运行为 null)。</summary>
    ConnectionState? DeviceStateOf(string tagId) => null;
    /// <summary>读取前为未在采集的点补读 PLC，保证返回最新值。</summary>
    /// <param name="activate">true = 顺带拉进活动窗口；默认 false，取值不该开启轮询。</param>
    Task RefreshIdleAsync(IReadOnlyCollection<string> tagIds, CancellationToken cancellationToken, bool activate = false) => Task.CompletedTask;
    Task<StatusCode> WriteAsync(string tagId, object value, CancellationToken cancellationToken);
    Task<ArrayWriteResult> WriteArrayAsync(string tagId, ArrayRuntimeValue value, CancellationToken cancellationToken);
    Task ReloadAsync(IgwProject project);
}

internal sealed class GatewayDcsTagRuntime : IDcsTagRuntime
{
    private readonly IGatewayHost _host;

    public GatewayDcsTagRuntime(IGatewayHost host) => _host = host;

    public IgwProject Project => _host.Project;
    public bool Reloading => _host.Reloading;
    public bool TryGet(string tagId, out DataValue value) => _host.Hub.TryGet(tagId, out value);
    public bool TryGetDef(string tagId, out TagDef definition) => _host.Hub.TryGetDef(tagId, out definition);
    public void Touch(string tagId) => _host.Hub.Touch(tagId);
    public ConnectionState? DeviceStateOf(string tagId) =>
        _host.Hub.TryGetDeviceName(tagId, out string device) ? _host.Engine.DeviceState(device) : null;
    public Task RefreshIdleAsync(IReadOnlyCollection<string> tagIds, CancellationToken cancellationToken, bool activate = false) =>
        _host.Engine.RefreshIdleAsync(tagIds, IgwRestApi.GetRefreshTimeout, cancellationToken, activate);
    public Task<StatusCode> WriteAsync(string tagId, object value, CancellationToken cancellationToken) =>
        _host.Engine.WriteAsync(tagId, value, cancellationToken);
    public Task<ArrayWriteResult> WriteArrayAsync(
        string tagId, ArrayRuntimeValue value, CancellationToken cancellationToken)
    {
        if (!_host.Hub.TryGetDef(tagId, out var definition) || definition.Array is null)
            return Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadNotConnected, $"数组标记不存在:{tagId}"));
        var slice = new ArraySlice(definition.Array.LowerBounds, definition.Array.Dimensions);
        return _host.Engine.WriteArrayAsync(tagId, slice, value, cancellationToken);
    }
    public Task ReloadAsync(IgwProject project) => _host.ReloadAsync(project);
}
