# Multi-NIC Industrial Protocol Source Binding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every configured outbound TCP industrial protocol honor the channel's selected server network adapter while preserving default Windows routing and existing protocol behavior.

**Architecture:** Add one protocol-transparent TCP relay and network-adapter resolver in `IGW.Core`. `RuntimeEngine` prepares a relay-backed runtime endpoint before calling an unchanged protocol factory, retains the real project endpoint, owns relay lifetime beside the driver, and merges relay failures into device diagnostics. The Web UI reads server adapters from a Runtime API and stores stable adapter ID, name, and IPv4 in existing channel settings.

**Tech Stack:** .NET 10, `System.Net.NetworkInformation`, `System.Net.Sockets`, ASP.NET Core Minimal APIs, xUnit, Vue 3, TypeScript, Element Plus, Node test runner.

## Global Constraints

- Do not modify Windows routes, metrics, gateways, or firewall configuration.
- Do not require additional administrator privileges.
- Do not use Ping as online status evidence.
- Default/blank adapter settings must preserve the current direct-connect behavior.
- An unavailable explicit adapter must fail closed and must not fall back to another adapter.
- Project endpoints and imported/exported JSON must continue to contain real device addresses, never relay addresses.
- Apply the same binding behavior to Siemens S7 Classic/Plus, Modbus TCP, Omron FINS TCP, Omron/AB EtherNet/IP explicit TCP, Mitsubishi MC TCP, OPC UA Client, and MQTT Client.
- Preserve existing uncommitted changes in `installer/version.txt`, `src/IGW.Core/Engine.cs`, and `src/IGW.Northbound.Mes/MesUploadEngine.cs`; patch overlapping Engine code without reverting it.
- Never log credentials.

---

## File Structure

- Create `src/IGW.Core/NetworkAdapterResolver.cs`: server adapter enumeration and explicit-selection resolution.
- Create `src/IGW.Core/BoundTcpRelay.cs`: loopback listener, source-bound outbound sockets, bidirectional forwarding, lifecycle, and relay diagnostics.
- Create `src/IGW.Core/NetworkBindingPlan.cs`: supported-driver target extraction, runtime config cloning, endpoint rewriting, and binding lease.
- Modify `src/IGW.Core/Engine.cs`: prepare binding before factory creation, retain leases, merge diagnostics, and dispose bindings.
- Modify `src/IGW.Core/IGatewayHost.cs`: expose the shared read-only network-adapter catalog.
- Modify `src/IGW.Runtime/GatewayHost.cs`: create one adapter catalog and pass it to RuntimeEngine.
- Modify `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`: pin discovered OPC UA endpoint to the relay URL at runtime.
- Modify `src/IGW.Northbound.RestApi/IgwRestApi.cs`: expose server adapters.
- Modify `src/IGW.WebUi/src/api.ts`: typed adapter API.
- Modify `src/IGW.WebUi/src/views/Project.vue`: real adapter selects, unavailable-item preservation, and stable settings.
- Modify `src/IGW.WebUi/src/i18n/catalog.ts`: bilingual adapter and diagnostics text.
- Create `tests/IGW.Tests/NetworkAdapterResolverTests.cs`: deterministic selection tests.
- Create `tests/IGW.Tests/BoundTcpRelayTests.cs`: real socket forwarding/binding tests.
- Create `tests/IGW.Tests/NetworkBindingPlanTests.cs`: all supported driver mappings and no-project-mutation tests.
- Create `src/IGW.WebUi/src/utils/networkAdapterSelection.ts`: pure Web selection helpers.
- Create `src/IGW.WebUi/src/utils/networkAdapterSelection.test.cjs`: Web persistence and unavailable-selection tests.

---

### Task 1: Resolve Server Network Adapters Deterministically

**Files:**
- Create: `src/IGW.Core/NetworkAdapterResolver.cs`
- Create: `tests/IGW.Tests/NetworkAdapterResolverTests.cs`

**Interfaces:**
- Produces: `NetworkAdapterInfo`, `NetworkAdapterSelection`, `NetworkAdapterResolution`, `INetworkAdapterCatalog`, and `NetworkAdapterResolver`.
- Consumes: `ChannelDef.Settings` keys `networkAdapter`, `networkAdapterId`, and `networkAdapterIpv4`.

- [ ] **Step 1: Write failing resolver tests**

```csharp
[Fact]
public void Default_selection_leaves_routing_to_the_operating_system()
{
    var result = NetworkAdapterResolver.Resolve(new Dictionary<string, string>
    {
        ["networkAdapter"] = "默认值"
    }, Array.Empty<NetworkAdapterInfo>());
    Assert.Equal(NetworkAdapterResolutionKind.SystemDefault, result.Kind);
    Assert.Null(result.LocalAddress);
}

[Fact]
public void Explicit_id_and_ipv4_resolve_only_the_saved_address()
{
    var adapters = new[] { new NetworkAdapterInfo("nic-1", "PLC LAN", "192.168.10.20", 24) };
    var result = NetworkAdapterResolver.Resolve(new Dictionary<string, string>
    {
        ["networkAdapter"] = "PLC LAN",
        ["networkAdapterId"] = "nic-1",
        ["networkAdapterIpv4"] = "192.168.10.20"
    }, adapters);
    Assert.Equal(NetworkAdapterResolutionKind.Bound, result.Kind);
    Assert.Equal("192.168.10.20", result.LocalAddress!.ToString());
}

[Fact]
public void Missing_explicit_adapter_fails_closed()
{
    var result = NetworkAdapterResolver.Resolve(new Dictionary<string, string>
    {
        ["networkAdapter"] = "PLC LAN",
        ["networkAdapterId"] = "removed",
        ["networkAdapterIpv4"] = "192.168.10.20"
    }, Array.Empty<NetworkAdapterInfo>());
    Assert.Equal(NetworkAdapterResolutionKind.Unavailable, result.Kind);
    Assert.Contains("不可用", result.Message);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~NetworkAdapterResolverTests`

Expected: compilation fails because the resolver types do not exist.

- [ ] **Step 3: Implement adapter enumeration and resolution**

```csharp
public sealed record NetworkAdapterInfo(string Id, string Name, string Ipv4, int PrefixLength)
{
    public string Label => $"{Name} — {Ipv4}";
}

public enum NetworkAdapterResolutionKind { SystemDefault, Bound, Unavailable }

public sealed record NetworkAdapterResolution(
    NetworkAdapterResolutionKind Kind,
    NetworkAdapterInfo? Adapter,
    IPAddress? LocalAddress,
    string Message);

public interface INetworkAdapterCatalog
{
    IReadOnlyList<NetworkAdapterInfo> Available();
}

public sealed class NetworkAdapterResolver : INetworkAdapterCatalog
{
    public IReadOnlyList<NetworkAdapterInfo> Available() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up
                && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ToIpv4Rows)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Ipv4, StringComparer.Ordinal)
            .ToArray();
}
```

Resolution rules must be exactly: blank/`默认值`/`Default` means `SystemDefault`; saved ID plus saved IPv4 must match one current row; legacy name without ID succeeds only for one unique current row; every other explicit selection returns `Unavailable` without fallback.

- [ ] **Step 4: Run resolver tests and verify GREEN**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~NetworkAdapterResolverTests`

Expected: all resolver tests pass.

- [ ] **Step 5: Commit the resolver**

```powershell
git add src/IGW.Core/NetworkAdapterResolver.cs tests/IGW.Tests/NetworkAdapterResolverTests.cs
git commit -m "feat(core): resolve configured server network adapters"
```

---

### Task 2: Forward TCP Through a Source-Bound Relay

**Files:**
- Create: `src/IGW.Core/BoundTcpRelay.cs`
- Create: `tests/IGW.Tests/BoundTcpRelayTests.cs`

**Interfaces:**
- Consumes: `Func<NetworkAdapterResolution>` so recovery is re-evaluated on every connection attempt.
- Produces: `BoundTcpRelay.Start(...)`, `LoopbackEndpoint`, `Snapshot()`, `LoopbackEndpointAllocator`, and `IAsyncDisposable` lifecycle.

- [ ] **Step 1: Write failing real-socket relay tests**

```csharp
[Fact]
public async Task Relay_binds_outbound_socket_to_selected_ipv4_and_forwards_both_directions()
{
    await using var target = await EchoServer.StartAsync(IPAddress.Loopback);
    await using var relay = BoundTcpRelay.Start(
        "device-1", target.Endpoint,
        () => new NetworkAdapterResolution(NetworkAdapterResolutionKind.Bound,
            new("loop", "Loop Test", "127.0.0.1", 8), IPAddress.Loopback, ""),
        TimeSpan.FromSeconds(2), RelayListenRequirement.Ephemeral);

    using var client = new TcpClient(AddressFamily.InterNetwork);
    await client.ConnectAsync(relay.LoopbackEndpoint);
    await client.GetStream().WriteAsync("IGW"u8.ToArray());
    var response = new byte[3];
    await client.GetStream().ReadExactlyAsync(response);

    Assert.Equal("IGW", Encoding.ASCII.GetString(response));
    Assert.Equal(IPAddress.Loopback, target.LastRemoteAddress);
    Assert.Equal(NetworkRelayState.Connected, relay.Snapshot().State);
}

[Fact]
public async Task Unavailable_adapter_accepts_no_fallback_connection_and_reports_source_failure()
{
    await using var target = await EchoServer.StartAsync(IPAddress.Loopback);
    await using var relay = BoundTcpRelay.Start("device-1", target.Endpoint,
        () => new(NetworkAdapterResolutionKind.Unavailable, null, null, "配置的网卡不可用"),
        TimeSpan.FromMilliseconds(300), RelayListenRequirement.Ephemeral);

    using var client = new TcpClient();
    await client.ConnectAsync(relay.LoopbackEndpoint);
    await Assert.ThrowsAnyAsync<Exception>(async () =>
        await client.GetStream().ReadExactlyAsync(new byte[1]));
    Assert.Equal("AdapterUnavailable", relay.Snapshot().Code);
}
```

- [ ] **Step 2: Run relay tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~BoundTcpRelayTests`

Expected: compilation fails because `BoundTcpRelay` does not exist.

- [ ] **Step 3: Implement the minimal asynchronous relay**

```csharp
public static BoundTcpRelay Start(
    string name,
    DnsEndPoint target,
    Func<NetworkAdapterResolution> resolveSource,
    TimeSpan connectTimeout,
    RelayListenRequirement listen)
{
    var lease = LoopbackEndpointAllocator.Acquire(listen);
    var listener = new TcpListener(lease.Address, lease.Port);
    listener.Start();
    var relay = new BoundTcpRelay(name, target, resolveSource, connectTimeout, listener);
    relay._acceptTask = relay.AcceptLoopAsync(relay._stop.Token);
    return relay;
}

private async Task ForwardOneAsync(TcpClient inbound, CancellationToken stop)
{
    var source = _resolveSource();
    if (source.Kind != NetworkAdapterResolutionKind.Bound || source.LocalAddress is null)
        throw Record("AdapterUnavailable", source.Message);

    using var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    outbound.Bind(new IPEndPoint(source.LocalAddress, 0));
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
    timeout.CancelAfter(_connectTimeout);
    await outbound.ConnectAsync(await ResolveIpv4Async(_target, timeout.Token), timeout.Token);
    await PumpBothWaysAsync(inbound.Client, outbound, stop);
}
```

The listener must bind only loopback addresses, track accepted connection tasks, cancel both copy directions when either side closes, cap connect timeout, and await all active tasks during disposal. `RelayListenRequirement.Ephemeral` uses `127.0.0.1:0`; `RelayListenRequirement.EtherNetIp` leases a unique address from `127.64.0.1` through `127.127.255.254` and listens on port 44818 because libplctag accepts a gateway host but not a custom port. Dispose the address lease with the relay. Do not retain unbounded payload buffers.

Add a real-socket test that starts two EtherNet/IP-style relays simultaneously and asserts different loopback IPs, port 44818 on both, successful forwarding, and address reuse only after disposal.

Define `EchoServer` as a private test helper in `BoundTcpRelayTests.cs`; it must listen on the requested IPv4, echo received bytes, capture the accepted socket's remote IPv4, and implement `IAsyncDisposable`.

- [ ] **Step 4: Run relay tests and verify GREEN**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~BoundTcpRelayTests`

Expected: bidirectional and unavailable-adapter tests pass without leaked listeners.

- [ ] **Step 5: Commit the relay**

```powershell
git add src/IGW.Core/BoundTcpRelay.cs tests/IGW.Tests/BoundTcpRelayTests.cs
git commit -m "feat(core): add source-bound TCP relay"
```

---

### Task 3: Prepare Runtime Endpoints for Every TCP Driver

**Files:**
- Create: `src/IGW.Core/NetworkBindingPlan.cs`
- Create: `tests/IGW.Tests/NetworkBindingPlanTests.cs`
- Modify: `src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs`

**Interfaces:**
- Consumes: `IDriverFactory`, `ChannelDef`, `DeviceDef`, `INetworkAdapterCatalog` and the relay from Task 2.
- Produces: `NetworkBindingPlan.Prepare(...)` returning `PreparedNetworkDevice(ChannelDef Channel, DeviceDef Device, NetworkBindingLease? Lease)`.

- [ ] **Step 1: Write failing all-driver mapping tests**

```csharp
[Theory]
[InlineData("Siemens.S7", "192.168.1.2:102", 102)]
[InlineData("Modbus.Tcp", "192.168.1.3:502", 502)]
[InlineData("Omron.Fins", "192.168.1.4:9600", 9600)]
[InlineData("Omron.Cip", "192.168.1.5:44818", 44818)]
[InlineData("AB.Eip", "192.168.1.6:44818", 44818)]
[InlineData("Mitsubishi.Mc", "192.168.1.7:5000", 5000)]
[InlineData("Mqtt.Client", "192.168.1.8:1883", 1883)]
public async Task Explicit_binding_rewrites_runtime_device_endpoint_only(string driverId, string endpoint, int _)
{
    var channel = BoundChannel(driverId);
    var device = new DeviceDef { Name = "D1", Endpoint = endpoint };
    await using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());
    Assert.StartsWith("127.", prepared.Device.Endpoint);
    Assert.Equal(endpoint, device.Endpoint);
    Assert.Same(device.Tags, prepared.Device.Tags);
    Assert.Same(device.Groups, prepared.Device.Groups);
}

[Fact]
public async Task Opcua_rewrites_only_runtime_url_and_preserves_real_project_url()
{
    var channel = BoundChannel("OpcUa.Client");
    channel.Settings["url"] = "opc.tcp://192.168.1.9:49320/IGW";
    await using var prepared = NetworkBindingPlan.Prepare(channel, new DeviceDef { Name = "UA" }, Catalog());
    Assert.StartsWith("opc.tcp://127.0.0.1:", prepared.Channel.Settings["url"]);
    Assert.EndsWith("/IGW", prepared.Channel.Settings["url"]);
    Assert.Equal("opc.tcp://192.168.1.9:49320/IGW", channel.Settings["url"]);
}

[Fact]
public async Task Default_adapter_returns_original_definitions_without_a_relay()
{
    var channel = new ChannelDef { DriverId = "Siemens.S7", Settings = { ["networkAdapter"] = "默认值" } };
    var device = new DeviceDef { Endpoint = "192.168.1.2:102" };
    await using var prepared = NetworkBindingPlan.Prepare(channel, device, Catalog());
    Assert.Same(channel, prepared.Channel);
    Assert.Same(device, prepared.Device);
    Assert.Null(prepared.Lease);
}
```

- [ ] **Step 2: Run mapping tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~NetworkBindingPlanTests`

Expected: compilation fails because `NetworkBindingPlan` is missing.

- [ ] **Step 3: Implement target parsing, shallow runtime clones, and relay leases**

```csharp
public sealed record PreparedNetworkDevice(
    ChannelDef Channel,
    DeviceDef Device,
    NetworkBindingLease? Lease) : IAsyncDisposable;

public static PreparedNetworkDevice Prepare(
    ChannelDef channel,
    DeviceDef device,
    INetworkAdapterCatalog adapters)
{
    var selection = NetworkAdapterResolver.Resolve(channel.Settings, adapters.Available());
    if (selection.Kind == NetworkAdapterResolutionKind.SystemDefault)
        return new(channel, device, null);

    var target = TcpDriverTarget.Parse(channel, device);
    var relay = BoundTcpRelay.Start($"{channel.Name}.{device.Name}", target.Endpoint,
        () => NetworkAdapterResolver.Resolve(channel.Settings, adapters.Available()), target.Timeout,
        target.DriverId is "AB.Eip" or "Omron.Cip"
            ? RelayListenRequirement.EtherNetIp
            : RelayListenRequirement.Ephemeral);
    return target.Rewrite(channel, device, relay.LoopbackEndpoint, new NetworkBindingLease(relay, target));
}
```

`TcpDriverTarget.Parse` must support exactly the eight drivers from the global constraints. Clone only `ChannelDef.Settings` and `DeviceDef.Settings` plus scalar fields; reuse read-only tag/group collections because runtime endpoint rewriting must not duplicate million-point structures. Do not modify the input objects.

- [ ] **Step 4: Pin OPC UA session creation to the relay URL**

After `SelectEndpoint`, copy the selected endpoint and replace only `EndpointUrl` with `_url` before constructing `ConfiguredEndpoint`:

```csharp
var discovered = CoreClientUtils.SelectEndpoint(_config, _url, _secure);
var epDesc = new EndpointDescription(discovered) { EndpointUrl = _url };
var ep = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(_config));
```

Add a focused test or extract a pure helper if the OPC UA SDK constructor makes direct testing impractical; assert that the project URL remains real and the runtime URL remains loopback through discovery/session setup.

- [ ] **Step 5: Run mapping and OPC UA tests and verify GREEN**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~NetworkBindingPlanTests|FullyQualifiedName~OpcUa"`

Expected: all selected tests pass.

- [ ] **Step 6: Commit protocol mapping**

```powershell
git add src/IGW.Core/NetworkBindingPlan.cs tests/IGW.Tests/NetworkBindingPlanTests.cs src/IGW.Drivers.OpcUaClient/OpcUaClientDeviceDriver.cs
git commit -m "feat(core): bind all TCP driver runtime endpoints"
```

---

### Task 4: Own Relays in Runtime and Surface Binding Diagnostics

**Files:**
- Modify: `src/IGW.Core/Engine.cs`
- Modify: `src/IGW.Core/IGatewayHost.cs`
- Modify: `src/IGW.Runtime/GatewayHost.cs`
- Create: `tests/IGW.Tests/RuntimeEngineNetworkBindingTests.cs`
- Modify: `src/IGW.Northbound.RestApi/AnomalyService.cs`

**Interfaces:**
- Consumes: `NetworkBindingPlan.Prepare` and `NetworkBindingLease` from Task 3.
- Produces: relay-backed factory creation, deterministic disposal, and `DeviceConnectionDetail` codes `NetworkAdapterUnavailable`, `NetworkConnectFailure`, and `NetworkBound`.

- [ ] **Step 1: Write failing RuntimeEngine lifecycle tests**

```csharp
[Fact]
public async Task Apply_adapter_change_disposes_old_relay_and_rebuilds_device()
{
    var first = Project("nic-a", "127.0.0.1");
    await using var runtime = Runtime(first, catalog: Catalog("nic-a", "127.0.0.1"));
    await runtime.StartAsync();
    var oldEndpoint = runtime.NetworkBindingSnapshot("C.D")!.LoopbackEndpoint;

    await runtime.ApplyAsync(Project("nic-b", "127.0.0.2"));

    Assert.False(await CanConnectAsync(oldEndpoint));
    Assert.NotEqual(oldEndpoint, runtime.NetworkBindingSnapshot("C.D")!.LoopbackEndpoint);
}

[Fact]
public async Task Unavailable_adapter_keeps_device_visible_and_reports_exact_failure()
{
    await using var runtime = Runtime(Project("missing", "192.168.10.20"), catalog: EmptyCatalog());
    await runtime.StartAsync();
    var status = Assert.Single(runtime.DeviceStatuses());
    Assert.NotEqual(ConnectionState.Connected, status.State);
    Assert.Equal("NetworkAdapterUnavailable", status.Detail?.Code);
    Assert.Contains("192.168.10.20", status.Detail?.Message);
}
```

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~RuntimeEngineNetworkBindingTests`

Expected: tests fail because Engine does not prepare or own bindings.

- [ ] **Step 3: Integrate prepared definitions without losing existing Engine edits**

Create one `NetworkAdapterResolver` in `GatewayHost`, expose it through `IGatewayHost.NetworkAdapters`, and pass it to the `RuntimeEngine` constructor. In `StartDevice`, prepare primary and secondary definitions before factory creation:

```csharp
var prepared = NetworkBindingPlan.Prepare(ch, eff, _networkAdapters);
IDeviceDriver driver = factory.CreateDevice(prepared.Channel, prepared.Device);

PreparedNetworkDevice? secondaryPrepared = null;
if (!string.IsNullOrWhiteSpace(secEp))
{
    var secondaryDef = CloneWithEndpoint(eff, secEp.Trim());
    secondaryPrepared = NetworkBindingPlan.Prepare(ch, secondaryDef, _networkAdapters);
    secondary = factory.CreateDevice(secondaryPrepared.Channel, secondaryPrepared.Device);
}
```

Extend `DeviceRuntime` with primary/secondary leases. In both `StopDeviceAsync` and `StopAsync`, dispose drivers first and then await relay leases. If factory creation fails, dispose the already-created lease in the catch path.

- [ ] **Step 4: Merge relay diagnostics ahead of generic driver transport errors**

```csharp
private static DeviceConnectionDetail? ConnectionDetail(DeviceRuntime d)
{
    var network = d.PrimaryBinding?.Snapshot();
    if (network is { Code: "AdapterUnavailable" })
        return new("NetworkAdapterUnavailable", network.Message);
    if (network is { Code: "ConnectFailure" })
        return new("NetworkConnectFailure", network.Message);
    return (d.Driver as IDeviceConnectionDiagnostics)?.ConnectionDetail()
        ?? network?.AsDeviceDetail();
}
```

Update `AnomalyService.RootDto` to prefer `d.Detail?.Message` over `Stats.LastError`, so the monitoring center shows source adapter, source IPv4, target endpoint, and failing stage.

- [ ] **Step 5: Run RuntimeEngine and reconnect tests and verify GREEN**

Run: `dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter "FullyQualifiedName~RuntimeEngineNetworkBindingTests|FullyQualifiedName~RuntimeEngineReconnectTests|FullyQualifiedName~DeviceLoopTransientReadTests"`

Expected: relay lifecycle tests pass and the existing disconnect-threshold behavior remains green.

- [ ] **Step 6: Commit Engine integration**

```powershell
git add src/IGW.Core/Engine.cs src/IGW.Core/IGatewayHost.cs src/IGW.Runtime/GatewayHost.cs tests/IGW.Tests/RuntimeEngineNetworkBindingTests.cs src/IGW.Northbound.RestApi/AnomalyService.cs
git commit -m "feat(runtime): apply channel network binding to devices"
```

---

### Task 5: Expose Server Adapters and Replace Free Text with a Stable Select

**Files:**
- Modify: `src/IGW.Northbound.RestApi/IgwRestApi.cs`
- Modify: `src/IGW.WebUi/src/api.ts`
- Create: `src/IGW.WebUi/src/utils/networkAdapterSelection.ts`
- Create: `src/IGW.WebUi/src/utils/networkAdapterSelection.test.cjs`
- Modify: `src/IGW.WebUi/src/views/Project.vue`
- Modify: `src/IGW.WebUi/src/i18n/catalog.ts`
- Create: `tests/IGW.Tests/NetworkAdapterApiTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/network-adapters` and `api.networkAdapters()` returning `{ id, name, ipv4, prefixLength, label }[]`.
- Consumes: existing channel settings and the adapter resolver from Task 1.

- [ ] **Step 1: Write failing API and Web helper tests**

```csharp
[Fact]
public async Task Adapter_api_returns_only_runtime_server_ipv4_rows()
{
    await using var app = TestRuntime.WithAdapters(new NetworkAdapterInfo("nic-1", "PLC LAN", "10.10.0.5", 24));
    var rows = await app.Client.GetFromJsonAsync<NetworkAdapterInfo[]>("/api/v1/network-adapters");
    var row = Assert.Single(rows!);
    Assert.Equal("PLC LAN — 10.10.0.5", row.Label);
}
```

```javascript
test('missing saved adapter is retained as unavailable and never replaced by default', () => {
  const options = selection.adapterOptions([], {
    networkAdapter: 'Old PLC LAN', networkAdapterId: 'old', networkAdapterIpv4: '10.1.0.20'
  })
  assert.equal(options[1].unavailable, true)
  assert.equal(options[1].value, 'old|10.1.0.20')
})

test('selection writes stable id name and IPv4 together', () => {
  const settings = selection.applyAdapterSelection({}, {
    id: 'nic-1', name: 'PLC LAN', ipv4: '10.10.0.5', prefixLength: 24, label: 'PLC LAN — 10.10.0.5'
  })
  assert.deepEqual(settings, {
    networkAdapter: 'PLC LAN', networkAdapterId: 'nic-1', networkAdapterIpv4: '10.10.0.5'
  })
})
```

- [ ] **Step 2: Run API and Web tests and verify RED**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~NetworkAdapterApiTests
node --test src/IGW.WebUi/src/utils/networkAdapterSelection.test.cjs
```

Expected: endpoint/helper missing failures.

- [ ] **Step 3: Add the read-only Runtime API and typed Web client**

```csharp
app.MapGet("/api/v1/network-adapters", () => Results.Ok(host.NetworkAdapters.Available()));
```

```typescript
export interface NetworkAdapterDto {
  id: string; name: string; ipv4: string; prefixLength: number; label: string
}

networkAdapters: () =>
  http.get<NetworkAdapterDto[]>('/api/v1/network-adapters').then((r) => r.data),
```

Expose the catalog through `IGatewayHost` or a read-only GatewayHost property; do not enumerate browser-local adapters.

- [ ] **Step 4: Implement pure selection helpers and both Web selects**

Replace the channel wizard's `allow-create` select and the channel property's free-text input with adapter-backed selects. Load rows when opening either UI. Include:

```vue
<el-option value="" :label="$t('默认值（由操作系统选择）')" />
<el-option
  v-for="adapter in adapterOptions"
  :key="adapter.value"
  :value="adapter.value"
  :label="adapter.unavailable ? `${adapter.label}（${$t('不可用')}）` : adapter.label"
  :disabled="adapter.unavailable"
/>
```

The saved unavailable option must remain visible and selected. Selecting default removes `networkAdapterId` and `networkAdapterIpv4` and stores `networkAdapter='默认值'`. Apply the same step to OPC UA Client instead of skipping the network-adapter step.

- [ ] **Step 5: Add Chinese and English strings**

Add exact translations for “默认值（由操作系统选择）”, “配置的网卡当前不可用”, “网卡名称 — IPv4”, “本地源地址”, “目标地址”, and “连接阶段”. Remove the old hint claiming the field is “仅记录”.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --filter FullyQualifiedName~NetworkAdapterApiTests
node --test src/IGW.WebUi/src/utils/networkAdapterSelection.test.cjs
npm --prefix src/IGW.WebUi run typecheck
```

Expected: API tests, Web helper tests, and Vue typecheck pass.

- [ ] **Step 7: Commit API and UI**

```powershell
git add src/IGW.Northbound.RestApi/IgwRestApi.cs src/IGW.WebUi/src/api.ts src/IGW.WebUi/src/utils/networkAdapterSelection.ts src/IGW.WebUi/src/utils/networkAdapterSelection.test.cjs src/IGW.WebUi/src/views/Project.vue src/IGW.WebUi/src/i18n/catalog.ts tests/IGW.Tests/NetworkAdapterApiTests.cs
git commit -m "feat(web): select server adapter for TCP channels"
```

---

### Task 6: Full Regression and Release Evidence

**Files:**
- Modify only files required by failures directly caused by Tasks 1-5.

**Interfaces:**
- Consumes all previous tasks.
- Produces verified source, Web bundle, and runtime build; installer packaging is performed only when requested after code verification.

- [ ] **Step 1: Run all backend tests**

Run: `dotnet test IGW.slnx --configuration Release --no-restore`

Expected: zero failed tests. Existing external-feed vulnerability warnings are reported separately and are not described as fixed.

- [ ] **Step 2: Run all Web tests**

Run: `node --test src/IGW.WebUi/src/**/*.test.cjs`

Expected: zero failed tests, including adapter persistence and unavailable-item coverage.

- [ ] **Step 3: Run Web typecheck and production build**

Run:

```powershell
npm --prefix src/IGW.WebUi run typecheck
npm --prefix src/IGW.WebUi run build
```

Expected: both commands exit 0; chunk-size warnings are recorded but are not test failures.

- [ ] **Step 4: Build the full solution**

Run: `dotnet build IGW.slnx --configuration Release --no-restore`

Expected: exit 0.

- [ ] **Step 5: Perform a local two-address integration check**

Run the relay integration tests without filters and inspect their complete output:

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~NetworkAdapter|FullyQualifiedName~BoundTcpRelay|FullyQualifiedName~NetworkBinding"
```

Expected: the captured echo-server peer address equals the selected local IPv4, unavailable adapters never reach the target, runtime definitions contain loopback endpoints, and project definitions retain real endpoints.

- [ ] **Step 6: Inspect the final diff and worktree ownership**

Run:

```powershell
git diff --check
git status --short
git diff -- src/IGW.Core/Engine.cs src/IGW.Northbound.Mes/MesUploadEngine.cs installer/version.txt
```

Expected: no whitespace errors; the pre-existing Engine/MES/installer edits remain present and are not accidentally reverted or included in unrelated commits.

- [ ] **Step 7: Commit any verification-only correction**

Only if a Task 1-5 regression required a correction:

```powershell
git add <only-the-corrected-feature-files>
git commit -m "fix: complete multi-NIC binding regression"
```

Do not commit `installer/version.txt` or unrelated existing edits as part of this feature.
