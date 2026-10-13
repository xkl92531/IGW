# IGW 网关启动脚本(独立分离进程:父进程退出不影响网关,stdout/stderr 直接落文件)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # 仓库根
$dll = Join-Path $root 'src\IGW.Runtime\bin\Debug\net10.0\IGW.Runtime.dll'
$dataDir = Join-Path $root 'rundata'
$logDir = Join-Path $dataDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$pidFile = Join-Path $dataDir 'igw.pid'

# 已在跑则不重复启动
if (Test-Path $pidFile) {
  $old = Get-Content $pidFile -ErrorAction SilentlyContinue
  if ($old -and (Get-Process -Id $old -ErrorAction SilentlyContinue)) { Write-Output "已在运行 pid=$old (http://127.0.0.1:48020)"; exit 0 }
}

# 环境变量经继承传给子进程
$env:IGW_DATA_DIR = $dataDir
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$p = Start-Process -FilePath 'dotnet' `
  -ArgumentList ('"' + $dll + '"') `
  -WorkingDirectory (Split-Path -Parent $dll) `
  -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $logDir 'runtime-console.log') `
  -RedirectStandardError  (Join-Path $logDir 'runtime-console.err.log') `
  -PassThru
Set-Content -Path $pidFile -Value $p.Id
Write-Output ("IGW 已启动 pid=" + $p.Id + " -> http://127.0.0.1:48020")
