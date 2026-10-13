# 从源码构建 IGW 与 PLC 模拟器

本目录迁移自 `E:\项目\IGW` 的当前工作目录，源提交为 `09645d05d4a0a09386d050ad57f63491ce4e648c`，包含最新 PLC 模拟器修复。
保留目标仓库现有的 GitHub origin。完整源码通过 main 分支维护，拉取后按下述步骤构建。

## 必需环境

- Windows x64、.NET 10 SDK（IGW）及 .NET 8 Windows Desktop 目标框架支持（模拟器，可由 SDK 还原）。
- Node.js 22 和 pnpm，按 `pnpm-lock.yaml` 安装前端依赖。
- Rust/Cargo 以及 MSVC C++ 构建工具，用于两个 S7 Plus 原生库。
- 制作 Windows 安装包还需 Inno Setup 6。安装脚本会获取指定版本的 QuestDB 发行文件。

`PlcSim/PlcSimulator/libs` 中两个 DLL 是模拟器的第三方编译依赖，必须随源码保留。
不包含发布包、运行数据、node_modules、bin/obj、Rust target 或私钥；构建会重新生成所需输出。

## 构建可直接运行的 IGW

在仓库根目录的 PowerShell 中执行：

```powershell
cargo build --release --manifest-path .\native\igw-s7plus\Cargo.toml
Push-Location .\src\IGW.WebUi
pnpm install --frozen-lockfile
pnpm build
Pop-Location
dotnet publish .\src\IGW.Runtime\IGW.Runtime.csproj -c Release -r win-x64 --self-contained true -o .\dist\IGW
```

运行 `dist/IGW/IGW.Runtime.exe`，默认界面地址为 `http://localhost:48020`。
Vite 会先生成 Runtime 的 `wwwroot`，发布时会同时包含前端页面；不能只编译后端而跳过前端。
该方式生成基础可运行目录；若需要随包内置 QuestDB 的完整安装版本，使用下面的安装包构建入口。

## 构建 PLC 模拟器

```powershell
dotnet publish .\PlcSim\PlcSimulator\PlcSimulator.csproj -c Release -r win-x64 --self-contained true -o .\dist\PlcSimulator
```

项目自动调用 Cargo 构建模拟器 S7 Plus 原生库。运行 `dist/PlcSimulator/PlcSimulator.exe`。
实际 PLC 工程 JSON/DB 文件属于用户运行数据，需要启动后导入。

## 制作 Windows 完整安装包

```powershell
.\installer\build.bat --no-bump
```

此入口构建原生库、前端、IGW、自带数据库工具和数据迁移工具，打包 QuestDB，检查发布布局并生成 Inno Setup 安装包。
安装包位于 `installer/out`。去掉 `--no-bump` 会递增版本号。

## 继续维护与拉取

后续可执行 `git pull --ff-only` 再按上述步骤构建。
依赖缓存保存在机器上即可，不需要提交缓存和已生成的程序包。

