using System.Text;
using System.Text.Json;
using IGW.Abstractions;
using IGW.Core;
using IGW.Core.Security;
using IGW.Licensing;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

/// <summary>授权码限定驱动/功能，以及网关账号、登录凭证和接口权限。</summary>
public sealed class GatewayPermissionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igw-accounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------------- 授权码里的驱动/功能 ----------------

    [Fact]
    public void Old_license_codes_without_lists_allow_every_driver_and_feature()
    {
        var state = new LicenseState { Status = LicenseStatus.Valid, Payload = new LicensePayload() };

        Assert.True(state.AllowsDriver("Siemens.S7"));
        Assert.True(state.AllowsFeature(LicenseCatalog.Mes));
    }

    [Fact]
    public void Listed_drivers_and_features_are_the_only_ones_allowed()
    {
        var state = new LicenseState
        {
            Status = LicenseStatus.Valid,
            Payload = new LicensePayload { Drivers = ["Siemens.S7"], Features = [LicenseCatalog.OpcUaServer] },
        };

        Assert.True(state.AllowsDriver("siemens.s7"));
        Assert.False(state.AllowsDriver("Modbus.Tcp"));
        Assert.True(state.AllowsDriver("Simulator"));             // 模拟器不受限制
        Assert.True(state.AllowsFeature(LicenseCatalog.OpcUaServer));
        Assert.False(state.AllowsFeature(LicenseCatalog.Mes));
        Assert.True(state.AllowsFeature("NotInCatalog"));          // 目录外的功能不受授权限制
    }

    [Fact]
    public void Driver_and_feature_lists_survive_the_license_code_json_and_are_omitted_when_unlimited()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string limited = JsonSerializer.Serialize(new LicensePayload { Drivers = ["Modbus.Tcp"], Features = [] }, options);
        string unlimited = JsonSerializer.Serialize(new LicensePayload(), options);

        LicensePayload parsed = JsonSerializer.Deserialize<LicensePayload>(limited, options)!;
        Assert.Equal(["Modbus.Tcp"], parsed.Drivers!);
        Assert.Empty(parsed.Features!);
        Assert.DoesNotContain("drivers", unlimited);
        Assert.Null(JsonSerializer.Deserialize<LicensePayload>(unlimited, options)!.Drivers);
    }

    [Fact]
    public void Registry_marks_unlicensed_drivers_in_descriptors()
    {
        var registry = new DriverRegistry();
        registry.Register(new FakeFactory("Siemens.S7"));
        registry.Register(new FakeFactory("Modbus.Tcp"));
        registry.Allows = id => id == "Siemens.S7";

        var descriptors = registry.Descriptors.ToDictionary(item => item.DriverId);
        Assert.True(descriptors["Siemens.S7"].Licensed);
        Assert.False(descriptors["Modbus.Tcp"].Licensed);
        Assert.Equal("Modbus.Tcp 显示名", descriptors["Modbus.Tcp"].DisplayName);
    }

    [Fact]
    public void Snapshot_save_rejects_new_channels_of_unlicensed_drivers_but_keeps_existing_ones()
    {
        var current = new IgwProject { Channels = [new ChannelDef { Id = "c1", Name = "老通道", DriverId = "Modbus.Tcp" }] };
        Func<string, bool> allows = id => id == "Siemens.S7";

        var keep = new IgwProject { Channels = [new ChannelDef { Id = "c1", Name = "老通道", DriverId = "Modbus.Tcp" }] };
        Assert.Null(DriverLicenseGuard.CheckSnapshot(current, keep, allows));

        var added = new IgwProject { Channels = [.. keep.Channels, new ChannelDef { Id = "c2", Name = "新通道", DriverId = "Modbus.Tcp" }] };
        Assert.Contains("新通道", DriverLicenseGuard.CheckSnapshot(current, added, allows));

        var switched = new IgwProject { Channels = [new ChannelDef { Id = "c1", Name = "老通道", DriverId = "Omron.Fins" }] };
        Assert.NotNull(DriverLicenseGuard.CheckSnapshot(current, switched, allows));
    }

    [Fact]
    public void Mutations_cannot_create_or_copy_channels_of_unlicensed_drivers()
    {
        var current = new IgwProject { Channels = [new ChannelDef { Id = "c1", Name = "老通道", DriverId = "Modbus.Tcp" }] };
        Func<string, bool> allows = id => id == "Siemens.S7";
        string Doc(string id, string driver) => JsonSerializer.Serialize(new ChannelDef { Id = id, Name = "通道" + id, DriverId = driver }, ProjectStore.Options);
        ProjectMutation Upsert(string id, string driver) =>
            new(ProjectMutationKind.UpsertEntity, ProjectNodeKind.Channel, [id], null, Doc(id, driver));

        Assert.Null(DriverLicenseGuard.CheckMutation(current, Upsert("c1", "Modbus.Tcp"), allows));    // 改老通道的其它设置
        Assert.Null(DriverLicenseGuard.CheckMutation(current, Upsert("c3", "Siemens.S7"), allows));    // 新建已授权驱动
        Assert.NotNull(DriverLicenseGuard.CheckMutation(current, Upsert("c2", "Modbus.Tcp"), allows));
        Assert.NotNull(DriverLicenseGuard.CheckMutation(current,
            new ProjectMutation(ProjectMutationKind.CopyEntities, ProjectNodeKind.Channel, ["c1"], null, null), allows));
        string batch = JsonSerializer.Serialize(new[] { Upsert("c3", "Siemens.S7"), Upsert("c2", "Modbus.Tcp") }, ProjectStore.Options);
        Assert.NotNull(DriverLicenseGuard.CheckMutation(current,
            new ProjectMutation(ProjectMutationKind.Batch, ProjectNodeKind.Project, [], null, batch), allows));
    }

    // ---------------- 账号 ----------------

    [Fact]
    public void First_run_creates_the_single_admin_who_must_change_the_initial_password()
    {
        var store = new GatewayAccountStore(_dir);

        GatewayAccount admin = Assert.Single(store.List());
        Assert.True(admin.IsAdmin);
        Assert.True(admin.MustChangePassword);
        Assert.NotNull(store.Authenticate("admin", GatewayAccountStore.InitialAdminPassword));
        Assert.Null(store.Authenticate("admin", "wrong"));
        Assert.Single(new GatewayAccountStore(_dir).List());   // 重启不会重复建管理员
    }

    [Fact]
    public void Admin_is_unique_and_cannot_be_deleted_or_restricted()
    {
        var store = new GatewayAccountStore(_dir);

        Assert.Throws<GatewayAccountException>(() => store.Create("admin", "", "123456", new Dictionary<string, AccessLevel>()));
        Assert.Throws<GatewayAccountException>(() => store.Delete("admin"));
        Assert.Throws<GatewayAccountException>(() => store.Update("admin", null,
            new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.View }, null, null));
        Assert.Throws<GatewayAccountException>(() => store.Update("admin", null, null, true, null));
    }

    [Fact]
    public void Admin_manages_normal_users_and_their_page_permissions()
    {
        var store = new GatewayAccountStore(_dir);
        store.Create("op1", "操作员", "abc123", new Dictionary<string, AccessLevel>
        {
            [GatewayModules.Monitor] = AccessLevel.View,
            [GatewayModules.Project] = AccessLevel.Edit,
            ["unknown"] = AccessLevel.Edit,
        });

        GatewayAccount user = store.Find("OP1")!;
        Assert.False(user.IsAdmin);
        Assert.True(user.MustChangePassword);
        Assert.Equal(AccessLevel.View, user.LevelOf(GatewayModules.Monitor));
        Assert.Equal(AccessLevel.Edit, user.LevelOf(GatewayModules.Project));
        Assert.Equal(AccessLevel.None, user.LevelOf(GatewayModules.Logs));
        Assert.DoesNotContain("unknown", user.Permissions.Keys);
        Assert.Throws<GatewayAccountException>(() => store.Create("op1", "", "abc123", new Dictionary<string, AccessLevel>()));
        Assert.Throws<GatewayAccountException>(() => store.Create("op2", "", "12", new Dictionary<string, AccessLevel>()));

        store.Update("op1", null, new Dictionary<string, AccessLevel> { [GatewayModules.Logs] = AccessLevel.View }, null, null);
        Assert.Equal(AccessLevel.None, store.Find("op1")!.LevelOf(GatewayModules.Project));
        Assert.Equal(AccessLevel.View, store.Find("op1")!.LevelOf(GatewayModules.Logs));

        store.Update("op1", null, null, true, null);
        Assert.Null(store.Authenticate("op1", "abc123"));   // 停用后不能登录

        store.Delete("op1");
        Assert.Null(store.Find("op1"));
    }

    [Fact]
    public void Users_change_their_own_password_with_the_old_one()
    {
        var store = new GatewayAccountStore(_dir);

        Assert.Throws<GatewayAccountException>(() => store.ChangePassword("admin", "bad", "newpass1"));
        Assert.Throws<GatewayAccountException>(() => store.ChangePassword("admin", "admin", "123"));
        GatewayAccount changed = store.ChangePassword("admin", "admin", "newpass1");

        Assert.False(changed.MustChangePassword);
        Assert.NotNull(store.Authenticate("admin", "newpass1"));
        Assert.Null(store.Authenticate("admin", "admin"));
    }

    [Fact]
    public void Session_tokens_expire_and_stop_working_after_password_or_permission_changes()
    {
        DateTime now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new GatewayAccountStore(_dir);
        var tokens = new GatewaySessionTokens(_dir, () => now);
        store.Create("op1", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Monitor] = AccessLevel.View });

        string token = tokens.Issue(store.Find("op1")!);
        Assert.Equal("op1", tokens.Validate(token, store)!.Username);
        Assert.Equal("op1", new GatewaySessionTokens(_dir, () => now).Validate(token, store)!.Username);   // 重启后仍有效
        Assert.Null(tokens.Validate(token[..^2] + "AA", store));   // 签名被改

        store.Update("op1", null, new Dictionary<string, AccessLevel>(), null, null);
        Assert.Null(tokens.Validate(token, store));   // 改权限后旧凭证失效

        string fresh = tokens.Issue(store.Find("op1")!);
        now += GatewaySessionTokens.Lifetime + TimeSpan.FromMinutes(1);
        Assert.Null(tokens.Validate(fresh, store));   // 过期
    }

    // ---------------- 接口权限 ----------------

    [Theory]
    [InlineData("GET", "/", AccessKind.Public)]
    [InlineData("POST", "/Tag/WriteTag", AccessKind.Public)]
    [InlineData("GET", "/api/v1/health", AccessKind.Public)]
    [InlineData("POST", "/api/v1/auth/login", AccessKind.Public)]
    [InlineData("GET", "/api/v1/tags", AccessKind.Public)]
    [InlineData("POST", "/api/v1/tags/PLC.D.T1/value", AccessKind.Public)]
    [InlineData("POST", "/api/v1/values/read", AccessKind.Public)]
    [InlineData("GET", "/api/v1/devices", AccessKind.Public)]
    [InlineData("GET", "/api/v1/auth/me", AccessKind.Session)]
    [InlineData("GET", "/api/v1/license", AccessKind.Session)]
    [InlineData("POST", "/api/v1/license", AccessKind.Admin)]
    [InlineData("GET", "/api/v1/users", AccessKind.Admin)]
    [InlineData("DELETE", "/api/v1/users/op1", AccessKind.Admin)]
    [InlineData("GET", "/api/v1/mes", AccessKind.Session)]
    [InlineData("POST", "/api/v1/some-new-endpoint", AccessKind.Admin)]
    public void Classifies_public_session_and_admin_endpoints(string method, string path, AccessKind kind) =>
        Assert.Equal(kind, GatewayAccessPolicy.Classify(path, method).Kind);

    [Theory]
    [InlineData("GET", "/api/v1/project/shell", null, GatewayModules.Project, false)]
    [InlineData("PUT", "/api/v1/project", null, GatewayModules.Project, true)]
    [InlineData("POST", "/api/v1/opcua/browse-preview", null, GatewayModules.Project, false)]
    [InlineData("POST", "/api/v1/devices/PLC1/browse", null, GatewayModules.Project, false)]
    [InlineData("POST", "/api/v1/project/mutations", "UpsertEntity", GatewayModules.Project, true)]
    [InlineData("POST", "/api/v1/project/mutations", "SaveMesTasks", GatewayModules.Flow, true)]
    [InlineData("POST", "/api/v1/project/mutations", "ReplaceProjectSections", GatewayModules.Settings, true)]
    [InlineData("GET", "/api/v1/project/mes-tasks", null, GatewayModules.Flow, false)]
    [InlineData("POST", "/api/v1/mes/test", null, GatewayModules.Flow, true)]
    [InlineData("PUT", "/api/v1/log-center/settings/retention", null, GatewayModules.Logs, true)]
    [InlineData("GET", "/api/v1/log-center/filter-options", null, GatewayModules.Logs, false)]
    [InlineData("POST", "/api/v1/scan-timings/tags", null, GatewayModules.Performance, false)]
    [InlineData("POST", "/api/v1/channels/C1/reconnect", null, GatewayModules.Monitor, true)]
    public void Maps_management_endpoints_to_pages(string method, string path, string? kind, string module, bool write)
    {
        AccessRule rule = GatewayAccessPolicy.Classify(path, method, kind);
        Assert.Equal(AccessKind.Module, rule.Kind);
        Assert.Equal(module, rule.Module);
        Assert.Equal(write, rule.Write);
    }

    [Fact]
    public void Checks_login_initial_password_page_level_and_license()
    {
        var store = new GatewayAccountStore(_dir);
        store.ChangePassword("admin", "admin", "newpass1");
        store.Create("op1", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.View });
        GatewayAccount admin = store.Find("admin")!;
        GatewayAccount viewer = store.Find("op1")!;
        var projectRead = new AccessRule(AccessKind.Module, GatewayModules.Project, false);
        var projectWrite = new AccessRule(AccessKind.Module, GatewayModules.Project, true);
        var flowRead = new AccessRule(AccessKind.Module, GatewayModules.Flow, false, "Mes");
        Func<string, bool> all = _ => true;

        Assert.Equal("login_required", GatewayAccessPolicy.Check(AccessRule.Session, null, all)?.Error);
        Assert.Equal("password_change_required", GatewayAccessPolicy.Check(projectRead, viewer, all)?.Error);   // 新用户先改初始密码
        // 还在用初始密码时，查看自己的账号和改密码必须放行
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify("/api/v1/auth/me", "GET"), viewer, all));
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify("/api/v1/auth/password", "POST"), viewer, all));
        viewer.MustChangePassword = false;
        Assert.Null(GatewayAccessPolicy.Check(projectRead, viewer, all));
        Assert.Equal("edit_forbidden", GatewayAccessPolicy.Check(projectWrite, viewer, all)?.Error);
        Assert.Equal("module_forbidden", GatewayAccessPolicy.Check(flowRead, viewer, all)?.Error);
        Assert.Equal("admin_required", GatewayAccessPolicy.Check(AccessRule.Admin, viewer, all)?.Error);
        Assert.Null(GatewayAccessPolicy.Check(projectWrite, admin, all));
        Assert.Equal("feature_unlicensed", GatewayAccessPolicy.Check(flowRead, admin, id => id != "Mes")?.Error);
        Assert.Null(GatewayAccessPolicy.Check(AccessRule.Public, null, all));
    }

    /// <summary>
    /// HSJM「PLC 程序上传后自动注册 IGW」调批量点位注册接口，不登录网页(和 /Tag/AddTags 一样是第三方集成接口)：
    /// 开了接口账号时由接口账号鉴权；已登录网页但工程配置只能看的账号仍不能调。其它工程写接口照旧要登录。
    /// </summary>
    [Fact]
    public void Bulk_tag_registration_is_an_integration_interface_without_web_login()
    {
        AccessRule rule = GatewayAccessPolicy.Classify("/api/v1/project/bulk/tags", "POST");
        Assert.Equal(AccessKind.Public, rule.Kind);
        Assert.True(rule.Write);
        Assert.Null(GatewayAccessPolicy.Check(rule, null, _ => true));

        var store = new GatewayAccountStore(_dir);
        store.Create("viewer", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.View });
        store.Create("editor", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.Edit });
        Assert.Equal("edit_forbidden", GatewayAccessPolicy.Check(rule, store.Find("viewer"), _ => true)?.Error);
        Assert.Null(GatewayAccessPolicy.Check(rule, store.Find("editor"), _ => true));

        Assert.Equal("login_required", GatewayAccessPolicy.Check(
            GatewayAccessPolicy.Classify("/api/v1/project/mutations", "POST", "UpsertEntity"), null, _ => true)?.Error);
    }

    // ---------------- 管理接口的读取不需要登录 ----------------

    /// <summary>第三方系统直接读工程、状态、日志等(GET)不用登录；带了网页登录凭证的仍按页面权限判断。</summary>
    [Theory]
    [InlineData("/api/v1/project")]
    [InlineData("/api/v1/project/shell")]
    [InlineData("/api/v1/drivers")]
    [InlineData("/api/v1/mes")]
    [InlineData("/api/v1/project/mes-tasks")]
    [InlineData("/api/v1/scan-timings")]
    [InlineData("/api/v1/log-center/logs")]
    [InlineData("/api/v1/log-center/filter-options")]
    [InlineData("/api/v1/license")]
    [InlineData("/api/v1/some-new-read")]
    public void Management_reads_do_not_need_a_login(string path)
    {
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify(path, "GET"), null, _ => true));
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify(path, "HEAD"), null, _ => true));
    }

    /// <summary>
    /// 仍要登录：所有修改、只读但会连设备的 POST(浏览、预览)、账号相关、用户管理，
    /// 以及带密钥的工程/模板导出、浏览网关服务器文件夹、会触发扫盘的日志清理。
    /// </summary>
    [Theory]
    [InlineData("PUT", "/api/v1/project")]
    [InlineData("POST", "/api/v1/project/mutations")]
    [InlineData("POST", "/api/v1/opcua/browse-preview")]
    [InlineData("POST", "/api/v1/channels/C1/reconnect")]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("GET", "/api/v1/ui/preferences")]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("GET", "/api/v1/project/export")]
    [InlineData("GET", "/api/v1/templates/T1/export")]
    [InlineData("GET", "/api/v1/mes/flow-templates/F1/export")]
    [InlineData("GET", "/api/v1/runtime-logs/directories")]
    [InlineData("GET", "/api/v1/log-center/cleanup")]
    public void Writes_and_sensitive_reads_still_need_a_login(string method, string path) =>
        Assert.Equal("login_required", GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify(path, method), null, _ => true)?.Error);

    [Fact]
    public void Anonymous_reads_still_respect_the_license_and_logged_in_users_keep_their_page_permissions()
    {
        // 授权码不含 MES：不登录也读不到采集流程
        Assert.Equal("feature_unlicensed", GatewayAccessPolicy.Check(
            GatewayAccessPolicy.Classify("/api/v1/project/mes-tasks", "GET"), null, id => id != "Mes")?.Error);
        // 已登录、这个页面「不可见」的账号：网页上照旧看不到
        var store = new GatewayAccountStore(_dir);
        store.Create("op1", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Logs] = AccessLevel.View });
        GatewayAccount viewer = store.Find("op1")!;
        viewer.MustChangePassword = false;
        Assert.Equal("module_forbidden", GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify("/api/v1/project", "GET"), viewer, _ => true)?.Error);
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify("/api/v1/log-center/logs", "GET"), viewer, _ => true));
    }

    [Theory]
    [InlineData("POST", "/api/v1/tags/PLC.D.T1/value", true)]
    [InlineData("PUT", "/api/v1/tags/PLC.D.T1/array/slice", true)]
    [InlineData("GET", "/api/v1/tags/PLC.D.T1/value", false)]
    [InlineData("POST", "/api/v1/values/read", false)]
    public void Plc_value_writes_are_public_but_marked_as_project_writes(string method, string path, bool write)
    {
        AccessRule rule = GatewayAccessPolicy.Classify(path, method);
        Assert.Equal(AccessKind.Public, rule.Kind);
        Assert.Equal(write, rule.Write);
        if (write) Assert.Equal(GatewayModules.Project, rule.Module);
    }

    [Fact]
    public void Web_users_need_project_edit_to_write_plc_values_while_third_party_calls_stay_open()
    {
        var store = new GatewayAccountStore(_dir);
        store.Create("viewer", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.View });
        store.Create("editor", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Project] = AccessLevel.Edit });
        store.Create("monitor", "", "abc123", new Dictionary<string, AccessLevel> { [GatewayModules.Monitor] = AccessLevel.Edit });
        Func<string, bool> all = _ => true;

        // 没带网页登录凭证：第三方系统，按工程里的接口账号判断(这里不管)
        Assert.Null(GatewayAccessPolicy.Check(AccessRule.PlcWrite, null, all));
        Assert.Equal("edit_forbidden", GatewayAccessPolicy.Check(AccessRule.PlcWrite, store.Find("viewer"), all)?.Error);
        Assert.Equal("edit_forbidden", GatewayAccessPolicy.Check(AccessRule.PlcWrite, store.Find("monitor"), all)?.Error);
        Assert.Null(GatewayAccessPolicy.Check(AccessRule.PlcWrite, store.Find("editor"), all));
        Assert.Null(GatewayAccessPolicy.Check(AccessRule.PlcWrite, store.Find("admin"), all));
        // 只读数据接口：谁都放行
        Assert.Null(GatewayAccessPolicy.Check(GatewayAccessPolicy.Classify("/api/v1/values/read", "POST"), store.Find("viewer"), all));
    }

    [Theory]
    [InlineData("""{"kind":"SaveMesTasks","documentJson":"{\"kind\":\"x\"}"}""", "SaveMesTasks")]
    [InlineData("""{"entityIds":["a"],"documentJson":"[{\"kind\":\"SaveMesTasks\"}]","kind":"Batch"}""", "Batch")]
    [InlineData("""{"entityKind":{"kind":"nested"},"kind":6}""", "ReplaceProjectSections")]
    [InlineData("""{"documentJson":null}""", null)]
    [InlineData("""[1,2]""", null)]
    public void Reads_only_the_top_level_mutation_kind(string json, string? expected) =>
        Assert.Equal(expected, GatewayAccessPolicy.FindTopLevelKind(Encoding.UTF8.GetBytes(json)));

    private sealed class FakeFactory(string id) : IDriverFactory
    {
        public string DriverId => id;
        public DriverDescriptor Describe() => new() { DriverId = id, DisplayName = id + " 显示名" };
        public IDeviceDriver CreateDevice(ChannelDef channel, DeviceDef device) => throw new NotSupportedException();
    }
}
