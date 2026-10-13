using IGW.Core;

namespace IGW.Northbound.RestApi;

public sealed record ConnectionDiagnosticDto(
    string Device,
    string Channel,
    string Protocol,
    string Code,
    string Category,
    string Scope,
    string State,
    string Availability,
    string Cause,
    string Suggestion,
    string RawError,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    DateTimeOffset? RecoveredAt,
    int OccurrenceCount,
    bool Retrying,
    int NextRetryMs,
    bool Active);

public sealed record ConnectionDiagnosticsDto(
    IReadOnlyList<ConnectionDiagnosticDto> Active,
    IReadOnlyList<ConnectionDiagnosticDto> Recovered);

public static class ConnectionDiagnosticsApi
{
    public static ConnectionDiagnosticDto ToDto(ConnectionIncident incident) => new(
        incident.Device,
        incident.Channel,
        incident.Protocol,
        incident.Code,
        incident.CategoryLabel,
        incident.Scope.ToString(),
        incident.State.ToString(),
        incident.Category == ConnectionDiagnosticCategory.PartialDataFailure && incident.State == IGW.Abstractions.ConnectionState.Connected
            ? "PartialOnline"
            : incident.State.ToString(),
        incident.Cause,
        incident.Suggestion,
        incident.RawError,
        incident.FirstOccurredAt,
        incident.LastOccurredAt,
        incident.RecoveredAt,
        incident.OccurrenceCount,
        incident.Retrying,
        incident.NextRetryMs,
        incident.Active);

    public static ConnectionDiagnosticsDto ToDto(ConnectionIncidentSnapshot snapshot) =>
        new(snapshot.Active.Select(ToDto).ToArray(), snapshot.Recovered.Select(ToDto).ToArray());
}
