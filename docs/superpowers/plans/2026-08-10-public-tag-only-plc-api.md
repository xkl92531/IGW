# Public Tag-Only PLC API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish only the existing `/Tag/*` contract as the anonymous `IGW PLC 接口` and remove the unused native `/api/v1/plc/*` API family.

**Architecture:** Keep the established Tag wire contract and DCS conversion internals, but remove their dependency on the native PLC API. Centralize public Swagger naming and route classification in `PublicPlcApiPolicy`, let Tag endpoints always call operations with a null user, and leave `/api/v1/*` management security intact.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle/OpenAPI, xUnit, TestServer.

## Global Constraints

- Swagger document title is exactly `IGW PLC 接口`.
- Swagger operation tag is exactly `PLC 操作`.
- Swagger includes only `/Tag/*`; all eleven existing route/method pairs remain unchanged.
- `/Tag/*` bypasses HTTP Basic, user read/write permission, and branch permission checks.
- `/Tag/AddTags` remains publicly callable and may update project point configuration.
- `/api/v1/*` internal management routes keep project-security checks.
- Delete all five `/api/v1/plc/*` routes and their unused implementation.
- Do not touch the pre-existing MES and Web UI working-tree changes.

---

### Task 1: Public Tag metadata and anonymous operation context

**Files:**
- Modify: `tests/IGW.Tests/DcsTagCompatibilityApiTests.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagOperations.cs`

**Interfaces:**
- Consumes: existing `MapDcsTagCompatibilityApi(IEndpointRouteBuilder, IDcsTagRuntime)` and eleven `/Tag/*` endpoints.
- Produces: endpoints tagged `PLC 操作`; endpoint calls that always pass `null` as `UserDef`; internal `DcsTagOperations.MaxBatchSize = 1000` independence from `PlcApi`.

- [ ] **Step 1: Write failing endpoint metadata and hostile-context tests**

Change the metadata assertion to:

```csharp
Assert.All(endpoints, endpoint =>
    Assert.Contains("PLC 操作", endpoint.Metadata.GetMetadata<ITagsMetadata>()!.Tags));
```

Add a test app middleware that inserts a denied user under the legacy item key, then assert the public API still reads `A`:

```csharp
[Fact]
public async Task Public_tag_endpoint_ignores_authenticated_user_permissions()
{
    await using var app = await StartAppAsync(
        DcsTagTestRuntime.WithGoodTag("A", 7),
        beforeMap: app => app.Use(async (context, next) =>
        {
            context.Items["IGW.AuthenticatedUser"] = new UserDef
            {
                CanWrite = false,
                ReadBranches = ["Denied.Branch"]
            };
            await next();
        }));

    var response = await app.GetTestClient().PostAsJsonAsync("/Tag/ReadTag1", new { tag = "A" });
    var body = await response.Content.ReadFromJsonAsync<DcsResponse<JsonElement>>(DcsJson.Options);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.True(body!.IsSuccess, body.ErrorMessage);
    Assert.Equal(7, body.BackResult.GetInt32());
}
```

Extend `StartAppAsync` with optional `Action<WebApplication>? beforeMap` and invoke it before mapping endpoints.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter FullyQualifiedName~DcsTagCompatibilityApiTests --no-restore
```

Expected: metadata test reports `DCS 兼容接口`, and hostile-context read reports a branch-permission failure.

- [ ] **Step 3: Implement minimal anonymous Tag behavior**

- Change `WithTags("DCS 兼容接口")` to `WithTags(PublicPlcApiPolicy.OperationTag)` after Task 2 introduces the policy; until then use the literal `PLC 操作` to keep this task independently green.
- Replace every `User(context)` argument passed to `DcsTagOperations` with `null`.
- Remove the `User(HttpContext)` helper and any endpoint `HttpContext` parameter no longer used.
- Add `private const int MaxBatchSize = 1000;` to `DcsTagOperations` and replace all `PlcApi.MaxBatchSize` references with it.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: all `DcsTagCompatibilityApiTests` pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add tests/IGW.Tests/DcsTagCompatibilityApiTests.cs src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs
git commit -m "feat: expose Tag PLC operations anonymously"
```

---

### Task 2: Public route policy, Swagger identity, and native API removal

**Files:**
- Delete: `src/IGW.Northbound.RestApi/DcsTagRoutePolicy.cs`
- Create: `src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs`
- Delete: `src/IGW.Northbound.RestApi/PlcApi.cs`
- Delete: `tests/IGW.Tests/PlcApiRegressionTests.cs`
- Modify: `tests/IGW.Tests/DcsTagRoutePolicyTests.cs`
- Modify: `tests/IGW.Tests/DcsTagCompatibilityApiTests.cs`
- Modify: `src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs`
- Modify: `src/IGW.Runtime/Program.cs`

**Interfaces:**
- Produces: `PublicPlcApiPolicy.DocumentTitle`, `OperationTag`, `IsProtectedPath(string)`, and `IsPublicSwaggerPath(string?)`.
- Removes: `MapPlcApi`, `PlcApi`, `DcsTagRoutePolicy.IsDcsPath`, and `RequiresWritePermission`.

- [ ] **Step 1: Replace policy tests with the desired public boundary**

Rename the test class to `PublicPlcApiPolicyTests` and assert:

```csharp
[Theory]
[InlineData("/api/v1/health", true)]
[InlineData("/api/v1/project", true)]
[InlineData("/Tag/ReadTag1", false)]
[InlineData("/tag/WriteTag", false)]
[InlineData("/swagger", false)]
public void Protects_internal_api_but_not_public_tag_routes(string path, bool expected) =>
    Assert.Equal(expected, PublicPlcApiPolicy.IsProtectedPath(path));

[Theory]
[InlineData("Tag/ReadTag1", true)]
[InlineData("tag/WriteTags", true)]
[InlineData("api/v1/plc/read", false)]
[InlineData("api/v1/project", false)]
[InlineData(null, false)]
public void Swagger_contains_only_tag_routes(string? path, bool expected) =>
    Assert.Equal(expected, PublicPlcApiPolicy.IsPublicSwaggerPath(path));

[Fact]
public void Public_names_are_stable()
{
    Assert.Equal("IGW PLC 接口", PublicPlcApiPolicy.DocumentTitle);
    Assert.Equal("PLC 操作", PublicPlcApiPolicy.OperationTag);
}
```

- [ ] **Step 2: Capture the failing live Runtime surface before removal**

Build and launch the current Runtime with a temporary empty project on an unused port. Query the real `/swagger/v1/swagger.json` and `/api/v1/plc/devices` endpoints. Assert and record the pre-change failures against the new contract:

```text
OpenAPI info.title == "IGW PLC 与 DCS 兼容接口" (must change)
OpenAPI contains /api/v1/plc/* paths (must be removed)
OpenAPI advertises BasicAuth (must be removed)
GET /api/v1/plc/devices does not return 404 (route still exists)
```

This is the RED evidence for the real hosted API surface; do not replace it with source-text assertions.

- [ ] **Step 3: Run focused policy tests and verify RED**

Run:

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~PublicPlcApiPolicyTests|FullyQualifiedName~DcsTagCompatibilityApiTests" --no-restore
```

Expected: compile failure for missing `PublicPlcApiPolicy`, or assertion failures showing the old Swagger/native API behavior.

- [ ] **Step 4: Implement the public policy and remove native API**

Create:

```csharp
namespace IGW.Northbound.RestApi;

public static class PublicPlcApiPolicy
{
    public const string DocumentTitle = "IGW PLC 接口";
    public const string OperationTag = "PLC 操作";

    public static bool IsProtectedPath(string path) =>
        path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase);

    public static bool IsPublicSwaggerPath(string? path) =>
        path?.StartsWith("Tag/", StringComparison.OrdinalIgnoreCase) == true;
}
```

Then:

- Use `PublicPlcApiPolicy.OperationTag` in `DcsTagCompatibilityApi`.
- Delete `DcsTagRoutePolicy.cs`, `PlcApi.cs`, and `PlcApiRegressionTests.cs`.
- Remove `app.MapPlcApi(host)` and the authenticated-user `ctx.Items` assignment from Runtime.
- Change the security condition to `PublicPlcApiPolicy.IsProtectedPath(path)` and retain the existing generic POST/PUT/DELETE permission classification for `/api/v1/*`.
- Remove all DCS/native-PLC special cases from the middleware.
- Use `PublicPlcApiPolicy.DocumentTitle` for OpenAPI title, Swagger endpoint label, and browser document title.
- Change the OpenAPI description to `IGW PLC 点位查询、读取、写入、校验和配置接口。读取使用实时采集缓存并激活对应点位，写入进入 PLC 设备写队列。`.
- Remove `AddSecurityDefinition` and `AddSecurityRequirement`.
- Use `PublicPlcApiPolicy.IsPublicSwaggerPath` for document inclusion.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Step 3 command. Expected: all selected tests pass.

- [ ] **Step 6: Commit Task 2**

```powershell
git add tests/IGW.Tests/DcsTagRoutePolicyTests.cs tests/IGW.Tests/DcsTagCompatibilityApiTests.cs src/IGW.Northbound.RestApi/DcsTagCompatibilityApi.cs src/IGW.Northbound.RestApi/DcsTagOperations.cs src/IGW.Northbound.RestApi/PublicPlcApiPolicy.cs src/IGW.Northbound.RestApi/DcsTagRoutePolicy.cs src/IGW.Northbound.RestApi/PlcApi.cs tests/IGW.Tests/PlcApiRegressionTests.cs src/IGW.Runtime/Program.cs
git commit -m "refactor: publish only Tag PLC API"
```

---

### Task 3: Full verification and live Swagger/security smoke test

**Files:**
- Verify: all changed files from Tasks 1 and 2.
- Preserve untouched: `src/IGW.Northbound.Mes/MesUploadEngine.cs`, `src/IGW.Northbound.Mes/MesResponseWritePlanner.cs`, `src/IGW.WebUi/src/views/MesFlow.vue`, and `src/IGW.WebUi/src/views/MesFlow.response-writeback.test.cjs`.

**Interfaces:**
- Consumes: compiled Runtime and its `/swagger/v1/swagger.json`, `/Tag/*`, and `/api/v1/*` routes.
- Produces: current build, test, route, Swagger, and security evidence.

- [ ] **Step 1: Run focused and full automated tests**

```powershell
dotnet test tests\IGW.Tests\IGW.Tests.csproj --filter "FullyQualifiedName~PublicPlcApiPolicyTests|FullyQualifiedName~DcsTagCompatibilityApiTests|FullyQualifiedName~DcsTagOperationsTests" --no-restore
dotnet test tests\IGW.Tests\IGW.Tests.csproj --no-restore
```

Expected: zero failed tests.

- [ ] **Step 2: Build Runtime in Release**

```powershell
dotnet build src\IGW.Runtime\IGW.Runtime.csproj -c Release --no-restore --nologo
```

Expected: zero errors.

- [ ] **Step 3: Run live HTTP smoke checks with temporary projects**

Start the Release Runtime on an unused temporary port with project security disabled and assert:

```text
GET /swagger/v1/swagger.json -> 200
OpenAPI info.title == "IGW PLC 接口"
OpenAPI paths count == 11
every OpenAPI path starts with /Tag/
OpenAPI has no securitySchemes and no global security requirement
GET /Tag/ReadConditionCache -> 200 without Authorization
GET /api/v1/plc/devices -> 404
```

Start a second temporary Runtime with `security.enabled=true` and no users, then assert:

```text
GET /Tag/ReadConditionCache -> 200 without Authorization
GET /api/v1/health -> 401 without Authorization
```

- [ ] **Step 4: Inspect diff and working-tree isolation**

```powershell
git diff --check
git status --short
git diff --stat HEAD~2..HEAD
```

Confirm no MES/Web UI working-tree file was staged or altered by this task.

- [ ] **Step 5: Commit only if verification required corrections**

Stage only exact PLC API files and use:

```powershell
git commit -m "fix: complete public PLC API verification"
```
