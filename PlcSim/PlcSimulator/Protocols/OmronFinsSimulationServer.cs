#nullable enable
using HslCommunication.Profinet.Omron;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

public sealed class OmronFinsSimulationServer : IAsyncDisposable
{
    private readonly StoppableFinsServer _server = new() { EnableWrite = true };
    private readonly SimulationValueStore _store;
    private readonly Dictionary<Guid, SimulationTag> _tags;
    private Timer? _poller;
    private int _syncing;
    private int _pollThreadId;

    public OmronFinsSimulationServer(SimulationDevice device, SimulationValueStore store, int port)
    {
        _store = store; _server.Port = port;
        _tags = device.EnumerateTags().Where(x => !string.IsNullOrWhiteSpace(x.ProtocolAddress) && x.DataType != SimulationDataType.Unknown).ToDictionary(x => x.Id);
    }

    public int OnlineCount => _server.OnlineCount;
    public void Start()
    {
        if (_server.IsStarted) return;
        foreach (var tag in _tags.Values) WriteServer(tag, _store.Read(tag.Id).Value);
        _store.ValueChanged += OnStoreChanged; _server.ServerStart();
        _poller = new Timer(_ => Poll(), null, 50, 50);
    }

    private void OnStoreChanged(SimulationValueChanged change)
    {
        // Skip only this poll callback; concurrent UI/other protocol writes still propagate.
        if (change.Source == SimulationWriteSource.Protocol && Volatile.Read(ref _pollThreadId) == Environment.CurrentManagedThreadId) return;
        if (_tags.TryGetValue(change.TagId, out var tag)) WriteServer(tag, _store.Read(change.TagId).Value);
    }

    private void Poll()
    {
        if (!_server.IsStarted || Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        Volatile.Write(ref _pollThreadId, Environment.CurrentManagedThreadId);
        try
        {
            foreach (var tag in _tags.Values)
            {
                var before = _store.Read(tag.Id);
                object? current = ReadServer(tag);
                if (current != null && !ArrayEquals(current, before.Value)) _store.TryWrite(tag.Id, current, SimulationWriteSource.Protocol, before.Version);
            }
        }
        catch { }
        finally { Volatile.Write(ref _pollThreadId, 0); Volatile.Write(ref _syncing, 0); }
    }

    private object? ReadServer(SimulationTag tag) => tag.IsArray ? ReadArray(tag) : tag.DataType switch
    {
        SimulationDataType.Boolean => _server.ReadBool(tag.ProtocolAddress).Content,
        SimulationDataType.Int16 => _server.ReadInt16(tag.ProtocolAddress).Content,
        SimulationDataType.UInt16 => _server.ReadUInt16(tag.ProtocolAddress).Content,
        SimulationDataType.Int32 => _server.ReadInt32(tag.ProtocolAddress).Content,
        SimulationDataType.UInt32 => _server.ReadUInt32(tag.ProtocolAddress).Content,
        SimulationDataType.Int64 => _server.ReadInt64(tag.ProtocolAddress).Content,
        SimulationDataType.UInt64 => _server.ReadUInt64(tag.ProtocolAddress).Content,
        SimulationDataType.Float32 => _server.ReadFloat(tag.ProtocolAddress).Content,
        SimulationDataType.Float64 => _server.ReadDouble(tag.ProtocolAddress).Content,
        _ => null,
    };

    private void WriteServer(SimulationTag tag, object? value)
    {
        if (tag.IsArray) { WriteArray(tag, value); return; }
        switch (tag.DataType)
        {
            case SimulationDataType.Boolean: _server.Write(tag.ProtocolAddress, Convert.ToBoolean(value)); break;
            case SimulationDataType.Int16: _server.Write(tag.ProtocolAddress, Convert.ToInt16(value)); break;
            case SimulationDataType.UInt16: _server.Write(tag.ProtocolAddress, Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32: _server.Write(tag.ProtocolAddress, Convert.ToInt32(value)); break;
            case SimulationDataType.UInt32: _server.Write(tag.ProtocolAddress, Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64: _server.Write(tag.ProtocolAddress, Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64: _server.Write(tag.ProtocolAddress, Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32: _server.Write(tag.ProtocolAddress, Convert.ToSingle(value)); break;
            case SimulationDataType.Float64: _server.Write(tag.ProtocolAddress, Convert.ToDouble(value)); break;
        }
    }

    private object? ReadArray(SimulationTag tag)
    {
        ushort count = checked((ushort)tag.Dimensions.Aggregate(1, (a, b) => checked(a * b)));
        string address = tag.ProtocolAddress;
        return tag.DataType switch
        {
            SimulationDataType.Boolean => Require(_server.ReadBool(address, count)),
            SimulationDataType.Int16 => Require(_server.ReadInt16(address, count)),
            SimulationDataType.UInt16 => Require(_server.ReadUInt16(address, count)),
            SimulationDataType.Int32 => Require(_server.ReadInt32(address, count)),
            SimulationDataType.UInt32 => Require(_server.ReadUInt32(address, count)),
            SimulationDataType.Int64 => Require(_server.ReadInt64(address, count)),
            SimulationDataType.UInt64 => Require(_server.ReadUInt64(address, count)),
            SimulationDataType.Float32 => Require(_server.ReadFloat(address, count)),
            SimulationDataType.Float64 => Require(_server.ReadDouble(address, count)),
            _ => throw new NotSupportedException($"暂不支持 {tag.DataType} 数组"),
        };
    }

    private void WriteArray(SimulationTag tag, object? value)
    {
        string address = tag.ProtocolAddress;
        var result = value switch
        {
            bool[] items => _server.Write(address, items),
            short[] items => _server.Write(address, items),
            ushort[] items => _server.Write(address, items),
            int[] items => _server.Write(address, items),
            uint[] items => _server.Write(address, items),
            long[] items => _server.Write(address, items),
            ulong[] items => _server.Write(address, items),
            float[] items => _server.Write(address, items),
            double[] items => _server.Write(address, items),
            _ => throw new NotSupportedException($"暂不支持 {tag.DataType} 数组"),
        };
        if (!result.IsSuccess) throw new SimulationValueException(result.Message);
    }

    private static T Require<T>(HslCommunication.OperateResult<T> result) => result.IsSuccess
        ? result.Content : throw new SimulationValueException(result.Message);

    private static bool ArrayEquals(object? left, object? right) => left is Array a && right is Array b
        ? a.Cast<object?>().SequenceEqual(b.Cast<object?>()) : Equals(left, right);

    public ValueTask DisposeAsync()
    {
        _poller?.Dispose(); _poller = null; _store.ValueChanged -= OnStoreChanged;
        try { _server.ServerClose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
