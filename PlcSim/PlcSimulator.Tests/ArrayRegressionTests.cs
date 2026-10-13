using System.Net;
using System.Net.Sockets;
using PlcSimulator;
using PlcSimulator.Core;
using PlcSimulator.Protocols;
using PlcSimulator.Import;

internal static class ArrayRegressionTests
{
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Func<Task>)[]
        {
            ("OPC UA array and structure writes", OpcUaArrayTests.RunAsync),
            ("UI array editing and validation", () => { UiRoundtrip(); return Task.CompletedTask; }),
            ("Imported IGW array keeps shape", () => { ImportedArray(); return Task.CompletedTask; }),
            ("S7 absolute addresses enable Siemens with another source driver", SiemensAddressFallback),
            ("DB UDT and field attributes preserve text layout", () => { DbTextLayout(); return Task.CompletedTask; }),
            ("STRING and WSTRING wire layout and neighboring value", TextMemoryLayout),
            ("CIP array roundtrip", CipRoundtrip),
            ("FINS array roundtrip", FinsRoundtrip),
            ("Classic S7 array roundtrip", ClassicRoundtrip),
            ("S7 Plus array symbols and wire values", () => { PlusArrays(); return Task.CompletedTask; }),
            ("Poll does not replay stale client array", PollDoesNotReplay),

        })
        {
            try { await test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL {name}: {ex}"); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("Array regression failures: " + string.Join(", ", failures));
    }

    private static async Task TextMemoryLayout()
    {
        var project = new SimulationProject("text-layout");
        var device = project.AddDevice("PLC"); var group = device.AddGroup("DB1");
        var text = group.AddTag("Code", SimulationDataType.String, ""); text.StringLength = 20; text.ClassicAddress = "DB1.DBB0";
        var wide = group.AddTag("Error", SimulationDataType.String, ""); wide.StringLength = 40; wide.IsWideString = true; wide.ClassicAddress = "DB1.DBB22";
        var number = group.AddTag("Voltage", SimulationDataType.Float32, 123.25f); number.ClassicAddress = "DB1.DBD106";
        var array = group.AddTag("Codes", SimulationDataType.String, new[] { "", "" }, [2]); array.StringLength = 20; array.ElementStrideBytes = 24; array.ClassicAddress = "DB1.DBB110";
        var gap = group.AddTag("GapMember", SimulationDataType.Int16, (short)777); gap.ClassicAddress = "DB1.DBW132";
        var store = new SimulationValueStore(project); int port = FreePort();
        await using var server = new SiemensClassicSimulationServer(device, store, port); server.Start();
        using var client = new HslCommunication.Profinet.Siemens.SiemensS7Net(HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1") { Port = port, Rack = 0, Slot = 1 };
        Success(client.ConnectServer());
        Sequence(new byte[] { 20, 0 }, Content(client.Read("DB1.0", 2)));
        Sequence(new byte[] { 0, 40, 0, 0 }, Content(client.Read("DB1.22", 4)));
        new SimulationTagRow(text, store) { EditValue = "12345678901234567890" }.Write();
        new SimulationTagRow(wide, store) { EditValue = "中文Ω" }.Write();
        Sequence(new byte[] { 0, 40, 0, 3, 0x4E, 0x2D, 0x65, 0x87, 0x03, 0xA9 }, Content(client.Read("DB1.22", 10)));
        if (Content(client.ReadFloat("DB1.106")) != 123.25f) throw new Exception("Text write overwrote adjacent REAL");
        long version = store.Read(wide.Id).Version;
        try { new SimulationTagRow(wide, store) { EditValue = new string('中', 41) }.Write(); throw new Exception("Oversized WSTRING accepted"); }
        catch (SimulationValueException) { }
        if (store.Read(wide.Id).Version != version || store.Read<string>(wide.Id).Value != "中文Ω") throw new Exception("Failed WSTRING write changed value");
        Success(client.Write("DB1.22", new byte[] { 0, 40, 0, 2, 0x4E, 0x2D, 0x65, 0x87 }));
        await WaitFor(() => store.Read<string>(wide.Id).Value == "中文");
        new SimulationTagRow(array, store) { EditValue = "[\"first\",\"second\"]" }.Write();
        Sequence(new byte[] { 20, 5 }, Content(client.Read("DB1.110", 2)));
        Sequence(new byte[] { 20, 6 }, Content(client.Read("DB1.134", 2)));
        if (Content(client.ReadInt16("DB1.132")) != 777) throw new Exception("Strided array overwrote gap member");
        new SimulationTagRow(text, store) { EditValue = "物料Ω" }.Write();
        Sequence(new byte[] { 20, 8, 0xE7, 0x89, 0xA9, 0xE6, 0x96, 0x99, 0xCE, 0xA9 }, Content(client.Read("DB1.0", 10)));
        if (Content(client.ReadFloat("DB1.106")) != 123.25f) throw new Exception("String array overwrote neighbor");
        client.ConnectClose();
    }

    private static void DbTextLayout()
    {
        const string source = """
        TYPE "Header"
        STRUCT
          Flag : Int;
          Code : String[20];
          Error : WString[40];
        END_STRUCT;
        END_TYPE
        DATA_BLOCK "DB2116_OutData1"
        { S7_Optimized_Access := 'FALSE' }
        STRUCT
          Static : "Header";
          Voltage { S7_SetPoint := 'True' } : Real;
        END_STRUCT;
        BEGIN
        END_DATA_BLOCK
        """;
        var imported = SiemensDbSimulationImporter.ImportText(source);
        var tags = imported.Project.EnumerateTags().ToArray();
        var code = tags.SingleOrDefault(t => t.SymbolicAddress == "DB2116_OutData1.Static.Code");
        if (code is null || code.StringLength != 20 || code.IsWideString) throw new Exception("UDT STRING layout discarded");
        var wide = tags.Single(t => t.SymbolicAddress == "DB2116_OutData1.Static.Error");
        if (wide.StringLength != 40 || !wide.IsWideString || !wide.ClassicAddress.StartsWith("DB2116.DBB24")) throw new Exception("WSTRING layout discarded");
        if (!tags.Single(t => t.Name == "Voltage").ClassicAddress.StartsWith("DB2116.DBD108")) throw new Exception("UDT size or attributed member offset incorrect");
        var bitStruct = SiemensDbSimulationImporter.ImportText("""
        TYPE "BitItem"
        STRUCT
          Flag : Bool;
        END_STRUCT;
        END_TYPE
        DATA_BLOCK "DB1"
        { S7_Optimized_Access := 'FALSE' }
        STRUCT
          Prefix : Byte;
          Item : "BitItem";
          Tail : Byte;
          Items : Array[0..1] of "BitItem";
        END_STRUCT;
        END_DATA_BLOCK
        """);
        var bitTags = bitStruct.Project.EnumerateTags().ToArray();
        if (bitTags.Single(t => t.SymbolicAddress == "DB1.Item.Flag").ClassicAddress != "DB1.DBX2.0" ||
            bitTags.Single(t => t.Name == "Tail").ClassicAddress != "DB1.DBB4" ||
            bitTags.Single(t => t.SymbolicAddress == "DB1.Items[1].Flag").ClassicAddress != "DB1.DBX8.0")
            throw new Exception("UDT boundaries are not word-aligned");
    }

    private static async Task SiemensAddressFallback()
    {
        const string json = """
        {"project":{"channels":[{"common.ALLTYPES_NAME":"PLC8","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"OPC UA Client","devices":[{"common.ALLTYPES_NAME":"PLC3","tag_groups":[{"common.ALLTYPES_NAME":"DB2116_OutData1","tags":[{"common.ALLTYPES_NAME":"MESFlag","servermain.TAG_ADDRESS":"DB2116.DBW0","servermain.TAG_DATA_TYPE":4}]}]}]}]}}
        """;
        var imported = KepJsonSimulationImporter.ImportJson(json);
        var device = imported.Project.Devices.Single();
        if (device.SiemensProfile != SiemensSimulationProfile.Classic) throw new Exception("Visible S7 addresses were classified as no Siemens device");
        if (!device.Endpoints.Any(x => x.Protocol == SimulationProtocol.OpcUa) || !device.Endpoints.Any(x => x.Protocol == SimulationProtocol.SiemensS7))
            throw new Exception("Imported source protocol and inferred Siemens protocol must both be available");
        if (device.Endpoints.Select(x => x.ListenPort).Distinct().Count() != 2) throw new Exception("Protocol ports collide");
        var tag = device.EnumerateTags().Single();
        if (tag.ClassicAddress != "DB2116.DBW0") throw new Exception("S7 absolute address missing");
        var store = new SimulationValueStore(imported.Project);
        int port = FreePort();
        await using var server = new SiemensClassicSimulationServer(device, store, port);
        server.Start();
        using var client = new HslCommunication.Profinet.Siemens.SiemensS7Net(HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1") { Port = port, Rack = 0, Slot = 1 };
        Success(client.ConnectServer()); Success(client.Write("DB2116.DBW0", (short)2));
        await WaitFor(() => store.Read<short>(tag.Id).Value == 2);
        client.ConnectClose();
        var unknown = KepJsonSimulationImporter.ImportJson(json.Replace("OPC UA Client", "Generic"));
        if (unknown.Project.Devices.Single().SiemensProfile != SiemensSimulationProfile.Classic) throw new Exception("Unknown source driver blocked explicit S7 addresses");
        var symbolic = KepJsonSimulationImporter.ImportJson(json.Replace("DB2116.DBW0", "Line.Speed"));
        if (symbolic.Project.Devices.Single().SiemensProfile != SiemensSimulationProfile.None) throw new Exception("Generic symbolic address misidentified as Siemens");
        foreach (string symbol in new[] { "DB1.WordCount", "DB1.BoolStatus", "DB1.IntegerValue", "DB1.DBW0Tail" })
            if (KepJsonSimulationImporter.ImportJson(json.Replace("DB2116.DBW0", symbol)).Project.Devices.Single().SiemensProfile != SiemensSimulationProfile.None)
                throw new Exception($"Symbolic name {symbol} misidentified as absolute Siemens address");
    }

    private static void PlusArrays()
    {
        var f = Fixture("DB1.Values");
        f.Tag.SymbolicAddress = "DB1.Values";
        var schema = SiemensPlusSchema.Create(f.Device);
        var descriptor = schema.Descriptors.SingleOrDefault(x => x.Symbol == "DB1.Values[1]");
        if (descriptor == null) throw new Exception("S7 Plus array element not exposed");
        var address = new S7PlusAddress(descriptor.AccessArea, 0, descriptor.Lids);
        if (!schema.TryRead(address, f.Store, out _, out var value) || !Equals(value, (short)20)) throw new Exception("S7 Plus element read failed");
        if (!schema.TryWrite(address, f.Store, (short)55)) throw new Exception("S7 Plus element write failed");
        Sequence(new short[] { 10, 55, 30 }, f.Store.Read<short[]>(f.Tag.Id).Value);
        using var stream = new MemoryStream();
        S7PlusCodec.WriteValue(stream, SimulationDataType.Int16, new short[] { 10, 20, 30 });
        byte[] wire = [0x10, 0x07, 0x03, 0, 10, 0, 20, 0, 30];
        Sequence(wire, stream.ToArray());
        int offset = 0;
        Sequence(new object[] { (short)10, (short)20, (short)30 }, (object[])S7PlusCodec.ReadValue(wire, ref offset)!);
    }

    private static void ImportedArray()
    {
        const string json = """
        {"project":{"channels":[{"common.ALLTYPES_NAME":"C","servermain.MULTIPLE_TYPES_DEVICE_DRIVER":"OPC UA Client","devices":[{"common.ALLTYPES_NAME":"PLC","tags":[{"common.ALLTYPES_NAME":"Values","servermain.TAG_ADDRESS":"Values","servermain.TAG_DATA_TYPE":4}]}]}]},
         "igw":{"entities":{"channels":[{"id":"c","document":{"name":"C"}}],"devices":[{"id":"d","parentId":"c","document":{"name":"PLC"}}],"groups":[{"id":"g","parentId":"d","document":{"name":"Tags"}}],"tags":[{"id":"t","parentId":"g","document":{"name":"Values","address":"Values","dataType":"Int16","isArray":true,"array":{"dimensions":[3]}}}]}}}
        """;
        var project = KepJsonSimulationImporter.ImportJson(json).Project;
        var tag = project.EnumerateTags().Single();
        if (!tag.IsArray) throw new Exception("Import discarded array metadata");
        Sequence(new[] { 3 }, tag.Dimensions);
        var store = new SimulationValueStore(project);
        new SimulationTagRow(tag, store) { EditValue = "[1,2,3]" }.Write();
        Sequence(new short[] { 1, 2, 3 }, store.Read<short[]>(tag.Id).Value);
        try { KepJsonSimulationImporter.ImportJson(json.Replace("\"dimensions\":[3]", "\"dimensions\":[2,3]")); throw new Exception("Multi-dimensional array silently flattened"); }
        catch (FormatException ex) when (ex.Message.Contains("一维")) { }
    }

    private static void UiRoundtrip()
    {
        var project = new SimulationProject("arrays");
        var group = project.AddDevice("PLC").AddGroup("Tags");
        var numbers = group.AddTag("Numbers", SimulationDataType.Int16, new short[] { 1, 2, 3 }, [3]);
        var strings = group.AddTag("Names", SimulationDataType.String, new[] { "a", "b" }, [2]);
        var flags = group.AddTag("Flags", SimulationDataType.Boolean, new[] { false, false }, [2]);
        var store = new SimulationValueStore(project);
        var row = new SimulationTagRow(numbers, store) { EditValue = "[4, -5, 6]" };
        row.Write();
        Sequence(new short[] { 4, -5, 6 }, store.Read<short[]>(numbers.Id).Value);
        foreach (string invalid in new[] { "[7,8]", "[1,40000,3]", "[1,broken,3]", "[1,null,3]" })
        {
            var version = store.Read(numbers.Id).Version;
            row.EditValue = invalid;
            try { row.Write(); throw new Exception("Invalid array was accepted: " + invalid); }
            catch (SimulationValueException) { }
            Sequence(new short[] { 4, -5, 6 }, store.Read<short[]>(numbers.Id).Value);
            if (store.Read(numbers.Id).Version != version) throw new Exception("Failed write changed version");
        }
        new SimulationTagRow(strings, store) { EditValue = "[\"a,b\",\"带\\\"引号\"]" }.Write();
        Sequence(new[] { "a,b", "带\"引号" }, store.Read<string[]>(strings.Id).Value);
        store.Write(strings.Id, new[] { new string('A', 300), new string('中', 100) }, SimulationWriteSource.Protocol);
        Sequence(new[] { new string('A', 300), new string('中', 100) }, store.Read<string[]>(strings.Id).Value);
        new SimulationTagRow(flags, store) { EditValue = "[true,false]" }.Write();
        Sequence(new[] { true, false }, store.Read<bool[]>(flags.Id).Value);
        store.Write(numbers.Id, new short[] { 10, 20, 30 }, SimulationWriteSource.Protocol);
        row.Refresh();
        if (row.LiveValue != "[10, 20, 30]") throw new Exception("Client write not shown in UI");
        if (store.TryWrite(numbers.Id, new short[] { 0, 0, 0 }, SimulationWriteSource.Protocol, 0)) throw new Exception("Stale poll overwrote UI write");
        Sequence(new short[] { 10, 20, 30 }, store.Read<short[]>(numbers.Id).Value);
        string preview = SimulationValueText.Format(Enumerable.Range(0, 10000).ToArray());
        if (preview.Length > 150 || !preview.Contains("10000")) throw new Exception("Array live preview is unbounded");
    }

    private static (SimulationProject Project, SimulationDevice Device, SimulationTag Tag, SimulationValueStore Store) Fixture(string address)
    {
        var project = new SimulationProject("loopback");
        var device = project.AddDevice("PLC");
        var tag = device.AddGroup("Tags").AddTag("Values", SimulationDataType.Int16, new short[] { 10, 20, 30 }, [3]);
        tag.ProtocolAddress = tag.ClassicAddress = address;
        return (project, device, tag, new SimulationValueStore(project));
    }

    private static async Task CipRoundtrip()
    {
        var f = Fixture("Values");
        int port = FreePort();
        await using var server = new OmronCipSimulationServer(f.Device, f.Store, port);
        server.Start();
        using var client = new HslCommunication.Profinet.Omron.OmronCipNet("127.0.0.1", port);
        Success(client.ConnectServer());
        Sequence(new short[] { 10, 20, 30 }, client.ByteTransform.TransInt16(Content(client.Read(new[] { "Values" }, new[] { 3 })), 0, 3));
        Success(client.Write("Values[0]", new short[] { 40, 50, 60 }));
        await WaitFor(() => f.Store.Read<short[]>(f.Tag.Id).Value.SequenceEqual(new short[] { 40, 50, 60 }));
        new SimulationTagRow(f.Tag, f.Store) { EditValue = "[70,80,90]" }.Write();
        Sequence(new short[] { 70, 80, 90 }, client.ByteTransform.TransInt16(Content(client.Read(new[] { "Values" }, new[] { 3 })), 0, 3));
        client.ConnectClose();
    }

    private static async Task FinsRoundtrip()
    {
        var f = Fixture("D100");
        int port = FreePort();
        await using var server = new OmronFinsSimulationServer(f.Device, f.Store, port);
        server.Start();
        using var client = new HslCommunication.Profinet.Omron.OmronFinsNet("127.0.0.1", port);
        Success(client.ConnectServer());
        Sequence(new short[] { 10, 20, 30 }, Content(client.ReadInt16("D100", 3)));
        Success(client.Write("D100", new short[] { 40, 50, 60 }));
        await WaitFor(() => f.Store.Read<short[]>(f.Tag.Id).Value.SequenceEqual(new short[] { 40, 50, 60 }));
        new SimulationTagRow(f.Tag, f.Store) { EditValue = "[70,80,90]" }.Write();
        Sequence(new short[] { 70, 80, 90 }, Content(client.ReadInt16("D100", 3)));
        client.ConnectClose();
    }

    private static async Task ClassicRoundtrip()
    {
        var f = Fixture("DB1.DBW0");
        int port = FreePort();
        await using var server = new SiemensClassicSimulationServer(f.Device, f.Store, port);
        server.Start();
        using var client = new HslCommunication.Profinet.Siemens.SiemensS7Net(HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1") { Port = port, Rack = 0, Slot = 1 };
        Success(client.ConnectServer());
        Sequence(new short[] { 10, 20, 30 }, Content(client.ReadInt16("DB1.DBW0", 3)));
        Success(client.Write("DB1.DBW0", new short[] { 40, 50, 60 }));
        await WaitFor(() => f.Store.Read<short[]>(f.Tag.Id).Value.SequenceEqual(new short[] { 40, 50, 60 }));
        new SimulationTagRow(f.Tag, f.Store) { EditValue = "[70,80,90]" }.Write();
        Sequence(new short[] { 70, 80, 90 }, Content(client.ReadInt16("DB1.DBW0", 3)));
        client.ConnectClose();
    }

    private static async Task PollDoesNotReplay()
    {
        var f = Fixture("D100");
        int port = FreePort();
        using var client = new HslCommunication.Profinet.Omron.OmronFinsNet("127.0.0.1", port);
        var injected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before the adapter: inject a newer wire write between the poll commit
        // and its store-change callback, deterministically reproducing the old-value replay.
        f.Store.ValueChanged += change =>
        {
            if (change.Source != SimulationWriteSource.Protocol || change.Value is not short[] values || values[0] != 40) return;
            try { Success(client.Write("D100", new short[] { 80, 81, 82 })); injected.TrySetResult(); }
            catch (Exception ex) { injected.TrySetException(ex); }
        };
        await using var server = new OmronFinsSimulationServer(f.Device, f.Store, port);
        server.Start(); Success(client.ConnectServer());
        Success(client.Write("D100", new short[] { 40, 41, 42 }));
        await injected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitFor(() => f.Store.Read<short[]>(f.Tag.Id).Value.SequenceEqual(new short[] { 80, 81, 82 }));
        Sequence(new short[] { 80, 81, 82 }, Content(client.ReadInt16("D100", 3)));
        client.ConnectClose();
    }

    internal static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    internal static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception($"Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]"); }
    internal static async Task WaitFor(Func<bool> condition) { for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(20); } throw new Exception("Timed out waiting for shared store"); }
    private static void Success(HslCommunication.OperateResult result) { if (!result.IsSuccess) throw new Exception(result.Message); }
    private static T Content<T>(HslCommunication.OperateResult<T> result) { Success(result); return result.Content; }
}
