using System.Text.Json.Serialization;

namespace IGW.OpcUaProbe;

public sealed record ProbeOptions(
    string Url,
    string SecurityPolicy = "None",
    bool Anonymous = true,
    string Username = "",
    string Password = "",
    int TimeoutMs = 15_000,
    int MaxNodes = 200_000,
    int MaxDepth = 128,
    bool IncludeNamespaceZero = false,
    bool AcceptUntrustedCertificate = true,
    string LocalIp = "");

public sealed class OpcUaProbeReport
{
    public string FormatVersion { get; init; } = "1.0";
    public DateTimeOffset CollectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required ProbeConnectionReport Connection { get; init; }
    public required IReadOnlyList<ProbeNamespaceReport> Namespaces { get; init; }
    public required IReadOnlyList<ProbeNodeReport> Nodes { get; init; }
    public IReadOnlyList<ProbeNodeReport> DataTypes { get; init; } = [];
    public required ProbeSummary Summary { get; init; }
    public ProbeWriteDiagnostics? WriteDiagnostics { get; init; }
    public bool VariableValuesRead => false;
    public bool BusinessVariableValuesRead => false;
    public bool ServerCapabilityValuesRead => true;
    public bool PlcWritesPerformed => false;
}

public sealed record ProbeConnectionReport(
    string RequestedEndpoint,
    string DiscoveredEndpoint,
    string SessionEndpoint,
    string ServerApplicationName,
    string ServerApplicationUri,
    string ServerProductUri,
    string SecurityMode,
    string SecurityPolicyUri,
    string UserIdentity,
    string? ServerCertificateSubject,
    string? ServerCertificateIssuer,
    string? ServerCertificateSha256,
    DateTime? ServerCertificateNotBefore,
    DateTime? ServerCertificateNotAfter,
    string? LocalSourceIp = null,
    string? TransportEndpoint = null);

public sealed record ProbeNamespaceReport(ushort Index, string Uri);

public sealed class ProbeNodeReport
{
    public required string NodeId { get; init; }
    public required ushort NamespaceIndex { get; init; }
    public required string NamespaceUri { get; init; }
    public required string IdentifierType { get; init; }
    public required string Identifier { get; init; }
    public required string NodeClass { get; init; }
    public required string BrowseName { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> PathSegments { get; init; }
    public required IReadOnlyList<ProbeIncomingReference> IncomingReferences { get; init; }
    public required IReadOnlyDictionary<string, ProbeAttributeReport> Attributes { get; init; }
    public required OpcUaNodeIdentifierAnalysis SiemensAddressAnalysis { get; init; }
}

public sealed record ProbeIncomingReference(
    string ParentNodeId,
    string ReferenceTypeId,
    string ReferenceTypeName,
    string Path);

public sealed record ProbeAttributeReport(
    uint AttributeId,
    string Status,
    string StatusCode,
    string? ValueType,
    object? Value,
    IReadOnlyList<string>? DecodedFlags = null);

public sealed record ProbeSummary(
    int TotalNodes,
    int Objects,
    int Variables,
    int Methods,
    int StringNodeIds,
    int NumericNodeIds,
    int SiemensSymbolCandidates,
    int UserReadableVariables,
    int UserWritableVariables,
    int AttributeErrors,
    bool Truncated,
    string MappingConclusion);

public sealed record ProbeWriteDiagnosticItem(
    string NodeId,
    string Path,
    bool DeclaredWritable,
    bool WritableByCurrentUser,
    string? DataTypeNodeId,
    int? ValueRank,
    IReadOnlyList<int>? ArrayDimensions,
    bool DataTypeDefinitionPublished,
    IReadOnlyList<uint> PublishedStringMaxLengths,
    IReadOnlyList<string> LikelyReasons,
    IReadOnlyList<string> EvidenceLimitations);

public sealed record ProbeWriteDiagnostics(
    int Variables,
    int WritableByCurrentUser,
    int ReadOnlyForCurrentUser,
    int UnknownWritePermission,
    IReadOnlyDictionary<string, ProbeAttributeReport> ServerCapabilities,
    IReadOnlyList<ProbeWriteDiagnosticItem> Items,
    string Limitation);

public static class ProbeAttributeCatalog
{
    private static readonly uint[] Ids =
    [
        2,  // NodeClass
        3,  // BrowseName
        4,  // DisplayName
        5,  // Description
        6,  // WriteMask
        7,  // UserWriteMask
        12, // EventNotifier
        14, // DataType
        15, // ValueRank
        16, // ArrayDimensions
        17, // AccessLevel
        18, // UserAccessLevel
        19, // MinimumSamplingInterval
        20, // Historizing
        21, // Executable
        22, // UserExecutable
        23, // DataTypeDefinition
        24, // RolePermissions
        25, // UserRolePermissions
        26, // AccessRestrictions
        27, // AccessLevelEx
    ];

    public static IReadOnlyList<uint> ReadOnlyAttributeIds() => Ids;
}

public static class ProbeServerObservationCatalog
{
    private static readonly string[] Fields =
    [
        "Server_ServiceLevel",
        "Server_Auditing",
        "Server_ServerStatus_State",
        "Server_ServerStatus_StartTime",
        "Server_ServerStatus_CurrentTime",
        "Server_ServerStatus_BuildInfo_ProductName",
        "Server_ServerStatus_BuildInfo_SoftwareVersion",
        "Server_ServerDiagnostics_EnabledFlag",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_CurrentSessionCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_RejectedSessionCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_SecurityRejectedSessionCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_SessionTimeoutCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_SessionAbortCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_RejectedRequestsCount",
        "Server_ServerDiagnostics_ServerDiagnosticsSummary_SecurityRejectedRequestsCount",
        "Server_ServerCapabilities_MaxBrowseContinuationPoints",
        "Server_ServerCapabilities_MaxQueryContinuationPoints",
        "Server_ServerCapabilities_MaxHistoryContinuationPoints",
        "Server_ServerCapabilities_MaxArrayLength",
        "Server_ServerCapabilities_MaxStringLength",
        "Server_ServerCapabilities_MaxByteStringLength",
        "Server_ServerCapabilities_MinSupportedSampleRate",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerRead",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerHistoryReadData",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerHistoryReadEvents",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerWrite",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerHistoryUpdateData",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerHistoryUpdateEvents",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerMethodCall",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerBrowse",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerRegisterNodes",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerTranslateBrowsePathsToNodeIds",
        "Server_ServerCapabilities_OperationLimits_MaxNodesPerNodeManagement",
        "Server_ServerCapabilities_OperationLimits_MaxMonitoredItemsPerCall",
    ];

    public static IReadOnlyList<string> FieldNames() => Fields;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpcUaProbeReport))]
internal partial class ProbeJsonContext : JsonSerializerContext;
