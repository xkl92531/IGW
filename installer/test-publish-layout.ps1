[CmdletBinding()]
param(
    [string]$RuntimeDirectory = '',
    [switch]$SourceOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    $RuntimeDirectory = Join-Path $PSScriptRoot '..\dist\IGW'
}

# Logs are stored by the Runtime itself; the installer only initializes the log directory and retires the old log service.
$inno = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'IGW.iss') -Raw
foreach ($token in @('--initialize-logs', 'CreateInputDirPage', 'RetireLogService')) {
    if (-not $inno.Contains($token)) { throw "Installer is missing log storage contract: $token" }
}
foreach ($forbidden in @('LogServiceDist', 'GetSpaceOnDisk', 'SaveStringToFile', '/im openobserve.exe')) {
    if ($inno.Contains($forbidden)) { throw "Installer must not contain: $forbidden" }
}
if ($SourceOnly) {
    Write-Host 'PASS: Windows installer log storage contract verified.'
    exit 0
}

$required = @(
    (Join-Path $RuntimeDirectory 'IGW.Runtime.exe')
    (Join-Path $RuntimeDirectory 'igw_s7plus.dll')
    (Join-Path $RuntimeDirectory 'wwwroot\index.html')
    (Join-Path $RuntimeDirectory 'questdb\bin\java.exe')
    (Join-Path $RuntimeDirectory 'questdb\lib\modules')
    (Join-Path $RuntimeDirectory 'questdb\legal\LICENSE.txt')
    (Join-Path $RuntimeDirectory 'questdb\igw-release.json')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Windows publish layout is missing: $path" }
}
if (Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'logservice')) { throw 'Runtime publish must not contain the retired log service.' }

# 原生库按导出符号校验，不能只看文件在不在：cargo 编译失败时 target\release 里
# 往往还躺着上一次的 DLL，照打进安装包就是「只换网关、原生库还是老的」——
# 网关兼容旧 ABI 不报错，但坏符号会把同一批点一起拖成坏值，现场极难归因。
$nativeDll = Join-Path $RuntimeDirectory 'igw_s7plus.dll'
$exports = & (Join-Path $PSScriptRoot 'native-abi-exports.ps1') -RepositoryRoot (Join-Path $PSScriptRoot '..')
$nativeText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($nativeDll))
$missingExports = $exports | Where-Object { $nativeText -notlike "*$_*" }
if ($missingExports) {
    throw ("igw_s7plus.dll 是旧版本(缺少导出符号:{0})。先在 native\igw-s7plus 重新 cargo build --release 再打包。" -f ($missingExports -join ', '))
}
Write-Host ('PASS: igw_s7plus.dll ABI 导出符号校验通过({0} 个)。' -f $exports.Count)
Write-Host 'PASS: Runtime Windows publish layout verified.'
