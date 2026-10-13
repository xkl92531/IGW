using System.Text.Json;
using IGW.Abstractions;

namespace IGW.Northbound.RestApi;

public sealed record TagValueStreamRequest
{
    public int ProtocolVersion { get; set; }
    public string ClientId { get; set; } = "";
    public string ProjectRevision { get; set; } = "";
    public List<string> TagIds { get; set; } = [];
    public List<string> Topics { get; set; } = [];
    public bool IncludeSnapshot { get; set; } = true;
    public int? HeartbeatSeconds { get; set; }
}

internal sealed record ValidatedTagValueStreamRequest(
    string ClientId,
    string ProjectRevision,
    IReadOnlyList<string> TagIds,
    IReadOnlyList<string> Topics,
    bool IncludeSnapshot,
    int HeartbeatSeconds);

internal sealed record TagValueStreamValidationResult(
    ValidatedTagValueStreamRequest? Request,
    string? ErrorCode)
{
    public bool IsValid => Request is not null;

    public static TagValueStreamValidationResult Success(ValidatedTagValueStreamRequest request) =>
        new(request, null);

    public static TagValueStreamValidationResult Failure(string errorCode) =>
        new(null, errorCode);
}

internal static class TagValueStreamRequestValidator
{
    internal const int ProtocolVersion = 1;
    internal const int MaxClientIdLength = 64;
    internal const int MaxProjectRevisionLength = 256;
    internal const int MaxTagIdLength = 512;
    internal const int MaxTopicLength = 256;
    internal const int HardMaxTagsPerStream = 10_000;
    internal const int MinHeartbeatSeconds = 5;
    internal const int MaxHeartbeatSeconds = 30;

    public static TagValueStreamValidationResult Validate(
        TagValueStreamRequest? request,
        TagStreamSettings? settings)
    {
        if (request is null || request.ProtocolVersion != ProtocolVersion)
            return TagValueStreamValidationResult.Failure("unsupported_protocol");

        string clientId = request.ClientId?.Trim() ?? "";
        if (clientId.Length is 0 or > MaxClientIdLength)
            return TagValueStreamValidationResult.Failure("invalid_client_id");

        string projectRevision = request.ProjectRevision?.Trim() ?? "";
        if (projectRevision.Length is 0 or > MaxProjectRevisionLength)
            return TagValueStreamValidationResult.Failure("invalid_project_revision");

        settings ??= new TagStreamSettings();
        int maxTags = Math.Clamp(settings.MaxTagsPerStream, 1, HardMaxTagsPerStream);
        if (request.TagIds is null || request.TagIds.Count > maxTags
            || (request.TagIds.Count == 0 && (request.Topics?.Count ?? 0) == 0))
            return TagValueStreamValidationResult.Failure("invalid_tag_count");

        var tagIds = new List<string>(request.TagIds.Count);
        foreach (string? item in request.TagIds)
        {
            string tagId = item?.Trim() ?? "";
            if (tagId.Length is 0 or > MaxTagIdLength)
                return TagValueStreamValidationResult.Failure("invalid_tag_id");
            tagIds.Add(tagId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIds = tagIds
            .Where(seen.Add)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var topicSet = new HashSet<string>(StringComparer.Ordinal);
        var topics = new List<string>(request.Topics?.Count ?? 0);
        foreach (string? item in request.Topics ?? [])
        {
            string topic = item?.Trim() ?? "";
            if (topic.Length is 0 or > MaxTopicLength)
                return TagValueStreamValidationResult.Failure("invalid_topic");
            if (topicSet.Add(topic)) topics.Add(topic);
        }
        topics.Sort(StringComparer.Ordinal);

        int defaultHeartbeat = Math.Clamp(
            settings.HeartbeatSeconds,
            MinHeartbeatSeconds,
            MaxHeartbeatSeconds);
        int heartbeat = request.HeartbeatSeconds ?? defaultHeartbeat;
        if (heartbeat is < MinHeartbeatSeconds or > MaxHeartbeatSeconds)
            return TagValueStreamValidationResult.Failure("invalid_heartbeat");

        return TagValueStreamValidationResult.Success(new ValidatedTagValueStreamRequest(
            clientId,
            projectRevision,
            normalizedIds,
            topics,
            request.IncludeSnapshot,
            heartbeat));
    }
}

internal sealed record TagStreamHello(
    int ProtocolVersion,
    string StreamId,
    string ProjectName,
    string ProjectRevision,
    int AcceptedTagCount,
    int MissingTagCount,
    IReadOnlyList<string> MissingTagIdSample,
    DateTime ServerTimeUtc);

internal sealed record TagStreamValue(
    string Id,
    object? Value,
    string ValueKind,
    string Quality,
    uint QualityCode,
    bool Good,
    DateTime SourceTs,
    DateTime ServerTs);

internal sealed record TagStreamSnapshot(
    int ChunkIndex,
    int ChunkCount,
    bool Complete,
    IReadOnlyList<TagStreamValue> Values);

internal sealed record TagStreamSnapshotBegin(
    string SnapshotId,
    int TotalValues,
    int ChunkCount,
    DateTime CapturedAtUtc);

internal sealed record TagStreamSnapshotChunk(
    string SnapshotId,
    int ChunkIndex,
    IReadOnlyList<TagStreamValue> Values);

internal sealed record TagStreamSnapshotComplete(
    string SnapshotId,
    int TotalValues,
    int ChunkCount,
    DateTime CompletedAtUtc);

internal sealed record TagStreamDelta(
    DateTime EmittedAtUtc,
    IReadOnlyList<TagStreamValue> Values);

internal sealed record TagStreamValueTooLarge(
    string Id,
    int SerializedBytes,
    string Context,
    DateTime ServerTimeUtc);

internal sealed record TagStreamTerminalError(
    string Code,
    string Message,
    DateTime ServerTimeUtc);

internal sealed record TagStreamDeviceHeartbeat(
    string Name,
    string State,
    int ConfiguredMinPeriodMs,
    DateTime? LastScanCompletedAtUtc,
    DateTime? LastGoodSampleAtUtc);

internal sealed record TagStreamSubscriptionHeartbeat(long Queued, long Dropped);

internal sealed record TagStreamHeartbeat(
    DateTime ServerTimeUtc,
    long LastSequence,
    string ProjectRevision,
    IReadOnlyList<TagStreamDeviceHeartbeat> Devices,
    TagStreamSubscriptionHeartbeat Subscription);

internal sealed record TagStreamResetRequired(
    string Reason,
    string Message,
    DateTime ServerTimeUtc);

internal sealed record TagStreamProjectChanged(
    string ProjectRevision,
    string Reason,
    DateTime ServerTimeUtc);

internal static class TagValueStreamJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
