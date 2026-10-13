using System.Diagnostics;
using IGW.Abstractions;
using IGW.Core;
using Xunit;
using Xunit.Abstractions;

namespace IGW.Tests;

public sealed class ArrayRecognitionPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void Expanded_protocol_arrays_collapse_to_one_logical_descriptor(int elements)
    {
        var groups = new List<TagGroupDef>(elements);
        for (int index = 0; index < elements; index++)
        {
            groups.Add(new TagGroupDef
            {
                Name = $"display-{index}",
                Tags =
                [
                    new TagDef
                    {
                        Name = "Value",
                        Address = $"ns=3;s=\"DB\".\"PhysicalArray\"[{index}].\"Value\"",
                        DataType = IgwDataType.Int16,
                        Access = TagAccess.ReadWrite,
                    },
                ],
            });
        }
        var project = new IgwProject
        {
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "OpcUa.Client",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "Plc",
                            Groups = [new TagGroupDef { Name = "DB", SubGroups = [new TagGroupDef { Name = "Business", SubGroups = groups }] }],
                        },
                    ],
                },
            ],
        };

        GC.Collect();
        // 该算法完全在当前测试线程执行。使用进程总分配量会把 xUnit 并行运行的
        // 其他数百项测试计入本用例，导致全量门禁随机失败而单独运行通过。
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var catalog = ProjectArrayRecognitionCatalog.Build(project);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var descriptor = Assert.Single(catalog.LogicalArrays);
        Assert.Equal(elements, descriptor.ElementCount);
        Assert.Equal(elements, descriptor.Leaves.Count);
        Assert.DoesNotContain(catalog.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"识别耗时 {watch.Elapsed}");
        Assert.True(allocated < 1_500_000_000L, $"识别分配 {allocated:N0} bytes");
        output.WriteLine($"elements={elements}; elapsedMs={watch.Elapsed.TotalMilliseconds:F1}; allocatedMB={allocated / 1024d / 1024d:F1}; logicalArrays={catalog.LogicalArrays.Count}");
    }
}
