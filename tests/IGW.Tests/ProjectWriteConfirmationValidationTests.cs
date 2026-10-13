using IGW.Abstractions;
using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectWriteConfirmationValidationTests
{
    [Fact]
    public void Rejects_pure_write_tag_without_an_explicit_confirmation_strategy()
    {
        IgwProject project = ProjectWith(
            new TagDef { Name = "Command", Address = "DB1.DBW0", Access = TagAccess.Write });

        ProjectStore.ValidationReport report = ProjectStore.Check(project);

        Assert.Contains(report.Errors, error =>
            error.Contains("Line.Plc.Command", StringComparison.Ordinal) &&
            error.Contains("确认方式", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_missing_self_referencing_and_cyclic_confirmation_tags()
    {
        var a = ConfirmedWrite("A", "Line.Plc.B");
        var b = ConfirmedWrite("B", "Line.Plc.A");
        var self = ConfirmedWrite("Self", "Line.Plc.Self");
        var missing = ConfirmedWrite("Missing", "Line.Plc.DoesNotExist");
        IgwProject project = ProjectWith(a, b, self, missing);

        ProjectStore.ValidationReport report = ProjectStore.Check(project);

        Assert.Contains(report.Errors, error => error.Contains("循环", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("自身", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("DoesNotExist", StringComparison.Ordinal));
    }

    [Fact]
    public void Allows_multiple_write_tags_to_use_readable_confirmation_tags_in_the_same_device()
    {
        var acceptedA = new TagDef
        {
            Name = "AcceptedA", Address = "DB1.DBX10.0", DataType = IgwDataType.Bool,
            Access = TagAccess.Read,
        };
        var acceptedB = new TagDef
        {
            Name = "AcceptedB", Address = "DB1.DBX10.1", DataType = IgwDataType.Bool,
            Access = TagAccess.Read,
        };
        IgwProject project = ProjectWith(
            ConfirmedWrite("CommandA", "Line.Plc.AcceptedA"),
            ConfirmedWrite("CommandB", "Line.Plc.AcceptedB"),
            acceptedA,
            acceptedB);

        ProjectStore.ValidationReport report = ProjectStore.Check(project);

        Assert.DoesNotContain(report.Errors, error => error.Contains("确认", StringComparison.Ordinal));
    }

    private static TagDef ConfirmedWrite(string name, string confirmationTagId) => new()
    {
        Name = name,
        Address = $"DB1.{name}",
        DataType = IgwDataType.Int16,
        Access = TagAccess.Write,
        WriteConfirmation = new WriteConfirmationDef
        {
            Mode = WriteConfirmationMode.ConfirmationTag,
            ConfirmationTagId = confirmationTagId,
            ExpectedValue = "true",
        },
    };

    private static IgwProject ProjectWith(params TagDef[] tags) => new()
    {
        Channels =
        [
            new ChannelDef
            {
                Name = "Line",
                DriverId = "Simulator",
                Devices =
                [
                    new DeviceDef { Name = "Plc", Tags = [.. tags] },
                ],
            },
        ],
    };
}
