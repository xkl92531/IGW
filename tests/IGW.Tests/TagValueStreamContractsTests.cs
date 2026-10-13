using System.Text.Json;
using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class TagValueStreamContractsTests
{
    [Fact]
    public void Tag_stream_defaults_enable_production_capacity_with_bounded_recovery()
    {
        var settings = new ProjectSettings().TagStream;

        Assert.True(settings.Enabled);
        Assert.Equal(10_000, settings.MaxTagsPerStream);
        Assert.Equal(100, settings.MaxStreams);
        Assert.Equal(10, settings.HeartbeatSeconds);
        Assert.Equal(60, settings.SnapshotIntervalSeconds);
        Assert.Equal(4, settings.MaxConcurrentSnapshots);
        Assert.Equal(1, settings.DeltaBatchWindowMs);
        Assert.Equal(262_144, settings.CoordinatorQueueCapacity);
    }

    [Fact]
    public void Older_project_json_gets_safe_tag_stream_defaults()
    {
        var project = JsonSerializer.Deserialize<IgwProject>("""
            {
              "name": "legacy",
              "settings": {
                "restPort": 48020,
                "opcUaPort": 4840,
                "managementPort": 48010
              }
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(project);
        Assert.True(project.Settings.TagStream.Enabled);
        Assert.Equal(10_000, project.Settings.TagStream.MaxTagsPerStream);
        Assert.Equal(100, project.Settings.TagStream.MaxStreams);
        Assert.Equal(60, project.Settings.TagStream.SnapshotIntervalSeconds);
    }

    [Fact]
    public void Valid_request_deduplicates_exact_ids_but_preserves_case_distinct_ids()
    {
        var request = new TagValueStreamRequest
        {
            ProtocolVersion = 1,
            ClientId = "hsjm-lineup-instance-01",
            ProjectRevision = "rev-1",
            TagIds = ["Channel.Plc.Z", "channel.plc.a", "Channel.Plc.Z"],
            Topics = ["devices", "devices"],
            IncludeSnapshot = true,
            HeartbeatSeconds = 15,
        };

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
        Assert.Equal(15, result.Request!.HeartbeatSeconds);
        Assert.Equal(["Channel.Plc.Z", "channel.plc.a"], result.Request.TagIds);
        Assert.Equal(["devices"], result.Request.Topics);
        Assert.Equal("rev-1", result.Request.ProjectRevision);
    }

    [Fact]
    public void Missing_heartbeat_uses_configured_default()
    {
        var request = ValidRequest();
        request.HeartbeatSeconds = null;

        var result = TagValueStreamRequestValidator.Validate(
            request,
            new TagStreamSettings { HeartbeatSeconds = 12 });

        Assert.True(result.IsValid);
        Assert.Equal(12, result.Request!.HeartbeatSeconds);
    }

    [Theory]
    [InlineData(0, "unsupported_protocol")]
    [InlineData(2, "unsupported_protocol")]
    public void Rejects_unsupported_protocol(int protocolVersion, string errorCode)
    {
        var request = ValidRequest();
        request.ProtocolVersion = protocolVersion;

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.False(result.IsValid);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("", "invalid_client_id")]
    [InlineData(" ", "invalid_client_id")]
    public void Rejects_blank_client_id(string clientId, string errorCode)
    {
        var request = ValidRequest();
        request.ClientId = clientId;

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.False(result.IsValid);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public void Rejects_client_id_over_64_characters()
    {
        var request = ValidRequest();
        request.ClientId = new string('x', 65);

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.False(result.IsValid);
        Assert.Equal("invalid_client_id", result.ErrorCode);
    }

    [Fact]
    public void Rejects_empty_or_excessive_tag_collections()
    {
        var empty = ValidRequest();
        empty.TagIds = [];
        var excessive = ValidRequest();
        excessive.TagIds = Enumerable.Range(0, 10_001).Select(i => $"C.P.Tag{i}").ToList();

        Assert.Equal(
            "invalid_tag_count",
            TagValueStreamRequestValidator.Validate(empty, new TagStreamSettings()).ErrorCode);
        Assert.Equal(
            "invalid_tag_count",
            TagValueStreamRequestValidator.Validate(excessive, new TagStreamSettings()).ErrorCode);
    }

    [Fact]
    public void Rejects_blank_or_overlong_tag_id()
    {
        var blank = ValidRequest();
        blank.TagIds = ["  "];
        var overlong = ValidRequest();
        overlong.TagIds = [new string('x', 513)];

        Assert.Equal(
            "invalid_tag_id",
            TagValueStreamRequestValidator.Validate(blank, new TagStreamSettings()).ErrorCode);
        Assert.Equal(
            "invalid_tag_id",
            TagValueStreamRequestValidator.Validate(overlong, new TagStreamSettings()).ErrorCode);
    }

    [Fact]
    public void Allows_topic_only_runtime_stream()
    {
        var request = ValidRequest();
        request.TagIds = [];
        request.Topics = ["devices", "mes"];
        request.IncludeSnapshot = false;

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_blank_project_revision()
    {
        var request = ValidRequest();
        request.ProjectRevision = " ";

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.False(result.IsValid);
        Assert.Equal("invalid_project_revision", result.ErrorCode);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(31)]
    public void Rejects_heartbeat_outside_5_to_30_seconds(int heartbeatSeconds)
    {
        var request = ValidRequest();
        request.HeartbeatSeconds = heartbeatSeconds;

        var result = TagValueStreamRequestValidator.Validate(request, new TagStreamSettings());

        Assert.False(result.IsValid);
        Assert.Equal("invalid_heartbeat", result.ErrorCode);
    }

    [Fact]
    public void Event_json_uses_protocol_field_names_without_credentials()
    {
        var hello = new TagStreamHello(
            ProtocolVersion: 1,
            StreamId: "stream-1",
            ProjectName: "Project",
            ProjectRevision: "sha256:abc",
            AcceptedTagCount: 1,
            MissingTagCount: 0,
            MissingTagIdSample: [],
            ServerTimeUtc: new DateTime(2026, 8, 21, 2, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(hello, TagValueStreamJson.Options);

        Assert.Contains("\"protocolVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"streamId\":\"stream-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"projectRevision\":\"sha256:abc\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    private static TagValueStreamRequest ValidRequest() => new()
    {
        ProtocolVersion = 1,
        ClientId = "client-1",
        ProjectRevision = "rev-1",
        TagIds = ["Channel.Plc.Tag"],
        Topics = [],
        IncludeSnapshot = true,
        HeartbeatSeconds = 10,
    };
}
