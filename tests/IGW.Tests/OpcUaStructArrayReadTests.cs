using IGW.Abstractions;
using IGW.Drivers.OpcUaClient;
using Opc.Ua;
using Xunit;

namespace IGW.Tests;

/// <summary>
/// 结构体数组整读：西门子把 UDT 数组展开成每个成员一个节点，208×11 就是 2288 个节点。
/// 数组本身是一个节点，读一次就够。
/// </summary>
public sealed class OpcUaStructArrayReadTests
{
    private const string Array2 = "ns=3;s=\"DB8217_BusbarWelding\".\"BusbarWelding\".\"EquipmentData\"";

    [Theory]
    [InlineData("ns=3;s=\"DB\".\"B\".\"EquipmentData\"[2].\"Position\"", "ns=3;s=\"DB\".\"B\".\"EquipmentData\"", 2, "Position")]
    [InlineData("ns=3;s=\"DB\".\"B\".\"Data\"[0].\"Result\"", "ns=3;s=\"DB\".\"B\".\"Data\"", 0, "Result")]
    [InlineData("ns=3;s=Plain[17].Member", "ns=3;s=Plain", 17, "Member")]
    public void Element_addresses_are_split_into_array_index_and_member(string address, string array, int index, string member)
    {
        Assert.True(OpcUaStructArrayPlan.TryParseElement(address, out string parsedArray, out int parsedIndex, out string parsedMember));
        Assert.Equal(array, parsedArray);
        Assert.Equal(index, parsedIndex);
        Assert.Equal(member, parsedMember);
    }

    [Theory]
    [InlineData("ns=3;s=\"DB\".\"B\".\"Heart\".\"HeartBeat\"")]
    [InlineData("ns=3;s=\"DB\".\"B\".\"Data\"[3]")]
    [InlineData("ns=3;s=\"DB\".\"B\".\"Data\".\"Member\"[3]")]
    [InlineData("")]
    public void Non_array_addresses_are_left_alone(string address)
    {
        Assert.False(OpcUaStructArrayPlan.TryParseElement(address, out _, out _, out _));
    }

    [Fact]
    public void Site_sized_array_becomes_one_group_and_other_tags_stay_per_tag()
    {
        var tags = new List<TagBinding>();
        for (int index = 1; index <= 208; index++)
            foreach (string member in new[] { "Position", "Result", "BSBWeldShieldGasFlow" })
                tags.Add(Binding($"{Array2}[{index}].\"{member}\"", IgwDataType.Float));
        tags.Add(Binding("ns=3;s=\"DB\".\"B\".\"TrolleyCode\"", IgwDataType.String));
        // 只有 3 个成员的小数组不值得整读。
        for (int index = 1; index <= 3; index++)
            tags.Add(Binding($"ns=3;s=\"DB\".\"Small\"[{index}].\"Value\"", IgwDataType.Int16));

        var (groups, others) = OpcUaStructArrayPlan.Plan(tags);

        var group = Assert.Single(groups);
        Assert.Equal(Array2, group.ArrayAddress);
        Assert.Equal(1, group.FirstIndex);
        Assert.Equal(208, group.LastIndex);
        Assert.Equal(624, group.Members.Count);
        Assert.Equal(4, others.Count);
    }

    [Fact]
    public void Arrays_larger_than_one_read_are_split_into_index_ranges()
    {
        var tags = new List<TagBinding>();
        for (int index = 0; index < 1200; index++)
            tags.Add(Binding($"{Array2}[{index}].\"Result\"", IgwDataType.Int16));

        var (groups, others) = OpcUaStructArrayPlan.Plan(tags);

        Assert.Empty(others);
        Assert.Equal(3, groups.Count);
        Assert.All(groups, item => Assert.True(item.ElementCount <= OpcUaStructArrayPlan.MaxElementsPerRead));
        Assert.Equal(1200, groups.Sum(item => item.Members.Count));
    }

    [Fact]
    public void Read_request_addresses_the_array_node_with_the_index_range()
    {
        var group = Assert.Single(OpcUaStructArrayPlan.Plan(
            Enumerable.Range(5, 20).Select(index => Binding($"{Array2}[{index}].\"Result\"", IgwDataType.Int16)).ToArray()).Groups);

        var request = OpcUaStructArrayPlan.ReadRequest(group, new NodeId("array", 3));

        Assert.Equal("5:24", request.IndexRange);
        Assert.Equal(Attributes.Value, request.AttributeId);
    }

    [Fact]
    public void Structure_definition_decodes_a_siemens_binary_element()
    {
        var definition = new StructureDefinition
        {
            StructureType = StructureType.Structure,
            Fields =
            [
                new StructureField { Name = "Position", DataType = DataTypeIds.Int16, ValueRank = -1 },
                new StructureField { Name = "Result", DataType = DataTypeIds.Int16, ValueRank = -1 },
                new StructureField { Name = "Flow", DataType = DataTypeIds.Float, ValueRank = -1 },
                new StructureField { Name = "Code", DataType = DataTypeIds.String, ValueRank = -1 },
                new StructureField { Name = "Ok", DataType = DataTypeIds.Boolean, ValueRank = -1 },
            ],
        };
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((short)7));
        body.AddRange(BitConverter.GetBytes((short)-3));
        body.AddRange(BitConverter.GetBytes(12.5f));
        body.AddRange(BitConverter.GetBytes(2));
        body.AddRange("AB"u8.ToArray());
        body.Add(1);

        var decoded = new OpcUaStructDecoder(definition, new Dictionary<NodeId, StructureDefinition>()).TryDecode([.. body]);

        Assert.NotNull(decoded);
        Assert.Equal((short)7, decoded!["Position"]);
        Assert.Equal((short)-3, decoded["Result"]);
        Assert.Equal(12.5f, decoded["Flow"]);
        Assert.Equal("AB", decoded["Code"]);
        Assert.Equal(true, decoded["Ok"]);
    }

    [Fact]
    public void Optional_fields_fall_back_to_per_tag_reads()
    {
        var definition = new StructureDefinition
        {
            StructureType = StructureType.StructureWithOptionalFields,
            Fields = [new StructureField { Name = "Position", DataType = DataTypeIds.Int16, ValueRank = -1, IsOptional = true }],
        };

        Assert.Null(new OpcUaStructDecoder(definition, new Dictionary<NodeId, StructureDefinition>())
            .TryDecode(BitConverter.GetBytes((short)1)));
    }

    [Fact]
    public void Truncated_body_falls_back_instead_of_publishing_garbage()
    {
        var definition = new StructureDefinition
        {
            StructureType = StructureType.Structure,
            Fields =
            [
                new StructureField { Name = "A", DataType = DataTypeIds.Int32, ValueRank = -1 },
                new StructureField { Name = "B", DataType = DataTypeIds.Int32, ValueRank = -1 },
            ],
        };

        Assert.Null(new OpcUaStructDecoder(definition, new Dictionary<NodeId, StructureDefinition>())
            .TryDecode(BitConverter.GetBytes(1)));
    }

    [Fact]
    public void Driver_plans_one_admission_unit_per_array_group()
    {
        var driver = new OpcUaClientDeviceDriver("opc.tcp://127.0.0.1:4840", 1000, true, "", "", "None");
        var tags = Enumerable.Range(1, 100).Select(index => Binding($"{Array2}[{index}].\"Result\"", IgwDataType.Int16))
            .Concat(Enumerable.Range(0, 300).Select(index => Binding($"ns=3;s=\"DB\".\"Flat{index}\"", IgwDataType.Int16)))
            .ToArray();

        var units = driver.PlanReadUnits(tags);

        Assert.Equal(100, units[0].Count);
        Assert.All(units[0], tag => Assert.Contains("EquipmentData", tag.Def.Address));
        Assert.Equal(400, units.Sum(unit => unit.Count));
    }

    [Fact]
    public void Struct_array_reads_can_be_switched_off_per_channel()
    {
        var channel = new ChannelDef
        {
            Name = "PLC", DriverId = OpcUaClientDriverFactory.Id,
            Settings = { ["url"] = "opc.tcp://127.0.0.1:4840", [OpcUaClientDriverFactory.StructArrayReadKey] = "false" },
        };
        var driver = (OpcUaClientDeviceDriver)new OpcUaClientDriverFactory().CreateDevice(channel, new DeviceDef { Name = "D" });
        var tags = Enumerable.Range(1, 100).Select(index => Binding($"{Array2}[{index}].\"Result\"", IgwDataType.Int16)).ToArray();

        var units = driver.PlanReadUnits(tags);

        Assert.All(units, unit => Assert.True(unit.Count <= driver.ReadChunkSize));
        Assert.Contains(new OpcUaClientDriverFactory().Describe().ChannelSettings,
            setting => setting.Key == OpcUaClientDriverFactory.StructArrayReadKey);
    }

    private static TagBinding Binding(string address, IgwDataType type) => new()
    {
        TagId = address,
        Def = new TagDef { Name = address, Address = address, DataType = type },
        Address = new(0, 0, 2),
    };
}
