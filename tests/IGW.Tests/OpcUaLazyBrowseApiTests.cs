using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace IGW.Tests;

public sealed class OpcUaLazyBrowseApiTests
{
    [Fact]
    public async Task Quick_client_scope_serializes_array_element_types_as_names()
    {
        var host = new BrowseHost();
        host.Project.Channels = [new ChannelDef { Name = "Line", Devices = [new DeviceDef
        {
            Name = "Plc", Tags = [new TagDef { Name = "TimeStamp", DataType = IgwDataType.Byte,
                Array = new ArrayDef { ElementType = IgwDataType.Byte, Dimensions = [8], LowerBounds = [0] } }],
        }] }];
        await using var app = await StartAppAsync(host);
        using var response = await app.GetTestClient().GetAsync("/api/v1/project/tag-scope?root=Line.Plc");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("byte", body.RootElement.GetProperty("definitions")[0].GetProperty("array").GetProperty("elementType").GetString());
    }
    private static readonly BrowsePreviewRequest Connection = new("opc.tcp://10.217.152.80:4840");

    [Fact]
    public async Task Browse_children_forwards_only_the_requested_parent_node()
    {
        var host = new BrowseHost();
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/opcua/browse-children", new
        {
            connection = Connection,
            nodeId = "ns=3;s=DataBlocksGlobal",
            pathSegments = new[] { "PLC8", "DataBlocksGlobal" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ns=3;s=DataBlocksGlobal", host.ChildrenNodeId);
        Assert.Equal(new[] { "PLC8", "DataBlocksGlobal" }, host.ChildrenPath);
    }

    [Fact]
    public async Task Browse_branch_forwards_the_exact_selected_node_and_limit()
    {
        var host = new BrowseHost();
        await using var app = await StartAppAsync(host);

        var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/opcua/browse-branch", new
        {
            connection = Connection,
            nodeId = "ns=3;s=DB2100",
            name = "DB2100",
            isVariable = false,
            pathSegments = new[] { "PLC8", "DataBlocksGlobal", "DB2100" },
            limit = 10000,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ns=3;s=DB2100", host.SelectedRoot?.NodeId);
        Assert.Equal(new[] { "PLC8", "DataBlocksGlobal", "DB2100" }, host.SelectedRoot?.PathSegments);
        Assert.Equal(10000, host.BranchLimit);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ns=3;s=DB2100.Bad", body.RootElement.GetProperty("issues")[0].GetProperty("nodeId").GetString());
        Assert.Equal("unsupported POINTER", body.RootElement.GetProperty("issues")[0].GetProperty("reason").GetString());
    }

    private static async Task<WebApplication> StartAppAsync(IGatewayHost host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapIgwApi(host);
        await app.StartAsync();
        return app;
    }

    private sealed class BrowseHost : IGatewayHost
    {
        public string? ChildrenNodeId { get; private set; }
        public IReadOnlyList<string>? ChildrenPath { get; private set; }
        public OpcUaBrowseNode? SelectedRoot { get; private set; }
        public int BranchLimit { get; private set; }

        public Task<OpcUaBrowseChildrenResult> BrowseOpcUaChildrenAsync(
            BrowsePreviewRequest connection, string nodeId, IReadOnlyList<string> pathSegments, CancellationToken ct)
        {
            ChildrenNodeId = nodeId;
            ChildrenPath = pathSegments;
            return Task.FromResult(new OpcUaBrowseChildrenResult([]));
        }

        public Task<BrowseResult> BrowseOpcUaBranchAsync(
            BrowsePreviewRequest connection, OpcUaBrowseNode selectedRoot, int limit, CancellationToken ct)
        {
            SelectedRoot = selectedRoot;
            BranchLimit = limit;
            return Task.FromResult(new BrowseResult([], false, Issues:
                [new OpcUaBrowseIssue("ns=3;s=DB2100.Bad", "Bad", ["PLC8", "DB2100", "Bad"], "unsupported POINTER")]));
        }

        public DataHub Hub => throw new NotSupportedException();
        public RuntimeEngine Engine => throw new NotSupportedException();
        public AlarmEngine Alarms => throw new NotSupportedException();
        public DataLogger? Logger => null;
        public ComputedEngine Computed => throw new NotSupportedException();
        public ScheduleEngine Scheduler => throw new NotSupportedException();
        public IgwProject Project { get; } = new();
        public IReadOnlyList<DriverDescriptor> Drivers => [];
        public INetworkAdapterCatalog NetworkAdapters { get; } = new NetworkAdapterResolver();
        public int OpcUaSessions => 0;
        public bool Reloading => false;
        public Task<BrowseResult> BrowsePreviewAsync(BrowsePreviewRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> ReconnectChannelAsync(string channelName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> SiemensAuthenticationStatusAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public Task<object> VerifySiemensAuthenticationAsync(string deviceName, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSiemensAuthenticationAsync(string deviceName, CancellationToken ct) => throw new NotSupportedException();
        public object ComponentsSnapshot() => new { };
        public object LicenseSnapshot() => new { blocked = false };
        public Task<object> ActivateLicenseAsync(string code) => throw new NotSupportedException();
        public Task ReloadAsync(IgwProject newProject) => throw new NotSupportedException();
        public object MesStatusSnapshot() => new { };
        public object MesLogSnapshot(int limit) => Array.Empty<object>();
        public IReadOnlyList<MesTaskStatus> MesTaskStatuses() => [];
        public IReadOnlyList<MesFlowEvent> MesTimeline(string taskName) => [];
        public Task<object> MesTestAsync(MesTask task, CancellationToken ct) => throw new NotSupportedException();
    }
}
