param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "IgwKeygenGui.csproj"
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("igw-keygen-portable-" + [Guid]::NewGuid().ToString("N"))
$process = $null

try {
    dotnet publish $project -c $Configuration -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $executables = @(Get-ChildItem -LiteralPath $publishDir -File -Filter "*.exe")
    if ($executables.Count -ne 1) {
        throw "Expected exactly one executable, found $($executables.Count)"
    }
    $exe = $executables[0].FullName

    $externalRuntimeFiles = @(
        Get-ChildItem -LiteralPath $publishDir -File |
            Where-Object { $_.Extension -in ".dll", ".json" }
    )
    if ($externalRuntimeFiles.Count -ne 0) {
        $names = ($externalRuntimeFiles.Name -join ", ")
        throw "Publish output is not a self-contained single file. External runtime files: $names"
    }

    if ((Get-Item -LiteralPath $exe).Length -lt 50MB) {
        throw "Executable is too small to contain the desktop runtime: $((Get-Item -LiteralPath $exe).Length) bytes"
    }

    if (Test-Path -LiteralPath (Join-Path $publishDir "private.key")) {
        throw "Publish output must not contain private.key"
    }

    $oldDotnetRoot = $env:DOTNET_ROOT
    $oldMultilevel = $env:DOTNET_MULTILEVEL_LOOKUP
    try {
        $env:DOTNET_ROOT = Join-Path $publishDir "missing-dotnet"
        $env:DOTNET_MULTILEVEL_LOOKUP = "0"
        $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            throw "Application exited immediately with code $($process.ExitCode)"
        }
    }
    finally {
        $env:DOTNET_ROOT = $oldDotnetRoot
        $env:DOTNET_MULTILEVEL_LOOKUP = $oldMultilevel
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
    }

    Write-Output "PASS: win-x64 self-contained single-file app starts without system .NET lookup."
}
finally {
    if (Test-Path -LiteralPath $publishDir) {
        $resolved = [System.IO.Path]::GetFullPath($publishDir)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a non-temporary directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
