using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 已经在用的 S7 Plus 会话被 PLC 掐断时，本批读取就地重连重读一次。
///
/// 现场 S7-1500 上，只做轮询、不带订阅的 S7 Plus 会话在建立约 60 秒后就会被 PLC 断开
/// (GRAPH 设备半天断了近千次)。原先每断一次，整批点位就被标成 BadCommFailure、设备记一次
/// 「连接失效」，外部 /Tag 读取正好碰上也整批失败，要等调用方自己重发。读取是幂等的，
/// 旧会话失效后换新会话再读一次就能拿到值。新建会话上的失败不重试(那是真连不上，
/// 重试只会拖慢)；重试仍失败按原有流程报坏。写入不在此列：写可能已经生效，不能盲目重发。
/// </summary>
public sealed class SiemensPlusStaleSessionRetryTests
{
    [Fact]
    public async Task A_session_dropped_by_the_plc_is_reopened_and_the_batch_read_again()
    {
        var plus = new DroppingPlus();
        await using var driver = Driver(plus);
        await driver.ReadAsync(Tags(), default);          // 建立会话

        plus.FailNextReads = 1;                             // PLC 掐断了已建立的会话
        var samples = await driver.ReadAsync(Tags(), default);

        Assert.All(samples, sample => Assert.True(sample.Value.IsGood));
        Assert.Equal(2, plus.Connects);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task A_failure_on_a_freshly_opened_session_is_not_retried()
    {
        var plus = new DroppingPlus { FailNextReads = 1 };
        await using var driver = Driver(plus);

        // 整批都坏且通道离线：照旧抛给引擎走连接失效流程
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(Tags(), default));
        Assert.Equal(1, plus.Connects);
        Assert.Equal(1, plus.Reads);
    }

    [Fact]
    public async Task The_retry_happens_once_and_a_second_failure_is_reported()
    {
        var plus = new DroppingPlus();
        await using var driver = Driver(plus);
        await driver.ReadAsync(Tags(), default);

        plus.FailNextReads = 2;
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(Tags(), default));
        Assert.Equal(2, plus.Connects);
        Assert.Equal(3, plus.Reads);
    }

    private static SiemensHybridDeviceDriver Driver(DroppingPlus plus) =>
        new("plc", 102, TimeSpan.FromSeconds(1), true, new NoClassic(), plus);

    private static TagBinding[] Tags() =>
    [
        new() { TagId = "Line.Plc.A", Def = new TagDef { Address = "DB1.A", DataType = IgwDataType.Int16 }, Address = new(0, 0, 2) },
        new() { TagId = "Line.Plc.B", Def = new TagDef { Address = "DB1.B", DataType = IgwDataType.Int16 }, Address = new(0, 0, 2) },
    ];

    private sealed class DroppingPlus : ISiemensPlusTransport
    {
        public int Connects, Reads, FailNextReads;
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(string host, ushort port, TimeSpan timeout, bool legacy, CancellationToken ct)
        {
            Connects++;
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SiemensPlusNativeBrowseTag>> BrowseAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SiemensPlusNativeBrowseTag>>([]);
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct)
        {
            Reads++;
            if (FailNextReads > 0)
            {
                FailNextReads--;
                IsConnected = false;
                throw new SiemensPlusException(SiemensPlusResultCode.TransportFailure);
            }
            return Task.FromResult<IReadOnlyList<TagSample>>(tags.Select(tag =>
                new TagSample(tag.TagId, DataValue.GoodAt((short)1, DateTime.UtcNow))).ToArray());
        }
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoClassic : ISiemensClassicTransport
    {
        public bool IsConnected => false;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagSample>>([]);
        public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WriteResult>>([]);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
