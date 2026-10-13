# IGW 工业网关 · libigw_s7plus.so 交叉编译脚本(Windows -> linux-x64)
# 产物:native\igw-s7plus\target\x86_64-unknown-linux-gnu\release\libigw_s7plus.so
# 编完重跑 deploy\package-docker.ps1 即自动打进 zip(csproj 按"文件存在"条件自动带上)
#
# 工具链按「项目自带 → 系统已装 → 装到项目内」的顺序找，换一台机器也能重建:
#   Rust : rundata\cargo(离线套件) 或这台机器自己的 rustup/cargo
#   zig  : rundata\ziglang(pip --target 装到项目内) 或 PATH 上的 zig，当交叉 C 编译器/链接器
#   包装器: deploy\zigwrap.cs(在版本库里) 编成 rundata\zig-build\zig-cc.exe / zig-ar.exe，改写目标三元组
#
# 用法:powershell -ExecutionPolicy Bypass -File deploy\build-s7plus-linux.ps1 [-RustDistServer <镜像>]
[CmdletBinding()]
param(
  [string]$RustDistServer = 'https://mirrors.huaweicloud.com/rustup'   # tuna/aliyun 缺 2026-08-20 组件目录,华为云全
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deployDir = $PSScriptRoot
$cargoHome = Join-Path $root 'rundata\cargo'
$rustupHome = Join-Path $root 'rundata\rustup'
$zigBuild = Join-Path $root 'rundata\zig-build'
$zigHome = Join-Path $root 'rundata\ziglang'
$proj = Join-Path $root 'native\igw-s7plus'
$so = Join-Path $proj 'target\x86_64-unknown-linux-gnu\release\libigw_s7plus.so'
if (-not (Test-Path $proj)) { throw "缺少源码目录:$proj" }

# ---------- 0. Rust 工具链(离线套件优先;rundata\ 不进版本库,换机器时退回系统 rustup) ----------
$cargo = Join-Path $cargoHome 'bin\cargo.exe'
$rustup = Join-Path $cargoHome 'bin\rustup.exe'
if ((Test-Path $cargo) -and (Test-Path $rustup)) {
  $env:CARGO_HOME = $cargoHome
  $env:RUSTUP_HOME = $rustupHome
  $env:RUSTUP_DIST_SERVER = $RustDistServer
  $env:RUSTUP_UPDATE_ROOT = "$RustDistServer/rustup"
  Write-Host "[0/4] 使用项目自带 Rust:$cargo"
}
else {
  $systemCargo = Get-Command cargo -ErrorAction SilentlyContinue
  $systemRustup = Get-Command rustup -ErrorAction SilentlyContinue
  if (-not ($systemCargo -and $systemRustup)) {
    throw '既没有 rundata\cargo 离线工具链,也没有系统 rustup/cargo;先装一个再重试'
  }
  $cargo = $systemCargo.Source
  $rustup = $systemRustup.Source
  Write-Host "[0/4] 使用系统 Rust:$cargo"
}

# ---------- 1. zig(项目内 → PATH → pip 装到项目内,不污染系统 Python) ----------
$zig = Join-Path $zigHome 'ziglang\zig.exe'
if (-not (Test-Path $zig)) {
  $onPath = Get-Command zig -ErrorAction SilentlyContinue
  if ($onPath) { $zig = $onPath.Source }
  else {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) { throw '找不到 python(用于 pip install ziglang);装好 Python 或自备 zig 后重试' }
    Write-Host '[1/4] 安装 ziglang(pip --target rundata\ziglang)...'
    & $python.Source -m pip install --quiet --disable-pip-version-check --target $zigHome ziglang
    if ($LASTEXITCODE -ne 0) { throw 'ziglang 安装失败' }
    $zig = Join-Path $zigHome 'ziglang\zig.exe'
  }
}
if (-not (Test-Path $zig)) { throw '仍找不到 zig.exe' }
Write-Host "[1/4] zig 就绪:$(& $zig version)"

# ---------- 2. rust-std(x86_64-unknown-linux-gnu) ----------
# 目标产物由 zig 链接,但 build script 仍按宿主编译:宿主缺 MSVC 库时用 GNU 宿主工具链,
# 那就得给 GNU 工具链也装上这个 target,否则编依赖时报 can't find crate for `core`。
$hostToolchain = $null
$targets = & $rustup target list --installed
if ($targets -notcontains 'x86_64-unknown-linux-gnu') {
  Write-Host "[2/4] 安装 rust-std x86_64-unknown-linux-gnu(镜像:$RustDistServer)..."
  & $rustup target add x86_64-unknown-linux-gnu
  if ($LASTEXITCODE -ne 0) { throw 'rustup target add 失败(可换 -RustDistServer https://rsproxy.cn/rustup 等镜像重试)' }
}
else { Write-Host '[2/4] rust-std x86_64-unknown-linux-gnu 已就绪' }
$hasMsvcLibs = Test-Path 'C:\Program Files\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\lib\x64\msvcrt.lib'
if (-not $hasMsvcLibs) {
  $gnuTargets = & $rustup target list --installed --toolchain stable-x86_64-pc-windows-gnu 2>$null
  if ($LASTEXITCODE -eq 0) {
    if ($gnuTargets -notcontains 'x86_64-unknown-linux-gnu') {
      Write-Host '        宿主缺 MSVC 库,给 GNU 宿主工具链补装 rust-std...'
      & $rustup target add x86_64-unknown-linux-gnu --toolchain stable-x86_64-pc-windows-gnu
      if ($LASTEXITCODE -ne 0) { throw 'GNU 宿主工具链补装 rust-std 失败' }
    }
    $hostToolchain = '+stable-x86_64-pc-windows-gnu'
    Write-Host '        build script 走 GNU 宿主工具链'
  }
}

# ---------- 3. zig 包装器(把 LLVM 风格 --target=x86_64-unknown-linux-gnu 改写为 zig 认的 -target x86_64-linux-gnu.2.17;
#             rustc 调 CC 型链接器时不传 target,包装器负责注入,否则 zig 按宿主 Windows 链接去找 dl.dll) ----------
$ccExe = Join-Path $zigBuild 'zig-cc.exe'
$arExe = Join-Path $zigBuild 'zig-ar.exe'
$wrapSource = Join-Path $deployDir 'zigwrap.cs'
if (-not (Test-Path $wrapSource)) { throw "缺少包装器源码:$wrapSource" }
$stale = (Test-Path $ccExe) -and ((Get-Item $wrapSource).LastWriteTimeUtc -gt (Get-Item $ccExe).LastWriteTimeUtc)
if ($stale -or -not ((Test-Path $ccExe) -and (Test-Path $arExe))) {
  Write-Host '[3/4] 生成 zig 包装器(csc)...'
  New-Item -ItemType Directory -Force -Path $zigBuild | Out-Null
  $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
  if (-not (Test-Path $csc)) { throw "缺少 $csc(.NET Framework 4 的 C# 编译器)" }
  & $csc /nologo "/out:$ccExe" $wrapSource
  if ($LASTEXITCODE -ne 0) { throw '包装器编译失败' }
  & $csc /nologo "/out:$arExe" $wrapSource
  if ($LASTEXITCODE -ne 0) { throw '包装器编译失败' }
}
else { Write-Host '[3/4] zig 包装器已就绪' }

# ---------- 4. 交叉编译 ----------
Write-Host '[4/4] cargo build --release --target x86_64-unknown-linux-gnu...'
$env:IGW_ZIG = $zig
$env:CC_x86_64_unknown_linux_gnu = $ccExe
$env:AR_x86_64_unknown_linux_gnu = $arExe
$env:CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER = $ccExe
Push-Location $proj
try {
  if ($hostToolchain) { & $cargo $hostToolchain build --release --target x86_64-unknown-linux-gnu }
  else { & $cargo build --release --target x86_64-unknown-linux-gnu }
  if ($LASTEXITCODE -ne 0) { throw "cargo build 失败(exit $LASTEXITCODE)" }
}
finally { Pop-Location }

if (-not (Test-Path $so)) { throw "编译完成但产物缺失:$so" }
$kb = [math]::Round((Get-Item $so).Length / 1KB)
$head = [System.IO.File]::ReadAllBytes($so)[0..3]
if (-not ($head[0] -eq 0x7F -and $head[1] -eq 0x45 -and $head[2] -eq 0x4C -and $head[3] -eq 0x46)) { throw '产物不是 ELF 文件!' }
# ABI 自检:现场出过「只换网关、原生库还是老的」——那会让坏符号把同一批点一起拖成坏值。
$bytes = [System.IO.File]::ReadAllBytes($so)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$required = & (Join-Path $PSScriptRoot '..\installer\native-abi-exports.ps1') -RepositoryRoot (Join-Path $PSScriptRoot '..')
foreach ($export in $required) {
  if ($text -notlike "*$export*") { throw "产物缺少导出符号 $export(ABI 版本不对,别拿去打包)" }
}
Write-Host ("完成:libigw_s7plus.so({0} KB,ELF + ABI v4 导出符号校验通过)" -f $kb) -ForegroundColor Green
Write-Host "下一步:重跑 deploy\package-docker.ps1 会自动把它打进 zip"
