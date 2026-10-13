using System.Globalization;
using IGW.Abstractions;

namespace IGW.Drivers.IoTClient;

/// <summary>
/// 将一个 S7 Plus 逻辑数组展开为协议可解析的符号叶子，并在边界重组为不可变列式数组。
/// s7commplus 当前不支持裸数组结构体读取，此层仍保持 IGW 中仅注册/调度一个逻辑 Tag。
/// </summary>
internal static class SiemensSymbolicArrayCodec
{
    // s7commplus 的 read/write 是一次一条完整 telegram,不内部拆分。数组元素符号数多时,
    // 一次发太多会拼出巨型 GetMultiVariables/SetMultiVariables 报文,S7-1500 通信负载高
    // (例如与其他采集程序共用一台 PLC)时会直接掐断会话。分片大小由 AdaptiveRequestWindow
    // 自适应:初始和上限均为 256(保留健康 PLC 的原始请求数),容量拒绝或断会话自动
    // 减半重试,成功后自动增长回来。window 为 null 时保持固定 256(兼容旧调用方)。
    private const int NativeBatchSize = 256;
    /// <summary>自适应数组分片的初始值(window 为 null 时不用;健康 PLC 会自动增长到 256)。</summary>
    public const int AdaptiveInitialBatchSize = 256;

    public static ArrayRuntimeValue Read(ISiemensPlusNativeApi native, TagBinding binding,
        AdaptiveRequestWindow? window = null, Func<bool>? priorityWorkPending = null)
    {
        var array = binding.Def.Array ?? throw new ArgumentException("点位不是数组。", nameof(binding));
        var plan = Build(binding.Def.Address, array.Shape, array);
        var items = plan.Columns.SelectMany(static column => column.Items).ToArray();
        int cursor = 0;
        while (cursor < items.Length)
        {
            if (priorityWorkPending?.Invoke() == true) throw new DriverReadYieldException();
            int size = Math.Min(window?.Capture().Current ?? NativeBatchSize, items.Length - cursor);
            var chunk = items[cursor..(cursor + size)];
            var symbols = chunk.Select(static item => item.Symbol).ToArray();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = native.ReadMeasured(symbols,
                () => { if (priorityWorkPending?.Invoke() == true) throw new DriverReadYieldException(); },
                new("Read", "S7 Plus array symbols", 1, -1, 0));
            if (result.Code == SiemensPlusResultCode.CapacityFailure && window is not null)
            {
                if (size > 1)
                {
                    window?.OnCapacityFailure(size);
                    continue;   // 同批缩半重试
                }
                chunk[0].Status = QualityFor(SiemensPlusResultCode.CapacityFailure);
                cursor += 1;
                continue;      // 单点也放不下:标 Bad 后继续,不再拖垮整批
            }
            if (result.Code != SiemensPlusResultCode.Ok)
            {
                // 断会话/超时让异常上抛(由驱动负责重连/熔断),但把分片收小:
                // 下次重连后的首个请求更温和,避免高负载 PLC 上"一连上就被大包打死"的循环。
                if (result.Code is SiemensPlusResultCode.TransportFailure or SiemensPlusResultCode.Timeout)
                    window?.OnCapacityFailure(size);
                throw new SiemensPlusException(result.Code);
            }
            if (result.Values.Count != chunk.Length)
                throw new SiemensPlusException(SiemensPlusResultCode.ProtocolFailure);
            for (int i = 0; i < chunk.Length; i++)
            {
                chunk[i].Value = result.Values[i].Value;
                chunk[i].Status = QualityFor(result.Values[i].Code);
            }
            window?.OnSuccess(size, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            cursor += size;
        }
        return Materialize(plan, array, binding.Def.TextEncoding);
    }

    public static ArrayWriteResult Write(ISiemensPlusNativeApi native, ArrayWriteRequest request,
        AdaptiveRequestWindow? window = null)
    {
        var array = request.Binding.Def.Array ?? throw new ArgumentException("点位不是数组。", nameof(request));
        var sourceShape = array.Shape;
        var sliceShape = request.Slice.ValidateAndCreateShape(sourceShape);
        if (!sliceShape.Dimensions.SequenceEqual(request.Value.Shape.Dimensions) ||
            !sliceShape.LowerBounds.SequenceEqual(request.Value.Shape.LowerBounds))
            throw new ArgumentException("写入数组值形状与请求切片不一致。", nameof(request));

        var plan = Build(request.Binding.Def.Address, sliceShape, array);
        var writes = new List<(SiemensPlusNativeWrite Write, int OuterOffset)>();
        foreach (var column in plan.Columns)
        {
            var values = ResolveMember(request.Value, column.Path);
            for (int i = 0; i < column.Items.Count; i++)
                writes.Add((new SiemensPlusNativeWrite(column.Items[i].Symbol,
                    Normalize(values.GetBoxedValue(i), column.DataType)), column.Items[i].OuterOffset));
        }

        var failed = new Dictionary<int, StatusCode>();
        int cursor = 0;
        while (cursor < writes.Count)
        {
            int size = Math.Min(window?.Capture().Current ?? NativeBatchSize, writes.Count - cursor);
            var chunk = writes.GetRange(cursor, size);
            var nativeWrites = chunk.Select(static item => item.Write).ToArray();
            var written = ProtocolOperationTiming.MeasureResult(() => native.WriteItems(nativeWrites),
                static result => result.Code != SiemensPlusResultCode.Ok,
                new("Write", "S7 Plus array symbols", 1, -1, 0));
            var code = written.Code;
            if (code == SiemensPlusResultCode.CapacityFailure && size > 1 && window is not null)
            {
                window?.OnCapacityFailure(size);
                continue;   // 缩半重试
            }
            if (code == SiemensPlusResultCode.Ok)
            {
                window?.OnSuccess(size, 1);
                // 逐项结果：只有被拒绝或找不到的元素记为失败
                for (int i = 0; i < chunk.Count && i < written.Items.Count; i++)
                    if (written.Items[i] != SiemensPlusResultCode.Ok) failed[chunk[i].OuterOffset] = QualityFor(written.Items[i]);
                cursor += size;
                continue;
            }
            var status = QualityFor(code);
            foreach (var item in chunk) failed[item.OuterOffset] = status;
            cursor += size;
        }
        var failures = failed.OrderBy(static pair => pair.Key).Select(pair => new ArrayElementFailure(
            sliceShape.GetIndices(pair.Key), pair.Value, "S7 Plus 数组符号写入失败")).ToArray();
        int succeeded = request.Value.Count - failed.Count;
        return new ArrayWriteResult(failures.Length == 0 ? StatusCode.Good :
            succeeded > 0 ? StatusCode.Uncertain : failures[0].Status, succeeded, failures);
    }

    private static SymbolicPlan Build(string baseSymbol, ArrayShape shape, ArrayDef array)
    {
        var rootSymbols = Enumerable.Range(0, checked((int)shape.ElementCount))
            .Select(i => $"{baseSymbol}{Indices(shape.GetIndices(i))}").ToArray();
        var columns = new List<SymbolicColumn>();
        if (array.ElementType == IgwDataType.Struct)
            AddStructColumns(columns, "", shape, rootSymbols,
                array.Struct ?? throw new InvalidDataException("结构体数组缺少布局"));
        else
            columns.Add(NewColumn("", shape, array.ElementType, array.StringLength,
                rootSymbols, Enumerable.Range(0, rootSymbols.Length).ToArray()));
        return new SymbolicPlan(shape, columns);
    }

    private static void AddStructColumns(List<SymbolicColumn> columns, string path,
        ArrayShape outerShape, IReadOnlyList<string> outerSymbols, StructDef schema)
    {
        foreach (var field in schema.Fields)
        {
            string fieldPath = path.Length == 0 ? field.Name : $"{path}.{field.Name}";
            if (field.Array is { } memberArray)
            {
                var memberShape = ArrayShape.Create(
                    outerShape.Dimensions.Concat(memberArray.Dimensions).ToArray(),
                    outerShape.LowerBounds.Concat(memberArray.LowerBounds).ToArray());
                int innerCount = checked((int)memberArray.Shape.ElementCount);
                var symbols = new string[checked(outerSymbols.Count * innerCount)];
                var owners = new int[symbols.Length];
                for (int outer = 0; outer < outerSymbols.Count; outer++)
                    for (int inner = 0; inner < innerCount; inner++)
                    {
                        int offset = checked(outer * innerCount + inner);
                        symbols[offset] = $"{outerSymbols[outer]}.{field.Name}{Indices(memberArray.Shape.GetIndices(inner))}";
                        owners[offset] = outer;
                    }
                if (memberArray.ElementType == IgwDataType.Struct)
                    AddStructColumns(columns, fieldPath, memberShape, symbols,
                        memberArray.Struct ?? throw new InvalidDataException($"{fieldPath} 缺少结构体布局"));
                else
                    columns.Add(NewColumn(fieldPath, memberShape, memberArray.ElementType,
                        memberArray.StringLength, symbols, owners));
            }
            else if (field.Struct is { } nested)
            {
                AddStructColumns(columns, fieldPath, outerShape,
                    outerSymbols.Select(symbol => $"{symbol}.{field.Name}").ToArray(), nested);
            }
            else
            {
                columns.Add(NewColumn(fieldPath, outerShape, field.DataType, field.StringLength,
                    outerSymbols.Select(symbol => $"{symbol}.{field.Name}").ToArray(),
                    Enumerable.Range(0, outerSymbols.Count).ToArray()));
            }
        }
    }

    private static SymbolicColumn NewColumn(string path, ArrayShape shape, IgwDataType type,
        int? stringLength, IReadOnlyList<string> symbols, IReadOnlyList<int> owners)
    {
        var items = new List<SymbolicItem>(symbols.Count);
        for (int i = 0; i < symbols.Count; i++) items.Add(new SymbolicItem(symbols[i], owners[i]));
        return new SymbolicColumn(path, shape, type, stringLength, items);
    }

    private static ArrayRuntimeValue Materialize(SymbolicPlan plan, ArrayDef array, PlcTextEncoding? encoding)
    {
        var values = plan.Columns.ToDictionary(static column => column.Path,
            static column => MaterializeColumn(column), StringComparer.OrdinalIgnoreCase);
        if (array.ElementType != IgwDataType.Struct) return values[""];
        return BuildStructValue(plan.Shape, array.Struct!, "", values);
    }

    private static StructArrayValue BuildStructValue(ArrayShape shape, StructDef schema, string path,
        IReadOnlyDictionary<string, ArrayRuntimeValue> columns)
    {
        var members = new Dictionary<string, ArrayRuntimeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema.Fields)
        {
            string fieldPath = path.Length == 0 ? field.Name : $"{path}.{field.Name}";
            if (field.Array is { ElementType: IgwDataType.Struct } memberArray)
            {
                var memberShape = ArrayShape.Create(
                    shape.Dimensions.Concat(memberArray.Dimensions).ToArray(),
                    shape.LowerBounds.Concat(memberArray.LowerBounds).ToArray());
                members[field.Name] = BuildStructValue(memberShape, memberArray.Struct!, fieldPath, columns);
            }
            else if (field.Struct is { } nested)
                members[field.Name] = BuildStructValue(shape, nested, fieldPath, columns);
            else members[field.Name] = columns[fieldPath];
        }

        var quality = new StatusCode[checked((int)shape.ElementCount)];
        Array.Fill(quality, StatusCode.Good);
        foreach (var member in members.Values)
        {
            int innerCount = checked(member.Count / quality.Length);
            for (int outer = 0; outer < quality.Length; outer++)
            {
                for (int inner = 0; inner < innerCount; inner++)
                    quality[outer] = Combine(quality[outer], member.ElementQuality.Span[outer * innerCount + inner]);
            }
        }
        return StructArrayValue.Create(shape, schema, members, quality);
    }

    private static ArrayRuntimeValue MaterializeColumn(SymbolicColumn column)
    {
        var q = column.Items.Select(static item => item.Status).ToArray();
        int count = column.Items.Count;
        return column.DataType switch
        {
            IgwDataType.Bool => PrimitiveArrayValue<bool>.Create(column.Shape, Values(count, i => Convert.ToBoolean(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Byte => PrimitiveArrayValue<byte>.Create(column.Shape, Values(count, i => Convert.ToByte(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Char => PrimitiveArrayValue<sbyte>.Create(column.Shape, Values(count, i => Convert.ToSByte(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(column.Shape, Values(count, i => Convert.ToInt16(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.UInt16 or IgwDataType.Bcd => PrimitiveArrayValue<ushort>.Create(column.Shape, Values(count, i => Convert.ToUInt16(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Int32 => PrimitiveArrayValue<int>.Create(column.Shape, Values(count, i => Convert.ToInt32(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.UInt32 or IgwDataType.LBcd => PrimitiveArrayValue<uint>.Create(column.Shape, Values(count, i => Convert.ToUInt32(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Int64 => PrimitiveArrayValue<long>.Create(column.Shape, Values(count, i => Convert.ToInt64(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.UInt64 => PrimitiveArrayValue<ulong>.Create(column.Shape, Values(count, i => Convert.ToUInt64(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(column.Shape, Values(count, i => Convert.ToSingle(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.Double => PrimitiveArrayValue<double>.Create(column.Shape, Values(count, i => Convert.ToDouble(column.Items[i].Value, CultureInfo.InvariantCulture)), q, column.DataType),
            IgwDataType.String or IgwDataType.WString => PrimitiveArrayValue<string>.Create(column.Shape, Values(count, i => Convert.ToString(column.Items[i].Value, CultureInfo.InvariantCulture) ?? ""), q, column.DataType),
            IgwDataType.ByteArray => PrimitiveArrayValue<byte[]>.Create(column.Shape, Values(count, i => column.Items[i].Value as byte[] ?? []), q, column.DataType),
            _ => throw new NotSupportedException($"S7 Plus 数组元素类型尚不支持:{column.DataType}"),
        };
    }

    private static ArrayRuntimeValue ResolveMember(ArrayRuntimeValue root, string path)
    {
        if (path.Length == 0) return root;
        var current = root;
        foreach (string part in path.Split('.'))
            current = current is StructArrayValue structured && structured.Members.TryGetValue(part, out var member)
                ? member : throw new InvalidDataException($"写入值缺少结构体成员:{path}");
        return current;
    }

    private static object? Normalize(object? value, IgwDataType type) => type switch
    {
        IgwDataType.Bcd => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
        IgwDataType.LBcd => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    private static StatusCode QualityFor(SiemensPlusResultCode code) => code switch
    {
        SiemensPlusResultCode.Ok => StatusCode.Good,
        SiemensPlusResultCode.Timeout => StatusCode.BadTimeout,
        SiemensPlusResultCode.TransportFailure => StatusCode.BadCommFailure,
        SiemensPlusResultCode.ProtocolFailure => StatusCode.BadDecodeError,
        // 符号在 PLC 程序中不存在：接入坏点隔离(连续 3 次后隔离、30 秒复探)，不影响其它点
        SiemensPlusResultCode.SymbolNotFound => StatusCode.BadNodeIdUnknown,
        _ => StatusCode.BadDeviceFailure,
    };

    private static StatusCode Combine(StatusCode left, StatusCode right)
    {
        if (left.IsBad) return left;
        if (right.IsBad) return right;
        return left.Class == QualityClass.Uncertain || right.Class == QualityClass.Uncertain
            ? StatusCode.Uncertain : StatusCode.Good;
    }

    private static string Indices(IReadOnlyList<int> indices) => $"[{string.Join(',', indices)}]";
    private static T[] Values<T>(int count, Func<int, T> factory)
    {
        var values = new T[count];
        for (int i = 0; i < count; i++) values[i] = factory(i);
        return values;
    }

    private sealed record SymbolicPlan(ArrayShape Shape, IReadOnlyList<SymbolicColumn> Columns);
    private sealed record SymbolicColumn(string Path, ArrayShape Shape, IgwDataType DataType,
        int? StringLength, IReadOnlyList<SymbolicItem> Items);
    private sealed class SymbolicItem(string symbol, int outerOffset)
    {
        public string Symbol { get; } = symbol;
        public int OuterOffset { get; } = outerOffset;
        public object? Value { get; set; }
        public StatusCode Status { get; set; } = StatusCode.Bad;
    }
}
