[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [ValidateSet('windows','linux')][string]$Platform = 'windows',
    [string]$CacheDirectory = 'D:\sdk\questdb'
)
$ErrorActionPreference = 'Stop'
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'questdb-release.json') -Raw | ConvertFrom-Json
$asset = $release.$Platform
$cache = Join-Path $CacheDirectory $release.version
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$archive = Join-Path $cache $asset.asset
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://github.com/questdb/questdb/releases/download/$($release.version)/$($asset.asset)" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
    throw "QuestDB $Platform archive checksum mismatch: $archive"
}
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
if (!(Test-Path -LiteralPath $runtime -PathType Container)) { throw 'Publish the IGW runtime before staging QuestDB.' }
$destination = Join-Path $runtime 'questdb'
if (Test-Path -LiteralPath $destination) { throw "QuestDB stage already exists: $destination. Use a fresh publish directory." }
$temporary = Join-Path $cache ('extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    & dotnet run --project (Join-Path $PSScriptRoot 'QuestDbPackage\QuestDbPackage.csproj') -- $archive $temporary
    if ($LASTEXITCODE -ne 0) { throw 'QuestDB extraction failed.' }
    $extracted = Join-Path $temporary ([IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileNameWithoutExtension($asset.asset)))
    Copy-Item -LiteralPath $extracted -Destination $destination -Recurse
    if (!(Test-Path -LiteralPath (Join-Path $destination 'legal\LICENSE.txt'))) { throw 'QuestDB distribution license is missing.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'questdb-release.json') -Destination (Join-Path $destination 'igw-release.json')
    Write-Host "Staged QuestDB $($release.version) ($Platform), SHA-256 verified."
}
finally {
    # Only remove the scratch directory just created beneath the verified SDK cache.
    $resolved = [IO.Path]::GetFullPath($temporary)
    $cacheRoot = [IO.Path]::GetFullPath($cache).TrimEnd('\') + '\'
    if ($resolved.StartsWith($cacheRoot, [StringComparison]::OrdinalIgnoreCase) -and !(Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
