[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("igw-tests-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $resultsDirectory | Out-Null
    $arguments = @(
        'test',
        (Join-Path $repoRoot 'IGW.slnx'),
        '-c', $Configuration,
        '--nologo',
        '--logger', 'trx;LogFileName=igw-tests.trx',
        '--results-directory', $resultsDirectory
    )
    if (-not $Restore) { $arguments += '--no-restore' }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Backend tests failed with exit code $LASTEXITCODE."
    }

    $resultFiles = @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -Recurse -File)
    if ($resultFiles.Count -eq 0) {
        throw 'Backend test gate failed: dotnet test produced no TRX result files.'
    }

    $total = 0
    $failed = 0
    foreach ($resultFile in $resultFiles) {
        [xml]$result = Get-Content -LiteralPath $resultFile.FullName -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if ($null -eq $counters) { continue }
        $total += [int]$counters.total
        $failed += [int]$counters.failed
    }

    if ($total -le 0) {
        throw 'Backend test gate failed: zero tests were discovered.'
    }
    if ($failed -ne 0) {
        throw "Backend test gate failed: $failed of $total tests failed."
    }

    Write-Host "Backend test gate passed: $total tests, 0 failures."
}
finally {
    if (Test-Path -LiteralPath $resultsDirectory) {
        Remove-Item -LiteralPath $resultsDirectory -Recurse -Force
    }
}
