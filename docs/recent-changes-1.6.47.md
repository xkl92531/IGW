# IGW 1.6.47 最近修改与跨电脑交接

## 获取代码

分支：`codex/openobserve-log-center`

```powershell
git fetch origin
git switch codex/openobserve-log-center
git lfs pull
```

OpenObserve Windows/Linux 包和许可证已提交到 `installer/dependencies/openobserve/`。克隆仓库时需要安装 Git LFS，但不需要再次从 OpenObserve 官网下载。

## 本批主要修改

- 新增 `IGW.Observability`：本机凭据、结构化日志、OTLP/HTTP、限量磁盘队列、查询、载荷分页、保留期和目录迁移。
- 新增独立 `IGW.LogService`，监管内置 OpenObserve，并与 PLC 采集进程隔离。
- 新增原创 IGW 日志工作台，包含范围导航、条件筛选、时间走廊、趋势、设备状态、详情和存储设置。
- 采集、设备状态、MES、HTTP/SignalR 和性能边界增加结构化摘要。
- OpenObserve 首选 `28020/28021`；首次初始化遇到占用时自动选择下一组空闲端口，实际端口显示在日志中心。
- Windows/Linux 安装脚本同时部署日志服务，保留日志目录，并避免结束其他产品的 OpenObserve 实例。
- OpenObserve 1.0.0 发布包通过 Git LFS 随代码交付。

## 构建

完整 Windows 构建：

```powershell
installer\build.bat --no-bump
```

构建机需要 .NET 10 SDK、Rust MSVC 工具链、完整的 Visual Studio C++/Windows SDK、Node/pnpm 和 Inno Setup 6。当前安装包路径和哈希见 `docs/log-center-acceptance-2026-09-13.md`。

## 回归命令

```powershell
dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore --nologo -v:minimal
Set-Location src/IGW.WebUi
pnpm install --frozen-lockfile
pnpm test
pnpm typecheck
pnpm build
Set-Location ../..
$files = @(git ls-files '*.test.cjs')
node --test $files
powershell -NoProfile -ExecutionPolicy Bypass -File installer/test-openobserve-package.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File installer/test-log-service.ps1 -RuntimeDirectory dist/IGW -LogServiceDirectory dist/IGW.LogService
```

验收结果、性能数据、端口占用测试和未执行项记录在 `docs/log-center-acceptance-2026-09-13.md`。
