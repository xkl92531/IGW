using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using IGW.Abstractions;
using IGW.Drivers.EtherNetIP;
using IGW.Drivers.IoTClient;
using IGW.Drivers.Mitsubishi;
using IGW.Drivers.Mqtt;
using IGW.Drivers.Simulator;
using IoTClient;
using IoTClient.Enums;
using IoTClient.Interfaces;
using libplctag;
using MQTTnet;
using MQTTnet.Server;
using Xunit;

namespace IGW.Tests;

public sealed class DriverProtocolTimingTests
{
    [Fact]
    public async Task Opcua_exposes_physical_window_to_collection_planner_for_prompt_publication()
    {
        await using var driver = new IGW.Drivers.OpcUaClient.OpcUaClientDeviceDriver(
            "opc.tcp://127.0.0.1:4840", 100, true, "", "", "None");
        Assert.False(driver.Capabilities.PartitionsReadRequests);
        Assert.False(DriverCapabilitySnapshot.FromLimits(new DriverLimits()).PartitionsReadRequests);
    }

    [Fact]
    public async Task Samples_are_bounded_immutable_and_preserve_sync_async_failure_and_cancellation()
    {
        using var timing = ProtocolOperationTiming.Begin();
        var descriptor = new ProtocolRequestDescriptor(new string('R', 1000), new string('S', 1000), 1, 2, 0);
        Assert.Throws<IOException>(() => ProtocolOperationTiming.Measure<int>(() => throw new IOException(), descriptor));
        Assert.Throws<OperationCanceledException>(() => ProtocolOperationTiming.Measure<int>(() => throw new OperationCanceledException(), descriptor));
        var before = timing.Snapshot();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            ProtocolOperationTiming.MeasureAsync(() => Task.FromResult(1), descriptor))));
        var snapshot = timing.Snapshot();
        Assert.Equal(102, snapshot.Requests);
        Assert.Equal(64, snapshot.Samples!.Count);
        Assert.Equal(38, snapshot.OmittedSamples);
        Assert.Equal(1, snapshot.FailedRequests);
        Assert.Equal(1, snapshot.CancelledRequests);
        Assert.Equal(2, before.Samples!.Count);
        Assert.All(snapshot.Samples, sample =>
        {
            Assert.True(sample.Descriptor!.Operation.Length <= 48);
            Assert.True(sample.Descriptor.Scope.Length <= 160);
        });
        Assert.Throws<NotSupportedException>(() => ((IList<ProtocolRequestSample>)snapshot.Samples).Clear());
    }

    [Fact]
    public async Task Eip_counts_each_native_operation_and_limits_workers_to_advertised_capacity()
    {
        int inFlight = 0, maximum = 0;
        var gates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Read(CancellationToken ct)
        {
            int current = Interlocked.Increment(ref inFlight);
            int seen;
            do { seen = maximum; } while (current > seen && Interlocked.CompareExchange(ref maximum, current, seen) != seen);
            if (current == 4) gates.TrySetResult();
            await gates.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref inFlight);
        }
        // 读会等到 4 个工作线程同时在读才放行；机器繁忙时凑齐可能超过 100ms，超时放宽，只验证并发上限和计数
        await using var driver = Eip(() => new FakeNativeTag { Read = Read }, timeoutMs: 3000);
        var tags = Enumerable.Range(0, 20).Select(i => Binding("T" + i)).ToArray();
        using (var timing = ProtocolOperationTiming.Begin())
        {
            Assert.All(await driver.ReadAsync(tags, default), sample => Assert.True(sample.Value.IsGood));
            Assert.Equal(40, timing.Snapshot().Requests); // initialize + read, per tag
            Assert.Equal(20, timing.Snapshot().Samples!.Count(s => s.Descriptor!.Operation == "Initialize"));
        }
        using (var timing = ProtocolOperationTiming.Begin())
        {
            await driver.ReadAsync(tags, default);
            Assert.Equal(20, timing.Snapshot().Requests); // cached tag initialization is local
        }
        Assert.Equal(driver.Capabilities.MaxInFlight, maximum);
        Assert.False(driver.Capabilities.SupportsBatchRead);
        Assert.True(driver.Capabilities.PartitionsReadRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Generic_iot_preserves_native_batch_and_counts_inner_socket_exchanges(bool fins, bool separateAreas)
    {
        await using var server = new PlcServer(fins);
        server.Start();
        await using var driver = new IoTPlcDeviceDriver(fins ? IoTOmronFinsDriverFactory.Id : IoTMitsubishiDriverFactory.Id,
            () => fins ? new TimedFinsClient("127.0.0.1", server.Port, 2000, EndianFormat.ABCD)
                : new TimedMitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", server.Port, 2000), readRetries: 0);
        await driver.ConnectAsync(default);
        int before = server.Requests;
        using var timing = ProtocolOperationTiming.Begin();
        var samples = await driver.ReadAsync([Binding("D100"), Binding(separateAreas ? "W0" : "D101")], default);
        Assert.All(samples, sample => Assert.True(sample.Value.IsGood));
        Assert.Equal(server.Requests - before, timing.Snapshot().PhysicalRequests);
        Assert.Equal(separateAreas ? 2 : 1, timing.Snapshot().Requests);
        Assert.True(driver.Capabilities.SupportsBatchRead);
        Assert.True(driver.Capabilities.PartitionsReadRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Native_batch_stops_at_each_socket_boundary_without_fallback_or_reconnect(bool fins, bool cancel)
    {
        await using var server = new PlcServer(fins);
        using var cancellation = new CancellationTokenSource();
        bool priority = false;
        server.OnRequest = number =>
        {
            if (number != 1) return;
            if (cancel) cancellation.Cancel();
            else Volatile.Write(ref priority, true);
        };
        server.Start();
        IIoTClient native = fins ? new TimedFinsClient("127.0.0.1", server.Port, 2000, EndianFormat.ABCD)
            : new TimedMitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", server.Port, 2000);
        int creations = 0;
        await using var driver = new IoTPlcDeviceDriver(fins ? IoTOmronFinsDriverFactory.Id : IoTMitsubishiDriverFactory.Id,
            () => { creations++; return native; }, readRetries: 2);
        await driver.ConnectAsync(default);
        driver.SetPriorityWorkProbe(() => Volatile.Read(ref priority));
        var first = Binding("D100");
        var second = Binding("W0");
        using var timing = ProtocolOperationTiming.Begin();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReadAsync([first, second], cancellation.Token));
        else
            await Assert.ThrowsAsync<DriverReadYieldException>(() => driver.ReadAsync([first, second], cancellation.Token));
        Assert.Equal(1, server.Requests);
        Assert.Equal(1, timing.Snapshot().PhysicalRequests);
        Assert.Equal(0, timing.Snapshot().FailedRequests);
        Assert.Equal(1, creations);
        Assert.True(native.Connected);
        Assert.Equal(ConnectionState.Connected, driver.State);
        Assert.Equal("", driver.LastReadError);

        // The pending write must complete on the original socket before the remaining range.
        Assert.True(Assert.Single(await driver.WriteAsync([new()
            { TagId = first.TagId, Def = first.Def, Address = first.Address, Value = (short)9 }], default)).Status.IsGood);
        Assert.Equal(2, server.Requests);
        Volatile.Write(ref priority, false);
        Assert.True(Assert.Single(await driver.ReadAsync([second], default)).Value.IsGood);
        Assert.Equal(new[] { "Read", "Write", "Read" }, timing.Snapshot().Samples!.Select(s => s.Descriptor!.Operation));
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task Unobservable_scalar_write_is_unknown_only_when_client_io_is_invoked()
    {
        var client = DispatchProxy.Create<IIoTClient, UnsupportedBatchClient>();
        var fake = (UnsupportedBatchClient)(object)client;
        await using var driver = new IoTPlcDeviceDriver("custom", () => client);
        await driver.ConnectAsync(default);
        var tag = Binding("D100");
        using (var timing = ProtocolOperationTiming.Begin())
        {
            Assert.True(Assert.Single(await driver.WriteAsync([new()
            { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = (short)9 }], default)).Status.IsGood);
            Assert.Null(timing.Snapshot().PhysicalRequests);
            Assert.Equal(1, timing.Snapshot().OpaqueOperations);
            Assert.Equal(1, fake.Writes);
        }
        using (var timing = ProtocolOperationTiming.Begin())
        {
            await driver.WriteAsync([], default);
            Assert.False(Assert.Single(await driver.WriteAsync([new()
            { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = "not a short" }], default)).Status.IsGood);
            Assert.Equal(0, timing.Snapshot().PhysicalRequests);
            Assert.Equal(1, fake.Writes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstrumented_register_array_write_is_unknown_but_rejected_array_has_zero_io(bool fins)
    {
        await using var server = new PlcServer(fins);
        server.Start();
        await using var driver = new IoTPlcDeviceDriver(fins ? IoTOmronFinsDriverFactory.Id : IoTMitsubishiDriverFactory.Id,
            () => fins ? new IoTClient.Clients.PLC.OmronFinsClient("127.0.0.1", server.Port, 2000, EndianFormat.ABCD)
                : new IoTClient.Clients.PLC.MitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", server.Port, 2000));
        await driver.ConnectAsync(default);
        var tag = new TagBinding
        {
            TagId = "Line.Plc.Array", Address = new(0, 100, 2),
            Def = new TagDef { Address = "D100", DataType = IgwDataType.Int16, Array = new ArrayDef
                { ElementType = IgwDataType.Int16, Dimensions = [2], LowerBounds = [0], ElementStrideBytes = 2 } }
        };
        var value = PrimitiveArrayValue<short>.Create(ArrayShape.Create([2], [0]), [7, 8]);
        using (var timing = ProtocolOperationTiming.Begin())
        {
            Assert.True((await driver.WriteArrayAsync(new(tag, new ArraySlice([0], [2]), value), default)).Status.IsGood);
            Assert.Equal(1, server.Requests);
            Assert.Null(timing.Snapshot().PhysicalRequests);
            Assert.Equal(1, timing.Snapshot().OpaqueOperations);
        }
        using (var timing = ProtocolOperationTiming.Begin())
        {
            Assert.False((await driver.WriteArrayAsync(new(Binding("D100"), new ArraySlice([0], [2]), value), default)).Status.IsGood);
            Assert.Equal(1, server.Requests);
            Assert.Equal(0, timing.Snapshot().PhysicalRequests);
        }
    }

    [Theory]
    [InlineData(false, 481, 960)]
    [InlineData(true, 961, 480)]
    public async Task Mc_physical_spans_respect_word_and_bit_limits(bool bit, int count, int maxPayload)
    {
        await using var server = new PlcServer(false);
        server.Start();
        await using var driver = new McTcpDeviceDriver("127.0.0.1", server.Port, 2000);
        await driver.ConnectAsync(default);
        var tags = Enumerable.Range(0, count).Select(i => Binding((bit ? "M" : "D") + i)).ToArray();
        using var timing = ProtocolOperationTiming.Begin();
        Assert.Equal(count, (await driver.ReadAsync(tags, default)).Count);
        Assert.Equal(2, timing.Snapshot().Requests);
        Assert.All(timing.Snapshot().Samples!, s => Assert.InRange(s.Descriptor!.PayloadBytes, 1, maxPayload));
    }

    [Fact]
    public async Task Eip_lane_wait_is_separate_from_native_request_duration()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        await using var driver = Eip(() => new FakeNativeTag { Read = async ct =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        } });
        var tag = Binding("Value");
        Task<IReadOnlyList<TagSample>> read = driver.ReadAsync([tag], default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var timing = ProtocolOperationTiming.Begin();
        Task<IReadOnlyList<WriteResult>> write = driver.WriteAsync([new()
        { TagId = tag.TagId, Def = tag.Def, Address = tag.Address, Value = (short)9 }], default);
        Assert.False(write.IsCompleted);
        await Task.Delay(20);
        release.TrySetResult();
        await read;
        Assert.True(Assert.Single(await write).Status.IsGood);
        Assert.Equal(2, timing.Snapshot().Requests); // ReadForWrite + Write, no outer lane count
        Assert.True(timing.Snapshot().QueueMs >= 10);
    }

    [Theory]
    [InlineData(101, 1, 0)]
    [InlineData(109, 1, 16)]
    [InlineData(110, 2, 0)]
    public async Task Mc_read_merges_only_safe_same_area_gaps(int second, int requests, int gapBytes)
    {
        await using var server = new PlcServer(false);
        server.Start();
        await using var driver = new McTcpDeviceDriver("127.0.0.1", server.Port, 2000);
        await driver.ConnectAsync(default);
        using var timing = ProtocolOperationTiming.Begin();
        var samples = await driver.ReadAsync([Binding("D100"), Binding("D" + second)], default);
        Assert.All(samples, sample => Assert.True(sample.Value.IsGood));
        Assert.Equal(requests, server.Requests);
        Assert.Equal(requests, timing.Snapshot().PhysicalRequests);
        Assert.Equal(gapBytes, timing.Snapshot().Samples![0].Descriptor!.GapBytes);
    }

    [Fact]
    public async Task Simulator_and_mqtt_cache_reads_have_no_polling_requests()
    {
        await using var simulator = new SimulatorDeviceDriver();
        using (var timing = ProtocolOperationTiming.Begin())
        {
            await simulator.ReadAsync([Binding("R1")], default);
            Assert.Equal(0, timing.Snapshot().PhysicalRequests);
            Assert.True(simulator.Capabilities.PartitionsReadRequests);
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var factory = new MqttFactory();
        using var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
        await broker.StartAsync();
        try
        {
            await using var mqtt = new MqttDeviceDriver("127.0.0.1", port, "", "", "timing-test");
            await mqtt.ConnectAsync(default);
            using var timing = ProtocolOperationTiming.Begin();
            await mqtt.ReadAsync([Binding("plant/value")], default);
            Assert.Equal(0, timing.Snapshot().PhysicalRequests);
            Assert.True(mqtt.Capabilities.PartitionsReadRequests);
        }
        finally { await broker.StopAsync(); }
    }

    [Fact]
    public async Task Unimplemented_native_batch_is_cached_and_unobservable_client_is_explicitly_unknown()
    {
        var client = DispatchProxy.Create<IIoTClient, UnsupportedBatchClient>();
        var fake = (UnsupportedBatchClient)(object)client;
        await using var driver = new IoTPlcDeviceDriver("custom", () => client, readRetries: 0);
        await driver.ConnectAsync(default);
        using var timing = ProtocolOperationTiming.Begin();
        for (int i = 0; i < 2; i++)
            Assert.All(await driver.ReadAsync([Binding("D100"), Binding("D101")], default), s => Assert.True(s.Value.IsGood));
        Assert.Equal(1, fake.Batches);
        Assert.Equal(4, fake.Reads);
        Assert.False(driver.Capabilities.SupportsBatchRead);
        Assert.Null(timing.Snapshot().PhysicalRequests);
        Assert.Equal(0, timing.Snapshot().Requests);
        Assert.Equal(2, timing.Snapshot().OpaqueOperations);
    }

    internal static EtherNetIpDeviceDriver Eip(Func<EtherNetIpDeviceDriver.INativeTag> factory, int timeoutMs = 100) =>
        new("Omron.Cip", "127.0.0.1", null, PlcType.Omron, timeoutMs, 128,
            _ => Task.FromResult(true), _ => factory());

    internal static TagBinding Binding(string address) => new()
    {
        TagId = "Line.Plc." + address,
        Def = new TagDef { Name = address, Address = address, DataType = IgwDataType.Int16, Access = TagAccess.ReadWrite },
        Address = new(0, 0, 1)
    };

    internal sealed class FakeNativeTag : EtherNetIpDeviceDriver.INativeTag
    {
        public Func<CancellationToken, Task> Read { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Write { get; init; } = _ => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ReadAsync(CancellationToken ct) => Read(ct);
        public Task WriteAsync(CancellationToken ct) => Write(ct);
        public byte[] GetBuffer() => [7, 0];
        public void SetBuffer(byte[] bytes) { }
        /// <summary>默认解出一个合法值；测解码失败时注入一个会抛的实现。</summary>
        public Func<IgwDataType, object> DecodeValue { get; init; } = _ => (short)7;
        public bool Disposed { get; private set; }
        public object Decode(IgwDataType type) => DecodeValue(type);
        public void Encode(object value, IgwDataType type) { }
        public void Dispose() => Disposed = true;
    }

    public class UnsupportedBatchClient : DispatchProxy
    {
        public int Batches, Reads, Writes;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Connected" => true,
            "Open" or "Close" => new Result { IsSucceed = true },
            "BatchRead" => Batch(),
            "ReadInt16" => Read(),
            "Write" => Write(),
            _ => throw new NotSupportedException(method.Name)
        };
        private object Batch() { Batches++; throw new NotImplementedException(); }
        private object Read() { Reads++; return new Result<short> { IsSucceed = true, Value = 7 }; }
        private object Write() { Writes++; return new Result { IsSucceed = true }; }
    }

    // Responds to actual socket frames from the pinned IoTClient and IGW MC implementations.
    internal sealed class PlcServer(bool fins) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _run;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests { get; private set; }
        public Action<int>? OnRequest { get; set; }
        public void Start() { _listener.Start(); _run = Task.Run(RunAsync); }
        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    var head = new byte[fins ? 8 : 9];
                    await stream.ReadExactlyAsync(head, _stop.Token);
                    int length = fins ? BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4)) : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(7));
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    byte[] response;
                    if (fins && BinaryPrimitives.ReadInt32BigEndian(body) == 0)
                    {
                        response = new byte[24];
                        "FINS"u8.CopyTo(response);
                        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), 16);
                        response[11] = 1; response[19] = 1; response[23] = 2;
                    }
                    else
                    {
                        Requests++;
                        OnRequest?.Invoke(Requests);
                        if (fins)
                        {
                            int count = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(24));
                            response = new byte[30 + count * 2];
                            head.CopyTo(response, 0); body.AsSpan(0, 20).CopyTo(response.AsSpan(8));
                            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), response.Length - 8);
                        }
                        else
                        {
                            bool read = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)) == 0x0401;
                            bool bit = body[4] == 1;
                            int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10));
                            int bytes = read ? bit ? (count + 1) / 2 : count * 2 : 0;
                            response = new byte[11 + bytes];
                            response[0] = 0xD0; response[3] = 0xFF; response[4] = 0xFF; response[5] = 3;
                            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(7), checked((ushort)(2 + bytes)));
                        }
                    }
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            if (_run is not null) await _run;
            _stop.Dispose();
        }
    }
}
