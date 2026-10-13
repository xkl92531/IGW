using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public interface ISiemensClassicTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    string LastError => "";
    int MaxTagsPerRead => 19;
    int MaxPayloadBytes => 200;
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct);
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct);
    ArrayDriverCapabilities ArrayCapabilities => new(
        NativeArrays: false, StructuredArrays: true, MultiDimensional: true,
        SliceRead: false, SliceWrite: true, AtomicWholeWrite: false,
        MaxElementsPerRequest: int.MaxValue, MaxBytesPerRequest: 200);
    Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        Task.FromResult(ArrayWriteResult.Failed(StatusCode.BadDeviceFailure,
            "当前 S7 Classic 传输未实现数组写入。"));
}

public sealed class SiemensClassicTransport : ISiemensClassicTransport, IDriverPriorityWorkAware
{
    private readonly IoTPlcDeviceDriver _driver;

    public SiemensClassicTransport(IoTPlcDeviceDriver driver) => _driver = driver;
    public bool IsConnected => _driver.State == ConnectionState.Connected;
    public string LastError => _driver.LastReadError;
    public int MaxTagsPerRead => _driver.Capabilities.MaxTagsPerRead;
    public int MaxPayloadBytes => _driver.Capabilities.MaxReadBytes;
    public void SetPriorityWorkProbe(Func<bool> priorityWorkPending) =>
        _driver.SetPriorityWorkProbe(priorityWorkPending);
    public Task ConnectAsync(CancellationToken ct) => _driver.ConnectAsync(ct);
    public Task DisconnectAsync() => _driver.DisconnectAsync();
    public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) => _driver.ReadAsync(tags, ct);
    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) => _driver.WriteAsync(writes, ct);
    public ArrayDriverCapabilities ArrayCapabilities => _driver.ArrayCapabilities;
    public Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct) =>
        _driver.WriteArrayAsync(request, ct);
    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}
