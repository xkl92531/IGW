using IGW.Abstractions;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class FlowTemplateTargetCatalogTests
{
    [Fact]
    public void Catalog_contains_only_mes_boundaries_and_task_state_without_tag_documents()
    {
        var project = new IgwProject
        {
            Templates =
            [
                new DeviceTemplate
                {
                    Name = "S7-Template", DriverId = "Siemens.S7",
                    Groups =
                    [
                        new TagGroupDef
                        {
                            Name = "DB1", Tags = [new TagDef { Name = "Ready" }],
                            SubGroups =
                            [
                                new TagGroupDef
                                {
                                    Name = "Struct", Tags = [new TagDef { Name = "Value" }],
                                    SubGroups = [new TagGroupDef { Name = "Nested", Tags = [new TagDef { Name = "Deep" }] }],
                                },
                            ],
                        },
                    ],
                },
            ],
            Channels =
            [
                new ChannelDef
                {
                    Name = "Line", DriverId = "Siemens.S7",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Name = "PLC", TemplateRef = "S7-Template",
                        },
                    ],
                },
            ],
            MesUpload = new MesUploadSettings
            {
                Tasks =
                [
                    new MesTask
                    {
                        Name = "DB1", GroupRef = "Line.PLC.DB1",
                        Flow = new MesFlow { Nodes = [new MesNode { Id = "trigger", Type = "Trigger" }] },
                    },
                ],
            },
        };

        TemplateApi.FlowTemplateTargetCatalog catalog = TemplateApi.BuildFlowTemplateTargets(project);

        Assert.Equal(["Line.PLC.DB1", "Line.PLC.DB1.Struct"], catalog.Groups.Select(group => group.Path));
        Assert.True(catalog.Groups[0].HasTask);
        Assert.True(catalog.Groups[0].HasFlow);
        Assert.Equal("S7-Template", catalog.Groups[0].TemplateRef);
        Assert.Equal(["S7-Template"], catalog.DeviceTemplates);
    }
}
