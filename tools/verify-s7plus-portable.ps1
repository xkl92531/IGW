param(
    [Parameter(Mandatory = $false)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $PSScriptRoot '..\dist\IGW'
}
$source = [System.IO.Path]::GetFullPath($PublishDir)
$runtime = Join-Path $source 'IGW.Runtime.exe'
$native = Join-Path $source 'igw_s7plus.dll'
if (-not (Test-Path -LiteralPath $runtime)) { throw "Missing Runtime: $runtime" }
if (-not (Test-Path -LiteralPath $native)) { throw "Missing S7 Plus native library: $native" }

$portableLabel = -join [char[]](0x4FBF, 0x643A, 0x9A8C, 0x8BC1)
$chinesePath = -join [char[]](0x4E2D, 0x6587, 0x8DEF, 0x5F84)
$appLabel = -join [char[]](0x5E94, 0x7528, 0x20, 0x7A0B, 0x5E8F)
$dataLabel = -join [char[]](0x8FD0, 0x884C, 0x20, 0x6570, 0x636E)
$projectLabel = (-join [char[]](0x7A7A, 0x5DE5, 0x7A0B)) + '.igwproj'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("IGW $portableLabel $chinesePath " + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $root $appLabel
$dataDir = Join-Path $root $dataLabel
$project = Join-Path $root $projectLabel
$port = Get-Random -Minimum 51000 -Maximum 59000
$process = $null

try {
    New-Item -ItemType Directory -Path $appDir, $dataDir -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $appDir -Recurse -Force
    $projectJson = @{
        schemaVersion = 1
        name = 'portable-smoke'
        settings = @{ restPort = $port; opcUaPort = 4840; managementPort = 0 }
        opcUa = @{ enabled = $false; port = 4840 }
        channels = @()
    } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($project, $projectJson, [System.Text.UTF8Encoding]::new($false))

    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class IgwNativeProbe {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint AbiVersion();
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", CharSet=CharSet.Ansi, SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
    public static uint Check(string path) {
        var module = LoadLibrary(path);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            var export = GetProcAddress(module, "igw_s7plus_abi_version");
            if (export == IntPtr.Zero) throw new MissingMethodException("igw_s7plus_abi_version");
            return Marshal.GetDelegateForFunctionPointer<AbiVersion>(export)();
        } finally { FreeLibrary(module); }
    }
}
'@
    $abi = [IgwNativeProbe]::Check((Join-Path $appDir 'igw_s7plus.dll'))
    if ($abi -ne 1) { throw "Unexpected S7 Plus ABI: $abi" }

    $oldDataDir = $env:IGW_DATA_DIR
    $env:IGW_DATA_DIR = $dataDir
    try {
        $process = Start-Process -FilePath (Join-Path $appDir 'IGW.Runtime.exe') -ArgumentList ('"' + $project + '"') -WorkingDirectory $appDir -WindowStyle Hidden -PassThru
    } finally { $env:IGW_DATA_DIR = $oldDataDir }

    $health = $null
    for ($i = 0; $i -lt 30; $i++) {
        if ($process.HasExited) { throw "Runtime exited early with code $($process.ExitCode)" }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/v1/health" -TimeoutSec 2
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health) {
        $logPath = Join-Path $dataDir 'logs\igw.log'
        $runtimeLog = if (Test-Path -LiteralPath $logPath) { (Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue) } else { '' }
        throw "Runtime health endpoint did not become ready. log=$runtimeLog"
    }

    $sentinel = 'IGW-SENTINEL-PASSWORD-MUST-NOT-APPEAR'
    $leaks = Get-ChildItem -LiteralPath $appDir, $dataDir -Recurse -File -ErrorAction SilentlyContinue |
        Select-String -SimpleMatch $sentinel -List -ErrorAction SilentlyContinue
    if ($leaks) { throw "Credential sentinel leaked into files: $($leaks.Path -join ', ')" }

    Write-Host "PASS: portable Runtime started from '$appDir'; S7 Plus ABI=$abi; health=$($health.status)."
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
