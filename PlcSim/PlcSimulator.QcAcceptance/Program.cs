using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IGW.Abstractions;
using IGW.Drivers.IoTClient;
using PlcSimulator.Core;
using PlcSimulator.Import;
using PlcSimulator.Protocols;

if (args.Length == 0) throw new ArgumentException("Pass project JSON path and optional --all");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
options.Converters.Add(new JsonStringEnumConverter());
using var json = JsonDocument.Parse(File.ReadAllText(args[0]).TrimStart('\uFEFF'));
var entities = json.RootElement.GetProperty("igw").GetProperty("entities");
var nodes = new Dictionary<string, (string? Parent, string Name)>();
foreach (string kind in new[] { "channels", "devices", "groups" })
foreach (var entity in entities.GetProperty(kind).EnumerateArray())
    nodes[entity.GetProperty("id").GetString()!] = (entity.TryGetProperty("parentId", out var parent) ? parent.GetString() : null,
        entity.GetProperty("document").GetProperty("name").GetString()!);
string PathOf(string id) { var node = nodes[id]; return node.Parent != null && nodes.ContainsKey(node.Parent) ? PathOf(node.Parent) + "." + node.Name : node.Name; }
var tags = entities.GetProperty("tags").EnumerateArray().Select(entity =>
{
    var def = entity.GetProperty("document").Deserialize<TagDef>(options)!;
    return (Path: PathOf(entity.GetProperty("parentId").GetString()!) + "." + def.Name, Def: def);
}).ToArray();
var imported = KepJsonSimulationImporter.ImportFile(args[0]);
var store = new SimulationValueStore(imported.Project);
int badTotal = 0;
int selectedDeviceIndex = Array.IndexOf(args, "--device");
foreach (var device in imported.Project.Devices.Where(d => selectedDeviceIndex >= 0 ? d.Name == args[selectedDeviceIndex + 1] : args.Contains("--all") || d.Name is "PLC1" or "PLC2"))
{
    var source = entities.GetProperty("devices").EnumerateArray().Single(e => e.GetProperty("document").GetProperty("name").GetString() == device.Name);
    var definition = source.GetProperty("document").Deserialize<DeviceDef>(options)!;
    var channel = entities.GetProperty("channels").EnumerateArray().Single(e => e.GetProperty("id").GetString() == source.GetProperty("parentId").GetString())
        .GetProperty("document").Deserialize<ChannelDef>(options)!;
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    if (args.Contains("--configured-endpoints"))
    {
        var parts = definition.Endpoint.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !IPAddress.IsLoopback(ip))
            throw new Exception("Configured endpoint acceptance only permits explicit loopback IP:port");
        port = int.Parse(parts[1]);
        if (device.Endpoints.Single(e => e.Protocol == SimulationProtocol.SiemensS7).ListenPort != port)
            throw new Exception($"{device.Name}: simulator listener differs from QC endpoint");
    }
    else { definition.Endpoint = args.Contains("--settings-port") ? "127.0.0.1" : $"127.0.0.1:{port}"; definition.Settings["port"] = port.ToString(); }
    await using var server = new SiemensClassicSimulationServer(device, store, port);
    server.Start();
    await using var driver = new IoTSiemensDriverFactory().CreateDevice(channel, definition);
    var bindings = tags.Where(t => t.Path.StartsWith(channel.Name + "." + definition.Name + ".", StringComparison.Ordinal)).Select(t =>
    {
        TagAddress address; string? error;
        bool parsed = driver.AddressParser is ITagDefinitionAddressParser full ? full.TryParse(t.Def, out address, out error)
            : driver.AddressParser.TryParse(t.Def.Address, t.Def.DataType, out address, out error);
        if (!parsed) throw new Exception(t.Path + ": " + error);
        return new TagBinding { TagId = t.Path, Def = t.Def, Address = address };
    }).ToArray();
    int selectedAddressIndex = Array.IndexOf(args, "--address");
    if (selectedAddressIndex >= 0) bindings = bindings.Where(b => b.Def.Address == args[selectedAddressIndex + 1]).ToArray();
    if (bindings.Length == 0) throw new Exception("No matching point in project");
    int dbRootIndex = Array.IndexOf(args, "--db-root");
    if (dbRootIndex >= 0 && Directory.Exists(Path.Combine(args[dbRootIndex + 1], device.Name + "DB")))
    {
        var dbImport = SiemensDbSimulationImporter.ImportFiles(Directory.GetFiles(Path.Combine(args[dbRootIndex + 1], device.Name + "DB"), "*.db"));
        var dbTags = dbImport.Project.EnumerateTags().ToDictionary(t => Regex.Match(t.ClassicAddress, @"^DB\d+\.DB[XBWD]\d+(?:\.\d+)?").Value);
        var mismatches = new List<string>();
        foreach (var binding in bindings)
        {
            if (!dbTags.TryGetValue(binding.Def.Address, out var expected)) { mismatches.Add(binding.TagId + ": address absent from DB source"); continue; }
            bool stringType = binding.Def.DataType is IgwDataType.String or IgwDataType.WString;
            if (stringType && (expected.StringLength != binding.Def.StringLength || expected.IsWideString != (binding.Def.DataType == IgwDataType.WString)))
                mismatches.Add(binding.TagId + ": text type/capacity differs from DB source");
        }
        Console.WriteLine($"{device.Name} DB comparison: files={Directory.GetFiles(Path.Combine(args[dbRootIndex + 1], device.Name + "DB"), "*.db").Length}, fields={dbTags.Count}, mismatches={mismatches.Count}");
        foreach (string mismatch in mismatches.Take(10)) Console.WriteLine("  " + mismatch);
        if (mismatches.Count > 0) badTotal += mismatches.Count;
    }
    await driver.ConnectAsync(CancellationToken.None);
    var samples = await driver.ReadAsync(bindings, CancellationToken.None);
    var bad = samples.Where(s => !s.Value.IsGood).ToArray(); badTotal += bad.Length;
    Console.WriteLine($"{device.Name}: tags={bindings.Length}, samples={samples.Count}, Good={samples.Count - bad.Length}, Bad={bad.Length}");
    foreach (var group in bad.GroupBy(s => bindings.Single(b => b.TagId == s.TagId).Def.DataType)) Console.WriteLine($"  {group.Key}: Bad={group.Count()}");
    foreach (var sample in bad.Take(8)) Console.WriteLine($"  {sample.TagId}: {sample.Value.Quality}");
    if (args.Contains("--read-only"))
    {
        foreach (var binding in bindings)
            Console.WriteLine($"PROBE {binding.TagId}: address={binding.Def.Address}, bytes={ProtocolStorageSize.Estimate(binding.Def, binding.Address)}, type={binding.Def.DataType}, capacity={binding.Def.StringLength}, quality={samples.Single(s => s.TagId == binding.TagId).Value.Quality}");
        await driver.DisconnectAsync();
        continue;
    }
    int textWrites = 0;
    foreach (var binding in bindings.Where(t => !t.Def.IsArray && t.Def.DataType is IgwDataType.String or IgwDataType.WString))
    {
        string text = binding.Def.DataType == IgwDataType.WString ? "错误Ω中文" :
            binding.Def.StringLength.GetValueOrDefault(254) >= 16 ? "物料-" + textWrites : "QC-" + textWrites;
        if (text.Length > binding.Def.StringLength.GetValueOrDefault(254)) text = text[..binding.Def.StringLength!.Value];
        var written = await driver.WriteAsync([new WriteRequest { TagId = binding.TagId, Def = binding.Def, Address = binding.Address, Value = text }], CancellationToken.None);
        var readback = (await driver.ReadAsync([binding], CancellationToken.None)).Single();
        if (written.Any(w => !w.Status.IsGood) || !readback.Value.IsGood || !Equals(readback.Value.Value, text))
            throw new Exception($"Text write/readback failed: {binding.TagId}, value={readback.Value.Value}, status={readback.Value.Quality}");
        var simulated = device.EnumerateTags().First(t => t.ProtocolAddress == binding.Def.Address && t.Name == binding.Def.Name);
        for (int retry = 0; retry < 100 && store.Read<string>(simulated.Id).Value != text; retry++) await Task.Delay(10);
        if (store.Read<string>(simulated.Id).Value != text) throw new Exception("Simulator text differs from QC: " + binding.TagId);
        textWrites++;
    }
    var beforeById = samples.ToDictionary(s => s.TagId);
    var bindingsById = bindings.ToDictionary(b => b.TagId);
    var after = await driver.ReadAsync(bindings, CancellationToken.None);
    int damaged = after.Count(s => !s.Value.IsGood);
    int numericChanged = after.Count(s => !bindingsById[s.TagId].Def.IsArray && bindingsById[s.TagId].Def.DataType is not (IgwDataType.String or IgwDataType.WString) &&
        !Equals(s.Value.Value, beforeById[s.TagId].Value.Value));
    Console.WriteLine($"{device.Name}: text roundtrips={textWrites}, bad after writes={damaged}, adjacent numeric changes={numericChanged}");
    if (damaged > 0 || numericChanged > 0) throw new Exception("Text writes damaged neighboring fields");
    int arrayWrites = 0;
    foreach (var binding in bindings.Where(b => b.Def.IsArray && b.Def.Array?.Struct == null))
    {
        var shape = binding.Def.Array!.Shape;
        int count = checked((int)shape.ElementCount);
        ArrayRuntimeValue value = binding.Def.Array.ElementType switch
        {
            IgwDataType.Float => PrimitiveArrayValue<float>.Create(shape, Enumerable.Range(0, count).Select(i => i + 0.25f).ToArray()),
            IgwDataType.Int16 => PrimitiveArrayValue<short>.Create(shape, Enumerable.Range(0, count).Select(i => (short)(i + 1)).ToArray()),
            IgwDataType.String => PrimitiveArrayValue<string>.Create(shape, Enumerable.Range(0, count).Select(i => "QC-" + (i + 1)).ToArray()),
            _ => throw new Exception("Unsupported acceptance array type: " + binding.Def.Array.ElementType),
        };
        var written = await ((IArrayCapableDriver)driver).WriteArrayAsync(new ArrayWriteRequest(binding,
            new ArraySlice(shape.LowerBounds, shape.Dimensions), value), CancellationToken.None);
        var readback = (ArrayRuntimeValue)(await driver.ReadAsync([binding], CancellationToken.None)).Single().Value.Value!;
        if (!written.Status.IsGood || !readback.OverallQuality.IsGood || Enumerable.Range(0, count).Any(i => !Equals(value.GetBoxedValue(i), readback.GetBoxedValue(i))))
            throw new Exception("QC array write/readback failed: " + binding.TagId);
        arrayWrites++;
    }
    var finalSamples = await driver.ReadAsync(bindings, CancellationToken.None);
    if (finalSamples.Any(s => !s.Value.IsGood)) throw new Exception("Bad quality after array writes");
    Console.WriteLine($"{device.Name}: array roundtrips={arrayWrites}, final Good={finalSamples.Count}, final Bad=0");
    await driver.DisconnectAsync();
}
Console.WriteLine($"TOTAL BAD={badTotal}");
Environment.ExitCode = badTotal == 0 ? 0 : 1;
