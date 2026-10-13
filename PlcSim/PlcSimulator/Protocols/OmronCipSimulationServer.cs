#nullable enable
using HslCommunication.Profinet.AllenBradley;
using PlcSimulator.Core;

namespace PlcSimulator.Protocols;

public sealed class OmronCipSimulationServer : IAsyncDisposable
{
    private readonly SimulationDevice _device;
    private readonly SimulationValueStore _store;
    private readonly StoppableCipServer _server = new() { CreateTagWithWrite = true, EnableWrite = true };
    private readonly Dictionary<Guid, SimulationTag> _tags;
    private Timer? _poller;
    private int _syncing;
    private int _pollThreadId;

    public OmronCipSimulationServer(SimulationDevice device, SimulationValueStore store, int port)
    {
        _device = device; _store = store; _server.Port = port;
        _tags = device.EnumerateTags().Where(x => x.DataType != SimulationDataType.Unknown).ToDictionary(x => x.Id);
    }

    public bool IsRunning => _server.IsStarted;
    public int OnlineCount => _server.OnlineCount;

    public void Start()
    {
        if (_server.IsStarted) return;
        foreach (var tag in _tags.Values) AddTag(tag, _store.Read(tag.Id).Value);
        _store.ValueChanged += StoreOnValueChanged;
        _server.ServerStart();
        _poller = new Timer(_ => Poll(), null, 50, 50);
    }

    private void StoreOnValueChanged(SimulationValueChanged change)
    {
        // Skip only this poll callback; concurrent UI/other protocol writes still propagate.
        if (change.Source == SimulationWriteSource.Protocol && Volatile.Read(ref _pollThreadId) == Environment.CurrentManagedThreadId) return;
        if (!_tags.TryGetValue(change.TagId, out var tag)) return;
        WriteTag(tag, _store.Read(change.TagId).Value);
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
                object? value = ReadTag(tag);
                if (value != null && !ArrayEquals(value, before.Value)) _store.TryWrite(tag.Id, value, SimulationWriteSource.Protocol, before.Version);
            }
        }
        catch { }
        finally { Volatile.Write(ref _pollThreadId, 0); Volatile.Write(ref _syncing, 0); }
    }

    private string Address(SimulationTag tag) => string.IsNullOrWhiteSpace(tag.ProtocolAddress) ? tag.SymbolPath : tag.ProtocolAddress;
    private void AddTag(SimulationTag tag, object? value)
    {
        if (tag.IsArray) { AddArray(tag, value); return; }
        string a = Address(tag);
        switch (tag.DataType)
        {
            case SimulationDataType.Boolean: _server.AddTagValue(a, Convert.ToBoolean(value)); break;
            case SimulationDataType.Byte: _server.AddTagValue(a, Convert.ToByte(value)); break;
            case SimulationDataType.Int16: _server.AddTagValue(a, Convert.ToInt16(value)); break;
            case SimulationDataType.UInt16: _server.AddTagValue(a, Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32: _server.AddTagValue(a, new[] { Convert.ToInt32(value) }); break;
            case SimulationDataType.UInt32: _server.AddTagValue(a, Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64: _server.AddTagValue(a, Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64: _server.AddTagValue(a, Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32: _server.AddTagValue(a, Convert.ToSingle(value)); break;
            case SimulationDataType.Float64: _server.AddTagValue(a, Convert.ToDouble(value)); break;
            case SimulationDataType.String: _server.AddTagValue(a, Convert.ToString(value) ?? string.Empty, 256); break;
        }
    }

    private object? ReadTag(SimulationTag tag)
    {
        if (tag.IsArray) return ReadArray(tag);
        string a = Address(tag);
        return tag.DataType switch
        {
            SimulationDataType.Boolean => _server.ReadBool(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.Byte => _server.ReadByte(a).Content,
            SimulationDataType.Int16 => _server.ReadInt16(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.UInt16 => _server.ReadUInt16(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.Int32 => _server.ReadInt32(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.UInt32 => _server.ReadUInt32(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.Int64 => _server.ReadInt64(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.UInt64 => _server.ReadUInt64(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.Float32 => _server.ReadFloat(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.Float64 => _server.ReadDouble(a, 1).Content?.FirstOrDefault(),
            SimulationDataType.String => _server.ReadString(a, 256, System.Text.Encoding.UTF8).Content,
            _ => null,
        };
    }

    private void WriteTag(SimulationTag tag, object? value)
    {
        if (tag.IsArray) { WriteArray(tag, value); return; }
        string a = Address(tag);
        switch (tag.DataType)
        {
            case SimulationDataType.Boolean: _server.Write(a, Convert.ToBoolean(value)); break;
            case SimulationDataType.Byte: _server.Write(a, Convert.ToByte(value)); break;
            case SimulationDataType.Int16: _server.Write(a, Convert.ToInt16(value)); break;
            case SimulationDataType.UInt16: _server.Write(a, Convert.ToUInt16(value)); break;
            case SimulationDataType.Int32: _server.Write(a, Convert.ToInt32(value)); break;
            case SimulationDataType.UInt32: _server.Write(a, Convert.ToUInt32(value)); break;
            case SimulationDataType.Int64: _server.Write(a, Convert.ToInt64(value)); break;
            case SimulationDataType.UInt64: _server.Write(a, Convert.ToUInt64(value)); break;
            case SimulationDataType.Float32: _server.Write(a, Convert.ToSingle(value)); break;
            case SimulationDataType.Float64: _server.Write(a, Convert.ToDouble(value)); break;
            case SimulationDataType.String: _server.Write(a, Convert.ToString(value) ?? string.Empty, System.Text.Encoding.UTF8); break;
        }
    }

    private void AddArray(SimulationTag tag, object? value)
    {
        string a = Address(tag);
        switch (value)
        {
            case bool[] items: _server.AddTagValue(a, items); break;
            case short[] items: _server.AddTagValue(a, items); break;
            case ushort[] items: _server.AddTagValue(a, items); break;
            case int[] items: _server.AddTagValue(a, items); break;
            case uint[] items: _server.AddTagValue(a, items); break;
            case long[] items: _server.AddTagValue(a, items); break;
            case ulong[] items: _server.AddTagValue(a, items); break;
            case float[] items: _server.AddTagValue(a, items); break;
            case double[] items: _server.AddTagValue(a, items); break;
            default: throw new NotSupportedException($"CIP 暂不支持 {tag.DataType} 数组");
        }
    }

    private object? ReadArray(SimulationTag tag)
    {
        ushort count = checked((ushort)tag.Dimensions.Aggregate(1, (a, b) => checked(a * b)));
        string address = Address(tag);
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
        string address = Address(tag);
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
        _poller?.Dispose(); _poller = null; _store.ValueChanged -= StoreOnValueChanged;
        try { _server.ServerClose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
