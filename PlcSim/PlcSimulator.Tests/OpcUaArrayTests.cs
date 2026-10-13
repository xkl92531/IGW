using Opc.Ua;
using Opc.Ua.Client;
using PlcSimulator;
using PlcSimulator.Core;
using PlcSimulator.Protocols;

#pragma warning disable CS0618
internal static class OpcUaArrayTests
{
    public static async Task RunAsync()
    {
        var project = new SimulationProject("opc-arrays");
        var group = project.AddDevice("PLC").AddGroup("Tags");
        var tag = group.AddTag("Values", SimulationDataType.Int16, new short[] { 1, 2, 3 }, [3]);
        var member = group.AddTag("Member", SimulationDataType.Int16, (short)12);
        member.ProtocolAddress = "ns=3;s=\"DB\".\"Items\"[0].\"Value\"";
        var secondMember = group.AddTag("Member2", SimulationDataType.Int16, (short)13);
        secondMember.ProtocolAddress = "ns=3;s=\"DB\".\"Items\"[1].\"Value\"";
        var nested = group.AddTag("NestedValues", SimulationDataType.Int16, new short[] { 1, 2, 3 }, [3]);
        nested.ProtocolAddress = "ns=3;s=\"DB\".\"Nested\"[0].\"Values\"";
        var store = new SimulationValueStore(project);
        int port = ArrayRegressionTests.FreePort();
        string pki = Path.Combine(Path.GetTempPath(), "plcsimulator-array-tests", Guid.NewGuid().ToString("N"));
        await using var server = new OpcUaSimulationServer();
        await server.StartAsync(project, store, port, pki);
        var config = new ApplicationConfiguration
        {
            ApplicationName = "Array regression client", ApplicationUri = "urn:plcsimulator:test", ApplicationType = ApplicationType.Client,
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier(), AutoAcceptUntrustedCertificates = true,
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "client-trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "client-issuers") },
                RejectedCertificateStore = new CertificateStoreIdentifier { StoreType = "Directory", StorePath = Path.Combine(pki, "client-rejected") },
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 10000 },
        };
        await config.Validate(ApplicationType.Client);
        var endpoint = CoreClientUtils.SelectEndpoint(config, $"opc.tcp://localhost:{port}", false);
        using var session = await Session.Create(config, new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config)), false, "arrays", 60000, new UserIdentity(), null);
        var id = new NodeId(tag.SymbolPath, (ushort)session.NamespaceUris.GetIndex("urn:plcsimulator"));
        ArrayRegressionTests.Sequence(new short[] { 1, 2, 3 }, (short[])session.ReadValue(id).Value);
        Write(session, id, new short[] { 4, 5, 6 });
        ArrayRegressionTests.Sequence(new short[] { 4, 5, 6 }, store.Read<short[]>(tag.Id).Value);
        Write(session, id, new short[] { 99 }, "1");
        ArrayRegressionTests.Sequence(new short[] { 4, 99, 6 }, store.Read<short[]>(tag.Id).Value);
        new SimulationTagRow(tag, store) { EditValue = "[7,8,9]" }.Write();
        ArrayRegressionTests.Sequence(new short[] { 7, 8, 9 }, (short[])session.ReadValue(id).Value);
        var structId = new NodeId("\"DB\".\"Items\"", 3);
        var structs = (ExtensionObject[])session.ReadValue(structId).Value;
        var encoded = new ExtensionObject(structs[0].TypeId, new byte[] { 55, 0 });
        Write(session, structId, new[] { encoded, new ExtensionObject(structs[0].TypeId, new byte[] { 66, 0 }) });
        if (store.Read<short>(member.Id).Value != 55) throw new Exception("Struct array write did not update leaf");
        Write(session, structId, new[] { new ExtensionObject(structs[0].TypeId, new byte[] { 77, 0 }) }, "1");
        if (store.Read<short>(secondMember.Id).Value != 77) throw new Exception("Struct array range write did not update leaf");
        var malformed = new[] { new ExtensionObject(structs[0].TypeId, new byte[] { 88, 0 }), new ExtensionObject(structs[0].TypeId, new byte[] { 1 }) };
        session.Write(null, new WriteValueCollection { new() { NodeId = structId, AttributeId = Attributes.Value, Value = new DataValue(new Variant(malformed)) } }, out var rejected, out _);
        if (StatusCode.IsGood(rejected[0])) throw new Exception("Malformed structure was accepted");
        if (store.Read<short>(member.Id).Value != 55 || store.Read<short>(secondMember.Id).Value != 77) throw new Exception("Rejected struct write changed leaves");
        session.Write(null, new WriteValueCollection { new() { NodeId = id, AttributeId = Attributes.Value, IndexRange = "8", Value = new DataValue(new Variant(new short[] { 100 })) } }, out rejected, out _);
        if (StatusCode.IsGood(rejected[0])) throw new Exception("Out of range write was accepted");
        ArrayRegressionTests.Sequence(new short[] { 7, 8, 9 }, store.Read<short[]>(tag.Id).Value);
        var nestedId = new NodeId("\"DB\".\"Nested\"", 3);
        var nestedValues = (ExtensionObject[])session.ReadValue(nestedId).Value;
        ArrayRegressionTests.Sequence(new byte[] { 3, 0, 0, 0, 1, 0, 2, 0, 3, 0 }, (byte[])nestedValues[0].Body);
        Write(session, nestedId, new[] { new ExtensionObject(nestedValues[0].TypeId, new byte[] { 3, 0, 0, 0, 4, 0, 5, 0, 6, 0 }) });
        ArrayRegressionTests.Sequence(new short[] { 4, 5, 6 }, store.Read<short[]>(nested.Id).Value);
        session.Close();
    }

    private static void Write(Session session, NodeId id, object value, string? range = null)
    {
        session.Write(null, new WriteValueCollection { new() { NodeId = id, AttributeId = Attributes.Value, IndexRange = range, Value = new DataValue(new Variant(value)) } }, out var results, out _);
        if (StatusCode.IsBad(results[0])) throw new Exception($"Write {id} range={range}: {results[0]}");
    }
}
