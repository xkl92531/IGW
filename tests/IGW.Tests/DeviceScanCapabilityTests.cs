using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class DeviceScanCapabilityTests
{
    [Fact]
    public async Task Driver_owned_partitioning_compiles_one_logical_batch_for_all_due_tags()
    {
        await using var hub = new DataHub();
        var bindings = CreateSamePeriodBindings(hub);
        var limits = new DriverLimits
        {
            MaxReadUnits = int.MaxValue,
            MaxGap = 0,
            MaxTagsPerRead = 10_000,
        };
        var capabilities = DriverCapabilitySnapshot.FromLimits(limits) with
        {
            PartitionsReadRequests = true,
            MaxTagsPerRead = 19,
            MaxReadBytes = 880,
        };

        var blocks = DeviceScanPlanCompiler.Compile(bindings, hub,
            DeviceScanDemandSnapshot.Empty, limits, capabilities, includeAllTags: true);

        var logical = Assert.Single(blocks);
        Assert.Equal(-1, logical.Area);
        Assert.Equal(0, logical.Start);
        Assert.Equal(0, logical.Count);
        Assert.Equal(72, logical.Tags.Count);
        Assert.Equal(72, logical.Tags.Select(tag => tag.TagId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(bindings.Select(tag => tag.TagId), logical.Tags.Select(tag => tag.TagId));
    }

    [Fact]
    public async Task Compatibility_planner_keeps_address_and_capacity_partitions_when_driver_does_not_own_them()
    {
        await using var hub = new DataHub();
        var bindings = CreateSamePeriodBindings(hub);
        var limits = new DriverLimits
        {
            MaxReadUnits = int.MaxValue,
            MaxGap = 0,
            MaxTagsPerRead = 10_000,
        };
        var capabilities = DriverCapabilitySnapshot.FromLimits(limits) with
        {
            PartitionsReadRequests = false,
            MaxTagsPerRead = 19,
            MaxReadBytes = 880,
        };

        var blocks = DeviceScanPlanCompiler.Compile(bindings, hub,
            DeviceScanDemandSnapshot.Empty, limits, capabilities, includeAllTags: true);

        Assert.True(blocks.Count > 1);
        Assert.All(blocks, block => Assert.InRange(block.Tags.Count, 1, 19));
        Assert.Equal([101, 202], blocks.Select(block => block.Area).Distinct().Order().ToArray());
        Assert.Equal(72, blocks.SelectMany(block => block.Tags).Select(tag => tag.TagId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Learned_request_window_splits_all_tags_without_dropping_any()
    {
        await using var hub = new DataHub();
        var bindings = new List<TagBinding>(2_477);
        for (int index = 0; index < 2_477; index++)
        {
            string tagId = $"Line.Plc.Group.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}",
                Address = $"DB1.DBW{index * 2}",
                DataType = IgwDataType.Int16,
                ScanRateMs = 100,
            };
            hub.Register(tagId, definition);
            bindings.Add(new TagBinding
            {
                TagId = tagId,
                Def = definition,
                Address = new TagAddress(1, index * 2, 2),
            });
        }

        var physical = new DriverLimits
        {
            MaxReadUnits = int.MaxValue,
            MaxGap = 0,
            MaxTagsPerRead = 10_000,
        };
        var learned = new DriverCapabilitySnapshot(
            MaxTagsPerRead: 512,
            MaxTagsPerWrite: 512,
            MaxReadBytes: int.MaxValue,
            MaxWriteBytes: int.MaxValue,
            MaxInFlight: 1,
            SupportsBatchRead: true,
            SupportsBatchWrite: true,
            SupportsSubscriptions: true,
            SupportsReadBackVerification: true,
            Version: 1);

        IReadOnlyList<CompiledScanBlock> blocks = DeviceScanPlanCompiler.Compile(
            bindings,
            hub,
            DeviceScanDemandSnapshot.Empty,
            physical,
            learned,
            includeAllTags: true);

        Assert.Equal(2_477, blocks.Sum(block => block.Tags.Count));
        Assert.All(blocks, block => Assert.InRange(block.Tags.Count, 1, 512));
        Assert.Equal(2_477, blocks.SelectMany(block => block.Tags)
            .Select(tag => tag.TagId)
            .Distinct(StringComparer.Ordinal)
            .Count());
    }

    [Fact]
    public async Task Negotiated_read_byte_limit_splits_large_values_without_dropping_tags()
    {
        await using var hub = new DataHub();
        var bindings = new List<TagBinding>();
        for (int index = 0; index < 5; index++)
        {
            string tagId = $"Line.Plc.Group.Text{index}";
            var definition = new TagDef
            {
                Name = $"Text{index}",
                Address = $"DB1.DBB{index * 102}",
                DataType = IgwDataType.String,
                StringLength = 100,
                ScanRateMs = 100,
            };
            hub.Register(tagId, definition);
            bindings.Add(new TagBinding
            {
                TagId = tagId,
                Def = definition,
                Address = new TagAddress(1, index * 102, 102),
            });
        }

        var limits = new DriverLimits
        {
            MaxReadUnits = int.MaxValue,
            MaxGap = 0,
            MaxTagsPerRead = 10_000,
        };
        var capabilities = DriverCapabilitySnapshot.FromLimits(limits) with
        {
            MaxReadBytes = 220,
        };

        IReadOnlyList<CompiledScanBlock> blocks = DeviceScanPlanCompiler.Compile(
            bindings,
            hub,
            DeviceScanDemandSnapshot.Empty,
            limits,
            capabilities,
            includeAllTags: true);

        Assert.Equal(5, blocks.Sum(block => block.Tags.Count));
        Assert.All(blocks, block => Assert.InRange(block.Tags.Count, 1, 2));
    }

    private static List<TagBinding> CreateSamePeriodBindings(DataHub hub)
    {
        var bindings = new List<TagBinding>(72);
        for (int index = 0; index < 72; index++)
        {
            int area = index < 36 ? 101 : 202;
            int offset = (index % 36) * 2;
            string tagId = $"Line.Plc.Group.Value{index}";
            var definition = new TagDef
            {
                Name = $"Value{index}",
                Address = $"DB{area}.DBW{offset}",
                DataType = IgwDataType.Int16,
                ScanRateMs = 100,
            };
            hub.Register(tagId, definition);
            bindings.Add(new TagBinding
            {
                TagId = tagId,
                Def = definition,
                Address = new TagAddress(area, offset, 2),
            });
        }

        return bindings;
    }
}
