using PlcSimulator.Core;
using PlcSimulator.Import;
using PlcSimulator.Protocols;
using PlcSimulator;
using System.Reflection;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ImportedProjectAcceptance
{
    public static async Task RunAsync(string path)
    {
        var imported = KepJsonSimulationImporter.ImportFile(path);
        Console.WriteLine($"Imported devices={imported.Project.Devices.Count}, tags={imported.Project.EnumerateTags().Count()}, warnings={imported.Warnings.Count}");
        var store = new SimulationValueStore(imported.Project);
        foreach (var device in imported.Project.Devices)
        {
            Console.WriteLine($"Device {device.Name}: profile={device.SiemensProfile}, endpoints={string.Join(',', device.Endpoints.Select(x => $"{x.Protocol}:{x.ListenPort}"))}");
            if (device.SiemensProfile != SiemensSimulationProfile.Classic) throw new Exception($"Expected Classic S7 for {device.Name}");
            int port = ArrayRegressionTests.FreePort();
            await using var server = new SiemensClassicSimulationServer(device, store, port);
            server.Start();
            using var client = new HslCommunication.Profinet.Siemens.SiemensS7Net(HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1") { Port = port, Rack = 0, Slot = 1 };
            var connect = client.ConnectServer();
            if (!connect.IsSuccess) throw new Exception($"{device.Name} connect: {connect.Message}");
            if (device.Name == "PLC1")
            {
                var wrongDeviceRead = client.ReadInt16("DB2110.DBW0");
                Console.WriteLine($"Wrong-endpoint reproduction: PLC1 reading PLC2 DB2110.DBW0 success={wrongDeviceRead.IsSuccess}, detail={wrongDeviceRead.Message}");
                if (wrongDeviceRead.IsSuccess) throw new Exception("Expected missing PLC2 DB to fail on PLC1 endpoint");
            }
            var tag = device.Name == "PLC2"
                ? device.EnumerateTags().Single(x => x.ClassicAddress == "DB2110.DBW0")
                : device.EnumerateTags().First(x => x.DataType == SimulationDataType.Int16 && !x.IsArray);
            var read = client.ReadInt16(tag.ClassicAddress);
            if (!read.IsSuccess) throw new Exception($"{device.Name} read: {read.Message}");
            var write = client.Write(tag.ClassicAddress, (short)123);
            if (!write.IsSuccess) throw new Exception($"{device.Name} write: {write.Message}");
            await ArrayRegressionTests.WaitFor(() => store.Read<short>(tag.Id).Value == 123);
            client.ConnectClose();
            Console.WriteLine($"PASS {device.Name}: start/connect/read/write {tag.ClassicAddress}");
        }
        await UiStartup(imported.Project);
    }

    public static Task RunStartupAsync() => UiStartup(null, commandLine: true);

    private static Task UiStartup(SimulationProject? project, bool commandLine = false)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    if (project != null) foreach (var endpoint in project.Devices.SelectMany(d => d.Endpoints)) endpoint.ListenPort = ArrayRegressionTests.FreePort();
                    window = new MainWindow();
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    if (!commandLine)
                    {
                        var single = new SimulationProject("single"); var singleDevice = single.AddDevice("SinglePLC");
                        singleDevice.SiemensProfile = SiemensSimulationProfile.Classic;
                        singleDevice.AddEndpoint(SimulationProtocol.SiemensS7, 102, 102);
                        singleDevice.AddGroup("DB1").AddTag("Value", SimulationDataType.Int16, (short)0).ClassicAddress = "DB1.DBW0";
                        await (Task)typeof(MainWindow).GetMethod("ApplyImportedProjectAsync", flags)!.Invoke(window, [single])!;
                        ((TextBox)window.FindName("PortBox")).Text = "2103";
                        if (!((TextBlock)window.FindName("SiemensEndpointMapText")).Text.Contains("127.0.0.1:2103"))
                            throw new Exception("Single-device pending port is not reflected in mapping");
                    }
                    if (commandLine)
                    {
                        bool loaded = await (Task<bool>)typeof(MainWindow).GetMethod("LoadCommandLineDbProjectAsync", flags)!.Invoke(window, null)!;
                        if (!loaded) throw new Exception("JSON command-line import did not load/start Siemens project");
                    }
                    else await (Task)typeof(MainWindow).GetMethod("ApplyImportedProjectAsync", flags)!.Invoke(window, [project])!;
                    string detected = ((TextBlock)window.FindName("DetectedSiemensProfileText")).Text;
                    if (!detected.Contains("Classic S7") || !detected.Contains("11")) throw new Exception("UI profile mismatch: " + detected);
                    bool started = await (Task<bool>)typeof(MainWindow).GetMethod("StartSiemensAsync", flags)!.Invoke(window, [false])!;
                    if (!started) throw new Exception("UI S7 start failed");
                    var servers = (IEnumerable<SiemensClassicSimulationServer>)typeof(MainWindow).GetField("_classicServers", flags)!.GetValue(window)!;
                    if (servers.Count(s => s.IsRunning) != 11) throw new Exception("UI did not start all 11 Siemens servers");
                    var endpointText = window.FindName("SiemensEndpointMapText") as TextBlock;
                    if (endpointText == null) throw new Exception("Multi-PLC S7 port mapping is missing from the right panel");
                    var loadedProject = (SimulationProject)typeof(MainWindow).GetField("_simulationProject", flags)!.GetValue(window)!;
                    foreach (var endpoint in loadedProject.Devices.SelectMany(d => d.Endpoints).Where(e => e.Protocol == SimulationProtocol.SiemensS7))
                        if (!endpointText.Text.Contains("127.0.0.1:" + endpoint.ListenPort)) throw new Exception("UI omits a real S7 endpoint");
                    if (((TextBox)window.FindName("PortBox")).Visibility != System.Windows.Visibility.Collapsed)
                        throw new Exception("Multi-PLC UI still presents a misleading single-port input");
                    var panel = (System.Windows.FrameworkElement)endpointText.Parent;
                    while (panel is not Border) panel = (System.Windows.FrameworkElement)panel.Parent;
                    panel.Measure(new System.Windows.Size(306, double.PositiveInfinity));
                    panel.Arrange(new System.Windows.Rect(new System.Windows.Point(), panel.DesiredSize));
                    panel.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(306, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory("artifacts");
                    using (var file = File.Create("artifacts/PlcSimulator-S7-portmap-preview.png")) image.Save(file);
                    await (Task)typeof(MainWindow).GetMethod("StopSiemensServersAsync", flags)!.Invoke(window, null)!;
                    if (endpointText.Text.Contains("运行中") || !endpointText.Text.Contains("已停止")) throw new Exception("S7 endpoint status stayed running after stop");
                    Console.WriteLine($"PASS WPF import/start: {detected}; 11 simultaneous S7 servers");
                    completed.TrySetResult();
                }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally
                {
                    if (window != null)
                        await (Task)typeof(MainWindow).GetMethod("StopImportedServersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
