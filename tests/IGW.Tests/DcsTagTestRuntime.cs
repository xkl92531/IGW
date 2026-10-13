using IGW.Abstractions;
using IGW.Northbound.RestApi;

namespace IGW.Tests;

internal sealed class DcsTagTestRuntime : IDcsTagRuntime
{
    private readonly Dictionary<string, TagDef> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public IgwProject Project { get; private set; } = new();
    public bool Reloading { get; set; }
    public List<string> Touched { get; } = [];
    public List<(string TagId, object Value)> Writes { get; } = [];
    public List<IgwProject> Reloads { get; } = [];
    public Func<string, object, int, StatusCode>? WriteHandler { get; set; }
    public Func<string, object, int, CancellationToken, Task<StatusCode>>? WriteAsyncHandler { get; set; }
    public int ConfirmOnWriteAttempt { get; set; } = 1;
    public Exception? ReloadFailure { get; set; }

    public bool TryGet(string tagId, out DataValue value) => _values.TryGetValue(tagId, out value);
    public bool TryGetDef(string tagId, out TagDef definition) => _definitions.TryGetValue(tagId, out definition!);
    public void Touch(string tagId) => Touched.Add(tagId);
    public Dictionary<string, ConnectionState> DeviceStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConnectionState? DeviceStateOf(string tagId) => DeviceStates.TryGetValue(tagId, out var state) ? state : null;
    public List<string> Refreshed { get; } = [];
    public Func<string, DataValue>? RefreshValue { get; set; }
    /// <summary>最近一次补读是否要求激活轮询。</summary>
    public bool LastRefreshActivate { get; private set; }
    public Task RefreshIdleAsync(IReadOnlyCollection<string> tagIds, CancellationToken cancellationToken, bool activate = false)
    {
        LastRefreshActivate = activate;
        foreach (string tagId in tagIds)
        {
            Refreshed.Add(tagId);
            if (RefreshValue is not null) _values[tagId] = RefreshValue(tagId);
        }
        return Task.CompletedTask;
    }

    public async Task<StatusCode> WriteAsync(string tagId, object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int attempt;
        lock (Writes)
        {
            Writes.Add((tagId, value));
            attempt = Writes.Count(write => write.TagId.Equals(tagId, StringComparison.OrdinalIgnoreCase));
        }
        StatusCode status = WriteAsyncHandler is not null
            ? await WriteAsyncHandler(tagId, value, attempt, cancellationToken)
            : WriteHandler?.Invoke(tagId, value, attempt) ?? StatusCode.Good;
        if (status.IsGood && attempt >= ConfirmOnWriteAttempt)
            _values[tagId] = DataValue.GoodAt(value, DateTime.UtcNow);
        return status;
    }

    public Task<ArrayWriteResult> WriteArrayAsync(
        string tagId, ArrayRuntimeValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add((tagId, value));
        _values[tagId] = DataValue.GoodAt(value, DateTime.UtcNow);
        return Task.FromResult(new ArrayWriteResult(StatusCode.Good, value.Count, []));
    }

    public Task ReloadAsync(IgwProject project)
    {
        Reloads.Add(project);
        if (ReloadFailure is not null) throw ReloadFailure;
        Project = project;
        return Task.CompletedTask;
    }

    public DcsTagTestRuntime AddTag(string tagId, TagDef definition, DataValue value)
    {
        _definitions[tagId] = definition;
        _values[tagId] = value;
        return this;
    }

    public static DcsTagTestRuntime WithWritableTag(string tagId, IgwDataType dataType)
    {
        object initial = dataType switch
        {
            IgwDataType.Bool => false,
            IgwDataType.Byte => (byte)0,
            IgwDataType.Int16 => (short)0,
            IgwDataType.UInt16 => (ushort)0,
            IgwDataType.Int32 => 0,
            IgwDataType.UInt32 => 0u,
            IgwDataType.Float => 0f,
            IgwDataType.Double => 0d,
            _ => "",
        };
        return new DcsTagTestRuntime().AddTag(
            tagId,
            new TagDef { DataType = dataType, Access = TagAccess.ReadWrite, ScanRateMs = 1 },
            DataValue.GoodAt(initial, DateTime.UtcNow));
    }

    public static DcsTagTestRuntime WithGoodTag(string tagId, object value) =>
        new DcsTagTestRuntime().AddTag(
            tagId,
            new TagDef { DataType = TypeOf(value), Access = TagAccess.ReadWrite },
            DataValue.GoodAt(value, DateTime.UtcNow));

    public static DcsTagTestRuntime WithGoodTags(params (string TagId, object Value)[] values)
    {
        var runtime = new DcsTagTestRuntime();
        foreach (var pair in values) runtime.AddTag(
            pair.TagId,
            new TagDef { DataType = TypeOf(pair.Value), Access = TagAccess.ReadWrite },
            DataValue.GoodAt(pair.Value, DateTime.UtcNow));
        return runtime;
    }

    public static DcsTagTestRuntime WithBadTag(string tagId, StatusCode quality) =>
        new DcsTagTestRuntime().AddTag(
            tagId,
            new TagDef { DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
            DataValue.BadAt(quality, DateTime.UtcNow));

    public DcsTagTestRuntime WithProject(IgwProject project)
    {
        Project = project;
        return this;
    }

    private static IgwDataType TypeOf(object value) => value switch
    {
        bool => IgwDataType.Bool,
        byte => IgwDataType.Byte,
        short => IgwDataType.Int16,
        ushort => IgwDataType.UInt16,
        int => IgwDataType.Int32,
        uint => IgwDataType.UInt32,
        long => IgwDataType.Int64,
        ulong => IgwDataType.UInt64,
        float => IgwDataType.Float,
        double => IgwDataType.Double,
        byte[] => IgwDataType.ByteArray,
        _ => IgwDataType.String,
    };
}
