using System.Runtime.InteropServices;
using System.Text;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

public enum SiemensPlusResultCode
{
    Ok = 0,
    AuthenticationRequired = 10,
    AuthenticationFailed = 11,
    PermissionDenied = 12,
    UnsupportedFirmware = 20,
    Timeout = 30,
    TransportFailure = 31,
    ProtocolFailure = 32,
    // Only positively identified protocol capacity pressure. The current Rust/pinned
    // library cannot identify opaque PLC return_value codes and never guesses this code.
    CapacityFailure = 33,
    // ABI v2：单个符号在 PLC 程序中找不到(解析失败或 PLC 对该项报错)，只影响该项。
    SymbolNotFound = 34,
    // ABI v3：逐项写入时 PLC 拒绝了该项(类型不符、只读、受保护等)，只影响该项。
    ItemRejected = 35,
    // ABI v4：订阅 id 不存在(已删除或连接重建过)。
    SubscriptionNotFound = 36,
    InvalidArgument = 40,
    BufferTooSmall = 41,
    Panic = 99,
}

public sealed record SiemensPlusNativeBrowseTag(string Symbol, byte SoftDataType, ushort StringMaxLength);
public sealed record SiemensPlusNativeBrowseResult(SiemensPlusResultCode Code, IReadOnlyList<SiemensPlusNativeBrowseTag> Tags);
/// <summary>PLC 上的一个数据块：块名与 DB 号(ABI v5)。</summary>
public sealed record SiemensPlusNativeDataBlock(string Name, int Number);
public sealed record SiemensPlusNativeDataBlocksResult(SiemensPlusResultCode Code, IReadOnlyList<SiemensPlusNativeDataBlock> Blocks);
public sealed record SiemensPlusNativeValue(SiemensPlusResultCode Code, object? Value);
/// <param name="ItemCodes">与传入符号一一对应：Ok = 已登记，SymbolNotFound = 该符号解析不到(只影响该项)。</param>
public sealed record SiemensPlusNativeSubscribeResult(
    SiemensPlusResultCode Code, uint SubscriptionId, IReadOnlyList<SiemensPlusResultCode> ItemCodes);
/// <param name="Values">(订阅时的项序号, 值)；空表示这一轮没有变化(心跳)。</param>
public sealed record SiemensPlusNativeNotification(
    SiemensPlusResultCode Code, IReadOnlyList<(int Slot, object? Value)> Values);
public sealed record SiemensPlusNativeReadResult(SiemensPlusResultCode Code, IReadOnlyList<SiemensPlusNativeValue> Values,
    bool LocalBufferRetried = false);
public sealed record SiemensPlusNativeWrite(string Symbol, object? Value);
/// <summary>逐项写入结果：Code 非 Ok 表示整批失败(此时 Items 为空)；否则 Items 与请求一一对应。</summary>
public sealed record SiemensPlusNativeWriteResult(SiemensPlusResultCode Code, IReadOnlyList<SiemensPlusResultCode> Items);

public interface ISiemensPlusNativeApi : IDisposable
{
    SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy);
    SiemensPlusResultCode Authenticate(string username, string password);
    SiemensPlusResultCode Probe();
    SiemensPlusNativeBrowseResult Browse();
    SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols);
    SiemensPlusNativeReadResult ReadMeasured(IReadOnlyList<string> symbols, Action beforeRequest,
        ProtocolRequestDescriptor descriptor)
    {
        beforeRequest();
        return ProtocolOperationTiming.MeasureResult(() => Read(symbols),
            static result => result.Code != SiemensPlusResultCode.Ok, descriptor);
    }
    SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes);

    /// <summary>ABI v4：建立变化订阅。返回每项结果 + 订阅句柄；原生库低于 v4 时返回 null。</summary>
    SiemensPlusNativeSubscribeResult? Subscribe(IReadOnlyList<string> symbols, ushort cycleTimeMs) => null;

    /// <summary>ABI v4：取下一批通知值。阻塞到 PLC 推来通知或读超时(超时返回空列表)。</summary>
    SiemensPlusNativeNotification PollNotification(uint subscriptionId, int capacity) =>
        new(SiemensPlusResultCode.SubscriptionNotFound, []);

    /// <summary>ABI v4：删除订阅。</summary>
    SiemensPlusResultCode DeleteSubscription(uint subscriptionId) => SiemensPlusResultCode.Ok;

    /// <summary>ABI v5：列出 PLC 上的数据块(块名 → DB 号)。原生库低于 v5 时返回 null。</summary>
    SiemensPlusNativeDataBlocksResult? ListDataBlocks() => null;
    /// <summary>逐项写入。不支持逐项结果的实现退回整批写入，所有项同一个结果。</summary>
    SiemensPlusNativeWriteResult WriteItems(IReadOnlyList<SiemensPlusNativeWrite> writes)
    {
        var code = Write(writes);
        return code == SiemensPlusResultCode.Ok
            ? new(code, Enumerable.Repeat(SiemensPlusResultCode.Ok, writes.Count).ToArray())
            : new(code, []);
    }
    SiemensPlusResultCode Disconnect();
    /// <summary>取出并清除“需要重建连接”的请求(PLC 程序可能已重新下载，缓存地址已过期)。旧原生库不支持时恒为 false。</summary>
    bool TakeResyncRequest() => false;
}

public sealed class SiemensPlusNative : ISiemensPlusNativeApi
{
    private const string LibraryName = "igw_s7plus";
    /// <summary>v2：单项结果码 SymbolNotFound；v3：逐项写入 igw_s7plus_write_items 与 ItemRejected。</summary>
    public const uint AbiVersion = 5;
    private const uint ItemWriteAbiVersion = 3;
    /// <summary>v4 起支持变化订阅(igw_s7plus_subscribe / poll_notification / delete_subscription)。</summary>
    private const uint SubscriptionAbiVersion = 4;
    /// <summary>v5 起可以列出数据块(igw_s7plus_list_datablocks)，导入 DB 源文件时按块名查 DB 号。</summary>
    private const uint DataBlocksAbiVersion = 5;
    /// <summary>
    /// v1 仍可加载：结构体与函数签名未变，只是不会返回 SymbolNotFound(坏符号时整片失败，同旧版)。
    /// Linux 的 .so 暂时只能构建 v1，不能因为托管侧升级就让 Plus 设备整体不可用。
    /// </summary>
    public const uint MinimumAbiVersion = 1;
    private IntPtr _handle;
    private int _payloadHint;
    /// <summary>复用的读取输出缓冲(调用经传输层操作通道串行)。每片新分配约 1 MB 在 35 万点时每轮约占托管开销的一半。</summary>
    private byte[]? _payload;

    public uint LoadedAbiVersion { get; }

    internal static bool IsSupportedAbi(uint version) => version is >= MinimumAbiVersion and <= AbiVersion;

    public SiemensPlusNative()
    {
        var version = Native.abi_version();
        if (!IsSupportedAbi(version))
            throw new InvalidOperationException(
                $"S7 Plus 原生组件 igw_s7plus 版本不匹配（支持 ABI {MinimumAbiVersion}～{AbiVersion}，实际 {version}）：原生库必须与网关程序同版本发行，请用完整安装包升级。");
        LoadedAbiVersion = version;
        _handle = Native.create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("无法创建 S7 Plus 原生会话。");
    }

    public SiemensPlusResultCode Connect(string host, ushort port, uint timeoutMs, bool legacy)
    {
        using var pins = new NativePins();
        var hostSlice = pins.PinUtf8(host);
        var request = new NativeConnectRequest(hostSlice.Pointer, hostSlice.Length, port, timeoutMs, legacy ? (byte)1 : (byte)0);
        return Native.connect(_handle, in request).Code;
    }

    public SiemensPlusResultCode Authenticate(string username, string password)
    {
        using var pins = new NativePins();
        var user = pins.PinUtf8(username);
        var secret = pins.PinUtf8(password);
        return Native.authenticate(_handle, user, secret).Code;
    }

    public SiemensPlusResultCode Probe() => Native.probe(_handle).Code;

    public SiemensPlusNativeBrowseResult Browse()
    {
        var payload = InvokeBuffer((handle, output) =>
        {
            using var pins = new NativePins();
            return Native.browse(handle, pins.Pin(output));
        });
        if (payload.Code != SiemensPlusResultCode.Ok)
            return new(payload.Code, []);

        var text = Encoding.UTF8.GetString(payload.Bytes);
        var tags = new List<SiemensPlusNativeBrowseTag>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length != 3 || !byte.TryParse(columns[1], out var type) || !ushort.TryParse(columns[2], out var maxLength))
                continue;
            tags.Add(new(Unescape(columns[0]), type, maxLength));
        }
        return new(SiemensPlusResultCode.Ok, tags);
    }

    public SiemensPlusNativeDataBlocksResult? ListDataBlocks()
    {
        if (LoadedAbiVersion < DataBlocksAbiVersion) return null;   // 旧原生库：调用方让用户手填 DB 号
        var payload = InvokeBuffer((handle, output) =>
        {
            using var pins = new NativePins();
            return Native.list_datablocks(handle, pins.Pin(output));
        });
        if (payload.Code != SiemensPlusResultCode.Ok) return new(payload.Code, []);
        var blocks = new List<SiemensPlusNativeDataBlock>();
        foreach (var line in Encoding.UTF8.GetString(payload.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split('\t');
            if (columns.Length == 2 && int.TryParse(columns[1], out var number))
                blocks.Add(new(Unescape(columns[0]), number));
        }
        return new(SiemensPlusResultCode.Ok, blocks);
    }

    public SiemensPlusNativeSubscribeResult? Subscribe(IReadOnlyList<string> symbols, ushort cycleTimeMs)
    {
        if (LoadedAbiVersion < SubscriptionAbiVersion) return null;   // 旧原生库：调用方回退轮询
        if (symbols.Count == 0) return null;
        using var pins = new NativePins();
        var items = symbols.Select(symbol => new NativeReadItem(pins.PinUtf8(symbol))).ToArray();
        var codes = new SiemensPlusResultCode[symbols.Count];
        var result = Native.subscribe(_handle, items, (nuint)items.Length, cycleTimeMs, codes, out uint id);
        return new SiemensPlusNativeSubscribeResult(result.Code, id, codes);
    }

    public SiemensPlusNativeNotification PollNotification(uint subscriptionId, int capacity)
    {
        if (LoadedAbiVersion < SubscriptionAbiVersion)
            return new SiemensPlusNativeNotification(SiemensPlusResultCode.SubscriptionNotFound, []);
        int size = Math.Max(1, capacity);
        var slots = new uint[size];
        var results = new NativeReadResult[size];
        // 通知里的字符串/字节值走 payload 缓冲；不够时原生层返回 BufferTooSmall 并给出所需大小
        byte[] payload = _notificationPayload ?? new byte[4096];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var pins = new NativePins();
            var result = Native.poll_notification(_handle, subscriptionId, (nuint)size,
                slots, results, out nuint count, pins.PinMutable(payload));
            if (result.Code == SiemensPlusResultCode.BufferTooSmall && attempt == 0)
            {
                payload = new byte[Math.Max((int)result.Detail, payload.Length * 2)];
                continue;
            }
            _notificationPayload = payload;
            if (result.Code != SiemensPlusResultCode.Ok)
                return new SiemensPlusNativeNotification(result.Code, []);
            int produced = checked((int)count);
            var values = new List<(int Slot, object? Value)>(produced);
            for (int index = 0; index < produced; index++)
                values.Add((checked((int)slots[index]), Decode(results[index], payload)));
            return new SiemensPlusNativeNotification(SiemensPlusResultCode.Ok, values);
        }
        return new SiemensPlusNativeNotification(SiemensPlusResultCode.BufferTooSmall, []);
    }

    public SiemensPlusResultCode DeleteSubscription(uint subscriptionId)
    {
        if (LoadedAbiVersion < SubscriptionAbiVersion) return SiemensPlusResultCode.Ok;
        return Native.delete_subscription(_handle, subscriptionId).Code;
    }

    private byte[]? _notificationPayload;

    public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols)
        => ReadMeasured(symbols, static () => { }, new("Read", "S7 Plus symbols", symbols.Count, -1, 0));

    public SiemensPlusNativeReadResult ReadMeasured(IReadOnlyList<string> symbols, Action beforeRequest,
        ProtocolRequestDescriptor descriptor) => ReadBuffered(symbols, (items, results, payload) =>
    {
        using var payloadPins = new NativePins();
        return Native.read_batch(_handle, items, (nuint)items.Length, results, payloadPins.Pin(payload));
    }, beforeRequest, descriptor, ref _payloadHint, ref _payload);

    // Shared by the real P/Invoke path and ABI-boundary tests. One invocation of readBatch
    // performs another PLC read, even if the previous call only exhausted the local output buffer.
    internal static SiemensPlusNativeReadResult ReadBuffered(IReadOnlyList<string> symbols,
        Func<NativeReadItem[], NativeReadResult[], byte[], NativeResult> readBatch,
        Action beforeRequest, ProtocolRequestDescriptor descriptor)
    {
        int payloadHint = 0;
        byte[]? payload = null;
        return ReadBuffered(symbols, readBatch, beforeRequest, descriptor, ref payloadHint, ref payload);
    }

    internal static SiemensPlusNativeReadResult ReadBuffered(IReadOnlyList<string> symbols,
        Func<NativeReadItem[], NativeReadResult[], byte[], NativeResult> readBatch,
        Action beforeRequest, ProtocolRequestDescriptor descriptor, ref int payloadHint)
    {
        byte[]? payload = null;
        return ReadBuffered(symbols, readBatch, beforeRequest, descriptor, ref payloadHint, ref payload);
    }

    /// <param name="payloadHint">上次因缓冲不足得到的实际大小：之后直接按它分配，避免缓冲不足时再向 PLC 读一遍。</param>
    /// <param name="reusable">可复用的输出缓冲：够大就直接用，不够就换成新分配的并带回给调用方。</param>
    internal static SiemensPlusNativeReadResult ReadBuffered(IReadOnlyList<string> symbols,
        Func<NativeReadItem[], NativeReadResult[], byte[], NativeResult> readBatch,
        Action beforeRequest, ProtocolRequestDescriptor descriptor, ref int payloadHint, ref byte[]? reusable)
    {
        using var pins = new NativePins();
        var items = symbols.Select(symbol => new NativeReadItem(pins.PinUtf8(symbol))).ToArray();
        var results = new NativeReadResult[items.Length];
        int wanted = Math.Max(Math.Max(4096, items.Length * 512), Volatile.Read(ref payloadHint));
        if (reusable is null || reusable.Length < wanted) reusable = new byte[wanted];
        var payload = reusable;
        NativeResult call;
        bool retried = false;
        for (var attempt = 0; ; attempt++)
        {
            beforeRequest();
            call = ProtocolOperationTiming.MeasureResult(() => readBatch(items, results, payload),
                static result => result.Code is not (SiemensPlusResultCode.Ok or SiemensPlusResultCode.BufferTooSmall), descriptor);
            if (call.Code != SiemensPlusResultCode.BufferTooSmall || attempt > 0)
                break;
            Volatile.Write(ref payloadHint, checked((int)call.Detail));
            payload = reusable = new byte[Math.Max(payload.Length * 2, checked((int)call.Detail))];
            retried = true;
        }
        if (call.Code != SiemensPlusResultCode.Ok)
            return new(call.Code, [], retried);

        var values = results.Select(result => new SiemensPlusNativeValue(result.Code, Decode(result, payload))).ToArray();
        return new(SiemensPlusResultCode.Ok, values, retried);
    }

    public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes)
    {
        using var pins = new NativePins();
        var items = writes.Select(write => Encode(write, pins)).ToArray();
        return Native.write_batch(_handle, items, (nuint)items.Length).Code;
    }

    public bool TakeResyncRequest() =>
        LoadedAbiVersion >= ItemWriteAbiVersion && Native.take_resync_request(_handle) != 0;

    public SiemensPlusNativeWriteResult WriteItems(IReadOnlyList<SiemensPlusNativeWrite> writes) =>
        WriteItems(LoadedAbiVersion, writes, Write, WriteItemsNative);

    /// <summary>
    /// 旧原生库(ABI &lt; 3)没有 write_items 导出：退回整批写入，所有项同一个结果。
    /// 不能写成转调接口默认实现 <c>((ISiemensPlusNativeApi)this).WriteItems</c>——本类自己实现了
    /// WriteItems，强转接口仍然分派回这里，会无限递归直到栈溢出，旧库上一写就让进程崩溃。
    /// </summary>
    internal static SiemensPlusNativeWriteResult WriteItems(uint loadedAbiVersion, IReadOnlyList<SiemensPlusNativeWrite> writes,
        Func<IReadOnlyList<SiemensPlusNativeWrite>, SiemensPlusResultCode> writeBatch,
        Func<IReadOnlyList<SiemensPlusNativeWrite>, SiemensPlusNativeWriteResult> writeItems)
    {
        if (loadedAbiVersion >= ItemWriteAbiVersion) return writeItems(writes);
        var code = writeBatch(writes);
        return code == SiemensPlusResultCode.Ok
            ? new(code, Enumerable.Repeat(SiemensPlusResultCode.Ok, writes.Count).ToArray())
            : new(code, []);
    }

    private SiemensPlusNativeWriteResult WriteItemsNative(IReadOnlyList<SiemensPlusNativeWrite> writes)
    {
        using var pins = new NativePins();
        var items = writes.Select(write => Encode(write, pins)).ToArray();
        var results = new SiemensPlusResultCode[items.Length];
        var code = Native.write_items(_handle, items, (nuint)items.Length, results).Code;
        return code == SiemensPlusResultCode.Ok ? new(code, results) : new(code, []);
    }

    public SiemensPlusResultCode Disconnect() => _handle == IntPtr.Zero
        ? SiemensPlusResultCode.Ok
        : Native.disconnect(_handle).Code;

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        Native.disconnect(handle);
        Native.destroy(handle);
    }

    private (SiemensPlusResultCode Code, byte[] Bytes) InvokeBuffer(BufferCall call)
    {
        var buffer = Array.Empty<byte>();
        var result = call(_handle, buffer);
        if (result.Code == SiemensPlusResultCode.BufferTooSmall)
        {
            buffer = new byte[checked((int)result.Detail)];
            result = call(_handle, buffer);
        }
        return result.Code == SiemensPlusResultCode.Ok
            ? (result.Code, buffer.AsSpan(0, checked((int)result.Detail)).ToArray())
            : (result.Code, []);
    }

    private static string Unescape(string value) => value
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\t", "\t", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static object? Decode(NativeReadResult result, byte[] payload) => result.ValueType switch
    {
        NativeValueType.Null => null,
        NativeValueType.Bool => result.UnsignedValue != 0,
        NativeValueType.USInt or NativeValueType.Byte => (byte)result.UnsignedValue,
        NativeValueType.UInt or NativeValueType.Word => (ushort)result.UnsignedValue,
        NativeValueType.UDInt or NativeValueType.DWord => (uint)result.UnsignedValue,
        NativeValueType.ULInt or NativeValueType.LWord or NativeValueType.Timestamp => result.UnsignedValue,
        NativeValueType.SInt => (sbyte)result.SignedValue,
        NativeValueType.Int => (short)result.SignedValue,
        NativeValueType.DInt => (int)result.SignedValue,
        NativeValueType.LInt or NativeValueType.Timespan => result.SignedValue,
        NativeValueType.Real => (float)result.FloatValue,
        NativeValueType.LReal => result.FloatValue,
        NativeValueType.Text => Encoding.UTF8.GetString(payload, checked((int)result.PayloadOffset), checked((int)result.PayloadLength)),
        NativeValueType.Bytes => payload.AsSpan(checked((int)result.PayloadOffset), checked((int)result.PayloadLength)).ToArray(),
        _ => null,
    };

    private static NativeWriteItem Encode(SiemensPlusNativeWrite write, NativePins pins)
    {
        var symbol = pins.PinUtf8(write.Symbol);
        return write.Value switch
        {
            null => new(symbol, NativeValueType.Null),
            bool value => new(symbol, NativeValueType.Bool, UnsignedValue: value ? 1UL : 0UL),
            byte value => new(symbol, NativeValueType.USInt, UnsignedValue: value),
            sbyte value => new(symbol, NativeValueType.SInt, SignedValue: value),
            short value => new(symbol, NativeValueType.Int, SignedValue: value),
            ushort value => new(symbol, NativeValueType.UInt, UnsignedValue: value),
            int value => new(symbol, NativeValueType.DInt, SignedValue: value),
            uint value => new(symbol, NativeValueType.UDInt, UnsignedValue: value),
            long value => new(symbol, NativeValueType.LInt, SignedValue: value),
            ulong value => new(symbol, NativeValueType.ULInt, UnsignedValue: value),
            float value => new(symbol, NativeValueType.Real, FloatValue: value),
            double value => new(symbol, NativeValueType.LReal, FloatValue: value),
            string value => new(symbol, NativeValueType.Text, Payload: pins.PinUtf8(value)),
            byte[] value => new(symbol, NativeValueType.Bytes, Payload: pins.Pin(value)),
            _ => throw new ArgumentException($"S7 Plus 不支持写入 CLR 类型 {write.Value.GetType().Name}。", nameof(write)),
        };
    }

    private delegate NativeResult BufferCall(IntPtr handle, byte[] output);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeResult(SiemensPlusResultCode Code, uint Detail);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeSlice(IntPtr Pointer, nuint Length);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeMutableSlice(IntPtr Pointer, nuint Length);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeConnectRequest(IntPtr HostPointer, nuint HostLength, ushort Port, uint TimeoutMs, byte Legacy);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeReadItem(NativeSlice Symbol);

    internal enum NativeValueType { Null, Bool, USInt, UInt, UDInt, ULInt, SInt, Int, DInt, LInt, Byte, Word, DWord, LWord, Real, LReal, Timestamp, Timespan, Text, Bytes }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeReadResult(
        SiemensPlusResultCode Code,
        NativeValueType ValueType,
        long SignedValue,
        ulong UnsignedValue,
        double FloatValue,
        uint PayloadOffset,
        uint PayloadLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeWriteItem(
        NativeSlice Symbol,
        NativeValueType ValueType,
        long SignedValue = 0,
        ulong UnsignedValue = 0,
        double FloatValue = 0,
        NativeSlice Payload = default);

    private sealed class NativePins : IDisposable
    {
        private readonly List<GCHandle> _pins = [];

        public NativeSlice PinUtf8(string value) => Pin(Encoding.UTF8.GetBytes(value ?? string.Empty));

        public NativeSlice Pin(byte[] bytes)
        {
            if (bytes.Length == 0) return default;
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _pins.Add(pin);
            return new(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
        }

        /// <summary>可写缓冲：原生层往里写通知里的字符串/字节值。</summary>
        public NativeMutableSlice PinMutable(byte[] bytes)
        {
            if (bytes.Length == 0) return default;
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _pins.Add(pin);
            return new(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
        }

        public void Dispose()
        {
            foreach (var pin in _pins) pin.Free();
            _pins.Clear();
        }
    }

    private static class Native
    {
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint abi_version();
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr create();
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void destroy(IntPtr handle);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_connect", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult connect(IntPtr handle, in NativeConnectRequest request);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_authenticate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult authenticate(IntPtr handle, NativeSlice user, NativeSlice password);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_probe", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult probe(IntPtr handle);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_browse", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult browse(IntPtr handle, NativeSlice output);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_list_datablocks", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult list_datablocks(IntPtr handle, NativeSlice output);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_subscribe", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult subscribe(IntPtr handle, [In] NativeReadItem[] items, nuint count,
            ushort cycleTimeMs, [Out] SiemensPlusResultCode[] results, out uint subscriptionId);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_poll_notification", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult poll_notification(IntPtr handle, uint subscriptionId, nuint capacity,
            [Out] uint[] slots, [Out] NativeReadResult[] results, out nuint count, NativeMutableSlice payload);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_delete_subscription", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult delete_subscription(IntPtr handle, uint subscriptionId);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_read_batch", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult read_batch(IntPtr handle, NativeReadItem[] items, nuint count, [Out] NativeReadResult[] results, NativeSlice payload);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_write_batch", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult write_batch(IntPtr handle, NativeWriteItem[] items, nuint count);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_take_resync_request", CallingConvention = CallingConvention.Cdecl)]
        internal static extern byte take_resync_request(IntPtr handle);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_write_items", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult write_items(IntPtr handle, NativeWriteItem[] items, nuint count, [Out] SiemensPlusResultCode[] results);
        [DllImport(LibraryName, EntryPoint = "igw_s7plus_disconnect", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult disconnect(IntPtr handle);
    }
}
