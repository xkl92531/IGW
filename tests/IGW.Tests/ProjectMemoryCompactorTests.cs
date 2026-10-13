using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectMemoryCompactorTests
{
    [Fact]
    public void Compaction_shares_repeated_strings_without_changing_any_value()
    {
        TagDef Tag(int index) => new()
        {
            Id = $"id-{index}",
            Name = new string("Result".ToCharArray()),
            Address = $"ns=3;s=\"DB{index % 2}\".\"Result\"",
            Description = new string(Array.Empty<char>()),
            Metadata = new Dictionary<string, string>
            {
                [new string("array.sourceFormat".ToCharArray())] = new string("OPC UA".ToCharArray()),
                [new string("array.sourceAddress".ToCharArray())] = $"ns=3;s=\"DB{index % 2}\".\"Result\"",
                [new string("opcua.valueRank".ToCharArray())] = new string("-1".ToCharArray()),
            },
        };
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "PLC", Devices = [new DeviceDef { Name = "D", Groups = [new TagGroupDef
            {
                Name = "G", Tags = [Tag(0), Tag(1), Tag(2)],
            }] }] }],
        };
        string before = ProjectStore.ToJson(project);

        ProjectMemoryCompactor.Compact(project);

        Assert.Equal(before, ProjectStore.ToJson(project));
        var tags = project.Channels[0].Devices[0].Groups[0].Tags;
        Assert.Same(tags[0].Name, tags[1].Name);
        Assert.Same(tags[0].Address, tags[2].Address);                              // 相同地址共用
        Assert.Same(tags[0].Metadata!["array.sourceFormat"], tags[1].Metadata!["array.sourceFormat"]);
        Assert.Same(tags[1].Address, tags[1].Metadata!["array.sourceAddress"]);      // 与地址相同的值直接引用地址
        Assert.Same(tags[0].Metadata!.Keys.First(), tags[2].Metadata!.Keys.First());
        Assert.Same(string.Empty, tags[0].Description);
    }
}
