# IGW 强制重装脚本(现场应急用)
#
# 适用：升级卡在 "Creating directories..."、取消后重装也卡。
# 原因：安装程序每次都会重设 C:\ProgramData\IGW 的权限，Windows 把权限逐个下发到目录里的每个文件；
#       接口报文、日志文件多达几百万个时，这一步要跑几个小时。
#
# 脚本做的事(全程不删除任何数据)：
#   1. 结束卡住的安装程序，停止网关服务
#   2. 导出配置：工程、授权、数据库连接、端口、证书、密钥等复制到桌面，并打成 zip
#   3. 把文件多的日志/报文目录改名挪到同一个盘的 IGW-大目录-时间戳 下(同盘改名，瞬间完成)
#   4. 运行安装包(默认正常界面，选 -Silent 则静默安装)，装完检查服务
#
# 用法(管理员 PowerShell)：
#   把脚本和安装包放在同一目录，直接运行即可(自动使用该目录下版本最新的 IGW-Setup-*.exe)：
#     powershell -ExecutionPolicy Bypass -File D:\force-reinstall.ps1
#   也可以指定安装包：         加 -Installer "D:\IGW-Setup-1.6.90.exe"
#   只导出配置、不安装：        加 -BackupOnly
#   静默安装(沿用原安装目录和配置)：加 -Silent
#   换安装目录：                加 -InstallDir "D:\IGW"(需先在控制面板卸载旧版，配置不受影响)

param(
    [string]$Installer = "",
    [switch]$BackupOnly,
    [switch]$Silent,
    [string]$InstallDir = ""
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'IGW Gateway'
$Data = Join-Path $env:ProgramData 'IGW'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Backup = Join-Path ([Environment]::GetFolderPath('Desktop')) "IGW-配置备份-$Stamp"
$Parking = Join-Path $env:ProgramData "IGW-大目录-$Stamp"

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用"以管理员身份运行"的 PowerShell 执行本脚本。'
}
if (-not (Test-Path $Data)) { throw "找不到数据目录 $Data" }
if (-not $BackupOnly) {
    # 没指定安装包：在脚本所在目录找版本最新的 IGW-Setup-*.exe；还找不到就让操作人员输入路径
    if (-not $Installer) {
        $found = Get-ChildItem -Path $PSScriptRoot -Filter 'IGW-Setup-*.exe' -File -ErrorAction SilentlyContinue |
            Sort-Object { try { [version]($_.BaseName -replace '^IGW-Setup-', '') } catch { [version]'0.0' } } -Descending |
            Select-Object -First 1
        if ($found) { $Installer = $found.FullName; Write-Host "使用安装包：$Installer" }
        else { $Installer = (Read-Host "未在 $PSScriptRoot 找到 IGW-Setup-*.exe，请输入安装包完整路径").Trim('"', ' ') }
    }
    if (-not $Installer -or -not (Test-Path $Installer)) { throw "找不到安装包：$Installer" }
    $Installer = (Resolve-Path $Installer).Path
}

Step '1/4 结束卡住的安装程序，停止网关服务'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'IGW-Setup*' } | ForEach-Object {
    Write-Host "  结束 $($_.ProcessName) (pid $($_.Id))"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    & sc.exe stop "$ServiceName" | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Service -Name $ServiceName).Status -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
    Write-Host "  服务状态：$((Get-Service -Name $ServiceName).Status)"
}
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'IGW.Runtime' } | ForEach-Object {
    Write-Host "  结束残留进程 IGW.Runtime (pid $($_.Id))"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}

Step "2/4 导出配置到 $Backup"
New-Item -ItemType Directory -Force -Path $Backup | Out-Null
# 数据目录根下的配置文件：工程(project.db + wal/shm)、授权、密钥、数据库连接、端口、日志目录设置、界面偏好
$patterns = @('project.db', 'project.db-wal', 'project.db-shm', 'license.key', 'igw.masterkey', '*.json',
              'ui-preferences.db', 'ui-preferences.db-wal', 'ui-preferences.db-shm', '*.key', '*.pem')
foreach ($pattern in $patterns) {
    Get-ChildItem -Path $Data -Filter $pattern -File -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName -Destination $Backup -Force
        Write-Host "  $($_.Name)  $([math]::Round($_.Length / 1KB)) KB"
    }
}
foreach ($dir in @('pki', 'secrets')) {
    $source = Join-Path $Data $dir
    if (Test-Path $source) {
        Copy-Item $source -Destination (Join-Path $Backup $dir) -Recurse -Force
        Write-Host "  $dir\"
    }
}
if (-not (Test-Path (Join-Path $Backup 'project.db'))) { Write-Warning '没有找到 project.db，请确认数据目录是否正确。' }
$zip = "$Backup.zip"
Compress-Archive -Path (Join-Path $Backup '*') -DestinationPath $zip -Force
Write-Host "  已打包：$zip" -ForegroundColor Green
if ($BackupOnly) { Write-Host ''; Write-Host '只导出配置，未做其它改动。' -ForegroundColor Green; exit 0 }

Step "3/4 把日志/报文等大目录挪到 $Parking(同盘改名，数据不丢)"
# 这些目录只存日志、接口报文和临时数据，不影响工程配置；挪走后安装程序不再逐个处理其中的文件。
$heavy = @('log-payloads', 'observability', 'telemetry-spool', 'project-import')
New-Item -ItemType Directory -Force -Path $Parking | Out-Null
foreach ($name in $heavy) {
    $source = Join-Path $Data $name
    if (Test-Path $source) {
        Move-Item -Path $source -Destination (Join-Path $Parking $name)
        Write-Host "  已挪走 $name"
    }
}
Write-Host '  数据目录剩余内容：'
Get-ChildItem $Data | ForEach-Object { Write-Host "    $($_.Name)" }

Step '4/4 运行安装包'
$arguments = @('/SP-', '/NORESTART')
if ($Silent) { $arguments += @('/SILENT', '/SUPPRESSMSGBOXES') }
if ($InstallDir) { $arguments += "/DIR=`"$InstallDir`"" }
Write-Host "  $Installer $($arguments -join ' ')"
$setup = Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru -Wait
Write-Host "  安装程序退出码：$($setup.ExitCode)"

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Running') {
    & sc.exe start "$ServiceName" | Out-Null
    Start-Sleep -Seconds 5
    $service = Get-Service -Name $ServiceName
}
Write-Host ''
if ($service -and $service.Status -eq 'Running') {
    Write-Host "网关服务已运行。配置备份：$zip" -ForegroundColor Green
} else {
    Write-Warning "网关服务未运行(状态：$($service.Status))。配置备份在 $zip，可把里面的文件放回 $Data 后再启动服务。"
}
Write-Host "挪走的日志/报文在：$Parking"
Write-Host '  不需要旧日志可直接删除(文件多，删除要一段时间，不影响网关)：'
Write-Host "    cmd /c rd /s /q `"$Parking`""
Write-Host '  需要保留则先放着，不要挪回原位置，否则下次升级还会卡。'
