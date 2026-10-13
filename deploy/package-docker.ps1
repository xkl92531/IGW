# IGW 工业网关 · Windows 一键打包脚本(Docker 部署包,deploy\ 自包含套件)
# 产物:deploy\out\igw-<版本>-docker-linux-x64.zip
#        (linux-x64 自包含发布 app\ + Dockerfile/compose/deploy.sh + basedeps.tar 基础镜像离线包)
# 上传 Ubuntu 后两种方式任选其一即可运行:
#   A) unzip igw-<版本>-docker-linux-x64.zip && cd igw-docker && sudo bash deploy.sh
#   B) sudo bash <(unzip -p igw-<版本>-docker-linux-x64.zip igw-docker/deploy.sh) igw-<版本>-docker-linux-x64.zip
# 前置:dotnet SDK 10、node(pnpm 装过依赖);若 vite 报依赖缺失,先在 src\IGW.WebUi 执行:
#   $env:CI='true'; pnpm install
# 基础镜像来源(默认 podman,不可用时回退 deploy\pull-base-image.py 注册表直拉,均免 Docker Desktop):
#   需 podman machine 可用(podman info 成功);镜像缓存在 rundata\image-cache\basedeps.tar
# 用法:powershell -ExecutionPolicy Bypass -File deploy\package-docker.ps1 [-SkipWebUi] [-SkipBaseImage] [-RefreshBaseImage]
[CmdletBinding()]
param(
  [switch]$SkipWebUi,        # 跳过 Web 界面构建,复用现有 src\IGW.Runtime\wwwroot
  [switch]$SkipBaseImage,    # 不捆绑基础镜像(目标服务器需能联网拉 mcr,或自行 docker load)
  [switch]$RefreshBaseImage, # 忽略缓存,强制重新拉取基础镜像
  [switch]$AllowStaleNative, # 允许缺少/旧版 libigw_s7plus.so(默认拒绝:旧原生库会让坏符号拖垮整批点位)
  [string]$BaseImage = 'mcr.microsoft.com/dotnet/runtime-deps:10.0-noble'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # 仓库根(只读引用,不改仓库其它文件)
$deployDir = $PSScriptRoot                        # deploy\ 自包含套件目录
$version = (Get-Content (Join-Path $root 'installer\version.txt') -Raw).Trim()
$outDir = Join-Path $deployDir 'out'
$stageRoot = Join-Path $outDir 'stage'
$stage = Join-Path $stageRoot 'igw-docker'
$zipPath = Join-Path $outDir ('igw-{0}-docker-linux-x64.zip' -f $version)
$imageCache = Join-Path $root 'rundata\image-cache\basedeps.tar'

Write-Host ("== IGW Docker 部署包打包 v{0} ==" -f $version) -ForegroundColor Cyan

# ---------- 1. Web 界面(产物写入 src\IGW.Runtime\wwwroot,发布时自动带上) ----------
if (-not $SkipWebUi) {
  $webUi = Join-Path $root 'src\IGW.WebUi'
  if (Test-Path (Join-Path $webUi 'node_modules')) {
    Write-Host '[1/6] 构建 Web 界面(Vite)...'
    Push-Location $webUi
    try {
      # 不用 pnpm run build:无 TTY 环境它会拒绝 purge node_modules
      & node 'node_modules\vite\bin\vite.js' build
      if ($LASTEXITCODE -ne 0) {
        throw "vite build 失败(exit $LASTEXITCODE)。若报依赖缺失:先在 src\IGW.WebUi 执行 `$env:CI='true'; pnpm install"
      }
    } finally { Pop-Location }
  }
  elseif (Test-Path (Join-Path $root 'src\IGW.Runtime\wwwroot\index.html')) {
    Write-Warning 'WebUi 缺 node_modules,复用现有 wwwroot(如需最新界面请先在 src\IGW.WebUi 执行 pnpm install,或用 -SkipWebUi 显式跳过)'
  }
  else {
    throw 'WebUi 未构建且无 wwwroot:请先在 src\IGW.WebUi 执行 pnpm install'
  }
}
else {
  if (-not (Test-Path (Join-Path $root 'src\IGW.Runtime\wwwroot\index.html'))) {
    throw '-SkipWebUi 但 src\IGW.Runtime\wwwroot 不存在:至少完整跑一次'
  }
  Write-Host '[1/6] 跳过 Web 界面构建(-SkipWebUi)' -ForegroundColor Yellow
}

# ---------- 2. 准备打包目录 ----------
Write-Host '[2/6] 准备打包目录...'
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app') | Out-Null

# ---------- 3. dotnet publish(linux-x64 自包含,目标机/容器无需 .NET) ----------
Write-Host '[3/6] dotnet publish linux-x64(自包含)...'
# -p:Version 必须传：程序集版本就是现场在 Web 界面和日志导出里看到的版本号，
# 漏掉的话容器里跑的永远显示 1.0.0，出问题时无法判断装的是哪一版。
dotnet publish (Join-Path $root 'src\IGW.Runtime\IGW.Runtime.csproj') `
  -c Release -r linux-x64 --self-contained true -p:Version=$version `
  -o (Join-Path $stage 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败(exit $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $stage 'app\IGW.Runtime'))) { throw '发布产物缺少 IGW.Runtime 可执行文件' }
& (Join-Path $root 'installer\package-questdb.ps1') -RuntimeDirectory (Join-Path $stage 'app') -Platform linux

# S7CommPlus 原生库:必须与网关同版本发布。现场出过「只换网关、原生库还是几天前的」——
# 网关兼容旧 ABI 不会报错,但旧库没有逐项坏点隔离(一个解析不了的符号会把同一批点全拖成坏值),
# 也没有变化订阅导出——后者更阴:托管侧拿不到导出就静默回退轮询,不报错,日志上看不出来。
# 所以这里按导出符号校验,不合格就拒绝出包(确实不需要 S7 Plus 时用 -AllowStaleNative)。
$nativeSo = Join-Path $stage 'app\libigw_s7plus.so'
if (Test-Path $nativeSo) {
  $nativeText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($nativeSo))
  # 先落到变量再进管道：清单脚本返回的是 ,$exports(防单元素退化)，
  # 直接接管道只展开一层，整个数组会被当成一个元素、字符串化后必然匹配不上。
  $required = & (Join-Path $root 'installer\native-abi-exports.ps1') -RepositoryRoot $root
  $missing = $required | Where-Object { $nativeText -notlike "*$_*" }
  if ($missing -and -not $AllowStaleNative) {
    throw ("libigw_s7plus.so 是旧版本(缺少导出符号:{0})。先跑 deploy\build-s7plus-linux.ps1 重新交叉编译再打包;确实要打包旧库请加 -AllowStaleNative。" -f ($missing -join ', '))
  }
  if ($missing) { Write-Warning ("已按 -AllowStaleNative 打包旧版原生库(缺少:{0})" -f ($missing -join ', ')) }
  else { Write-Host ('        已包含 libigw_s7plus.so({0} KB,ABI 导出符号校验通过)' -f [math]::Round((Get-Item $nativeSo).Length / 1KB)) }
}
elseif ($AllowStaleNative) {
  Write-Warning '未包含 libigw_s7plus.so:S7-1500 符号地址(S7CommPlus)不可用;Classic 绝对地址不受影响。'
}
else {
  throw '缺少 libigw_s7plus.so:S7-1500 符号地址(S7CommPlus)会整个不可用。先跑 deploy\build-s7plus-linux.ps1;确实不需要请加 -AllowStaleNative。'
}

# ---------- 4. 基础镜像离线包(服务器无外网/DNS 坏时,deploy.sh 自动 docker load 包内 basedeps.tar) ----------
if ($SkipBaseImage) {
  Write-Host '[4/6] 跳过基础镜像捆绑(-SkipBaseImage):目标服务器须能自行拉取/加载基础镜像' -ForegroundColor Yellow
}
else {
  Write-Host "[4/6] 基础镜像包($BaseImage)..."
  if ((Test-Path $imageCache) -and -not $RefreshBaseImage) {
    Write-Host '        复用缓存 rundata\image-cache\basedeps.tar(-RefreshBaseImage 强制更新)'
  }
  else {
    # 优先 podman(machine 可用时);否则 deploy\pull-base-image.py 走注册表 API 直拉(免守护进程)
    $usedPodman = $false
    if (Get-Command podman -ErrorAction SilentlyContinue) {
      podman info *> $null
      if ($LASTEXITCODE -eq 0) {
        Write-Host '        podman 拉取并导出 docker-archive...'
        podman pull $BaseImage
        if ($LASTEXITCODE -ne 0) { throw "podman pull 失败(exit $LASTEXITCODE)" }
        New-Item -ItemType Directory -Force -Path (Split-Path $imageCache) | Out-Null
        podman save --format docker-archive -o $imageCache $BaseImage
        if ($LASTEXITCODE -ne 0) { throw "podman save 失败(exit $LASTEXITCODE)" }
        $usedPodman = $true
      }
    }
    if (-not $usedPodman) {
      $py = 'D:\Program Files\Python\Python313\python.exe'
      if (-not (Test-Path $py)) { $py = 'python' }
      Write-Host '        podman 不可用,改用注册表 API 直拉(python)...'
      New-Item -ItemType Directory -Force -Path (Split-Path $imageCache) | Out-Null
      & $py (Join-Path $deployDir 'pull-base-image.py') $BaseImage $imageCache
      if ($LASTEXITCODE -ne 0) { throw "基础镜像直拉失败(exit $LASTEXITCODE):检查网络,或加 -SkipBaseImage 先打包" }
    }
  }
  if (-not (Test-Path $imageCache)) { throw "基础镜像缓存缺失:$imageCache" }
  Copy-Item $imageCache (Join-Path $stage 'basedeps.tar') -Force
  Write-Host ("        已放入包内:basedeps.tar({0} MB)" -f [math]::Round((Get-Item $imageCache).Length / 1MB, 1))
}

# ---------- 5. Docker 部署文件 ----------
Write-Host '[5/6] 组装 Docker 部署文件...'
Copy-Item (Join-Path $deployDir 'Dockerfile') $stage -Force

# compose:镜像 tag 打上当次版本号(LF + 无 BOM)
# 必须按 UTF-8 读:PS 5.1 的 Get-Content 会把无 BOM 的 UTF-8 当 GBK 读,中文注释吞掉换行,
# 把 restart / volumes 行并进注释(数据落到匿名卷、容器不自启)
$compose = [System.IO.File]::ReadAllText((Join-Path $deployDir 'docker-compose.yml'), [System.Text.Encoding]::UTF8)
$compose = $compose -replace 'image:\s*igw:[\w.\-]+', ('image: igw:{0}' -f $version)
[System.IO.File]::WriteAllText((Join-Path $stage 'docker-compose.yml'), $compose, (New-Object System.Text.UTF8Encoding($false)))

# deploy.sh:强制 LF + 无 BOM(bash 兼容)
$deploy = [System.IO.File]::ReadAllText((Join-Path $deployDir 'deploy.sh'))
$deploy = ($deploy.TrimStart([char]0xFEFF)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText((Join-Path $stage 'deploy.sh'), $deploy, (New-Object System.Text.UTF8Encoding($false)))

# README(带 BOM,Windows 记事本打开不乱码)
$baseNote = if ($SkipBaseImage) { '  - 本包未捆绑基础镜像:服务器需能访问 mcr.microsoft.com,或自行 docker load 后再跑 deploy.sh' } else { '  - 已捆绑 basedeps.tar 基础镜像:服务器无需外网,deploy.sh 自动 docker load(完全离线可部署)' }
$readme = @"
IGW 工业网关 v$version · Docker 部署包(linux-x64)
====================================================

一、上传
  把本 zip 整个上传到 Ubuntu 服务器(例:/opt/)。

二、一键部署(二选一)
  方式A(推荐,直观):
    sudo apt-get install -y unzip        # 已有 unzip 可跳过
    unzip igw-$version-docker-linux-x64.zip -d /opt
    cd /opt/igw-docker
    sudo bash deploy.sh

  方式B(一行命令,免手工解压):
    sudo bash `<(unzip -p igw-$version-docker-linux-x64.zip igw-docker/deploy.sh) igw-$version-docker-linux-x64.zip

  deploy.sh 自动完成:安装 Docker(如缺)-> 基础镜像(本地已有/包内 basedeps.tar 离线加载/联网拉取)
  -> 构建镜像 -> 启动容器 -> 健康检查。

三、访问
  管理界面 / REST API : http://<服务器IP>:48020/
  OPC UA Server       : opc.tcp://<服务器IP>:4840

四、数据与授权(igwdata 目录,位于解压根目录 = igw-docker 旁边,zip 内容之外)
  igwdata/data/        工程/授权/日志/SQLite/OPC UA 证书(容器内 /var/lib/igw)
  igwdata/machine-id   机器码标识(首次部署自动生成,授权绑定,勿删)
  部署/升级脚本永不覆盖或删除 igwdata,由人工管理(备份=整个目录拷走,恢复=拷回)。
  想放别处:IGW_HOST_DATA=/var/lib/igwdata sudo bash deploy.sh(须与 igw-docker 同机)

五、常用命令(在 igw-docker 目录)
  docker compose logs -f igw      # 查日志
  docker compose restart igw      # 重启
  docker compose down             # 停止
  sudo bash deploy.sh             # 升级:重跑即可,igw-docker 随便删换,igwdata 不受影响

六、说明
$baseNote
  - 镜像自包含 .NET 运行时,服务器无需安装 .NET。
  - 包内未含 libigw_s7plus.so 时,S7-1500 符号地址(S7CommPlus)不可用,Classic 绝对地址不受影响。
"@
[System.IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 6. 压 zip(显式 / 分隔符,保证 Linux unzip 识别目录结构) ----------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
  }
}
finally { $archive.Dispose() }

$mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ("[6/6] 完成:{0}({1} MB)" -f $zipPath, $mb) -ForegroundColor Green
Write-Host ''
Write-Host '上传 Ubuntu 后一键部署(在该机器上执行):' -ForegroundColor Cyan
Write-Host ("  sudo bash <(unzip -p igw-{0}-docker-linux-x64.zip igw-docker/deploy.sh) igw-{0}-docker-linux-x64.zip" -f $version)
