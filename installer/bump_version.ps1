# 打包前自增版本号:补丁号达到 100 时向次版本进位(例如 1.5.99 → 1.6.0)
# 由 build.bat 和 PS 打包流程在 ISCC 之前调用;IGW.iss 编译时读取 version.txt 作为 AppVer。
$ErrorActionPreference = 'Stop'
$vf = Join-Path $PSScriptRoot 'version.txt'
$v = if (Test-Path $vf) { (Get-Content -Path $vf -Raw).Trim() } else { '1.0.0' }
$p = @($v -split '\.')
while ($p.Count -lt 3) { $p += '0' }
$patch = [int]$p[2] + 1
if ($patch -ge 100) {
    $p[1] = [string]([int]$p[1] + 1)
    $p[2] = '0'
}
else {
    $p[2] = [string]$patch
}
$new = ($p -join '.')
[System.IO.File]::WriteAllText($vf, $new)   # 无 BOM,ASCII,供 ISPP FileRead 读取
Write-Output $new
