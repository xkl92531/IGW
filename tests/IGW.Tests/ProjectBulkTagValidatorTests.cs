using IGW.Abstractions;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectBulkTagValidatorTests
{
    private static ProjectBulkTagRequest Request(params ProjectBulkTagItem[] items) => new()
    {
        Channel = new ProjectBulkChannelSpec("PLC1", "Siemens.S7", null),
        Device = new ProjectBulkDeviceSpec("S7Plus", "10.0.0.1:102", null),
        Items = [.. items],
    };

    private static ProjectBulkTagItem Item(string name, params string[] groups) => new()
    {
        GroupPath = [.. groups], Name = name, Address = "DB1.X." + name, DataType = IgwDataType.Bool,
        Access = TagAccess.ReadWrite, ScanRateMs = 100,
    };

    [Fact]
    public void Valid_request_has_no_errors() =>
        Assert.Empty(ProjectBulkTagValidator.Validate(Request(Item("A", "DB1", "Sub"), Item("A", "DB2"))));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a.b")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    public void Invalid_tag_name_is_rejected(string name)
    {
        var errors = ProjectBulkTagValidator.Validate(Request(Item(name, "DB1")));
        Assert.Equal(0, Assert.Single(errors).Index);
    }

    [Fact]
    public void Dotted_group_segment_is_rejected() =>
        Assert.Equal(0, Assert.Single(ProjectBulkTagValidator.Validate(Request(Item("A", "DB1.Type")))).Index);

    [Fact]
    public void Duplicate_name_in_same_group_is_rejected_case_insensitively()
    {
        var errors = ProjectBulkTagValidator.Validate(Request(Item("Run", "DB1"), Item("RUN", "db1")));
        Assert.Equal(1, Assert.Single(errors).Index);
    }

    [Fact]
    public void Channel_device_and_replace_group_names_are_checked()
    {
        var request = Request(Item("A"));
        request.Channel = request.Channel with { Name = "a.b" };
        request.Device = request.Device with { Name = "" };
        request.ReplaceGroups = [[], ["x.y"]];
        var errors = ProjectBulkTagValidator.Validate(request);
        Assert.Equal(4, errors.Count);
        Assert.All(errors, error => Assert.Equal(-1, error.Index));
    }

    [Fact]
    public void Errors_are_capped()
    {
        var items = Enumerable.Range(0, 200).Select(_ => Item("")).ToArray();
        Assert.Equal(50, ProjectBulkTagValidator.Validate(Request(items)).Count);
    }
    [Fact]
    public void Group_path_depth_is_capped()
    {
        string[] okPath = Enumerable.Range(0, ProjectBulkTagValidator.MaxGroupDepth).Select(i => "G" + i).ToArray();
        string[] deepPath = Enumerable.Range(0, ProjectBulkTagValidator.MaxGroupDepth + 1).Select(i => "G" + i).ToArray();
        Assert.Equal(16, ProjectBulkTagValidator.MaxGroupDepth);
        Assert.Empty(ProjectBulkTagValidator.Validate(Request(Item("A", okPath))));
        Assert.Equal(0, Assert.Single(ProjectBulkTagValidator.Validate(Request(Item("A", deepPath)))).Index);
    }

    [Fact]
    public void Replace_group_depth_is_capped()
    {
        var request = Request(Item("A"));
        request.ReplaceGroups = [[.. Enumerable.Range(0, ProjectBulkTagValidator.MaxGroupDepth + 1).Select(i => "G" + i)]];
        Assert.Equal(-1, Assert.Single(ProjectBulkTagValidator.Validate(request)).Index);
        request.ReplaceGroups = [[.. Enumerable.Range(0, ProjectBulkTagValidator.MaxGroupDepth).Select(i => "G" + i)]];
        Assert.Empty(ProjectBulkTagValidator.Validate(request));
    }

    [Fact]
    public void Name_and_address_lengths_are_capped()
    {
        string maxName = new('n', ProjectBulkTagValidator.MaxNameLength);
        Assert.Empty(ProjectBulkTagValidator.Validate(Request(Item(maxName, maxName))));
        Assert.Single(ProjectBulkTagValidator.Validate(Request(Item(maxName + "n"))));
        Assert.Single(ProjectBulkTagValidator.Validate(Request(Item("A", maxName + "n"))));

        var longAddress = Item("A");
        longAddress.Address = new string('x', ProjectBulkTagValidator.MaxAddressLength + 1);
        Assert.Single(ProjectBulkTagValidator.Validate(Request(longAddress)));
        longAddress.Address = new string('x', ProjectBulkTagValidator.MaxAddressLength);
        Assert.Empty(ProjectBulkTagValidator.Validate(Request(longAddress)));

        var request = Request(Item("A"));
        request.Channel = request.Channel with { Name = maxName + "c" };
        request.Device = request.Device with { Name = maxName + "d" };
        Assert.Equal(2, ProjectBulkTagValidator.Validate(request).Count);
    }

    [Theory]
    [InlineData("a\u0001b")]
    [InlineData("a\nb")]
    [InlineData("a\u007fb")]
    public void Control_characters_are_rejected_in_every_name(string name)
    {
        Assert.Single(ProjectBulkTagValidator.Validate(Request(Item(name))));
        Assert.Single(ProjectBulkTagValidator.Validate(Request(Item("A", name))));
        var request = Request(Item("A"));
        request.Channel = request.Channel with { Name = name };
        Assert.Single(ProjectBulkTagValidator.Validate(request));
        request = Request(Item("A"));
        request.Device = request.Device with { Name = name };
        Assert.Single(ProjectBulkTagValidator.Validate(request));
        request = Request(Item("A"));
        request.ReplaceGroups = [[name]];
        Assert.Single(ProjectBulkTagValidator.Validate(request));
    }

    [Fact]
    public void Undefined_enum_values_and_non_positive_scan_rates_are_rejected()
    {
        var badType = Item("A"); badType.DataType = (IgwDataType)9999;
        var badAccess = Item("B"); badAccess.Access = (TagAccess)77;
        var zeroRate = Item("C"); zeroRate.ScanRateMs = 0;
        var negativeRate = Item("D"); negativeRate.ScanRateMs = -5;
        var errors = ProjectBulkTagValidator.Validate(Request(badType, badAccess, zeroRate, negativeRate, Item("E")));
        Assert.Equal([0, 1, 2, 3], errors.Select(e => e.Index).ToArray());
    }

    [Fact]
    public void Item_and_replace_group_counts_are_capped()
    {
        Assert.Equal(200_000, ProjectBulkTagValidator.MaxItems);
        Assert.Equal(10_000, ProjectBulkTagValidator.MaxReplaceGroups);
        var request = Request(Item("A"));
        Assert.Null(ProjectBulkTagValidator.CheckLimits(request));

        request.Items = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxItems + 1).Select(i => (ProjectBulkTagItem)null!)];
        Assert.NotNull(ProjectBulkTagValidator.CheckLimits(request));

        request = Request(Item("A"));
        request.ReplaceGroups = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxReplaceGroups + 1).Select(i => new List<string> { "G" + i })];
        Assert.NotNull(ProjectBulkTagValidator.CheckLimits(request));
        request.ReplaceGroups = [.. Enumerable.Range(0, ProjectBulkTagValidator.MaxReplaceGroups).Select(i => new List<string> { "G" + i })];
        Assert.Null(ProjectBulkTagValidator.CheckLimits(request));
    }
}
