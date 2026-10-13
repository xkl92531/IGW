using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class ConnectionDiagnosticsApiTests
{
    [Fact]
    public void Maps_incident_to_stable_api_contract()
    {
        var first = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var incident = new ConnectionIncident(
            "LineA.PLC1", "LineA", "siemens-s7", ConnectionDiagnosticCategory.ConnectionTimeout,
            "连接超时", "连接或协议握手超时。", "检查网络。", "connect timeout",
            ConnectionDiagnosticScope.Transport, ConnectionState.Faulted,
            first, first.AddSeconds(2), null, 3, true, 5000);

        var dto = ConnectionDiagnosticsApi.ToDto(incident);

        Assert.Equal("ConnectionTimeout", dto.Code);
        Assert.Equal("Transport", dto.Scope);
        Assert.Equal("Faulted", dto.State);
        Assert.True(dto.Active);
        Assert.Equal(3, dto.OccurrenceCount);
        Assert.Equal(5000, dto.NextRetryMs);
    }

    [Fact]
    public void Maps_snapshot_without_losing_recovered_incidents()
    {
        var now = DateTimeOffset.UtcNow;
        var active = Incident("LineA.PLC1", null, now);
        var recovered = Incident("LineA.PLC2", now, now.AddSeconds(-2));

        var dto = ConnectionDiagnosticsApi.ToDto(new ConnectionIncidentSnapshot([active], [recovered]));

        Assert.Single(dto.Active);
        Assert.Single(dto.Recovered);
        Assert.False(dto.Recovered[0].Active);
    }

    [Fact]
    public void Partial_data_incident_exposes_partial_online_without_changing_connection_state()
    {
        var now = DateTimeOffset.UtcNow;
        var incident = new ConnectionIncident(
            "LineA.PLC1", "LineA", "siemens-s7", ConnectionDiagnosticCategory.PartialDataFailure,
            "部分数据异常", "数组部分元素失败。", "检查失败索引。", "Bad [100..119]",
            ConnectionDiagnosticScope.DataAccess, ConnectionState.Connected,
            now, now, null, 1, false, 0);

        var dto = ConnectionDiagnosticsApi.ToDto(incident);

        Assert.Equal("PartialOnline", dto.Availability);
        Assert.Equal("Connected", dto.State);
    }

    private static ConnectionIncident Incident(string device, DateTimeOffset? recoveredAt, DateTimeOffset occurredAt) =>
        new(device, "LineA", "opcua-client", ConnectionDiagnosticCategory.SecurityPolicyMismatch,
            "安全策略不匹配", "证书校验失败。", "信任证书。", "BadSecurityChecksFailed",
            ConnectionDiagnosticScope.Session, ConnectionState.Faulted,
            occurredAt, occurredAt, recoveredAt, 1, recoveredAt is null, recoveredAt is null ? 1000 : 0);
}
