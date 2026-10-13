using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Drivers.IoTClient;
using IGW.Drivers.OpcUaClient;
using Microsoft.Extensions.Logging.Abstractions;

var observations = new List<object>();
TagBinding Tag(string id) => new() { TagId = id, Def = new() { Name = id, Address = "ns=2;s=" + id, DataType = IgwDataType.Int16, ScanRateMs = 20 }, Address = new(1, 0, 2) };
var reconcile = typeof(DeviceLoop).GetMethod("ReconcileRealtimeSubscriptionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
var close = typeof(DeviceLoop).GetMethod("CloseRealtimeSubscriptionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

// A partially accepted subscription must not turn an unchanged request into endless rebuilds.
await using (var hub = new DataHub())
{
    var tags = new[] { Tag("L.P.A"), Tag("L.P.B") };
    foreach(var tag in tags) { hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId); }
    var driver = new SubDriver { RejectLast = true };
    var loop = new DeviceLoop(driver, tags, hub, NullLogger.Instance, "L.P", scanMode: "active");
    await driver.ConnectAsync(default);
    for (int i=0; i<3; i++) await (Task)reconcile.Invoke(loop, [3_000L + i * 3_000L, CancellationToken.None])!;
    observations.Add(new { probe="partial_subscription", reconciliations=3, creations=driver.Creates, disposals=driver.Disposes });
    await (Task)close.Invoke(loop, [])!;
}

// Verify actual retry latency, not merely the configured retry deadline.
await using (var hub = new DataHub())
{
    var tag = Tag("L.P.A"); hub.Register(tag.TagId, tag.Def); hub.Touch(tag.TagId);
    var driver = new SubDriver { InterruptFirst = true, ReadDelayMs = 2500 };
    var loop = new DeviceLoop(driver, [tag], hub, NullLogger.Instance, "L.P", scanMode: "active", idlePollMs: 5);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    loop.Start(stop.Token);
    try { await driver.Retried.Task.WaitAsync(TimeSpan.FromSeconds(4)); }
    finally { stop.Cancel(); await loop.Completion; }
    observations.Add(new { probe="interrupted_subscription_retry", slowReadMs=2500, actualRetryMs=driver.RetryMs, readsBeforeRetry=driver.Reads });
}

// Model pure timeout with a tiny delay: count amplification without waiting minutes.
await using (var driver = new OpcUaClientDeviceDriver("opc.tcp://audit.invalid:4840",100,true,"","","None"))
{
    var window = new AdaptiveRequestWindow(8,8);
    typeof(OpcUaClientDeviceDriver).GetField("_readWindow",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(driver,window);
    var calls=new List<int>();
    async Task<Opc.Ua.DataValueCollection> Timeout(Opc.Ua.ReadValueIdCollection request,CancellationToken ct)
    { calls.Add(request.Count); await Task.Delay(5,ct); throw new Opc.Ua.ServiceResultException(Opc.Ua.StatusCodes.BadRequestTimeout); }
    int? bad = null;
    string? failureScope = null;
    try
    {
        var samples=await driver.ReadCoreAsync(Enumerable.Range(0,8).Select(i=>Tag("T"+i)).ToArray(),Timeout,()=>true,default);
        bad=samples.Count(x=>!x.Value.IsGood);
    }
    catch(DriverOperationException failure) { failureScope=failure.Scope.ToString(); }
    observations.Add(new {probe="timeout_amplification", points=8, physicalRequests=calls.Count, requestSizes=calls, resultingWindow=window.Current, bad, failureScope});
}

// Cancel after array telegram 1; healthy later telegrams must not continue after cancellation.
using (var cancel = new CancellationTokenSource())
{
    var native = new NativeProbe { AfterRead = count => {if(count==1)cancel.Cancel();} };
    await using var transport=new SiemensPlusTransport(native);
    var arrayTag=new TagBinding {TagId="L.P.Array",Def=new(){Address="DB.Array",DataType=IgwDataType.Bool,Array=new(){ElementType=IgwDataType.Bool,Dimensions=[768],LowerBounds=[0]}},Address=new(0,0,1)};
    bool canceled=false; bool good=false;
    try { good=(await transport.ReadAsync([arrayTag],cancel.Token))[0].Value.IsGood; }
    catch(OperationCanceledException) {canceled=true;}
    observations.Add(new {probe="s7_array_cancel",cancelAfterTelegram=1,actualTelegrams=native.Reads,cancellationObserved=canceled,returnedGood=good});
    var readWindow=new AdaptiveRequestWindow(64,256,()=>0);
    var failing=new NativeProbe {Failure=SiemensPlusResultCode.TransportFailure};
    try {SiemensSymbolicArrayCodec.Read(failing,arrayTag,readWindow);} catch(SiemensPlusException){}
    observations.Add(new{probe="s7_array_transport_capacity_training",initialWindow=64,resultingWindow=readWindow.Current});
}
{
    var transport = new ClassicProbe();
    var parser = (ITagDefinitionAddressParser)new SiemensScanAddressParser();
    var tags = Enumerable.Range(0,8).Select(i => {
        var def = new TagDef {Address=$"DB999.DBW{i*2}",DataType=IgwDataType.Int16};
        if(!parser.TryParse(def,out var address,out var error)) throw new Exception(error);
        return new TagBinding {TagId=def.Address,Def=def,Address=address};
    }).ToArray();
    var samples=await SiemensClassicBlockCodec.ReadAsync(transport,tags,default);
    observations.Add(new {probe="classic_bad_db_fanout",points=tags.Length,physicalRequests=transport.Reads,bad=samples.Count(x=>!x.Value.IsGood)});
}
Console.WriteLine(JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));

sealed class SubDriver : IDeviceDriver, IDriverCapabilityProvider, IDriverSubscriptionProvider
{
    public bool RejectLast, InterruptFirst;
    public int ReadDelayMs, Creates, Disposes, Reads;
    long first;
    public double RetryMs;
    public TaskCompletionSource Retried=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string DriverId=>"audit";
    public ConnectionState State{get;private set;}
    public event Action<ConnectionState>? StateChanged;
    public IAddressParser AddressParser{get;}=new Parser();
    public DriverLimits Limits{get;}=new(){MaxTagsPerRead=100};
    public DriverCapabilitySnapshot Capabilities=>DriverCapabilitySnapshot.FromLimits(Limits) with {SupportsSubscriptions=true};
    public Task ConnectAsync(CancellationToken ct){State=ConnectionState.Connected;StateChanged?.Invoke(State);return Task.CompletedTask;}
    public Task DisconnectAsync(){State=ConnectionState.Disconnected;return Task.CompletedTask;}
    public async Task<IReadOnlyList<TagSample>> ReadAsync(IReadOnlyList<TagBinding> tags,CancellationToken ct){Reads++;await Task.Delay(ReadDelayMs,ct);return tags.Select(t=>new TagSample(t.TagId,DataValue.GoodAt((short)1,DateTime.UtcNow))).ToArray();}
    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> writes,CancellationToken ct)=>Task.FromResult<IReadOnlyList<WriteResult>>([]);
    public Task<IDriverSubscription> SubscribeAsync(IReadOnlyList<TagBinding> tags,Action<IReadOnlyList<TagSample>> publish,CancellationToken ct)
    {
        Creates++;
        if(InterruptFirst && Creates==1){first=Stopwatch.GetTimestamp();throw new DriverOperationException("interrupt",DriverFailureScope.Block,DriverRetryDisposition.RetrySameConnection,StatusCode.BadRequestInterrupted);}
        var accepted=RejectLast?tags.Take(tags.Count-1).ToArray():tags.ToArray();
        publish(accepted.Select(t=>new TagSample(t.TagId,DataValue.GoodAt((short)2,DateTime.UtcNow))).ToArray());
        if(InterruptFirst){RetryMs=Stopwatch.GetElapsedTime(first).TotalMilliseconds;Retried.TrySetResult();}
        return Task.FromResult<IDriverSubscription>(new Lease(accepted.Select(t=>t.TagId).ToArray(),()=>Disposes++));
    }
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    sealed class Lease(IReadOnlyCollection<string> ids,Action dispose):IDriverSubscription {public IReadOnlyCollection<string> TagIds=>ids;public ValueTask DisposeAsync(){dispose();return ValueTask.CompletedTask;}}
    sealed class Parser:IAddressParser {public bool TryParse(string a,IgwDataType t,out TagAddress r,out string? e){r=new(1,0,2);e=null;return true;}}
}
sealed class NativeProbe:ISiemensPlusNativeApi
{
    public int Reads;
    public Action<int>? AfterRead;
    public SiemensPlusResultCode? Failure;
    public SiemensPlusResultCode Connect(string h,ushort p,uint t,bool l)=>SiemensPlusResultCode.Ok;
    public SiemensPlusResultCode Authenticate(string u,string p)=>SiemensPlusResultCode.Ok;
    public SiemensPlusResultCode Probe()=>SiemensPlusResultCode.Ok;
    public SiemensPlusNativeBrowseResult Browse()=>new(SiemensPlusResultCode.Ok,[]);
    public SiemensPlusNativeReadResult Read(IReadOnlyList<string> symbols){Reads++;AfterRead?.Invoke(Reads);return Failure is {} failure?new(failure,[]):new(SiemensPlusResultCode.Ok,symbols.Select(_=>new SiemensPlusNativeValue(SiemensPlusResultCode.Ok,true)).ToArray());}
    public SiemensPlusResultCode Write(IReadOnlyList<SiemensPlusNativeWrite> writes)=>SiemensPlusResultCode.Ok;
    public SiemensPlusResultCode Disconnect()=>SiemensPlusResultCode.Ok;
    public void Dispose(){}
}
sealed class ClassicProbe : ISiemensByteTransport
{
    public int Reads;
    public int MaxPayloadBytes=>200;
    public ValueTask<SiemensByteReadResult> ReadAsync(int db,int offset,int length,CancellationToken ct)
    {Reads++;return ValueTask.FromResult(SiemensByteReadResult.Failed(new StatusCode(Opc.Ua.StatusCodes.BadNodeIdUnknown),"DB does not exist"));}
    public ValueTask<StatusCode> WriteAsync(int db,int offset,ReadOnlyMemory<byte> data,CancellationToken ct)=>throw new NotSupportedException();
}
