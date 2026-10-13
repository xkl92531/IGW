using System.Runtime.InteropServices;
using System.Text;
using PlcSimulator;
using PlcSimulator.Core;
using PlcSimulator.Import;
using PlcSimulator.Protocols;

if (args.Length == 2 && args[0] == "--startup-json")
{
    await ImportedProjectAcceptance.RunStartupAsync();
    return;
}

if (args.Length == 2 && args[0] == "--import-json")
{
    await ImportedProjectAcceptance.RunAsync(args[1]);
    return;
}

try { await ArrayRegressionTests.RunAsync(); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; return; }
if (args.Contains("--arrays-only")) return;

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected))
        throw new InvalidOperationException($"sequence: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
}

static void AssertContains(string text, string expected, string name)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: missing '{expected}'");
}

static void AssertNotContains(string text, string unexpected, string name)
{
    if (text.Contains(unexpected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: still contains '{unexpected}'");
}

var buffer = new TrafficLogBuffer(3);
foreach (string line in new[] { "1", "2", "3", "4", "5" }) buffer.Enqueue(line);
var batch = buffer.Drain(10);
AssertSequence(batch.Lines, "3", "4", "5");
AssertEqual(2, batch.Dropped, "dropped oldest entries");
AssertEqual(0, buffer.Count, "count after full drain");

foreach (string line in new[] { "a", "b", "c" }) buffer.Enqueue(line);
var partial = buffer.Drain(2);
AssertSequence(partial.Lines, "a", "b");
AssertEqual(1, buffer.Count, "count after partial drain");
AssertSequence(buffer.Drain(10).Lines, "c");

var concurrent = new TrafficLogBuffer(64);
Parallel.For(0, 10_000, i => concurrent.Enqueue(i.ToString()));
if (concurrent.Count > 64)
    throw new InvalidOperationException($"parallel capacity exceeded: {concurrent.Count}");

Console.WriteLine("All traffic log buffer tests passed.");

var shutdownCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var shutdownCoordinator = new AsyncShutdownCoordinator();
Task shutdownTask = shutdownCoordinator.RunAsync(() => shutdownCleanup.Task);
AssertEqual(true, shutdownCoordinator.IsStarted, "async shutdown starts once");
AssertEqual(false, shutdownCoordinator.IsCompleted, "async shutdown remains pending while cleanup runs");
AssertEqual(false, shutdownTask.IsCompleted, "async shutdown must not synchronously block the caller");
shutdownCleanup.SetResult();
await shutdownTask.WaitAsync(TimeSpan.FromSeconds(2));
AssertEqual(true, shutdownCoordinator.IsCompleted, "async shutdown completes after cleanup");
AssertEqual(false, await shutdownCoordinator.RunAsync(() => Task.CompletedTask), "duplicate shutdown is ignored");
Console.WriteLine("All asynchronous simulator shutdown tests passed.");

var sourceDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (sourceDirectory != null && !Directory.Exists(Path.Combine(sourceDirectory.FullName, "PlcSim", "PlcSimulator"))) sourceDirectory = sourceDirectory.Parent;
string sourceRoot = Path.Combine(sourceDirectory?.FullName ?? throw new DirectoryNotFoundException("Simulator source root not found"), "PlcSim", "PlcSimulator");
string windowSource = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
string windowXaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
string projectSource = File.ReadAllText(Path.Combine(sourceRoot, "PlcSimulator.csproj"));
string nativeServerSource = File.ReadAllText(Path.Combine(sourceRoot, "..", "native", "igw-s7plus-server", "src", "lib.rs"));

AssertNotContains(windowSource, "Dispatcher.BeginInvoke", "traffic callback must not dispatch per packet");
AssertContains(windowSource, "_trafficLogs.Enqueue(entry.ToString())", "traffic callback must enqueue after filtering");
AssertContains(windowSource, "_trafficLogs.Drain(100)", "UI log flush batch limit");
AssertContains(windowSource, "TabDbs.SelectedItem is not TabItem", "selected DB lookup");
AssertContains(windowSource, "_rowsByDb.TryGetValue", "selected DB rows");
AssertContains(windowSource, "if (!IsSiemensRunning) return;", "OPC-only refresh must not read stopped S7 server");
AssertNotContains(windowSource, "foreach (var row in _rows.Values)", "must not refresh every DB row");
AssertContains(windowXaml, "x:Name=\"ChkLogRead\" Content=\"记录读\" IsChecked=\"False\"", "read log default");
AssertEqual(6, windowXaml.Split("LogFilter_Changed", StringSplitOptions.None).Length - 1, "checked and unchecked filter event bindings");
AssertNotContains(windowXaml, "x:Name=\"ModelBox\"", "Siemens protocol must not require manual model selection");
AssertContains(windowXaml, "x:Name=\"DetectedSiemensProfileText\"", "detected Siemens protocol summary");
AssertContains(windowXaml, "x:Name=\"DetectedSiemensEvidenceText\"", "detected Siemens protocol evidence");
AssertContains(windowSource, "SiemensClassicSimulationServer", "imported Classic project server integration");
AssertContains(windowSource, "SiemensPlusSimulationServer", "imported S7 Plus project server integration");
AssertContains(windowSource, "匿名、无需证书配置", "S7 Plus user-facing anonymous mode");
AssertContains(windowSource, "UpdateDetectedSiemensProfile", "detected Siemens profile UI refresh");
AssertContains(projectSource, "igw_plcsim_s7plus.dll", "S7 Plus native transport is packaged");
AssertContains(nativeServerSource, "TcpListener::bind((\"127.0.0.1\", port))", "S7 Plus simulator is loopback only");
AssertContains(nativeServerSource, ".with_no_client_auth()", "S7 Plus simulator accepts anonymous clients");

Console.WriteLine("All UI backpressure source contracts passed.");

static byte[] Packet(byte cotpType, byte function = 0)
{
    var data = new byte[19];
    data[0] = 0x03;
    data[5] = cotpType;
    data[7] = 0x32;
    data[8] = 0x01;
    data[18] = function;
    return data;
}

AssertEqual(TrafficKind.Read, PlcSimServer.ClassifyTraffic(Packet(0xF0, 0x04)), "read classification");
AssertEqual(TrafficKind.Write, PlcSimServer.ClassifyTraffic(Packet(0xF0, 0x05)), "write classification");
AssertEqual(TrafficKind.Connect, PlcSimServer.ClassifyTraffic(Packet(0xE0)), "connect classification");
AssertEqual(TrafficKind.Other, PlcSimServer.ClassifyTraffic(Packet(0xF0, 0x07)), "other classification");
AssertEqual(TrafficKind.None, PlcSimServer.ClassifyTraffic(Array.Empty<byte>()), "invalid packet classification");

Console.WriteLine("All traffic classification tests passed.");

var project = new SimulationProject("fixture");
var device = project.AddDevice("PLC1");
var group = device.AddGroup("DB2100");
var flag = group.AddTag("MESFlag", SimulationDataType.UInt16, 0);
var temperatures = group.AddTag("Temperature", SimulationDataType.Float32, new float[] { 1.5f, 2.5f }, dimensions: new[] { 2 });
var structure = group.AddGroup("CellData_0");
var isNg = structure.AddTag("IsNG", SimulationDataType.Byte, (byte)0);

AssertEqual("PLC1.DB2100.MESFlag", flag.SymbolPath, "stable symbol path");
AssertEqual("PLC1.DB2100.CellData_0.IsNG", isNg.SymbolPath, "nested symbol path");
AssertEqual(2, temperatures.Dimensions.Single(), "array dimension");

device.SiemensProfile = SiemensSimulationProfile.Hybrid;
device.ProtocolEvidence = "1 absolute + 1 symbolic";
flag.ClassicAddress = "DB2100.DBW4";
flag.SymbolicAddress = "DB2100.MESFlag";
AssertEqual("DB2100.DBW4", flag.AddressFor(SimulationProtocol.SiemensS7), "classic address projection");
AssertEqual("DB2100.MESFlag", flag.AddressFor(SimulationProtocol.SiemensS7Plus), "plus address projection");

var store = new SimulationValueStore(project);
var changes = new List<SimulationValueChanged>();
store.ValueChanged += changes.Add;
AssertEqual((ushort)0, store.Read<ushort>(flag.Id).Value, "initial typed value");
var written = store.Write(flag.Id, "42", SimulationWriteSource.UserInterface);
AssertEqual((ushort)42, store.Read<ushort>(flag.Id).Value, "converted UInt16 value");
AssertEqual(1L, written.Version, "first write version");
AssertEqual(1, changes.Count, "change notification count");
AssertEqual(SimulationWriteSource.UserInterface, changes[0].Source, "change source");

bool rejected = false;
try { store.Write(isNg.Id, 256, SimulationWriteSource.Protocol); }
catch (SimulationValueException) { rejected = true; }
AssertEqual(true, rejected, "byte range rejection");
AssertEqual((byte)0, store.Read<byte>(isNg.Id).Value, "rejected write keeps old value");

var removable = project.AddDevice("Removable");
var removableRoot = removable.AddGroup("Root");
var removableChild = removableRoot.AddGroup("Child");
AssertEqual(true, removableRoot.RemoveGroup(removableChild), "nested group can be removed");
AssertEqual(0, removableRoot.Groups.Count, "nested group removed from model");
AssertEqual(true, removable.RemoveGroup(removableRoot), "root group can be removed");
AssertEqual(0, removable.Groups.Count, "root group removed from model");
AssertEqual(true, project.RemoveDevice(removable), "device can be removed");
AssertEqual(false, project.Devices.Any(x => x.Name == "Removable"), "device removed from model");

Parallel.For(0, 2000, i => store.Write(flag.Id, i % 100, SimulationWriteSource.Protocol));
AssertEqual(2001L, store.Read<ushort>(flag.Id).Version, "atomic version increments");

Console.WriteLine("All shared simulation model tests passed.");

const string kepFixture = """
{"project":{"channels":[{"common.ALLTYPES_NAME":"Channel1","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"Omron NJ Ethernet","devices":[{"common.ALLTYPES_NAME":"PLC1","tags":[{"common.ALLTYPES_NAME":"Counter","servermain.TAG_ADDRESS":"Counter","servermain.TAG_DATA_TYPE":6},{"common.ALLTYPES_NAME":"Ready","servermain.TAG_ADDRESS":"Status/31","servermain.TAG_DATA_TYPE":6}]}]},{"common.ALLTYPES_NAME":"Channel2","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"Omron NJ Ethernet","devices":[{"common.ALLTYPES_NAME":"PLC2","tags":[{"common.ALLTYPES_NAME":"Counter","servermain.TAG_ADDRESS":"Counter","servermain.TAG_DATA_TYPE":6}]}]}]}}
""";
var kepImport = KepJsonSimulationImporter.ImportJson(kepFixture);
AssertEqual(2, kepImport.ChannelCount, "KEP channel count");
AssertEqual(2, kepImport.Project.Devices.Count, "KEP device count");
AssertEqual(3, kepImport.Project.EnumerateTags().Count(), "KEP tag count");
var plc1 = kepImport.Project.Devices.Single(x => x.Name == "PLC1");
AssertEqual("Channel1", plc1.SourceChannelName, "KEP channel hierarchy");
AssertEqual(SimulationProtocol.OmronCip, plc1.Endpoints.Single().Protocol, "KEP Omron driver mapping");
AssertEqual(44818, plc1.Endpoints.Single().RequestedPort, "KEP source port");
AssertEqual(true, plc1.EnumerateTags().Any(x => x.Name == "Ready" && x.DataType == SimulationDataType.Boolean), "KEP bit tag mapping");
AssertEqual(2, kepImport.Project.Devices.SelectMany(x => x.Endpoints).Select(x => x.ListenPort).Distinct().Count(), "independent local ports");

const string unknownKep = """
{"project":{"channels":[{"common.ALLTYPES_NAME":"UnknownChannel","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"Vendor X","devices":[{"common.ALLTYPES_NAME":"UnknownDevice","servermain.DEVICE_ID_STRING":"10.0.0.1","tags":[{"common.ALLTYPES_NAME":"Mystery","servermain.TAG_ADDRESS":"X1","servermain.TAG_DATA_TYPE":999}]}]}]}}
""";
var unknownImport = KepJsonSimulationImporter.ImportJson(unknownKep);
AssertEqual(SimulationProtocol.Unknown, unknownImport.Project.Devices.Single().Endpoints.Single().Protocol, "unknown driver retained");
AssertEqual(SimulationDataType.Unknown, unknownImport.Project.EnumerateTags().Single().DataType, "unknown type retained");
AssertEqual(true, unknownImport.Warnings.Count >= 2, "unknown import warnings");

AssertEqual(SiemensSimulationProfile.Classic,
    SiemensProtocolClassifier.Detect("Siemens TCP/IP Ethernet", new[] { "DB1.DBW0", "M0.0" }).Profile,
    "KEP absolute profile");
AssertEqual(SiemensSimulationProfile.Plus,
    SiemensProtocolClassifier.Detect("Siemens S7 Plus", new[] { "Recipe.Header.Code" }).Profile,
    "KEP explicit plus profile");
AssertEqual(SiemensSimulationProfile.Hybrid,
    SiemensProtocolClassifier.Detect("Siemens", new[] { "DB1.DBW0", "Recipe.Header.Code" }).Profile,
    "KEP mixed profile");
const string hybridKep = """
{"project":{"channels":[{"common.ALLTYPES_NAME":"S7","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"Siemens TCP/IP Ethernet","devices":[{"common.ALLTYPES_NAME":"PLC","tags":[{"common.ALLTYPES_NAME":"Absolute","servermain.TAG_ADDRESS":"DB1.DBW0","servermain.TAG_DATA_TYPE":4},{"common.ALLTYPES_NAME":"Symbolic","servermain.TAG_ADDRESS":"Recipe.Header.Code","servermain.TAG_DATA_TYPE":4}]}]}]}}
""";
var hybridImport = KepJsonSimulationImporter.ImportJson(hybridKep);
var hybridDevice = hybridImport.Project.Devices.Single();
AssertEqual(SiemensSimulationProfile.Hybrid, hybridDevice.SiemensProfile, "KEP importer hybrid profile");
AssertEqual(2, hybridDevice.Endpoints.Count, "KEP hybrid endpoints");
AssertEqual(1, hybridDevice.Endpoints.Select(x => x.ListenPort).Distinct().Count(), "KEP hybrid shares one port");
AssertEqual("DB1.DBW0", hybridDevice.EnumerateTags().Single(x => x.Name == "Absolute").ClassicAddress, "KEP classic address projection");
AssertEqual("Recipe.Header.Code", hybridDevice.EnumerateTags().Single(x => x.Name == "Symbolic").SymbolicAddress, "KEP symbolic address projection");

Console.WriteLine("All native KEP JSON import tests passed.");

const string optimizedVarDb = """
DATA_BLOCK "DB2100_电芯OCV_DataDB"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Request : Bool; // 请求
      PLCFlag : Int; // PLC标志位
      MESFlag : Int; // MES标志位
      CellData : Array[0..10] of Struct
         DMCCode : String[50]; // 条码
         KValue : Real; // K值
         Voltage : Real; // 电压值
         ErrorMessage : String[100]; // MES返回NG原因
         IsNG : Int; // 电芯状态
         Errorinfo : String[100]; // 异常信息
      END_STRUCT;
      Temperature : Real; // 环境温度
   END_VAR
BEGIN
END_DATA_BLOCK
""";
var dbImport = SiemensDbSimulationImporter.ImportText(optimizedVarDb.Replace("TRUE", "FALSE"));
AssertEqual("DB2100_电芯OCV_DataDB", dbImport.DataBlockName, "Siemens DB name");
AssertEqual(2100, dbImport.DbNumber, "Siemens DB number");
AssertEqual(false, dbImport.OptimizedAccess, "Siemens DB optimized flag");
AssertEqual(70, dbImport.Project.EnumerateTags().Count(), "Siemens nested structure tag count");
var importedIsNg = dbImport.Project.EnumerateTags().Single(x => x.SymbolPath.EndsWith("CellData_0.IsNG", StringComparison.Ordinal));
AssertEqual("DB2100.DBW168", importedIsNg.ProtocolAddress, "Siemens nested structure absolute address");
AssertEqual(SiemensSimulationProfile.Classic, dbImport.Project.Devices.Single().SiemensProfile, "nonoptimized profile");
AssertEqual(SimulationProtocol.SiemensS7, dbImport.Project.Devices.Single().Endpoints.Single().Protocol, "nonoptimized endpoint");
AssertEqual("DB2100.DBW168", importedIsNg.ClassicAddress, "classic address retained");
AssertEqual("DB2100_电芯OCV_DataDB.CellData[0].IsNG", importedIsNg.SymbolicAddress, "symbol path retained");
AssertEqual(true, importedIsNg.Description.Contains("电芯状态", StringComparison.Ordinal), "Siemens Chinese comment retained");
AssertEqual(true, dbImport.Project.Devices.Single().Groups.Single().Groups.Any(x => x.Name == "CellData_10"), "Siemens structure array expanded");

Console.WriteLine("All Siemens DB import tests passed.");

var optimizedVarImport = SiemensDbSimulationImporter.ImportText(optimizedVarDb);
AssertEqual(true, optimizedVarImport.OptimizedAccess, "optimized VAR DB flag");
AssertEqual(70, optimizedVarImport.Project.EnumerateTags().Count(), "optimized VAR DB tag count");
AssertEqual(11, optimizedVarImport.Project.Devices.Single().Groups.Single().Groups.Count, "optimized VAR DB structure array groups");
AssertEqual(true, optimizedVarImport.Project.EnumerateTags().Any(x => x.Name == "Temperature"), "optimized VAR DB trailing field");
AssertEqual(true, optimizedVarImport.Project.EnumerateTags().Where(x => x.Name == "DMCCode").All(x => x.Description == "条码"), "optimized VAR DB Chinese comment");
AssertEqual(true, optimizedVarImport.Project.EnumerateTags().Where(x => x.Name == "Errorinfo").All(x => x.DataType == SimulationDataType.String), "optimized VAR DB string field");
AssertEqual(SiemensSimulationProfile.Plus, optimizedVarImport.Project.Devices.Single().SiemensProfile, "optimized profile");
AssertEqual(SimulationProtocol.SiemensS7Plus, optimizedVarImport.Project.Devices.Single().Endpoints.Single().Protocol, "optimized endpoint");
AssertEqual(true, optimizedVarImport.Project.EnumerateTags().All(x => x.SymbolicAddress.Length > 0), "optimized symbols generated");
AssertEqual(
    "DB2100_电芯OCV_DataDB.CellData[0].DMCCode",
    optimizedVarImport.Project.EnumerateTags().Single(x => x.SymbolPath.EndsWith("CellData_0.DMCCode", StringComparison.Ordinal)).SymbolicAddress,
    "optimized structure-array symbol keeps PLC bracket notation");

Console.WriteLine("All optimized VAR Siemens DB import tests passed.");

SimulationProject? multiPlusProject = null;
string batchRoot = Path.Combine(Path.GetTempPath(), "PlcSimulator-Batch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(batchRoot);
try
{
    string optimizedPath = Path.Combine(batchRoot, "DB2100.db");
    string classicPath = Path.Combine(batchRoot, "DB2101.db");
    string optimizedAlarmPath = Path.Combine(batchRoot, "DB2101-optimized.db");
    File.WriteAllText(optimizedPath, optimizedVarDb);
    File.WriteAllText(classicPath, """
DATA_BLOCK "DB2101_设备异常"
{ S7_Optimized_Access := 'FALSE' }
VAR
  PLCFlag : Int;
END_VAR
BEGIN
END_DATA_BLOCK
""");
    File.WriteAllText(optimizedAlarmPath, """
DATA_BLOCK "DB2101_设备异常"
{ S7_Optimized_Access := 'TRUE' }
VAR
  PLCFlag : Int;
  MESFlag : Int;
  ErrorMessage : String[100];
END_VAR
BEGIN
END_DATA_BLOCK
""");
    var batchImport = SiemensDbSimulationImporter.ImportFiles([optimizedPath, classicPath]);
    AssertEqual(2, batchImport.Project.Devices.Single().Groups.Count, "multi DB import keeps both DB roots");
    AssertEqual(71, batchImport.Project.EnumerateTags().Count(), "multi DB import keeps every tag");
    AssertEqual(SiemensSimulationProfile.Hybrid, batchImport.Project.Devices.Single().SiemensProfile, "multi DB import detects mixed protocol profile");
    AssertEqual(2, batchImport.Project.Devices.Single().Endpoints.Count, "multi DB import creates both S7 endpoints");
    AssertEqual(1, batchImport.Project.Devices.Single().Endpoints.Select(x => x.ListenPort).Distinct().Count(), "multi DB import keeps one configured Siemens port");
    multiPlusProject = SiemensDbSimulationImporter.ImportFiles([optimizedPath, optimizedAlarmPath]).Project;
    AssertEqual(SiemensSimulationProfile.Plus, multiPlusProject.Devices.Single().SiemensProfile, "multi optimized DB import detects Plus");
    AssertEqual(73, multiPlusProject.EnumerateTags().Count(), "multi optimized DB import keeps every tag");
}
finally
{
    Directory.Delete(batchRoot, true);
}

Console.WriteLine("All multi-DB Siemens import tests passed.");

var plusSymbols = SiemensPlusSimulationServer.Describe(optimizedVarImport.Project.Devices.Single());
AssertEqual(optimizedVarImport.Project.EnumerateTags().Count(), plusSymbols.Count, "S7 Plus schema leaf count");
AssertEqual(true, plusSymbols.All(x => x.Symbol.Length > 0 && x.Lids.Count > 0), "S7 Plus schema symbols and LIDs");
AssertEqual(true, plusSymbols.Any(x => x.Symbol.EndsWith(".IsNG", StringComparison.Ordinal)), "S7 Plus nested structure symbol");
var firstAlarmMember = SiemensPlusSimulationServer.Describe(multiPlusProject!.Devices.Single())
    .Single(x => x.Symbol == "DB2101_设备异常.PLCFlag");
AssertEqual((uint)2, firstAlarmMember.Lids.Single(), "S7 Plus first DB member avoids reserved type-info LID 1");
Console.WriteLine("All Siemens Plus schema tests passed.");

string? nativePlusDll = Environment.GetEnvironmentVariable("IGW_S7PLUS_DLL");
if (!string.IsNullOrWhiteSpace(nativePlusDll) && File.Exists(nativePlusDll))
{
    await RunNativePlusLoopback(nativePlusDll, optimizedVarImport.Project);
    await RunNativePlusLoopback(nativePlusDll, multiPlusProject!);
    Console.WriteLine("All IGW native S7 Plus real-protocol loopback tests passed.");
}
else Console.WriteLine("IGW native S7 Plus loopback skipped (IGW_S7PLUS_DLL not set).");

var classicDevice = dbImport.Project.Devices.Single();
var classicStore = new SimulationValueStore(dbImport.Project);
var classicPlcFlag = classicDevice.EnumerateTags().Single(x => x.Name == "PLCFlag");
classicStore.Write(classicPlcFlag.Id, (short)12, SimulationWriteSource.UserInterface);
int classicPort = FreeTcpPort();
await using (var classicServer = new SiemensClassicSimulationServer(classicDevice, classicStore, classicPort))
{
    classicServer.Start();
    var classicClient = new HslCommunication.Profinet.Siemens.SiemensS7Net(
        HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1")
    {
        Port = classicPort,
        Rack = 0,
        Slot = 1,
    };
    var connected = classicClient.ConnectServer();
    AssertEqual(true, connected.IsSuccess, "Siemens Classic loopback connect");
    AssertEqual((short)12, classicClient.ReadInt16("DB2100.DBW2").Content, "Siemens Classic initial read");
    AssertEqual(true, classicClient.Write("DB2100.DBW2", (short)66).IsSuccess, "Siemens Classic loopback write");
    await Task.Delay(150);
    AssertEqual((short)66, classicStore.Read<short>(classicPlcFlag.Id).Value, "Siemens Classic write reaches shared store");
    classicClient.ConnectClose();
}
Console.WriteLine("All Siemens Classic imported-project loopback tests passed.");

var opcDescriptors = OpcUaSimulationServer.Describe(project);
AssertEqual(true, opcDescriptors.Any(x => x.NodeId == "PLC1.DB2100.MESFlag" && !x.IsFolder && x.DataType == SimulationDataType.UInt16), "OPC UA variable descriptor");
AssertEqual(true, opcDescriptors.Any(x => x.NodeId == "PLC1.DB2100.CellData_0" && x.IsFolder), "OPC UA nested folder descriptor");
Console.WriteLine("All OPC UA address-space descriptor tests passed.");

var cipProject = new SimulationProject("cip-loopback");
var cipDevice = cipProject.AddDevice("NJ");
var cipGroup = cipDevice.AddGroup("Tags");
var cipTag = cipGroup.AddTag("Counter", SimulationDataType.Int32, 7);
cipTag.ProtocolAddress = "Counter";
var cipStore = new SimulationValueStore(cipProject);
int cipPort = FreeTcpPort();
await using (var cipServer = new OmronCipSimulationServer(cipDevice, cipStore, cipPort))
{
    cipServer.Start();
    var cipClient = new HslCommunication.Profinet.Omron.OmronCipNet("127.0.0.1", cipPort);
    var connected = cipClient.ConnectServer();
    AssertEqual(true, connected.IsSuccess, "Omron CIP loopback connect");
    var read = cipClient.ReadInt32("Counter", 1);
    AssertEqual(true, read.IsSuccess, "Omron CIP loopback read");
    AssertEqual(7, read.Content[0], "Omron CIP initial value");
    var write = cipClient.Write("Counter", 88);
    AssertEqual(true, write.IsSuccess, "Omron CIP loopback write");
    await Task.Delay(150);
    AssertEqual(88, cipStore.Read<int>(cipTag.Id).Value, "Omron CIP write reaches shared store");
    cipClient.ConnectClose();
}
Console.WriteLine("All Omron CIP real-protocol loopback tests passed.");

var finsProject = new SimulationProject("fins-loopback");
var finsDevice = finsProject.AddDevice("CJ");
var finsGroup = finsDevice.AddGroup("DM");
var finsTag = finsGroup.AddTag("Counter", SimulationDataType.Int16, (short)12);
finsTag.ProtocolAddress = "D100";
var finsStore = new SimulationValueStore(finsProject);
int finsPort = FreeTcpPort();
await using (var finsServer = new OmronFinsSimulationServer(finsDevice, finsStore, finsPort))
{
    finsServer.Start();
    var finsClient = new HslCommunication.Profinet.Omron.OmronFinsNet("127.0.0.1", finsPort);
    var connected = finsClient.ConnectServer();
    AssertEqual(true, connected.IsSuccess, "Omron FINS loopback connect");
    AssertEqual((short)12, finsClient.ReadInt16("D100").Content, "Omron FINS initial read");
    AssertEqual(true, finsClient.Write("D100", (short)66).IsSuccess, "Omron FINS loopback write");
    await Task.Delay(150);
    AssertEqual((short)66, finsStore.Read<short>(finsTag.Id).Value, "Omron FINS write reaches shared store");
    finsClient.ConnectClose();
}
Console.WriteLine("All Omron FINS real-protocol loopback tests passed.");

AssertContains(windowXaml, "x:Name=\"BtnImportDb\"", "Siemens DB import toolbar entry");
AssertContains(windowXaml, "x:Name=\"BtnImportKep\"", "KEP import toolbar entry");
AssertContains(windowXaml, "x:Name=\"OpcUaPortBox\"", "OPC UA endpoint setting");
AssertContains(windowXaml, "x:Name=\"BtnStartAll\"", "multi-protocol start entry");
AssertContains(windowXaml, "x:Name=\"DeviceTree\"", "always visible device tree");
AssertContains(windowXaml, "x:Name=\"BtnDeleteTreeNode\"", "visible delete selected tree node entry");
AssertContains(windowXaml, "KeyDown=\"DeviceTree_KeyDown\"", "delete key tree interaction");
AssertContains(windowXaml, "x:Name=\"ProtocolCards\"", "visible protocol workspace");
AssertContains(windowXaml, "x:Name=\"OpcUaStatusText\"", "OPC UA running state");
AssertContains(windowXaml, "x:Name=\"OmronCipStatusText\"", "Omron CIP running state");
AssertContains(windowXaml, "x:Name=\"OmronFinsStatusText\"", "Omron FINS running state");
AssertContains(windowSource, "KepJsonSimulationImporter.ImportFile", "native KEP import wiring");
AssertContains(windowSource, "Multiselect = true", "Siemens DB import supports selecting multiple DB files");
AssertContains(windowSource, "SiemensDbSimulationImporter.ImportFiles(dialog.FileNames)", "Siemens multi-DB import wiring");
AssertContains(windowSource, "LoadCommandLineDbProjectAsync", "command-line DB project loading supports direct simulator startup");
AssertContains(windowSource, "ShowImportPreview", "import preview wiring");
AssertContains(windowSource, "BuildProjectWorkspace", "imported device tree and tag grid");
AssertContains(windowSource, "HorizontalScrollBarVisibility = ScrollBarVisibility.Visible", "point grids always show horizontal scrollbar");
AssertContains(windowSource, "DeleteSelectedTreeNodeAsync", "tree delete updates project model");
AssertContains(windowSource, "OpcUaSimulationServer", "OPC UA server wiring");
AssertContains(windowSource, "OmronCipSimulationServer", "Omron CIP server wiring");
AssertContains(windowSource, "OmronFinsSimulationServer", "Omron FINS server wiring");
Console.WriteLine("All standalone simulator workspace source contracts passed.");

string manifest = File.ReadAllText(Path.Combine(sourceRoot, "app.manifest"));
AssertContains(manifest, "requestedExecutionLevel level=\"asInvoker\"", "simulator must not force UAC elevation");
AssertNotContains(manifest, "requireAdministrator", "simulator must remain portable for normal users");
Console.WriteLine("All simulator portability manifest tests passed.");

static int FreeTcpPort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}

static async Task RunNativePlusLoopback(string dllPath, SimulationProject project)
{
    var device = project.Devices.Single();
    var arrayTag = device.EnumerateTags().FirstOrDefault(x => x.Name == "ArrayValues");
    if (arrayTag == null)
    {
        var rootGroup = device.Groups.First();
        arrayTag = rootGroup.AddTag("ArrayValues", SimulationDataType.Int16, new short[] { 10, 20, 30 }, [3]);
        arrayTag.SymbolicAddress = rootGroup.Name + ".ArrayValues";
    }
    var store = new SimulationValueStore(project);
    var plcFlag = device.EnumerateTags().First(x => x.Name == "PLCFlag");
    store.Write(plcFlag.Id, (short)12, SimulationWriteSource.UserInterface);
    int port = FreeTcpPort();
    await using var server = new SiemensPlusSimulationServer(device, store, port);
    server.Start();

    IntPtr library = NativeLibrary.Load(dllPath);
    var abi = Load<AbiVersion>(library, "igw_s7plus_abi_version");
    var create = Load<CreateHandle>(library, "igw_s7plus_create");
    var destroy = Load<DestroyHandle>(library, "igw_s7plus_destroy");
    var connect = Load<ConnectHandle>(library, "igw_s7plus_connect");
    var browse = Load<BrowseHandle>(library, "igw_s7plus_browse");
    var read = Load<ReadHandle>(library, "igw_s7plus_read_batch");
    var write = Load<WriteHandle>(library, "igw_s7plus_write_batch");
    var disconnect = Load<DisconnectHandle>(library, "igw_s7plus_disconnect");
    AssertEqual(5u, abi(), "native S7 Plus ABI");

    IntPtr handle = create();
    try
    {
        using var host = new PinnedBytes("127.0.0.1");
        var request = new NativeConnectRequest(host.Slice.Pointer, host.Slice.Length, (ushort)port, 5_000, 0);
        NativeCode connectCode = connect(handle, in request);
        if (connectCode != NativeCode.Ok)
        {
            await Task.Delay(100);
            throw new InvalidOperationException($"native S7 Plus connect: expected Ok, actual {connectCode}; activity={server.LastActivity}; server={server.LastError}");
        }

        NativeResult browseSize = browse(handle, default);
        AssertEqual(NativeCode.BufferTooSmall, browseSize.Code, "native S7 Plus browse size");
        using var browseBuffer = new PinnedBytes(checked((int)browseSize.Detail));
        NativeResult browseResult = browse(handle, browseBuffer.MutableSlice);
        AssertEqual(NativeCode.Ok, browseResult.Code, "native S7 Plus browse");
        string browseText = Encoding.UTF8.GetString(browseBuffer.Bytes, 0, checked((int)browseResult.Detail));
        AssertContains(browseText, "DB2100_电芯OCV_DataDB.PLCFlag", "native S7 Plus browse symbol");
        AssertContains(browseText, "DB2100_电芯OCV_DataDB.CellData[0].DMCCode", "native S7 Plus browse keeps structure-array brackets");

        using var symbol = new PinnedBytes("DB2100_电芯OCV_DataDB.PLCFlag");
        var readItems = new[] { new NativeReadItem(symbol.Slice) };
        var readResults = new NativeReadResult[1];
        using var payload = new PinnedBytes(4096);
        NativeResult readCall = read(handle, readItems, 1, readResults, payload.MutableSlice);
        AssertEqual(NativeCode.Ok, readCall.Code, "native S7 Plus read call");
        AssertEqual(NativeCode.Ok, readResults[0].Code, "native S7 Plus read item");
        AssertEqual(12L, readResults[0].SignedValue, "native S7 Plus initial value");

        SimulationTag? firstAlarmMember = device.EnumerateTags().FirstOrDefault(x =>
            x.SymbolicAddress == "DB2101_设备异常.PLCFlag");
        if (firstAlarmMember != null)
        {
            store.Write(firstAlarmMember.Id, (short)21, SimulationWriteSource.UserInterface);
            using var firstAlarmSymbol = new PinnedBytes(firstAlarmMember.SymbolicAddress);
            var firstAlarmItems = new[] { new NativeReadItem(firstAlarmSymbol.Slice) };
            var firstAlarmResults = new NativeReadResult[1];
            using var firstAlarmPayload = new PinnedBytes(1024);
            AssertEqual(NativeCode.Ok, read(handle, firstAlarmItems, 1, firstAlarmResults, firstAlarmPayload.MutableSlice).Code,
                "native S7 Plus first DB member read call");
            AssertEqual(NativeCode.Ok, firstAlarmResults[0].Code, "native S7 Plus first DB member read item");
            AssertEqual(NativeValueType.Int, firstAlarmResults[0].ValueType, "native S7 Plus first DB member returns an Int value");
            AssertEqual(21L, firstAlarmResults[0].SignedValue, "native S7 Plus first DB member returns PLC data instead of its type id");
        }

        var allTags = device.EnumerateTags().Where(x => x.DataType != SimulationDataType.Unknown && !x.IsArray).ToArray();
        var allSymbols = allTags.Select(x => new PinnedBytes(x.SymbolicAddress)).ToArray();
        try
        {
            var allItems = allSymbols.Select(x => new NativeReadItem(x.Slice)).ToArray();
            var allResults = new NativeReadResult[allItems.Length];
            using var allPayload = new PinnedBytes(256 * 1024);
            NativeResult allReadCall = read(handle, allItems, (nuint)allItems.Length, allResults, allPayload.MutableSlice);
            var chunkFailures = new List<string>();
            for (int offset = 0; offset < allItems.Length; offset++)
            {
                var chunkItems = allItems.Skip(offset).Take(1).ToArray();
                var chunkResults = new NativeReadResult[chunkItems.Length];
                using var chunkPayload = new PinnedBytes(64 * 1024);
                NativeResult chunkCall = read(handle, chunkItems, (nuint)chunkItems.Length, chunkResults, chunkPayload.MutableSlice);
                if (chunkCall.Code != NativeCode.Ok)
                    chunkFailures.Add($"{allTags[offset].SymbolicAddress}={chunkCall.Code}");
            }
            if (chunkFailures.Count > 0)
                throw new InvalidOperationException("native S7 Plus single-item reads failed: " + string.Join(", ", chunkFailures.Select(x => x)));
            if (allReadCall.Code != NativeCode.Ok)
                throw new InvalidOperationException($"native S7 Plus full batch read call: expected Ok, actual {allReadCall.Code}; activity={server.LastActivity}; server={server.LastError}");
            var failed = allResults.Select((result, index) => (result, index)).Where(x => x.result.Code != NativeCode.Ok).ToArray();
            if (failed.Length > 0)
                throw new InvalidOperationException("native S7 Plus full batch failed: " + string.Join(", ", failed.Take(10).Select(x => $"{allTags[x.index].SymbolicAddress}={x.result.Code}")));
        }
        finally
        {
            foreach (var item in allSymbols) item.Dispose();
        }

        var writes = new[] { new NativeWriteItem(symbol.Slice, NativeValueType.Int, 66, 0, 0, default) };
        AssertEqual(NativeCode.Ok, write(handle, writes, 1).Code, "native S7 Plus write");
        await Task.Delay(100);
        AssertEqual((short)66, store.Read<short>(plcFlag.Id).Value, "native S7 Plus write reaches shared store");

        SimulationTag nestedIsNg = device.EnumerateTags().First(x =>
            x.SymbolicAddress.EndsWith("CellData[0].IsNG", StringComparison.Ordinal));
        using var nestedSymbol = new PinnedBytes(nestedIsNg.SymbolicAddress);
        var nestedWrites = new[] { new NativeWriteItem(nestedSymbol.Slice, NativeValueType.Int, 3, 0, 0, default) };
        AssertEqual(NativeCode.Ok, write(handle, nestedWrites, 1).Code, "native S7 Plus structure-array write");
        await Task.Delay(100);
        AssertEqual((short)3, store.Read<short>(nestedIsNg.Id).Value, "native S7 Plus structure-array write reaches shared store");

        var nestedReadItems = new[] { new NativeReadItem(nestedSymbol.Slice) };
        var nestedReadResults = new NativeReadResult[1];
        using var nestedPayload = new PinnedBytes(1024);
        AssertEqual(NativeCode.Ok, read(handle, nestedReadItems, 1, nestedReadResults, nestedPayload.MutableSlice).Code,
            "native S7 Plus structure-array read call");
        AssertEqual(NativeCode.Ok, nestedReadResults[0].Code, "native S7 Plus structure-array read item");
        AssertEqual(3L, nestedReadResults[0].SignedValue, "native S7 Plus structure-array readback");

        using var arraySymbol = new PinnedBytes(arrayTag.SymbolicAddress + "[1]");
        var arrayItems = new[] { new NativeReadItem(arraySymbol.Slice) };
        var arrayResults = new NativeReadResult[1];
        using var arrayPayload = new PinnedBytes(1024);
        AssertEqual(NativeCode.Ok, read(handle, arrayItems, 1, arrayResults, arrayPayload.MutableSlice).Code, "S7 Plus array element read");
        AssertEqual(NativeCode.Ok, arrayResults[0].Code, "S7 Plus array item status");
        AssertEqual(20L, arrayResults[0].SignedValue, "S7 Plus array initial element");
        var arrayWrites = new[] { new NativeWriteItem(arraySymbol.Slice, NativeValueType.Int, 55, 0, 0, default) };
        AssertEqual(NativeCode.Ok, write(handle, arrayWrites, 1).Code, "S7 Plus array element write");
        AssertEqual((short)55, store.Read<short[]>(arrayTag.Id).Value[1], "S7 Plus array write reaches store");
        new SimulationTagRow(arrayTag, store) { EditValue = "[77,88,99]" }.Write();
        AssertEqual(NativeCode.Ok, read(handle, arrayItems, 1, arrayResults, arrayPayload.MutableSlice).Code, "S7 Plus UI array readback");
        AssertEqual(88L, arrayResults[0].SignedValue, "S7 Plus UI array reaches client");

        DateTime stopStarted = DateTime.UtcNow;
        await server.DisposeAsync();
        if (DateTime.UtcNow - stopStarted > TimeSpan.FromSeconds(3))
            throw new InvalidOperationException("S7 Plus server stop exceeded 3 seconds with an active anonymous client");
        _ = disconnect(handle);
    }
    finally
    {
        destroy(handle);
        NativeLibrary.Free(library);
    }
}

static T Load<T>(IntPtr library, string name) where T : Delegate =>
    Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint AbiVersion();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateHandle();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyHandle(IntPtr handle);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate NativeCode ConnectHandle(IntPtr handle, in NativeConnectRequest request);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate NativeResult BrowseHandle(IntPtr handle, NativeMutableSlice output);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate NativeResult ReadHandle(IntPtr handle, [In] NativeReadItem[] items, nuint count, [Out] NativeReadResult[] results, NativeMutableSlice output);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate NativeResult WriteHandle(IntPtr handle, [In] NativeWriteItem[] items, nuint count);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate NativeResult DisconnectHandle(IntPtr handle);

enum NativeCode { Ok = 0, AuthenticationRequired = 10, AuthenticationFailed = 11, PermissionDenied = 12, UnsupportedFirmware = 20, Timeout = 30, TransportFailure = 31, ProtocolFailure = 32, InvalidArgument = 40, BufferTooSmall = 41, Panic = 99 }
enum NativeValueType { Null, Bool, USInt, UInt, UDInt, ULInt, SInt, Int, DInt, LInt, Byte, Word, DWord, LWord, Real, LReal, Timestamp, Timespan, Text, Bytes }
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeResult(NativeCode Code, uint Detail);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeSlice(IntPtr Pointer, nuint Length);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeMutableSlice(IntPtr Pointer, nuint Length);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeConnectRequest(IntPtr HostPointer, nuint HostLength, ushort Port, uint TimeoutMs, byte Legacy);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeReadItem(NativeSlice Symbol);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeReadResult(NativeCode Code, NativeValueType ValueType, long SignedValue, ulong UnsignedValue, double FloatValue, uint PayloadOffset, uint PayloadLength);
[StructLayout(LayoutKind.Sequential)] readonly record struct NativeWriteItem(NativeSlice Symbol, NativeValueType ValueType, long SignedValue, ulong UnsignedValue, double FloatValue, NativeSlice Payload);

sealed class PinnedBytes : IDisposable
{
    private readonly GCHandle _pin;
    public PinnedBytes(string value) : this(Encoding.UTF8.GetBytes(value)) { }
    public PinnedBytes(int length) : this(new byte[length]) { }
    private PinnedBytes(byte[] bytes) { Bytes = bytes; _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned); }
    public byte[] Bytes { get; }
    public NativeSlice Slice => new(_pin.AddrOfPinnedObject(), (nuint)Bytes.Length);
    public NativeMutableSlice MutableSlice => new(_pin.AddrOfPinnedObject(), (nuint)Bytes.Length);
    public void Dispose() { if (_pin.IsAllocated) _pin.Free(); }
}
