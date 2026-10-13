#nullable enable
// SPDX-License-Identifier: LGPL-3.0-or-later
// S7CommPlus wire behavior is interoperable with the LGPL s7commplus-rs client used by IGW.
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

public sealed record SiemensPlusSymbolDescriptor(
    string Symbol,
    uint AccessArea,
    IReadOnlyList<uint> Lids,
    byte SoftDataType);

/// <summary>
/// S7CommPlus TLS simulator for imported optimized Siemens projects. It exposes the standard
/// InitSsl, session, Explore/type-info and Get/SetMultiVariables flow used by IGW.
/// </summary>
public sealed class SiemensPlusSimulationServer : IAsyncDisposable
{
    private readonly SimulationValueStore _store;
    private readonly SiemensPlusSchema _schema;
    private readonly int _port;
    private SiemensPlusNativeTransport? _transport;
    private string _lastError = string.Empty;

    public SiemensPlusSimulationServer(SimulationDevice device, SimulationValueStore store, int port)
    {
        _store = store;
        _schema = SiemensPlusSchema.Create(device);
        _port = port;
    }

    public bool IsRunning => _transport != null;
    public int ListenPort => _port;
    public int OnlineCount => _transport?.OnlineCount ?? 0;
    public bool EnableWrite { get; set; } = true;
    public string LastError => _transport?.LastError is { Length: > 0 } error ? error : _lastError;
    public string LastActivity { get; private set; } = "未启动";

    public static IReadOnlyList<SiemensPlusSymbolDescriptor> Describe(SimulationDevice device) =>
        SiemensPlusSchema.Create(device).Descriptors;

    public void Start()
    {
        if (_transport != null) return;
        (byte[] certificateDer, byte[] privateKey) = CreateEphemeralCertificateMaterial();
        try
        {
            _transport = SiemensPlusNativeTransport.Start(
                _port, certificateDer, privateKey, HandleNativeRequest);
            LastActivity = $"匿名监听 127.0.0.1:{_port}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private nuint HandleNativeRequest(nint requestPointer, nuint requestLength, nint outputPointer, nuint outputCapacity)
    {
        try
        {
            if (requestLength > int.MaxValue) throw new InvalidDataException("S7 Plus 请求过大");
            byte[] requestBytes = new byte[(int)requestLength];
            Marshal.Copy(requestPointer, requestBytes, 0, requestBytes.Length);
            var request = S7PlusCodec.ParseRequest(requestBytes);
            LastActivity = request.FunctionCode == S7PlusCodec.InitSsl
                ? "匿名 TLS 握手"
                : $"匿名会话处理 0x{request.FunctionCode:X4}";
            byte[] response = HandleRequest(request);
            if ((nuint)response.Length <= outputCapacity && outputPointer != 0)
                Marshal.Copy(response, 0, outputPointer, response.Length);
            return (nuint)response.Length;
        }
        catch (Exception ex)
        {
            _lastError = ex.ToString();
            return 0;
        }
    }

    private byte[] HandleRequest(S7PlusRequest request) => request.FunctionCode switch
    {
        S7PlusCodec.CreateObject => CreateSessionResponse(request),
        S7PlusCodec.SetMulti => SetMultiResponse(request),
        S7PlusCodec.GetMulti => GetMultiResponse(request),
        S7PlusCodec.Explore => ExploreResponse(request),
        _ => S7PlusCodec.Response(request.Version, request.FunctionCode, request.Sequence,
            request.TransportFlags, _ => { }, returnValue: 0x4000_0000_0000_0001),
    };

    private static byte[] CreateSessionResponse(S7PlusRequest request) =>
        S7PlusCodec.Response(request.Version, request.FunctionCode, request.Sequence, request.TransportFlags, body =>
        {
            body.WriteByte(2);
            S7PlusCodec.WriteVlq(body, 0x7000_0001);
            S7PlusCodec.WriteVlq(body, 0x7000_0002);
            S7PlusCodec.WriteObjectStart(body, 0x7000_0001, 287, 0);
            body.WriteByte(0xa3);
            S7PlusCodec.WriteVlq(body, 306);
            S7PlusCodec.WriteEmptyStruct(body, 0x100);
            body.WriteByte(0xa2);
        });

    private byte[] ExploreResponse(S7PlusRequest request)
    {
        int offset = request.PayloadOffset;
        uint exploreId = S7PlusCodec.ReadU32(request.Bytes, ref offset);
        return S7PlusCodec.Response(request.Version, request.FunctionCode, request.Sequence, request.TransportFlags, body =>
        {
            S7PlusCodec.WriteU32(body, exploreId);
            S7PlusCodec.WriteVlq(body, 0); // integrity id
            if (exploreId == 3) _schema.WriteProgramExplore(body);
            else if (exploreId == 537) { }
            else _schema.WriteTypeExplore(body, exploreId);
        });
    }

    private byte[] GetMultiResponse(S7PlusRequest request)
    {
        int offset = request.PayloadOffset;
        _ = S7PlusCodec.ReadU32(request.Bytes, ref offset); // LinkId
        uint count = S7PlusCodec.ReadVlq(request.Bytes, ref offset);
        _ = S7PlusCodec.ReadVlq(request.Bytes, ref offset); // field count
        var addresses = new List<S7PlusAddress>((int)count);
        for (int i = 0; i < count; i++) addresses.Add(S7PlusCodec.ReadAddress(request.Bytes, ref offset));

        return S7PlusCodec.Response(request.Version, request.FunctionCode, request.Sequence, request.TransportFlags, body =>
        {
            for (int i = 0; i < addresses.Count; i++)
            {
                S7PlusCodec.WriteVlq(body, (uint)i + 1);
                if (_schema.TryGetTypeId(addresses[i], out uint typeId))
                    S7PlusCodec.WriteRid(body, typeId);
                else if (_schema.TryRead(addresses[i], _store, out var type, out object? value))
                    S7PlusCodec.WriteValue(body, type, value);
                else
                    S7PlusCodec.WriteNull(body);
            }
            S7PlusCodec.WriteVlq(body, 0); // end values
            S7PlusCodec.WriteVlq(body, 0); // end errors
            S7PlusCodec.WriteVlq(body, 0); // integrity id
        });
    }

    private byte[] SetMultiResponse(S7PlusRequest request)
    {
        int offset = request.PayloadOffset;
        uint inObject = S7PlusCodec.ReadU32(request.Bytes, ref offset);
        uint count = S7PlusCodec.ReadVlq(request.Bytes, ref offset);
        if (inObject == 0)
        {
            _ = S7PlusCodec.ReadVlq(request.Bytes, ref offset); // total field count
            var addresses = new List<S7PlusAddress>((int)count);
            for (int i = 0; i < count; i++) addresses.Add(S7PlusCodec.ReadAddress(request.Bytes, ref offset));
            for (int i = 0; i < count; i++)
            {
                _ = S7PlusCodec.ReadVlq(request.Bytes, ref offset); // item number
                object? value = S7PlusCodec.ReadValue(request.Bytes, ref offset);
                if (EnableWrite) _schema.TryWrite(addresses[i], _store, value);
            }
        }

        return S7PlusCodec.Response(request.Version, request.FunctionCode, request.Sequence, request.TransportFlags, body =>
        {
            S7PlusCodec.WriteVlq(body, 0); // no per-item errors
            S7PlusCodec.WriteVlq(body, 0); // integrity id
        });
    }

    private static (byte[] Certificate, byte[] PrivateKey) CreateEphemeralCertificateMaterial()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PLC Simulator", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return (generated.Export(X509ContentType.Cert), rsa.ExportPkcs8PrivateKey());
    }

    public async ValueTask DisposeAsync()
    {
        SiemensPlusNativeTransport? transport = Interlocked.Exchange(ref _transport, null);
        if (transport == null) return;
        await Task.Run(transport.Dispose);
        LastActivity = "已停止";
    }
}

internal sealed class SiemensPlusNativeTransport : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeByteSlice(nint Pointer, nuint Length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint RequestCallback(
        nint context, nint request, nuint requestLength, nint output, nuint outputCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint StartDelegate(
        ushort port,
        NativeByteSlice certificate,
        NativeByteSlice privateKey,
        RequestCallback callback,
        nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint OnlineCountDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint LastErrorDelegate(nint handle, nint output, nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StopDelegate(nint handle);

    private nint _library;
    private readonly OnlineCountDelegate _onlineCount;
    private readonly LastErrorDelegate _lastError;
    private readonly StopDelegate _stop;
    private readonly RequestCallback _callbackRoot;
    private nint _handle;

    private SiemensPlusNativeTransport(
        nint library,
        nint handle,
        RequestCallback callbackRoot,
        OnlineCountDelegate onlineCount,
        LastErrorDelegate lastError,
        StopDelegate stop)
    {
        _library = library;
        _handle = handle;
        _callbackRoot = callbackRoot;
        _onlineCount = onlineCount;
        _lastError = lastError;
        _stop = stop;
    }

    public int OnlineCount => _handle == 0 ? 0 : checked((int)_onlineCount(_handle));
    public string LastError => ReadLastError(_lastError, _handle);

    public static SiemensPlusNativeTransport Start(
        int port,
        byte[] certificate,
        byte[] privateKey,
        Func<nint, nuint, nint, nuint, nuint> handler)
    {
        if (port is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
        string path = ResolveLibraryPath();
        nint library = NativeLibrary.Load(path);
        try
        {
            var abi = Load<AbiVersionDelegate>(library, "igw_plcsim_s7plus_abi_version");
            var start = Load<StartDelegate>(library, "igw_plcsim_s7plus_start");
            var online = Load<OnlineCountDelegate>(library, "igw_plcsim_s7plus_online_count");
            var lastError = Load<LastErrorDelegate>(library, "igw_plcsim_s7plus_last_error");
            var stop = Load<StopDelegate>(library, "igw_plcsim_s7plus_stop");
            if (abi() != 1) throw new InvalidOperationException("S7 Plus 模拟服务组件 ABI 版本不兼容");

            RequestCallback callback = (_, request, requestLength, output, outputCapacity) =>
                handler(request, requestLength, output, outputCapacity);
            nint certificatePointer = Marshal.AllocHGlobal(certificate.Length);
            nint keyPointer = Marshal.AllocHGlobal(privateKey.Length);
            try
            {
                Marshal.Copy(certificate, 0, certificatePointer, certificate.Length);
                Marshal.Copy(privateKey, 0, keyPointer, privateKey.Length);
                nint handle = start(
                    checked((ushort)port),
                    new NativeByteSlice(certificatePointer, (nuint)certificate.Length),
                    new NativeByteSlice(keyPointer, (nuint)privateKey.Length),
                    callback,
                    0);
                if (handle == 0)
                {
                    string error = ReadLastError(lastError, 0);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                        ? "S7 Plus 模拟服务启动失败"
                        : error);
                }
                return new SiemensPlusNativeTransport(library, handle, callback, online, lastError, stop);
            }
            finally
            {
                Marshal.Copy(new byte[certificate.Length], 0, certificatePointer, certificate.Length);
                Marshal.Copy(new byte[privateKey.Length], 0, keyPointer, privateKey.Length);
                Marshal.FreeHGlobal(certificatePointer);
                Marshal.FreeHGlobal(keyPointer);
            }
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    private static string ResolveLibraryPath()
    {
        string? configured = Environment.GetEnvironmentVariable("IGW_PLCSIM_S7PLUS_DLL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string fullPath = Path.GetFullPath(configured);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到指定的 S7 Plus 模拟服务组件", fullPath);
            return fullPath;
        }
        string packaged = Path.Combine(AppContext.BaseDirectory, "igw_plcsim_s7plus.dll");
        if (File.Exists(packaged)) return packaged;
        throw new FileNotFoundException(
            "缺少 S7 Plus 模拟服务组件 igw_plcsim_s7plus.dll，请重新构建或安装最新版模拟器。",
            packaged);
    }

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static string ReadLastError(LastErrorDelegate reader, nint handle)
    {
        nuint required = reader(handle, 0, 0);
        if (required == 0 || required > int.MaxValue) return string.Empty;
        nint buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            nuint written = reader(handle, buffer, required);
            if (written == 0 || written > required) return string.Empty;
            byte[] bytes = new byte[(int)written];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) _stop(handle);
        GC.KeepAlive(_callbackRoot);
        nint library = Interlocked.Exchange(ref _library, 0);
        if (library != 0) NativeLibrary.Free(library);
    }
}

internal sealed class SiemensPlusSchema
{
    private static readonly Regex DbNumber = new(@"DB\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArrayElement = new(@"^(?<name>.+)\[(?<index>-?\d+)\]$", RegexOptions.Compiled);
    private readonly List<DbNode> _blocks = new();
    private readonly Dictionary<uint, TypeNode> _types = new();
    private readonly Dictionary<string, SimulationTag> _tagsByAddress = new(StringComparer.Ordinal);

    private SiemensPlusSchema() { }

    public IReadOnlyList<SiemensPlusSymbolDescriptor> Descriptors { get; private set; } = [];

    public static SiemensPlusSchema Create(SimulationDevice device)
    {
        var schema = new SiemensPlusSchema();
        var roots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (SimulationTag tag in device.EnumerateTags())
        {
            string symbol = string.IsNullOrWhiteSpace(tag.SymbolicAddress)
                ? tag.SymbolPath[(device.Name.Length + 1)..]
                : tag.SymbolicAddress;
            string[] parts = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (!roots.TryGetValue(parts[0], out TypeNode? root)) roots[parts[0]] = root = new TypeNode(parts[0]);
            TypeNode node = root;
            for (int i = 1; i < parts.Length - 1; i++)
            {
                Match element = ArrayElement.Match(parts[i]);
                node = element.Success && int.TryParse(element.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    ? node.GetOrAddStructArrayElement(element.Groups["name"].Value, index)
                    : node.GetOrAdd(parts[i]);
            }
            node.AddLeaf(parts[^1], tag);
        }

        uint typeId = 0x9000_1000;
        int fallbackDb = 1;
        foreach ((string name, TypeNode root) in roots)
        {
            AssignTypeIds(root, ref typeId, schema._types, reserveTypeInfoLid: true);
            Match match = DbNumber.Match(name);
            uint number = match.Success && uint.TryParse(match.Groups["n"].Value, out uint parsed)
                ? parsed
                : (uint)fallbackDb++;
            uint area = 0x8a0e_0000 | (number & 0xffff);
            var block = new DbNode(name, area, root.TypeId, root);
            schema._blocks.Add(block);
            BuildAddresses(block, root, [], schema._tagsByAddress);
        }

        schema.Descriptors = schema._blocks.SelectMany(block => Describe(block, block.Type, [], block.Name)).ToArray();
        return schema;
    }

    private static void AssignTypeIds(
        TypeNode node,
        ref uint next,
        Dictionary<uint, TypeNode> types,
        bool reserveTypeInfoLid = false)
    {
        node.TypeId = next++;
        types[node.TypeId] = node;
        node.AssignLids(reserveTypeInfoLid ? 2u : 1u);
        foreach (TypeNode child in node.Children) AssignTypeIds(child, ref next, types);
        foreach (StructArrayNode array in node.StructArrays)
        {
            TypeNode elementType = array.ElementType;
            AssignTypeIds(elementType, ref next, types);
            foreach (TypeNode element in array.Elements.Where(x => !ReferenceEquals(x, elementType)))
                element.CopySchemaFrom(elementType);
        }
    }

    private static void BuildAddresses(DbNode block, TypeNode node, IReadOnlyList<uint> prefix, Dictionary<string, SimulationTag> target)
    {
        foreach (LeafNode leaf in node.Leaves)
        {
            uint[] lids = [.. prefix, leaf.Lid];
            target[Key(block.Area, lids)] = leaf.Tag;
        }
        foreach (TypeNode child in node.Children)
            BuildAddresses(block, child, [.. prefix, child.Lid], target);
        foreach (StructArrayNode array in node.StructArrays)
        {
            foreach ((int index, TypeNode element) in array.IndexedElements)
            {
                uint elementId = checked((uint)(index - array.LowerBound));
                BuildAddresses(block, element, [.. prefix, array.Lid, elementId, 1], target);
            }
        }
    }

    private static IEnumerable<SiemensPlusSymbolDescriptor> Describe(DbNode block, TypeNode node, IReadOnlyList<uint> prefix, string path)
    {
        foreach (LeafNode leaf in node.Leaves)
        {
            yield return new SiemensPlusSymbolDescriptor($"{path}.{leaf.Name}", block.Area, [.. prefix, leaf.Lid], SoftType(leaf.Tag.DataType));
            if (leaf.Tag.IsArray)
                for (int index = 0; index < leaf.Tag.Dimensions.Aggregate(1, (a, b) => checked(a * b)); index++)
                    yield return new SiemensPlusSymbolDescriptor($"{path}.{leaf.Name}[{index}]", block.Area, [.. prefix, leaf.Lid, (uint)index], SoftType(leaf.Tag.DataType));
        }
        foreach (TypeNode child in node.Children)
            foreach (var item in Describe(block, child, [.. prefix, child.Lid], $"{path}.{child.Name}")) yield return item;
        foreach (StructArrayNode array in node.StructArrays)
        {
            foreach ((int index, TypeNode element) in array.IndexedElements)
            {
                uint elementId = checked((uint)(index - array.LowerBound));
                foreach (var item in Describe(block, element, [.. prefix, array.Lid, elementId, 1], $"{path}.{array.Name}[{index}]"))
                    yield return item;
            }
        }
    }

    public bool TryRead(S7PlusAddress address, SimulationValueStore store, out SimulationDataType type, out object? value)
    {
        if (_tagsByAddress.TryGetValue(Key(address.Area, address.Lids), out SimulationTag? tag))
        {
            type = tag.DataType;
            value = store.Read(tag.Id).Value;
            return true;
        }
        if (TryArrayElement(address, out tag, out int index))
        {
            type = tag.DataType;
            value = ((Array)store.Read(tag.Id).Value!).GetValue(index);
            return true;
        }
        type = SimulationDataType.Unknown;
        value = null;
        return false;
    }

    public bool TryGetTypeId(S7PlusAddress address, out uint typeId)
    {
        DbNode? block = address.Lids.Count == 1 && address.Lids[0] == 1
            ? _blocks.FirstOrDefault(x => x.Area == address.Area)
            : null;
        typeId = block?.TypeId ?? 0;
        return block != null;
    }

    public bool TryWrite(S7PlusAddress address, SimulationValueStore store, object? value)
    {
        if (!_tagsByAddress.TryGetValue(Key(address.Area, address.Lids), out SimulationTag? tag))
        {
            if (!TryArrayElement(address, out tag, out int index)) return false;
            store.WriteElement(tag.Id, index, value, SimulationWriteSource.Protocol);
            return true;
        }
        store.Write(tag.Id, value, SimulationWriteSource.Protocol);
        return true;
    }

    private bool TryArrayElement(S7PlusAddress address, out SimulationTag tag, out int index)
    {
        tag = null!; index = -1;
        if (address.Lids.Count < 2 || address.Lids[^1] > int.MaxValue) return false;
        if (!_tagsByAddress.TryGetValue(Key(address.Area, address.Lids.Take(address.Lids.Count - 1).ToArray()), out var candidate) || !candidate.IsArray) return false;
        index = (int)address.Lids[^1];
        if (index >= candidate.Dimensions.Aggregate(1, (a, b) => checked(a * b))) return false;
        tag = candidate; return true;
    }

    public void WriteProgramExplore(Stream body)
    {
        S7PlusCodec.WriteObjectStart(body, 3, 0, 0);
        foreach (DbNode block in _blocks)
        {
            S7PlusCodec.WriteObjectStart(body, block.Area, 2574, 0);
            body.WriteByte(0xa3);
            S7PlusCodec.WriteVlq(body, 233);
            S7PlusCodec.WriteWString(body, block.Name);
            body.WriteByte(0xa2);
        }
        body.WriteByte(0xa2);
    }

    public void WriteTypeExplore(Stream body, uint typeId)
    {
        if (!_types.TryGetValue(typeId, out TypeNode? type)) return;
        S7PlusCodec.WriteObjectStart(body, typeId, 0, 0);
        body.WriteByte(0xab);
        using (var list = new MemoryStream())
        {
            S7PlusCodec.WriteU32Little(list, 1);
            foreach (TypeNode child in type.Children) WriteTypeElement(list, child.Lid, 0, child.TypeId, 0);
            foreach (StructArrayNode array in type.StructArrays) WriteStructArrayTypeElement(list, array);
            foreach (LeafNode leaf in type.Leaves)
            {
                if (leaf.Tag.IsArray) WriteArrayTypeElement(list, leaf);
                else WriteTypeElement(list, leaf.Lid, SoftType(leaf.Tag.DataType), null,
                    leaf.Tag.DataType == SimulationDataType.String ? (ushort)254 : (ushort)0);
            }
            S7PlusCodec.WriteU16(body, checked((ushort)list.Length));
            list.Position = 0;
            list.CopyTo(body);
        }
        S7PlusCodec.WriteU16(body, 0);
        body.WriteByte(0xac);
        using (var names = new MemoryStream())
        {
            foreach (string name in type.Children.Select(x => x.Name)
                         .Concat(type.StructArrays.Select(x => x.Name))
                         .Concat(type.Leaves.Select(x => x.Name)))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name);
                names.WriteByte(checked((byte)Math.Min(255, bytes.Length)));
                names.Write(bytes, 0, Math.Min(255, bytes.Length));
                names.WriteByte(0);
            }
            S7PlusCodec.WriteU16(body, checked((ushort)names.Length));
            names.Position = 0;
            names.CopyTo(body);
        }
        S7PlusCodec.WriteU16(body, 0);
        body.WriteByte(0xa2);
    }

    private static void WriteTypeElement(Stream stream, uint lid, byte softType, uint? relationId, ushort stringLength)
    {
        S7PlusCodec.WriteU32Little(stream, lid);
        S7PlusCodec.WriteU32Little(stream, 0);
        stream.WriteByte(softType);
        ushort flags = relationId.HasValue ? (ushort)0xc000 : stringLength > 0 ? (ushort)0x9000 : (ushort)0x8000;
        S7PlusCodec.WriteU16(stream, flags);
        stream.WriteByte(0);
        S7PlusCodec.WriteU16Little(stream, stringLength);
        S7PlusCodec.WriteU16Little(stream, 0);
        if (relationId.HasValue || stringLength > 0)
        {
            S7PlusCodec.WriteU32Little(stream, 0);
            S7PlusCodec.WriteU32Little(stream, 0);
        }
        if (relationId.HasValue)
        {
            S7PlusCodec.WriteU32Little(stream, relationId.Value);
            for (int i = 0; i < 4; i++) S7PlusCodec.WriteU32Little(stream, 0);
        }
    }

    private static void WriteStructArrayTypeElement(Stream stream, StructArrayNode array)
    {
        S7PlusCodec.WriteU32Little(stream, array.Lid);
        S7PlusCodec.WriteU32Little(stream, 0); // symbol CRC
        stream.WriteByte(0);                  // struct has no scalar soft datatype
        S7PlusCodec.WriteU16(stream, 0xd000); // Struct1Dim offset-info layout
        stream.WriteByte(0);
        S7PlusCodec.WriteU16Little(stream, 0);
        S7PlusCodec.WriteU16Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, unchecked((uint)array.LowerBound));
        S7PlusCodec.WriteU32Little(stream, checked((uint)array.ElementCount));
        S7PlusCodec.WriteU32Little(stream, 0); // non-optimized element size
        S7PlusCodec.WriteU32Little(stream, 0); // optimized element size
        S7PlusCodec.WriteU32Little(stream, array.ElementType.TypeId);
        for (int i = 0; i < 4; i++) S7PlusCodec.WriteU32Little(stream, 0);
    }

    private static void WriteArrayTypeElement(Stream stream, LeafNode leaf)
    {
        S7PlusCodec.WriteU32Little(stream, leaf.Lid);
        S7PlusCodec.WriteU32Little(stream, 0);
        stream.WriteByte(SoftType(leaf.Tag.DataType));
        S7PlusCodec.WriteU16(stream, 0xa000);
        stream.WriteByte(0);
        S7PlusCodec.WriteU16Little(stream, 0); S7PlusCodec.WriteU16Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, 0); S7PlusCodec.WriteU32Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, 0);
        S7PlusCodec.WriteU32Little(stream, checked((uint)leaf.Tag.Dimensions.Aggregate(1, (a, b) => checked(a * b))));
    }

    private static byte SoftType(SimulationDataType type) => type switch
    {
        SimulationDataType.Boolean => 1,
        SimulationDataType.Byte => 52,
        SimulationDataType.SByte => 55,
        SimulationDataType.Int16 => 5,
        SimulationDataType.UInt16 => 53,
        SimulationDataType.Int32 => 7,
        SimulationDataType.UInt32 => 54,
        SimulationDataType.Int64 => 50,
        SimulationDataType.UInt64 => 49,
        SimulationDataType.Float32 => 8,
        SimulationDataType.Float64 => 48,
        SimulationDataType.String => 62,
        _ => 52,
    };

    private static string Key(uint area, IReadOnlyList<uint> lids) => area.ToString("X8", CultureInfo.InvariantCulture) + ":" + string.Join('.', lids);

    private sealed class TypeNode(string name)
    {
        private readonly Dictionary<string, TypeNode> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StructArrayNode> _structArrays = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LeafNode> _leaves = new(StringComparer.Ordinal);
        public string Name { get; } = name;
        public uint TypeId { get; set; }
        public uint Lid { get; set; }
        public IEnumerable<TypeNode> Children => _children.Values;
        public IEnumerable<StructArrayNode> StructArrays => _structArrays.Values;
        public IEnumerable<LeafNode> Leaves => _leaves.Values;
        public TypeNode GetOrAdd(string child) => _children.TryGetValue(child, out TypeNode? value) ? value : _children[child] = new TypeNode(child);
        public TypeNode GetOrAddStructArrayElement(string array, int index)
        {
            if (!_structArrays.TryGetValue(array, out StructArrayNode? value))
                _structArrays[array] = value = new StructArrayNode(array);
            return value.GetOrAdd(index);
        }
        public void AddLeaf(string leaf, SimulationTag tag) => _leaves[leaf] = new LeafNode(leaf, tag);
        public void AssignLids(uint firstLid)
        {
            uint lid = firstLid;
            foreach (TypeNode child in _children.Values) child.Lid = lid++;
            foreach (StructArrayNode array in _structArrays.Values) array.Lid = lid++;
            foreach (LeafNode leaf in _leaves.Values) leaf.Lid = lid++;
        }

        public void CopySchemaFrom(TypeNode source)
        {
            TypeId = source.TypeId;
            Lid = source.Lid;
            foreach ((string name, LeafNode sourceLeaf) in source._leaves)
                if (_leaves.TryGetValue(name, out LeafNode? leaf)) leaf.Lid = sourceLeaf.Lid;
            foreach ((string name, TypeNode sourceChild) in source._children)
                if (_children.TryGetValue(name, out TypeNode? child)) child.CopySchemaFrom(sourceChild);
            foreach ((string name, StructArrayNode sourceArray) in source._structArrays)
                if (_structArrays.TryGetValue(name, out StructArrayNode? array)) array.CopySchemaFrom(sourceArray);
        }
    }

    private sealed class StructArrayNode(string name)
    {
        private readonly SortedDictionary<int, TypeNode> _elements = new();
        public string Name { get; } = name;
        public uint Lid { get; set; }
        public int LowerBound => _elements.Keys.First();
        public int ElementCount => checked(_elements.Keys.Last() - LowerBound + 1);
        public TypeNode ElementType => _elements.Values.First();
        public IEnumerable<TypeNode> Elements => _elements.Values;
        public IEnumerable<KeyValuePair<int, TypeNode>> IndexedElements => _elements;
        public TypeNode GetOrAdd(int index) => _elements.TryGetValue(index, out TypeNode? value)
            ? value
            : _elements[index] = new TypeNode($"{Name}[{index}]");
        public void CopySchemaFrom(StructArrayNode source)
        {
            Lid = source.Lid;
            TypeNode sourceType = source.ElementType;
            foreach (TypeNode element in _elements.Values) element.CopySchemaFrom(sourceType);
        }
    }

    private sealed class LeafNode(string name, SimulationTag tag)
    {
        public string Name { get; } = name;
        public SimulationTag Tag { get; } = tag;
        public uint Lid { get; set; }
    }

    private sealed record DbNode(string Name, uint Area, uint TypeId, TypeNode Type);
}

internal sealed record S7PlusRequest(byte Version, ushort FunctionCode, ushort Sequence, byte TransportFlags, byte[] Bytes, int PayloadOffset);
internal sealed record S7PlusAddress(uint Area, uint SubArea, IReadOnlyList<uint> Lids);

internal static class S7PlusCodec
{
    public const ushort CreateObject = 0x04ca;
    public const ushort Explore = 0x04bb;
    public const ushort SetMulti = 0x0542;
    public const ushort GetMulti = 0x054c;
    public const ushort InitSsl = 0x05b3;

    public static S7PlusRequest ParseRequest(byte[] bytes)
    {
        if (bytes.Length < 18 || bytes[0] != 0x72 || bytes[4] != 0x31) throw new InvalidDataException("无效的 S7 Plus 请求");
        ushort function = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(7, 2));
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(11, 2));
        return new S7PlusRequest(bytes[1], function, sequence, bytes[17], bytes, 18);
    }

    public static byte[] Response(byte version, ushort function, ushort sequence, byte flags, Action<Stream> writeBody, ulong returnValue = 0)
    {
        using var body = new MemoryStream();
        body.WriteByte(0x32);
        WriteU16(body, 0);
        WriteU16(body, function);
        WriteU16(body, 0);
        WriteU16(body, sequence);
        body.WriteByte(flags);
        WriteVlq(body, returnValue);
        writeBody(body);
        return Frame(version, body.ToArray());
    }

    public static byte[] Frame(byte version, byte[] body)
    {
        using var output = new MemoryStream();
        int offset = 0;
        while (offset < body.Length)
        {
            int count = Math.Min(60_000, body.Length - offset);
            output.WriteByte(0x72); output.WriteByte(version); WriteU16(output, (ushort)count);
            output.Write(body, offset, count);
            offset += count;
        }
        output.WriteByte(0x72); output.WriteByte(version); WriteU16(output, 0);
        return output.ToArray();
    }

    public static async Task<byte[]> ReadTelegramAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        byte version = 2;
        while (true)
        {
            byte[] header = new byte[4];
            await ReadExactlyAsync(stream, header, ct);
            if (header[0] != 0x72) throw new InvalidDataException("S7 Plus 分片头无效");
            version = header[1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            if (length == 0) break;
            byte[] chunk = new byte[length];
            await ReadExactlyAsync(stream, chunk, ct);
            body.Write(chunk);
        }
        return Frame(version, body.ToArray());
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] destination, CancellationToken ct)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public static S7PlusAddress ReadAddress(byte[] bytes, ref int offset)
    {
        _ = ReadVlq(bytes, ref offset); // symbol CRC
        uint area = ReadVlq(bytes, ref offset);
        uint count = ReadVlq(bytes, ref offset);
        uint subArea = ReadVlq(bytes, ref offset);
        var lids = new uint[Math.Max(0, checked((int)count - 1))];
        for (int i = 0; i < lids.Length; i++) lids[i] = ReadVlq(bytes, ref offset);
        return new S7PlusAddress(area, subArea, lids);
    }

    public static uint ReadU32(byte[] bytes, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4; return value;
    }

    public static uint ReadVlq(byte[] bytes, ref int offset)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++) { byte b = bytes[offset++]; value = (value << 7) + (uint)(b & 0x7f); if ((b & 0x80) == 0) break; }
        return value;
    }

    private static ulong ReadVlq64(byte[] bytes, ref int offset)
    {
        ulong value = 0; byte cont = 0;
        for (int i = 0; i < 8; i++) { byte b = bytes[offset++]; value = (value << 7) + (ulong)(b & 0x7f); cont = (byte)(b & 0x80); if (cont == 0) break; }
        if (cont != 0) value = (value << 8) + bytes[offset++];
        return value;
    }

    private static long ReadSignedVlq(byte[] bytes, ref int offset, int maximum)
    {
        long value = 0; byte cont = 0;
        for (int i = 1; i <= maximum; i++)
        {
            byte b = bytes[offset++];
            if (i == 1 && (b & 0x40) != 0) { b &= 0xbf; value = -64; } else value <<= 7;
            cont = (byte)(b & 0x80); value += b & 0x7f;
            if (cont == 0) break;
        }
        if (cont != 0) value = (value << 8) + bytes[offset++];
        return value;
    }

    public static object? ReadValue(byte[] bytes, ref int offset)
    {
        byte flags = bytes[offset++];
        byte type = bytes[offset++];
        if ((flags & 0x70) != 0)
        {
            if ((flags & 0x40) != 0) throw new InvalidDataException("暂不支持稀疏 S7 Plus 数组");
            uint count = ReadVlq(bytes, ref offset);
            if (count > 1_000_000 || count > bytes.Length - offset) throw new InvalidDataException("S7 Plus 数组长度无效");
            if (type == 0x02) { byte[] data = bytes.AsSpan(offset, checked((int)count)).ToArray(); offset += data.Length; return data; }
            var values = new object?[count];
            for (int i = 0; i < values.Length; i++) values[i] = ReadScalarValue(type, bytes, ref offset);
            return values;
        }
        return ReadScalarValue(type, bytes, ref offset);
    }

    private static object? ReadScalarValue(byte type, byte[] bytes, ref int offset)
    {
        return type switch
        {
            0x00 => null,
            0x01 => bytes[offset++] != 0,
            0x02 or 0x0a => bytes[offset++],
            0x06 => unchecked((sbyte)bytes[offset++]),
            0x03 or 0x0b => ReadFixedU16(bytes, ref offset),
            0x07 => unchecked((short)ReadFixedU16(bytes, ref offset)),
            0x04 => ReadVlq(bytes, ref offset),
            0x08 => checked((int)ReadSignedVlq(bytes, ref offset, 5)),
            0x05 => ReadVlq64(bytes, ref offset),
            0x09 => ReadSignedVlq(bytes, ref offset, 8),
            0x0c => ReadU32(bytes, ref offset),
            0x0d => ReadFixedU64(bytes, ref offset),
            0x0e => BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(bytes, ref offset))),
            0x0f => BitConverter.Int64BitsToDouble(unchecked((long)ReadFixedU64(bytes, ref offset))),
            0x15 => ReadText(bytes, ref offset),
            _ => throw new InvalidDataException($"暂不支持 S7 Plus 写入类型 0x{type:X2}"),
        };
    }

    private static ushort ReadFixedU16(byte[] bytes, ref int offset) { ushort v = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)); offset += 2; return v; }
    private static ulong ReadFixedU64(byte[] bytes, ref int offset) { ulong v = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, 8)); offset += 8; return v; }
    private static string ReadText(byte[] bytes, ref int offset) { int n = checked((int)ReadVlq(bytes, ref offset)); string v = Encoding.UTF8.GetString(bytes, offset, n); offset += n; return v; }

    public static void WriteValue(Stream stream, SimulationDataType type, object? value)
    {
        if (value is Array array)
        {
            using var element = new MemoryStream();
            WriteValue(element, type, array.Length > 0 ? array.GetValue(0) : null);
            byte wireType = element.ToArray()[1];
            stream.WriteByte(0x10); stream.WriteByte(wireType); WriteVlq(stream, checked((uint)array.Length));
            foreach (object? item in array)
            {
                element.SetLength(0); WriteValue(element, type, item);
                stream.Write(element.GetBuffer(), 2, checked((int)element.Length - 2));
            }
            return;
        }
        switch (type)
        {
            case SimulationDataType.Boolean: stream.WriteByte(0); stream.WriteByte(0x01); stream.WriteByte(Convert.ToBoolean(value) ? (byte)1 : (byte)0); break;
            case SimulationDataType.Byte: stream.WriteByte(0); stream.WriteByte(0x02); stream.WriteByte(Convert.ToByte(value)); break;
            case SimulationDataType.SByte: stream.WriteByte(0); stream.WriteByte(0x06); stream.WriteByte(unchecked((byte)Convert.ToSByte(value))); break;
            case SimulationDataType.Int16: stream.WriteByte(0); stream.WriteByte(0x07); WriteU16(stream, unchecked((ushort)Convert.ToInt16(value))); break;
            case SimulationDataType.UInt16: stream.WriteByte(0); stream.WriteByte(0x03); WriteU16(stream, Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32: stream.WriteByte(0); stream.WriteByte(0x08); WriteSignedVlq(stream, Convert.ToInt32(value)); break;
            case SimulationDataType.UInt32: stream.WriteByte(0); stream.WriteByte(0x04); WriteVlq(stream, Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64: stream.WriteByte(0); stream.WriteByte(0x09); WriteSignedVlq(stream, Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64: stream.WriteByte(0); stream.WriteByte(0x05); WriteVlq(stream, Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32: stream.WriteByte(0); stream.WriteByte(0x0e); WriteU32(stream, unchecked((uint)BitConverter.SingleToInt32Bits(Convert.ToSingle(value)))); break;
            case SimulationDataType.Float64: stream.WriteByte(0); stream.WriteByte(0x0f); WriteU64(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)))); break;
            case SimulationDataType.String: WriteWString(stream, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
            default: WriteNull(stream); break;
        }
    }

    public static void WriteNull(Stream stream) { stream.WriteByte(0); stream.WriteByte(0); }
    public static void WriteRid(Stream stream, uint value) { stream.WriteByte(0); stream.WriteByte(0x12); WriteU32(stream, value); }
    public static void WriteWString(Stream stream, string value) { stream.WriteByte(0); stream.WriteByte(0x15); byte[] bytes = Encoding.UTF8.GetBytes(value); WriteVlq(stream, (uint)bytes.Length); stream.Write(bytes); }
    public static void WriteEmptyStruct(Stream stream, uint id) { stream.WriteByte(0); stream.WriteByte(0x17); WriteU32(stream, id); stream.WriteByte(0); }

    public static void WriteObjectStart(Stream stream, uint relationId, uint classId, uint attributeId)
    {
        stream.WriteByte(0xa1); WriteU32(stream, relationId); WriteVlq(stream, classId); WriteVlq(stream, 0); WriteVlq(stream, attributeId);
    }

    public static void WriteVlq(Stream stream, uint value) => WriteVlq(stream, (ulong)value);
    public static void WriteVlq(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[10]; int count = 0;
        do { bytes[count++] = (byte)(value & 0x7f); value >>= 7; } while (value != 0);
        for (int i = count - 1; i >= 0; i--) stream.WriteByte((byte)(bytes[i] | (i == 0 ? 0 : 0x80)));
    }

    private static void WriteSignedVlq(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[10]; int count = 0; bool more;
        do
        {
            byte current = (byte)(value & 0x7f);
            value >>= 7;
            bool sign = (current & 0x40) != 0;
            more = !((value == 0 && !sign) || (value == -1 && sign));
            bytes[count++] = current;
        } while (more);
        for (int i = count - 1; i >= 0; i--) stream.WriteByte((byte)(bytes[i] | (i == 0 ? 0 : 0x80)));
    }

    public static void WriteU16(Stream stream, ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    public static void WriteU32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    public static void WriteU64(Stream stream, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); }
    public static void WriteU16Little(Stream stream, ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); stream.Write(b); }
    public static void WriteU32Little(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
}
