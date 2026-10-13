# IGW 网关停止脚本
$pidFile = 'D:\AI\AIProject\IGW\rundata\igw.pid'
if (-not (Test-Path $pidFile)) { Write-Output '无 pid 文件'; exit 0 }
$id = Get-Content $pidFile -ErrorAction SilentlyContinue
if ($id -and (Get-Process -Id $id -ErrorAction SilentlyContinue)) {
  Stop-Process -Id $id -Force
  Write-Output "已停止 pid=$id"
} else { Write-Output '进程已不在' }
Remove-Item $pidFile -ErrorAction SilentlyContinue
