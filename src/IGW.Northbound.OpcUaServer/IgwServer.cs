using IGW.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace IGW.Northbound.OpcUaServer;

/// <summary>IGW 的 OPC UA 服务器:用自定义 NodeManager 暴露工程地址空间。</summary>
public sealed class IgwServer : StandardServer
{
    private readonly IgwProject _project;
    private readonly IDataHub _hub;
    private readonly Func<string, object, Task<bool>> _writer;
    private readonly Func<string, ArraySlice, ArrayRuntimeValue, Task<ArrayWriteResult>> _arrayWriter;
    private readonly IPointSubscription _pushSub;   // 推送订阅:监控项建立/删除时由 NodeManager Add/Remove 点

    public IgwNodeManager? NodeManager { get; private set; }

    public IgwServer(IgwProject project, IDataHub hub, Func<string, object, Task<bool>> writer,
        Func<string, ArraySlice, ArrayRuntimeValue, Task<ArrayWriteResult>> arrayWriter,
        IPointSubscription pushSub)
    {
        _project = project;
        _hub = hub;
        _writer = writer;
        _arrayWriter = arrayWriter;
        _pushSub = pushSub;
    }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new IgwNodeManager(server, configuration, _project, _hub, _writer, _arrayWriter, _pushSub);
        return new MasterNodeManager(server, configuration, null, new INodeManager[] { NodeManager });
    }

    // 服务启动后挂用户鉴权钩子
    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);
        server.SessionManager.ImpersonateUser += OnImpersonateUser;
    }

    // 用户名/密码校验(对标 KEP 安全策略);AllowAnonymous=false 时拒绝匿名
    private void OnImpersonateUser(object session, ImpersonateEventArgs args)
    {
        var ua = _project.OpcUa ?? new OpcUaSettings();
        if (args.NewIdentity is UserNameIdentityToken u)
        {
            string pw = u.DecryptedPassword is { Length: > 0 } b ? System.Text.Encoding.UTF8.GetString(b) : "";
            bool ok = ua.Users.Any(x =>
                string.Equals(x.Username, u.UserName, StringComparison.Ordinal) &&
                string.Equals(x.Password, pw, StringComparison.Ordinal));
            if (!ok) throw new ServiceResultException(StatusCodes.BadUserAccessDenied, "OPC UA 用户名或密码错误");
            return;   // 认证通过
        }
        // 匿名或其它令牌
        if (!ua.AllowAnonymous)
            throw new ServiceResultException(StatusCodes.BadIdentityTokenRejected, "服务器不允许匿名访问");
    }
}
