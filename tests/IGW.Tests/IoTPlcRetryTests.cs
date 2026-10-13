using System.Reflection;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using IoTClient;
using IoTClient.Enums;
using IoTClient.Interfaces;
using Xunit;

namespace IGW.Tests;

public sealed class IoTPlcRetryTests
{
    [Fact]
    public async Task Transient_transport_timeout_reopens_session_and_returns_good_sample_before_device_offline()
    {
        int created = 0;
        IIoTClient Create()
        {
            int current = Interlocked.Increment(ref created);
            var client = DispatchProxy.Create<IIoTClient, FakeClientProxy>();
            ((FakeClientProxy)(object)client).FailBatchRead = current == 1;
            return client;
        }

        await using var driver = new IoTPlcDeviceDriver("Siemens.S7", Create, batchSize: 19, readRetries: 2);
        await driver.ConnectAsync(default);
        var def = new TagDef { Name = "Value", Address = "DB1.DBW0", DataType = IgwDataType.Int16 };

        var samples = await driver.ReadAsync([
            new TagBinding { TagId = "Line.Plc.DB.Value", Def = def, Address = new TagAddress(1, 0, 2) },
        ], default);

        Assert.Equal(2, created);
        Assert.True(Assert.Single(samples).Value.IsGood);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    public class FakeClientProxy : DispatchProxy
    {
        public bool FailBatchRead { get; set; }
        private bool _connected;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_Version" => "test",
                "get_Connected" => _connected,
                "get_WarningLog" => null,
                "set_WarningLog" => null,
                "Open" => Open(),
                "Close" => Close(),
                "BatchRead" => BatchRead((Dictionary<string, DataTypeEnum>)args![0]!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }

        private Result Open()
        {
            _connected = true;
            return new Result { IsSucceed = true };
        }

        private Result Close()
        {
            _connected = false;
            return new Result { IsSucceed = true };
        }

        private Result<Dictionary<string, object>> BatchRead(Dictionary<string, DataTypeEnum> addresses)
        {
            if (FailBatchRead)
            {
                _connected = false;
                return new Result<Dictionary<string, object>>
                {
                    IsSucceed = false,
                    Err = "连接超时",
                };
            }

            return new Result<Dictionary<string, object>>
            {
                IsSucceed = true,
                Value = addresses.Keys.ToDictionary(address => address, _ => (object)(short)7),
            };
        }
    }
}
