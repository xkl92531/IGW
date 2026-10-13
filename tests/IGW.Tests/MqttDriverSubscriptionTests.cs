using System.Net;
using System.Net.Sockets;
using IGW.Abstractions;
using IGW.Drivers.Mqtt;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Xunit;

namespace IGW.Tests;

public sealed class MqttDriverSubscriptionTests
{
    [Fact]
    public async Task Broker_message_immediately_invokes_native_subscription_callback()
    {
        await using var fixture = await Fixture.ConnectAsync();
        var received = new TaskCompletionSource<IReadOnlyList<TagSample>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Assert.IsAssignableFrom<IDriverSubscriptionProvider>(fixture.Driver);
        await using var lease = await provider.SubscribeAsync(
            [fixture.Binding("plant/temp")], samples => received.TrySetResult(samples), default);

        await fixture.PublishAsync("plant/temp", "42");

        var sample = Assert.Single(await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(42, Assert.IsType<int>(sample.Value.Value));
        Assert.Equal(StatusCode.Good, sample.Value.Quality);
    }

    [Fact]
    public async Task Burst_coalesces_to_final_value_and_disposing_one_lease_keeps_shared_connection()
    {
        await using var fixture = await Fixture.ConnectAsync();
        var provider = Assert.IsAssignableFrom<IDriverSubscriptionProvider>(fixture.Driver);
        int firstCalls = 0;
        var final = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = fixture.Binding("plant/burst");
        await using var first = await provider.SubscribeAsync(
            [binding], _ => Interlocked.Increment(ref firstCalls), default);
        await using var second = await provider.SubscribeAsync([binding], samples =>
        {
            int value = Convert.ToInt32(samples.Single().Value.Value);
            if (value == 100) final.TrySetResult(value);
        }, default);

        await first.DisposeAsync();
        int callsAfterDispose = Volatile.Read(ref firstCalls);
        for (int value = 1; value <= 100; value++)
            await fixture.PublishAsync("plant/burst", value.ToString());

        Assert.Equal(100, await final.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await Task.Delay(100);
        Assert.Equal(callsAfterDispose, Volatile.Read(ref firstCalls));
        Assert.Equal(ConnectionState.Connected, fixture.Driver.State);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MqttServer _server;
        private readonly IMqttClient _publisher;

        private Fixture(MqttServer server, IMqttClient publisher, MqttDeviceDriver driver)
        {
            _server = server;
            _publisher = publisher;
            Driver = driver;
        }

        public MqttDeviceDriver Driver { get; }

        public static async Task<Fixture> ConnectAsync()
        {
            int port = ReservePort();
            var factory = new MqttFactory();
            var server = factory.CreateMqttServer(new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port)
                .Build());
            await server.StartAsync();

            var driver = new MqttDeviceDriver("127.0.0.1", port, "", "", "igw-driver-test");
            await driver.ConnectAsync(default);
            var publisher = factory.CreateMqttClient();
            await publisher.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("igw-publisher-test")
                .Build(), default);
            return new Fixture(server, publisher, driver);
        }

        public TagBinding Binding(string topic) => new()
        {
            TagId = "Line.Mqtt." + topic.Replace('/', '.'),
            Def = new TagDef { Name = topic, Address = topic, DataType = IgwDataType.Int32 },
            Address = new TagAddress(0, 0, 1),
        };

        public Task PublishAsync(string topic, string payload) =>
            _publisher.PublishStringAsync(topic, payload);

        public async ValueTask DisposeAsync()
        {
            try { await Driver.DisposeAsync(); } catch { }
            try { await _publisher.DisconnectAsync(); } catch { }
            _publisher.Dispose();
            try { await _server.StopAsync(); } catch { }
            _server.Dispose();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
