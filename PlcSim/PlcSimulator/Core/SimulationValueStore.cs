#nullable enable
using System.Collections.Concurrent;
using System.Globalization;

namespace PlcSimulator.Core;

public sealed class SimulationValueStore
{
    private sealed class Cell
    {
        public Cell(SimulationTag tag, object? value) { Tag = tag; Value = value; Timestamp = DateTimeOffset.UtcNow; }
        public SimulationTag Tag { get; }
        public object Gate { get; } = new();
        public object? Value { get; set; }
        public long Version { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Cell> _cells = new();
    public SimulationValueStore(SimulationProject project)
    {
        foreach (var tag in project.EnumerateTags())
            if (!_cells.TryAdd(tag.Id, new Cell(tag, Clone(ConvertValue(tag, tag.InitialValue))))) throw new SimulationValueException($"点位 ID 重复：{tag.Id}");
    }

    public event Action<SimulationValueChanged>? ValueChanged;
    public long ReadVersion(Guid id) { var cell = Get(id); lock (cell.Gate) return cell.Version; }
    public SimulationUntypedValue Read(Guid id)
    {
        var cell = Get(id);
        lock (cell.Gate) return new SimulationUntypedValue(Clone(cell.Value), cell.Version, cell.Timestamp);
    }

    public SimulationValue<T> Read<T>(Guid id)
    {
        var cell = Get(id);
        lock (cell.Gate)
        {
            if (cell.Value is not T value) throw new SimulationValueException($"{cell.Tag.SymbolPath} 的值类型不是 {typeof(T).Name}");
            return new SimulationValue<T>((T)Clone(value)!, cell.Version, cell.Timestamp);
        }
    }

    public SimulationValueChanged Write(Guid id, object? value, SimulationWriteSource source) => WriteCore(id, value, source, null)!;

    public bool TryWrite(Guid id, object? value, SimulationWriteSource source, long expectedVersion) => WriteCore(id, value, source, expectedVersion) != null;

    private SimulationValueChanged? WriteCore(Guid id, object? value, SimulationWriteSource source, long? expectedVersion)
    {
        var cell = Get(id); SimulationValueChanged change;
        lock (cell.Gate)
        {
            if (expectedVersion.HasValue && cell.Version != expectedVersion.Value) return null;
            var converted = ConvertValue(cell.Tag, value);
            cell.Value = Clone(converted); cell.Version++; cell.Timestamp = DateTimeOffset.UtcNow;
            change = new(id, Clone(converted), cell.Version, cell.Timestamp, source);
        }
        ValueChanged?.Invoke(change); return change;
    }

    private Cell Get(Guid id) => _cells.TryGetValue(id, out var cell) ? cell : throw new SimulationValueException($"未注册点位：{id}");

    public SimulationValueChanged WriteElement(Guid id, int index, object? value, SimulationWriteSource source)
    {
        var cell = Get(id);
        SimulationValueChanged change;
        lock (cell.Gate)
        {
            if (!cell.Tag.IsArray || cell.Value is not Array current || index < 0 || index >= current.Length)
                throw new SimulationValueException("数组索引越界");
            var next = (Array)current.Clone();
            try { var converted = ConvertScalar(cell.Tag.DataType, value); ValidateString(cell.Tag, converted); next.SetValue(converted, index); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            { throw new SimulationValueException($"{cell.Tag.SymbolPath}[{index}] 的值无法转换为 {cell.Tag.DataType}", ex); }
            cell.Value = next; cell.Version++; cell.Timestamp = DateTimeOffset.UtcNow;
            change = new(id, Clone(next), cell.Version, cell.Timestamp, source);
        }
        ValueChanged?.Invoke(change); return change;
    }
    internal static object? ConvertValue(SimulationTag tag, object? value)
    {
        try
        {
            if (!tag.IsArray) { var scalar = ConvertScalar(tag.DataType, value); ValidateString(tag, scalar); return scalar; }
            if (value is not Array source) throw new SimulationValueException($"{tag.SymbolPath} 需要数组值");
            int expected = tag.Dimensions.Aggregate(1, (a, b) => checked(a * b));
            if (source.Length != expected) throw new SimulationValueException($"{tag.SymbolPath} 数组长度应为 {expected}");
            var result = Array.CreateInstance(ClrType(tag.DataType), source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                var scalar = ConvertScalar(tag.DataType, source.GetValue(i)); ValidateString(tag, scalar); result.SetValue(scalar, i);
            }
            return result;
        }
        catch (SimulationValueException) { throw; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { throw new SimulationValueException($"{tag.SymbolPath} 的值无法转换为 {tag.DataType}", ex); }
    }

    private static void ValidateString(SimulationTag tag, object? value)
    {
        if (tag.DataType != SimulationDataType.String || value is not string text) return;
        bool classic = !string.IsNullOrWhiteSpace(tag.ClassicAddress);
        if (!classic && tag.StringLength == null) return;
        int length = tag.IsWideString || !classic ? text.Length : SimulationStringCodec.Encode(tag, text).Length;
        int maximum = tag.StringLength ?? 254;
        if (length > maximum) throw new SimulationValueException($"{tag.SymbolPath} 最多允许 {maximum} 个{(tag.IsWideString || !classic ? "UTF-16 字符" : "字节")}");
    }

    private static object? ConvertScalar(SimulationDataType type, object? value)
    {
        if (value == null) return type switch { SimulationDataType.String => string.Empty, SimulationDataType.Bytes => Array.Empty<byte>(), _ => Activator.CreateInstance(ClrType(type)) };
        var c = CultureInfo.InvariantCulture;
        return type switch
        {
            SimulationDataType.Boolean => value is string s ? bool.Parse(s) : Convert.ToBoolean(value, c),
            SimulationDataType.Byte => Convert.ToByte(value, c), SimulationDataType.SByte => Convert.ToSByte(value, c),
            SimulationDataType.Int16 => Convert.ToInt16(value, c), SimulationDataType.UInt16 => Convert.ToUInt16(value, c),
            SimulationDataType.Int32 => Convert.ToInt32(value, c), SimulationDataType.UInt32 => Convert.ToUInt32(value, c),
            SimulationDataType.Int64 => Convert.ToInt64(value, c), SimulationDataType.UInt64 => Convert.ToUInt64(value, c),
            SimulationDataType.Float32 => Convert.ToSingle(value, c), SimulationDataType.Float64 => Convert.ToDouble(value, c),
            SimulationDataType.String => Convert.ToString(value, c) ?? string.Empty,
            SimulationDataType.DateTime => value is DateTimeOffset dto ? dto : DateTimeOffset.Parse(Convert.ToString(value, c)!, c),
            SimulationDataType.TimeSpan => value is TimeSpan ts ? ts : TimeSpan.Parse(Convert.ToString(value, c)!, c),
            SimulationDataType.Bytes => value is byte[] bytes ? bytes.ToArray() : throw new InvalidCastException(),
            SimulationDataType.Unknown => value, _ => throw new SimulationValueException($"不支持的数据类型：{type}")
        };
    }

    private static Type ClrType(SimulationDataType type) => type switch
    {
        SimulationDataType.Boolean => typeof(bool), SimulationDataType.Byte => typeof(byte), SimulationDataType.SByte => typeof(sbyte),
        SimulationDataType.Int16 => typeof(short), SimulationDataType.UInt16 => typeof(ushort), SimulationDataType.Int32 => typeof(int),
        SimulationDataType.UInt32 => typeof(uint), SimulationDataType.Int64 => typeof(long), SimulationDataType.UInt64 => typeof(ulong),
        SimulationDataType.Float32 => typeof(float), SimulationDataType.Float64 => typeof(double), SimulationDataType.String => typeof(string),
        SimulationDataType.DateTime => typeof(DateTimeOffset), SimulationDataType.TimeSpan => typeof(TimeSpan), SimulationDataType.Bytes => typeof(byte[]), _ => typeof(object)
    };
    private static object? Clone(object? value) => value is Array a ? a.Clone() : value;
}
