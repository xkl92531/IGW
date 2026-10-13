namespace IGW.Abstractions;

/// <summary>驱动对数组读写和分包的真实能力。</summary>
public sealed record ArrayDriverCapabilities(
    bool NativeArrays,
    bool StructuredArrays,
    bool MultiDimensional,
    bool SliceRead,
    bool SliceWrite,
    bool AtomicWholeWrite,
    int MaxElementsPerRequest,
    int MaxBytesPerRequest);

/// <summary>数组切片写入请求；Binding 指向唯一的父数组逻辑点。</summary>
public sealed record ArrayWriteRequest(TagBinding Binding, ArraySlice Slice, ArrayRuntimeValue Value);

/// <summary>某个元素（或空索引表示整个分块）的写入失败。</summary>
public sealed record ArrayElementFailure(
    IReadOnlyList<int> Indices,
    StatusCode Status,
    string Message = "");

/// <summary>数组写入的精确结果；部分成功不得伪装成整体成功。</summary>
public sealed record ArrayWriteResult(
    StatusCode Status,
    int Succeeded,
    IReadOnlyList<ArrayElementFailure> Failures)
{
    public bool IsPartial => Succeeded > 0 && Failures.Count > 0;

    public static ArrayWriteResult Failed(StatusCode status, string message) =>
        new(status, 0, [new ArrayElementFailure([], status, message)]);
}

/// <summary>可选数组能力；数组读取仍通过 IDeviceDriver.ReadAsync 返回一个 ArrayRuntimeValue。</summary>
public interface IArrayCapableDriver
{
    ArrayDriverCapabilities ArrayCapabilities { get; }
    Task<ArrayWriteResult> WriteArrayAsync(ArrayWriteRequest request, CancellationToken ct);
}
