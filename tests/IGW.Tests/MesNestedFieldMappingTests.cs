using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.Mes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class MesNestedFieldMappingTests
{
    private const string Block = "Line.Plc.DB";
    private static readonly string[] CellMembers = ["OCV", "Resistance", "Voltage", "Current", "Temperature", "Capacity", "Energy", "Result"];

    [Theory]
    [InlineData("Bracket")]
    [InlineData("Underscore")]
    [InlineData("Numeric")]
    [InlineData("Mixed")]
    public void Whole_block_mapping_keeps_cell_array_inside_each_slot(string style)
    {
        var project = Project(style, out _);
        var catalog = MesDataRangeCatalog.Build(project);
        Assert.True(catalog.TryGetLogicalArray(Block + ".Slot_Data", out _));
        var (fields, structs) = MesFieldMappingGenerator.Generate([new MesDataRange { SourcePath = Block }], catalog, Block);

        Assert.Equal([Block + ".Quantity", Block + ".Status"], fields.Select(field => field.TagId));
        var slots = Assert.Single(structs);
        Assert.Equal(("Array", "Enum", Block + ".Slot_Data", "Slot_Data", 1, 4),
            (slots.Kind, slots.CountMode, slots.BaseGroup, slots.AliasName, slots.StartIndex, slots.FixedCount));
        Assert.Equal(["StructCount"], slots.Members.Select(member => member.TagId));
        var cells = Assert.Single(slots.Structs);
        Assert.Equal(("Array", "Enum", "Cell_Date_1st", "Cell_Date_1st", 5, 40),
            (cells.Kind, cells.CountMode, cells.BaseGroup, cells.AliasName, cells.StartIndex, cells.FixedCount));
        Assert.Equal(CellMembers, cells.Members.Select(member => member.TagId));
    }

    [Theory]
    [InlineData("Bracket")]
    [InlineData("Underscore")]
    [InlineData("Numeric")]
    [InlineData("Mixed")]
    public async Task Generated_mapping_uploads_and_writes_back_each_slots_real_cell_path(string style)
    {
        var project = Project(style, out var points);
        var (fields, structs) = Generate(project, Block);
        await using var hub = new DataHub();
        foreach (var point in points)
        {
            hub.Register(point.Id, point.Tag);
            hub.Update(point.Id, DataValue.GoodAt(point.Value, DateTime.UtcNow));
        }
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var payload = new MesPayload { Mode = MesPayloadModes.FieldMapping, Fields = fields, Structs = structs };
        await engine.TestRunAsync(new MesTask
        {
            Payload = payload,
            Steps = [new MesStep { Http = new MesHttp { Url = "http://nested.test/upload" } }],
        }, default);

        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal(["Quantity", "Status", "Slot_Data"], json.RootElement.EnumerateObject().Select(property => property.Name));
        var slots = json.RootElement.GetProperty("Slot_Data");
        Assert.Equal(4, slots.GetArrayLength());
        Assert.Equal(41, slots[0].GetProperty("StructCount").GetInt32());
        Assert.Equal(44, slots[3].GetProperty("StructCount").GetInt32());
        var firstCells = slots[0].GetProperty("Cell_Date_1st");
        var lastCells = slots[3].GetProperty("Cell_Date_1st");
        Assert.Equal(40, firstCells.GetArrayLength());
        Assert.Equal(40, lastCells.GetArrayLength());
        Assert.Equal(1050, firstCells[0].GetProperty("OCV").GetDouble());
        Assert.Equal(1070, firstCells[2].GetProperty("OCV").GetDouble());
        Assert.Equal(4447, lastCells[39].GetProperty("Result").GetDouble());
        Assert.All(slots.EnumerateArray(), slot =>
            Assert.Equal(["StructCount", "Cell_Date_1st"], slot.EnumerateObject().Select(property => property.Name)));

        var changed = JsonNode.Parse(handler.Body)!;
        changed["Slot_Data"]![1]!["Cell_Date_1st"]![2]!["OCV"] = 99.5;
        changed["Slot_Data"]![3]!["Cell_Date_1st"]![39]!["Result"] = 88.25;
        using var response = JsonDocument.Parse(changed.ToJsonString());
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null, hub.ListTagIds());
        Assert.Equal(points.Count, writes.Count);
        Assert.Contains(writes, write => write.TagId == CellPath(style, 2, 7) + ".OCV" && write.Value == "99.5");
        Assert.Contains(writes, write => write.TagId == CellPath(style, 1, 7) + ".OCV" && write.Value == "1070");
        Assert.Contains(writes, write => write.TagId == CellPath(style, 2, 5) + ".OCV" && write.Value == "2050");
        Assert.Contains(writes, write => write.TagId == CellPath(style, 4, 44) + ".Result" && write.Value == "88.25");
        var registered = points.Select(point => point.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(writes, write => Assert.Contains(write.TagId, registered));
    }

    [Fact]
    public void Struct_with_own_members_keeps_its_nested_array_when_selected_or_expanded()
    {
        var project = Project("Bracket", out _);
        var block = project.Channels[0].Devices[0].Groups[0];
        block.SubGroups = [new TagGroupDef { Name = "Station", Tags = [Tag("Counter", IgwDataType.Int16)], SubGroups = block.SubGroups }];

        foreach (string selected in new[] { Block, Block + ".Station" })
        {
            var (_, structs) = Generate(project, selected);
            var station = Assert.Single(structs);
            Assert.Equal(("Object", Block + ".Station", "Station"), (station.Kind, station.BaseGroup, station.AliasName));
            Assert.Equal(["Counter"], station.Members.Select(member => member.TagId));
            var slots = Assert.Single(station.Structs);
            Assert.Equal(("Array", "Slot_Data"), (slots.Kind, slots.BaseGroup));
            Assert.Equal("Cell_Date_1st", Assert.Single(slots.Structs).BaseGroup);
        }
    }

    [Theory]
    [InlineData(2, "Bracket")]
    [InlineData(3, "Bracket")]
    [InlineData(2, "Underscore")]
    [InlineData(3, "Underscore")]
    public async Task Same_named_nested_arrays_keep_each_parent_boundary_during_upload_and_writeback(int depth, string style)
    {
        var project = SameNamedProject(depth, style, out var points);
        var (fields, structs) = Generate(project, Block);
        await using var hub = new DataHub();
        foreach (var point in points)
        {
            hub.Register(point.Id, point.Tag);
            hub.Update(point.Id, DataValue.GoodAt(point.Value, DateTime.UtcNow));
        }
        var handler = new Handler();
        await using var engine = new MesUploadEngine(hub, new MesUploadSettings(), (_, _, _) => Task.FromResult(true),
            NullLogger.Instance, httpMessageHandler: handler);
        var payload = new MesPayload { Mode = MesPayloadModes.FieldMapping, Fields = fields, Structs = structs };
        await engine.TestRunAsync(new MesTask
        {
            Payload = payload,
            Steps = [new MesStep { Http = new MesHttp { Url = "http://nested.test/upload" } }],
        }, default);

        using var request = JsonDocument.Parse(handler.Body);
        Assert.Equal(2, request.RootElement.GetProperty("Items").GetArrayLength());
        string expected = depth == 2
            ? """{"Items":[{"Items":[{"Value":11},{"Value":12}]},{"Items":[{"Value":21},{"Value":22}]}]}"""
            : """{"Items":[{"Items":[{"Items":[{"Value":111},{"Value":112}]},{"Items":[{"Value":121},{"Value":122}]}]},{"Items":[{"Items":[{"Value":211},{"Value":212}]},{"Items":[{"Value":221},{"Value":222}]}]}]}""";
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(handler.Body)), handler.Body);

        var changed = JsonNode.Parse(handler.Body)!;
        var selected = changed["Items"]![1]!["Items"]![0]!;
        if (depth == 3) selected = selected["Items"]![1]!;
        selected["Value"] = 91;
        using var response = JsonDocument.Parse(changed.ToJsonString());
        var writes = MesResponseWritePlanner.Plan(payload, response.RootElement, null, hub.ListTagIds());
        string target = style == "Bracket"
            ? (depth == 2 ? Block + ".Items[2].Items[1].Value" : Block + ".Items[2].Items[1].Items[2].Value")
            : (depth == 2 ? Block + ".Items_2.Items_1.Value" : Block + ".Items_2.Items_1.Items_2.Value");
        Assert.Equal(depth == 2 ? 4 : 8, writes.Count);
        Assert.Equal(target, Assert.Single(writes, write => write.Value == "91").TagId);
        string unchanged = style == "Bracket"
            ? (depth == 2 ? Block + ".Items[1].Items[1].Value" : Block + ".Items[1].Items[1].Items[2].Value")
            : (depth == 2 ? Block + ".Items_1.Items_1.Value" : Block + ".Items_1.Items_1.Items_2.Value");
        Assert.Contains(writes, write => write.TagId == unchanged && write.Value == (depth == 2 ? "11" : "112"));
        var registered = points.Select(point => point.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(writes, write => Assert.Contains(write.TagId, registered));
    }

    [Fact]
    public void Address_recognized_sparse_array_keeps_legacy_flat_field_mapping()
    {
        var sparse = new TagGroupDef
        {
            Name = "AnyBusinessName",
            SubGroups = new[] { (Name: "first", Index: 1), (Name: "third", Index: 3), (Name: "ninth", Index: 9) }
                .Select(item => new TagGroupDef
                {
                    Name = item.Name,
                    Tags = [new TagDef { Name = "Value", Address = $"ns=3;s=\"DB\".\"SparseValues\"[{item.Index}].\"Value\"", DataType = IgwDataType.Float }],
                }).ToList(),
        };
        var project = new IgwProject { Channels = [new ChannelDef { Name = "Line", DriverId = "OpcUa.Client", Devices = [new DeviceDef { Name = "Plc", Groups = [new TagGroupDef { Name = "DB", SubGroups = [sparse] }] }] }] };
        var catalog = MesDataRangeCatalog.Build(project);
        Assert.True(catalog.TryGetLogicalArray(Block + ".AnyBusinessName", out _));
        Assert.False(catalog.TryGetIndexedArray(Block + ".AnyBusinessName", out _));
        var (fields, structs) = MesFieldMappingGenerator.Generate([new MesDataRange { SourcePath = Block }], catalog, Block);
        Assert.Empty(structs);
        Assert.Equal([Block + ".AnyBusinessName.first.Value", Block + ".AnyBusinessName.third.Value", Block + ".AnyBusinessName.ninth.Value"], fields.Select(field => field.TagId));
    }

    private static (List<MesField> Fields, List<MesStruct> Structs) Generate(IgwProject project, string source) =>
        MesFieldMappingGenerator.Generate([new MesDataRange { SourcePath = source, Scope = "Group" }], MesDataRangeCatalog.Build(project), Block);

    private static IgwProject SameNamedProject(int depth, string style, out List<(string Id, TagDef Tag, object Value)> points)
    {
        var block = new TagGroupDef { Name = "DB" };
        var leaves = new List<(string Id, TagDef Tag, object Value)>();
        void AddElements(TagGroupDef parent, string path, string address, int level, int prefix)
        {
            for (int index = 1; index <= 2; index++)
            {
                string name = style == "Bracket" ? $"Items[{index}]" : $"Items_{index}";
                var element = new TagGroupDef { Name = name };
                parent.SubGroups.Add(element);
                string elementPath = path + "." + name;
                string elementAddress = $"{address}.Items[{index}]";
                int value = prefix * 10 + index;
                if (level < depth) AddElements(element, elementPath, elementAddress, level + 1, value);
                else
                {
                    var tag = Tag("Value", IgwDataType.Int32);
                    tag.Address = "ns=2;s=" + elementAddress + ".Value";
                    element.Tags.Add(tag);
                    leaves.Add((elementPath + ".Value", tag, value));
                }
            }
        }
        AddElements(block, Block, Block, 1, 0);
        points = leaves;
        return new IgwProject { Channels = [new ChannelDef { Name = "Line", DriverId = "OpcUa.Client", Devices = [new DeviceDef { Name = "Plc", Groups = [block] }] }] };
    }

    private static IgwProject Project(string style, out List<(string Id, TagDef Tag, object Value)> points)
    {
        var block = new TagGroupDef { Name = "DB", Tags = [Tag("Quantity", IgwDataType.Int16), Tag("Status", IgwDataType.Int16)] };
        var slotsParent = style == "Numeric" ? new TagGroupDef { Name = "Slot_Data" } : block;
        if (style == "Numeric") block.SubGroups.Add(slotsParent);
        for (int slot = 1; slot <= 4; slot++)
        {
            var slotGroup = new TagGroupDef { Name = ElementName(style, "Slot_Data", slot, false), Tags = [Tag("StructCount", IgwDataType.Int16)] };
            slotsParent.SubGroups.Add(slotGroup);
            var cellsParent = style == "Numeric" ? new TagGroupDef { Name = "Cell_Date_1st" } : slotGroup;
            if (style == "Numeric") slotGroup.SubGroups.Add(cellsParent);
            for (int cell = 5; cell < 45; cell++)
                cellsParent.SubGroups.Add(new TagGroupDef { Name = ElementName(style, "Cell_Date_1st", cell, true), Tags = CellMembers.Select(name => Tag(name, IgwDataType.Float)).ToList() });
        }
        points = [];
        Collect(block, Block, points);
        foreach (var point in points)
        {
            string address = Block + "." + point.Tag.Name;
            if (point.Id.Contains("Slot_Data", StringComparison.Ordinal))
            {
                string[] segments = point.Id.Split('.');
                int slot = Index(segments[3] == "Slot_Data" ? segments[4] : segments[3]);
                address = $"{Block}.Slot_Data[{slot}]";
                if (point.Tag.DataType == IgwDataType.Float) address += $".Cell_Date_1st[{Index(segments[^2])}]";
                address += "." + point.Tag.Name;
            }
            point.Tag.Address = "ns=2;s=" + address;
        }
        return new IgwProject { Channels = [new ChannelDef { Name = "Line", DriverId = "OpcUa.Client", Devices = [new DeviceDef { Name = "Plc", Groups = [block] }] }] };
    }

    private static void Collect(TagGroupDef group, string path, List<(string Id, TagDef Tag, object Value)> points)
    {
        foreach (var tag in group.Tags)
        {
            object value = (short)1;
            if (tag.Name == "Status") value = (short)2;
            if (path.Contains("Slot_Data", StringComparison.Ordinal))
            {
                string[] segments = path.Split('.');
                int slot = Index(segments[3] == "Slot_Data" ? segments[4] : segments[3]);
                value = (short)(40 + slot);
                if (tag.DataType == IgwDataType.Float)
                    value = (float)(slot * 1000 + Index(segments[^1]) * 10 + Array.IndexOf(CellMembers, tag.Name));
            }
            points.Add(($"{path}.{tag.Name}", tag, value));
        }
        foreach (var child in group.SubGroups) Collect(child, $"{path}.{child.Name}", points);
    }

    private static int Index(string name) => int.Parse(name.Contains('[') ? name[(name.LastIndexOf('[') + 1)..^1] : name[(name.LastIndexOf('_') + 1)..], CultureInfo.InvariantCulture);
    private static string ElementName(string style, string name, int index, bool cell) => style switch
    {
        "Numeric" => index.ToString(CultureInfo.InvariantCulture),
        "Underscore" => $"{name}_{index}",
        "Mixed" when !cell => $"{name}_{index}",
        _ => $"{name}[{index}]",
    };
    private static string CellPath(string style, int slot, int cell) => style == "Numeric"
        ? $"{Block}.Slot_Data.{slot}.Cell_Date_1st.{cell}"
        : $"{Block}.{ElementName(style, "Slot_Data", slot, false)}.{ElementName(style, "Cell_Date_1st", cell, true)}";
    private static TagDef Tag(string name, IgwDataType type) => new() { Name = name, Address = "MEM", DataType = type, Access = TagAccess.ReadWrite };

    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"httpStatusCode\":200}", Encoding.UTF8, "application/json") };
        }
    }
}
