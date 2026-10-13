# 读取原生库必须导出的符号清单(native\igw-s7plus\abi-exports.txt)。
# 单独成文件是为了让三个打包脚本共用同一份清单——抄成三份就会漂移。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
$listPath = Join-Path $RepositoryRoot 'native\igw-s7plus\abi-exports.txt'
if (-not (Test-Path -LiteralPath $listPath)) {
    throw "缺少原生库导出清单:$listPath(校验没法做，不能就这么出包)"
}
$exports = Get-Content -LiteralPath $listPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') }
if (-not $exports) { throw "原生库导出清单是空的:$listPath" }
,$exports
