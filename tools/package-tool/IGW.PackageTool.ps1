# =====================================================================
# IGW 打包工具(WinForms GUI)
# 功能:git 拉取最新代码 -> 按目标平台构建 -> 保存到本地或上传服务器
#   - git 需要账号密码时弹窗输入,DPAPI 加密永久记住(仅当前 Windows 用户可解密)
#   - 打包目标可选:本地目录 / 服务器(自动探测 Windows|Linux)
#       Linux   -> deploy\package-docker.ps1 出 Docker 部署包 zip,Posh-SSH(SFTP)上传
#       Windows -> installer\build.bat 出 IGW-Setup exe,SMB(UNC)凭据拷贝
#   - 每次打包自动自增版本号(installer\version.txt)
# 用法:双击「启动打包工具.bat」,或 powershell -STA -ExecutionPolicy Bypass -File 本文件
# 自检:powershell -ExecutionPolicy Bypass -File 本文件 -SelfTest
# =====================================================================
param([switch]$SelfTest)
#requires -version 5.1
# 注意:不用 'Stop' —— PS5.1 下 Stop + 原生命令 stderr(git/pull 大量写 stderr)会抛
# NativeCommandError 中止流程;所有失败路径均以退出码显式判断并 throw。
$ErrorActionPreference = 'Continue'

# ---------- C# 辅助:跨线程输出泵 + 全局崩溃日志 ----------
# 进程输出事件在线程池线程触发,PowerShell scriptblock 在无 runspace 的线程上会崩,
# 必须用纯 .NET 委托把行塞进 ConcurrentQueue,由 UI 线程取出显示。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
public class LinePump {
  public static ConcurrentQueue<string> Q;
  public void Attach(System.Diagnostics.Process p) { p.OutputDataReceived += Out; p.ErrorDataReceived += Err; }
  public void Detach(System.Diagnostics.Process p) { p.OutputDataReceived -= Out; p.ErrorDataReceived -= Err; }
  void Out(object s, System.Diagnostics.DataReceivedEventArgs e) { if (Q != null && e.Data != null) Q.Enqueue(e.Data); }
  void Err(object s, System.Diagnostics.DataReceivedEventArgs e) { if (Q != null && e.Data != null) Q.Enqueue("[stderr] " + e.Data); }
}
public static class CrashLog {
  static string _path;
  public static void Register(string path) {
    _path = path;
    System.AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
  }
  static void OnUnhandled(object s, UnhandledExceptionEventArgs e) {
    try { System.IO.File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  UNHANDLED:" + Environment.NewLine + e.ExceptionObject.ToString() + Environment.NewLine + new string('-', 60) + Environment.NewLine); } catch { }
  }
}
'@ -ReferencedAssemblies System.dll -ErrorAction SilentlyContinue

# ---------- 路径 ----------
$script:AppDir       = Join-Path $env:APPDATA 'IGW\PackageTool'
$script:ConfigFile   = Join-Path $script:AppDir 'config.json'
$script:GitSecret    = Join-Path $script:AppDir 'git-credentials.dpapi'
$script:SrvSecret    = Join-Path $script:AppDir 'server-credentials.dpapi'
$script:AskPassCmd   = Join-Path $script:AppDir 'askpass.cmd'
$script:AskPassPs1   = Join-Path $script:AppDir 'askpass.ps1'
$script:CrashLogFile = Join-Path $script:AppDir 'crash.log'
New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null
[CrashLog]::Register($script:CrashLogFile)   # 之后任何未处理异常都会落盘,便于定位"自动关闭"
# 本文件位于 <仓库>\tools\package-tool\ 下,默认仓库根为三层上级
$script:DefaultRepo  = (Get-Item (Split-Path -Parent $PSScriptRoot)).Parent.Parent.FullName

# ---------- 配置读写(非敏感) ----------
function Get-ToolConfig {
  if (Test-Path $script:ConfigFile) {
    try { return Get-Content $script:ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
  }
  return $null
}
function Save-ToolConfig {
  param($cfg)
  New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null
  $cfg | ConvertTo-Json -Depth 4 | Set-Content $script:ConfigFile -Encoding UTF8
}

# ---------- 凭据(DPAPI CurrentUser 加密,永久保存) ----------
function Read-Secret {
  param([string]$Path)
  if (-not (Test-Path $Path)) { return $null }
  try {
    $ss = (Get-Content $Path -Raw).Trim() | ConvertTo-SecureString   # DPAPI 解密(仅本 Windows 用户)
    $b  = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) | ConvertFrom-Json) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
  } catch { return $null }
}
function Write-Secret {
  param([string]$Path, $obj)
  New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null
  $ss = ConvertTo-SecureString (($obj | ConvertTo-Json -Depth 3) -as [string]) -AsPlainText -Force
  (ConvertFrom-SecureString $ss) | Set-Content $Path -Encoding ASCII   # DPAPI 加密文本
}

# ---------- GIT_ASKPASS 助手(git 静默读取已存凭据,仓库配置零改动) ----------
function Ensure-Askpass {
  if ($true) {   # 每次重写,保证与主脚本同版本
    $ps1 = @"
param([string]`$Prompt)
`$ErrorActionPreference='Stop'
function Read-Secret([string]`$Path){
  if(-not(Test-Path `$Path)){return `$null}
  try{
    `$ss=(Get-Content `$Path -Raw).Trim()|ConvertTo-SecureString
    `$b=[Runtime.InteropServices.Marshal]::SecureStringToBSTR(`$ss)
    try{ return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR(`$b)|ConvertFrom-Json) }finally{ [Runtime.InteropServices.Marshal]::ZeroFreeBSTR(`$b) }
  }catch{ return `$null }
}
`$s = Read-Secret -Path '$script:GitSecret'
if(`$s){ if(`$Prompt -match '(?i)username'){ Write-Output `$s.gitUser } else { Write-Output `$s.gitPass } }
"@
    New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null
    [IO.File]::WriteAllText($script:AskPassPs1, $ps1, (New-Object Text.UTF8Encoding($true)))
    $cmd = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$script:AskPassPs1`" %1`r`n"
    [IO.File]::WriteAllText($script:AskPassCmd, $cmd, [Text.Encoding]::ASCII)
  }
}

# ---------- 弹窗:账号密码(永久记住) ----------
function Get-CredentialDialog {
  param([string]$Title, [string]$Account = '')
  $f = New-Object System.Windows.Forms.Form
  $f.Text = $Title; $f.StartPosition = 'CenterParent'; $f.ClientSize = New-Object System.Drawing.Size(340, 150)
  $f.FormBorderStyle = 'FixedDialog'; $f.MaximizeBox = $false; $f.MinimizeBox = $false
  $f.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
  $l1 = New-Object System.Windows.Forms.Label; $l1.Text = '账号'; $l1.Location = '12,15'; $l1.AutoSize = $true
  $t1 = New-Object System.Windows.Forms.TextBox; $t1.Location = '90,12'; $t1.Size = '230,23'; $t1.Text = $Account
  $l2 = New-Object System.Windows.Forms.Label; $l2.Text = '密码'; $l2.Location = '12,48'; $l2.AutoSize = $true
  $t2 = New-Object System.Windows.Forms.TextBox; $t2.Location = '90,45'; $t2.Size = '230,23'; $t2.UseSystemPasswordChar = $true
  $cb = New-Object System.Windows.Forms.CheckBox; $cb.Text = '永久记住(加密保存,下次免输)'; $cb.Location = '88,78'; $cb.AutoSize = $true; $cb.Checked = $true
  $ok = New-Object System.Windows.Forms.Button; $ok.Text = '确定'; $ok.Location = '170,110'; $ok.Size = '75,27'
  $cancel = New-Object System.Windows.Forms.Button; $cancel.Text = '取消'; $cancel.Location = '250,110'; $cancel.Size = '75,27'
  $f.Controls.AddRange(@($l1, $t1, $l2, $t2, $cb, $ok, $cancel))
  $ok.Add_Click({ $f.Tag = @{ user = $t1.Text; pass = $t2.Text; remember = $cb.Checked }; $f.Close() })
  $cancel.Add_Click({ $f.Tag = $null; $f.Close() })
  [void]$f.ShowDialog()
  return $f.Tag
}

# ---------- 日志(UI 线程安全) ----------
$script:LogBox = $null
function Write-Log {
  param([string]$Text, [string]$Color = 'Black')
  if ($null -eq $script:LogBox) { Write-Host $Text; return }
  if ($script:LogBox.InvokeRequired) { $script:LogBox.BeginInvoke([Action[string,string]]{ param($t, $c) Write-Log $t $c }, $Text, $Color) | Out-Null; return }
  $script:LogBox.SelectionStart = $script:LogBox.TextLength
  $script:LogBox.SelectionColor = [System.Drawing.Color]::FromName($Color)
  $script:LogBox.AppendText("$Text`r`n")
  $script:LogBox.SelectionStart = $script:LogBox.TextLength
  $script:LogBox.ScrollToCaret()
  [System.Windows.Forms.Application]::DoEvents()
}

# ---------- 子进程(实时流式输出到日志) ----------
function Invoke-ToolProcess {
  param([string]$FileName, [string]$Arguments = '', [string]$WorkDir = '')
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $FileName; $psi.Arguments = $Arguments
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  if ($WorkDir) { $psi.WorkingDirectory = $WorkDir }
  try { # 子进程多为 GBK 控制台输出(build.bat/ISCC 中文)
    $psi.StandardOutputEncoding = [Text.Encoding]::GetEncoding(936)
    $psi.StandardErrorEncoding  = [Text.Encoding]::GetEncoding(936)
  } catch {}
  $p = New-Object System.Diagnostics.Process
  $p.StartInfo = $psi
  $queue = New-Object System.Collections.Concurrent.ConcurrentQueue[string]
  [LinePump]::Q = $queue                       # 静态字段供委托写入(工具单进程内串行使用)
  $pump = New-Object LinePump
  $pump.Attach($p)                             # 纯 .NET 事件挂接,线程池回调不经过 PowerShell,不会崩
  [void]$p.Start()
  $p.BeginOutputReadLine(); $p.BeginErrorReadLine()
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $lastBeat = 0
  while (-not $p.HasExited) {
    $line = $null
    while ($queue.TryDequeue([ref]$line)) { Write-Log $line }
    if ($sw.Elapsed.TotalSeconds - $lastBeat -ge 30) {   # 心跳:长时间无输出也不像卡死
      $lastBeat = [int]$sw.Elapsed.TotalSeconds
      Write-Log ("        ... 进行中({0} 分钟)" -f ([math]::Round($sw.Elapsed.TotalMinutes, 1))) 'Gray'
    }
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 40
  }
  $p.WaitForExit()
  $line = $null
  while ($queue.TryDequeue([ref]$line)) { Write-Log $line }
  $pump.Detach($p)
  return $p.ExitCode
}

# ---------- 端口探测 / 服务器系统识别 ----------
function Test-HostPort {
  param([string]$HostName, [int]$Port, [int]$TimeoutMs = 1500)
  $c = New-Object Net.Sockets.TcpClient
  try {
    $t = $c.BeginConnect($HostName, $Port, $null, $null)
    if ($t.AsyncWaitHandle.WaitOne($TimeoutMs) -and $c.Connected) { return $true }
    return $false
  } catch { return $false } finally { $c.Close() }
}
function Get-ServerOs {
  param([string]$HostName, [string]$Force)   # Force: '' 自动 | 'linux' | 'windows'
  if ($Force) { return $Force }
  $ssh = Test-HostPort $HostName 22
  $smb = Test-HostPort $HostName 445
  Write-Log ("        端口探测:SSH(22)={0}  SMB(445)={1}" -f ($ssh -as [string]), ($smb -as [string])) 'Gray'
  if ($ssh) { return 'linux' }
  if ($smb) { return 'windows' }
  return $null
}

# ---------- git ----------
function Invoke-Git {
  param([string]$RepoDir, [string[]]$GitArgs, [switch]$UseAskpass)
  $env:GIT_TERMINAL_PROMPT = '0'
  $env:GCM_INTERACTIVE = 'never'   # 禁止凭据管理器弹自己的框,失败走本工具弹窗
  $extra = @()
  if ($UseAskpass) {
    $env:GIT_ASKPASS = $script:AskPassCmd
    $extra = @('-c', 'credential.helper=')   # askpass 阶段禁用外部凭据助手,避免干扰
  }
  try {
    $out = & git -C $RepoDir @extra @GitArgs 2>&1
    $code = $LASTEXITCODE
  } finally {
    Remove-Item Env:\GIT_ASKPASS -ErrorAction SilentlyContinue
  }
  return @{ code = $code; text = (($out | Out-String).Trim()) }
}
function Test-GitAuthFailure {
  param([string]$Text)
  return ($Text -match '(?i)could not read (Username|Password)|Authentication failed|terminal prompts disabled|Access denied|HTTP 401|request failed with status code: 401')
}
function Update-GitRepo {
  param([string]$RepoDir, [string]$GitUrl)
  # 0) 地址:仓库不存在且给了地址 -> clone;存在且地址与 origin 不同 -> set-url
  if ($GitUrl) {
    if (-not (Test-Path (Join-Path $RepoDir '.git'))) {
      Write-Log "[git] 仓库不存在,从 $GitUrl 克隆到 $RepoDir ..."
      New-Item -ItemType Directory -Force -Path $RepoDir | Out-Null
      $r = Invoke-Git $RepoDir @('clone', $GitUrl, '.')
      Write-Log $r.text; if ($r.code -ne 0) { throw "git clone 失败(exit $($r.code))" }
      return
    }
    $origin = (& git -C $RepoDir remote get-url origin 2>$null)
    if ($origin -and ($origin -ne $GitUrl)) {
      $r = Invoke-Git $RepoDir @('remote', 'set-url', 'origin', $GitUrl)
      if ($r.code -eq 0) { Write-Log "[git] origin 已更新为 $GitUrl" } else { Write-Log "[git] 更新 origin 失败:$($r.text)" 'OrangeRed' }
    }
  }
  if (-not (Test-Path (Join-Path $RepoDir '.git'))) { throw "目录不是 git 仓库,且未填写 git 地址:$RepoDir" }
  # 1) 先静默尝试(系统缓存凭据可用则直接过,不弹任何框)
  Write-Log '[git] 拉取最新代码(pull --ff-only)...'
  $r = Invoke-Git $RepoDir @('pull', '--ff-only', 'origin')
  if ($r.code -eq 0) { Write-Log ('[git] ' + ($r.text -replace "`r?`n", ' | ')) 'Green'; return }
  if (-not (Test-GitAuthFailure $r.text)) { throw "[git] 拉取失败(非凭据问题,请手动处理):$($r.text)" }
  Write-Log '[git] 需要账号密码 ...' 'OrangeRed'
  Ensure-Askpass
  $sec = Read-Secret $script:GitSecret
  # 2) 已存凭据 -> GIT_ASKPASS 静默重试
  if ($sec -and $sec.gitUser -and $sec.gitPass) {
    Write-Log '[git] 使用已保存的账号密码重试...'
    $r = Invoke-Git $RepoDir @('pull', '--ff-only', 'origin') -UseAskpass
    if ($r.code -eq 0) { Write-Log ('[git] ' + ($r.text -replace "`r?`n", ' | ')) 'Green'; return }
    if (-not (Test-GitAuthFailure $r.text)) { throw "[git] 拉取失败:$($r.text)" }
    Write-Log '[git] 已保存的账号密码无效,请重新输入' 'OrangeRed'
  }
  # 3) 弹窗输入(永久记住)
  $cred = Get-CredentialDialog -Title 'git 账号密码(拉取代码)' -Account ($sec.gitUser)
  if (-not $cred) { throw '已取消:未提供 git 凭据' }
  if ($cred.remember) { Write-Secret $script:GitSecret @{ gitUser = $cred.user; gitPass = $cred.pass }; Write-Log '[git] 凭据已加密保存(下次免输)' 'Gray' }
  Ensure-Askpass   # 重新生成,读最新保存内容
  $r = Invoke-Git $RepoDir @('pull', '--ff-only', 'origin') -UseAskpass
  if ($r.code -ne 0) { throw "[git] 拉取失败(凭据已保存但仍未通过):$($r.text)" }
  Write-Log ('[git] ' + ($r.text -replace "`r?`n", ' | ')) 'Green'
}

# ---------- 构建 ----------
function Invoke-VersionBump {
  $v = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:RepoDir 'installer\bump_version.ps1')
  if ($LASTEXITCODE -ne 0 -or -not $v) { throw '版本号自增失败(bump_version.ps1)' }
  Write-Log "[版本] 自增至 $v" 'Green'
  return (@($v -split '\.') | ForEach-Object { [int]$_ }) -join '.'
}
function Invoke-BuildWinExe {
  Write-Log '[构建] Windows 安装包(build.bat --no-bump,含前端/原生组件/双服务/ISCC)...'
  $code = Invoke-ToolProcess 'cmd.exe' '/c installer\build.bat --no-bump' $script:RepoDir
  if ($code -ne 0) { throw "build.bat 失败(exit $code)" }
  $exe = Join-Path $script:RepoDir ("installer\out\IGW-Setup-{0}.exe" -f $script:Version)
  if (-not (Test-Path $exe)) { throw "未找到产物:$exe" }
  $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
  "$hash  IGW-Setup-$($script:Version).exe" | Set-Content ($exe + '.sha256') -Encoding ASCII
  Write-Log ("[构建] 完成:{0}({1:N1} MB)+ sha256" -f $exe, ((Get-Item $exe).Length / 1MB)) 'Green'
  return $exe
}
function Invoke-BuildLinuxZip {
  Write-Log '[构建] Linux Docker 部署包(package-docker.ps1,首次需拉基础镜像,较慢)...'
  $arg = '-NoProfile -ExecutionPolicy Bypass -File deploy\package-docker.ps1'
  if ($script:Cfg.dockerSkipBaseImage) { $arg += ' -SkipBaseImage' }
  $code = Invoke-ToolProcess 'powershell.exe' $arg $script:RepoDir
  if ($code -ne 0) { throw "package-docker.ps1 失败(exit $code)" }
  $zip = Join-Path $script:RepoDir ("deploy\out\igw-{0}-docker-linux-x64.zip" -f $script:Version)
  if (-not (Test-Path $zip)) { throw "未找到产物:$zip" }
  Write-Log ("[构建] 完成:{0}({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) 'Green'
  return $zip
}

# ---------- 上传 ----------
function Send-ToLinuxServer {
  param([string]$HostName, [string]$User, [string]$Pass, [string]$RemotePath, [string]$Artifact, [bool]$AutoDeploy)
  if (-not (Get-Module -ListAvailable Posh-SSH)) {
    Write-Log '[上传] 缺少 Posh-SSH 模块,尝试安装(PSGallery,仅首次)...' 'OrangeRed'
    $ans = [System.Windows.Forms.MessageBox]::Show('Linux 上传需要 Posh-SSH 模块(免费开源),现在自动安装?选择"否"将中止上传。', '安装 Posh-SSH', 'YesNo', 'Question')
    if ($ans -ne 'Yes') { throw '已取消:缺少 Posh-SSH' }
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
    Install-Module Posh-SSH -Scope CurrentUser -Force
    Write-Log '[上传] Posh-SSH 安装完成' 'Green'
  }
  Import-Module Posh-SSH
  $secure = ConvertTo-SecureString $Pass -AsPlainText -Force
  $cred = New-Object System.Management.Automation.PSCredential($User, $secure)
  Write-Log "[上传] SFTP 连接 $HostName ..."
  $session = $null
  try {
    $session = New-SFTPSession -ComputerName $HostName -Credential $cred -AcceptKey -Force
    if (-not $session) { throw 'SFTP 会话建立失败(账号密码或网络问题)' }
    Write-Log "[上传] -> ${HostName}:${RemotePath}"
    Set-SFTPItem -SessionId $session.SessionId -Path $Artifact -Destination $RemotePath -Force
    Write-Log '[上传] 完成' 'Green'
    if ($AutoDeploy) {
      Write-Log '[部署] 上传后自动部署(docker stop/rm + unzip + deploy.sh)...' 'OrangeRed'
      $zipName = Split-Path $Artifact -Leaf
      $dir = $RemotePath.TrimEnd('/')
      $cmd = "cd '$dir' && docker stop igw 2>/dev/null; docker rm igw 2>/dev/null; unzip -o '$dir/$zipName' -d '$dir' && cd '$dir/igw-docker' && bash deploy.sh"
      $r = Invoke-SSHCommand -SessionId $session.SessionId -Command $cmd -TimeOut 600
      Write-Log ($r.Output -join "`n")
      if ($r.ExitStatus -ne 0) { Write-Log "[部署] 远端命令退出码 $($r.ExitStatus),请登录服务器检查" 'OrangeRed' }
      else { Write-Log '[部署] 完成' 'Green' }
    } else {
      Write-Log "        服务器上手动部署:cd $RemotePath && unzip -o <zip> -d <目录> && bash igw-docker/deploy.sh" 'Gray'
    }
  } finally {
    if ($session) { Remove-SFTPSession -SessionId $session.SessionId | Out-Null }
  }
}
function Send-ToWindowsServer {
  param([string]$HostName, [string]$User, [string]$Pass, [string]$RemotePath, [string]$Artifact)
  # RemotePath 形如 \\host\share\dir(或服务器本地 D:\dir -> 自动转 \\host\D$\dir,需管理员权限)
  $unc = $RemotePath.Trim()
  if ($unc -match '^[A-Za-z]:\\(.+)$') {
    $unc = "\\$HostName\$($Matches[0][0])`$\$($Matches[1])"
    Write-Log "[上传] 远程路径已转为管理共享:$unc(需要管理员账号)" 'Gray'
  }
  if ($unc -notmatch '^\\\\[^\\]+\\[^\\]+') { throw "Windows 目标路径需为 UNC(\\\\服务器\共享\目录)或服务器本地盘符路径,当前:$unc" }
  $trimmed = $unc.TrimEnd('\')
  $root = $trimmed -replace '^(\\\\[^\\]+\\[^\\]+).*$', '$1'
  $sub = $trimmed.Substring($root.Length).TrimStart('\')
  $secure = ConvertTo-SecureString $Pass -AsPlainText -Force
  $cred = New-Object System.Management.Automation.PSCredential($User, $secure)
  $drive = 'IGW_UPLOAD'
  Write-Log "[上传] 连接 $root ..."
  try {
    New-PSDrive -Name $drive -PSProvider FileSystem -Root $root -Credential $cred -Scope Global -ErrorAction Stop | Out-Null
    $dest = if ($sub) { "$drive`:\$sub" } else { "$drive`:\" }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $Artifact -Destination $dest -Force
    Copy-Item ($Artifact + '.sha256') -Destination $dest -Force -ErrorAction SilentlyContinue
    Write-Log "[上传] 完成 -> $unc" 'Green'
  } finally {
    Remove-PSDrive -Name $drive -Force -ErrorAction SilentlyContinue
  }
}

# ---------- 自检(无 GUI)----------
if ($SelfTest) {
  $fail = 0
  New-Item -ItemType Directory -Force -Path $script:AppDir | Out-Null
  $t = "$env:TEMP\igw-pt-selftest.dpapi"
  Write-Secret $t @{ gitUser = 'u1'; gitPass = 'p@ss中文!' }
  $s = Read-Secret $t
  if ($s.gitUser -eq 'u1' -and $s.gitPass -eq 'p@ss中文!') { Write-Host 'PASS  DPAPI 凭据加解密' } else { Write-Host 'FAIL  DPAPI 凭据加解密'; $fail++ }
  Remove-Item $t -Force -ErrorAction SilentlyContinue
  Ensure-Askpass
  if ((Test-Path $script:AskPassCmd) -and (Test-Path $script:AskPassPs1)) { Write-Host 'PASS  askpass 助手生成' } else { Write-Host 'FAIL  askpass 助手生成'; $fail++ }
  $v = & powershell -NoProfile -ExecutionPolicy Bypass -Command "& { 'ok' }"
  if ($v -eq 'ok') { Write-Host 'PASS  子进程调用' } else { Write-Host 'FAIL  子进程调用'; $fail++ }
  if (Test-HostPort '127.0.0.1' 9998 -TimeoutMs 300) { Write-Host 'FAIL  端口探测(未监听端口应返回 false)'; $fail++ } else { Write-Host 'PASS  端口探测' }
  if (Test-GitAuthFailure 'fatal: could not read Username for http://x: terminal prompts disabled') { Write-Host 'PASS  git 凭据失败识别' } else { Write-Host 'FAIL  git 凭据失败识别'; $fail++ }
  if ($fail -eq 0) { Write-Host '自检全部通过' } else { Write-Host "自检有 $fail 项失败" }
  if ($fail -eq 0) { exit 0 } else { exit 1 }
}

# =============================== GUI ===============================
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$cfg = Get-ToolConfig
if (-not $cfg) { $cfg = [pscustomobject]@{} }

$form = New-Object System.Windows.Forms.Form
$form.Text = 'IGW 打包工具'
$form.StartPosition = 'CenterScreen'; $form.ClientSize = New-Object System.Drawing.Size(720, 640)
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$form.AutoSizeMode = 'GrowAndShrink'; $form.MaximizeBox = $false

# --- git 组 ---
$gGit = New-Object System.Windows.Forms.GroupBox; $gGit.Text = '1. 代码(git 拉取)'; $gGit.Location = '10,10'; $gGit.Size = '695,92'
$lRepo = New-Object System.Windows.Forms.Label; $lRepo.Text = '本地仓库'; $lRepo.Location = '12,26'; $lRepo.AutoSize = $true
$tRepo = New-Object System.Windows.Forms.TextBox; $tRepo.Location = '80,23'; $tRepo.Size = '480,23'
$tRepo.Text = if ($cfg.repoDir) { $cfg.repoDir } else { $script:DefaultRepo }
$bRepo = New-Object System.Windows.Forms.Button; $bRepo.Text = '浏览...'; $bRepo.Location = '570,21'; $bRepo.Size = '110,27'
$lUrl = New-Object System.Windows.Forms.Label; $lUrl.Text = 'git 地址'; $lUrl.Location = '12,58'; $lUrl.AutoSize = $true
$tUrl = New-Object System.Windows.Forms.TextBox; $tUrl.Location = '80,55'; $tUrl.Size = '480,23'; $tUrl.Text = [string]$cfg.gitUrl
$lUrlHint = New-Object System.Windows.Forms.Label; $lUrlHint.Text = '留空=用仓库 origin;填了则改指它;仓库不存在时自动克隆'
$lUrlHint.Location = '570,58'; $lUrlHint.Size = '120,30'; $lUrlHint.ForeColor = 'Gray'; $lUrlHint.AutoSize = $false
$gGit.Controls.AddRange(@($lRepo, $tRepo, $bRepo, $lUrl, $tUrl, $lUrlHint))

# --- 目标组 ---
$gDst = New-Object System.Windows.Forms.GroupBox; $gDst.Text = '2. 打包目标'; $gDst.Location = '10,108'; $gDst.Size = '695,190'
$rbLocal = New-Object System.Windows.Forms.RadioButton; $rbLocal.Text = '本地目录'; $rbLocal.Location = '12,24'; $rbLocal.AutoSize = $true
$rbServer = New-Object System.Windows.Forms.RadioButton; $rbServer.Text = '服务器(自动识别 Windows / Linux)'; $rbServer.Location = '240,24'; $rbServer.AutoSize = $true
$lLocal = New-Object System.Windows.Forms.Label; $lLocal.Text = '复制产物到'; $lLocal.Location = '30,56'; $lLocal.AutoSize = $true
$tLocal = New-Object System.Windows.Forms.TextBox; $tLocal.Location = '110,53'; $tLocal.Size = '450,23'; $tLocal.Text = [string]$cfg.localOutDir
$bLocal = New-Object System.Windows.Forms.Button; $bLocal.Text = '浏览...'; $bLocal.Location = '570,51'; $bLocal.Size = '110,27'
$lLocalHint = New-Object System.Windows.Forms.Label; $lLocalHint.Text = '留空=不复制,产物留在仓库 installer\out(或 deploy\out)'; $lLocalHint.Location = '110,80'; $lLocalHint.AutoSize = $true; $lLocalHint.ForeColor = 'Gray'
$cPlatform = New-Object System.Windows.Forms.ComboBox; $cPlatform.DropDownStyle = 'DropDownList'; $cPlatform.Location = '110,108'; $cPlatform.Size = '180,23'
[void]$cPlatform.Items.Add('Windows 安装包(exe)'); [void]$cPlatform.Items.Add('Linux Docker 部署包(zip)'); [void]$cPlatform.Items.Add('两者都构建')
$cPlatform.SelectedIndex = 0
$lPlatform = New-Object System.Windows.Forms.Label; $lPlatform.Text = '产物类型'; $lPlatform.Location = '30,111'; $lPlatform.AutoSize = $true
# 服务器行
$lSrv = New-Object System.Windows.Forms.Label; $lSrv.Text = '服务器地址'; $lSrv.Location = '30,140'; $lSrv.AutoSize = $true
$tSrv = New-Object System.Windows.Forms.TextBox; $tSrv.Location = '110,137'; $tSrv.Size = '180,23'; $tSrv.Text = [string]$cfg.serverAddress
$lRpath = New-Object System.Windows.Forms.Label; $lRpath.Text = '远程路径'; $lRpath.Location = '305,140'; $lRpath.AutoSize = $true
$tRpath = New-Object System.Windows.Forms.TextBox; $tRpath.Location = '370,137'; $tRpath.Size = '190,23'; $tRpath.Text = [string]$cfg.serverRemotePath
$bProbe = New-Object System.Windows.Forms.Button; $bProbe.Text = '识别系统'; $bProbe.Location = '570,134'; $bProbe.Size = '110,27'
$lOs = New-Object System.Windows.Forms.Label; $lOs.Text = '未识别'; $lOs.Location = '110,166'; $lOs.AutoSize = $true; $lOs.ForeColor = 'Gray'
$cOsForce = New-Object System.Windows.Forms.ComboBox; $cOsForce.DropDownStyle = 'DropDownList'; $cOsForce.Location = '370,163'; $cOsForce.Size = '190,23'
[void]$cOsForce.Items.Add('自动识别'); [void]$cOsForce.Items.Add('强制按 Linux(SSH)'); [void]$cOsForce.Items.Add('强制按 Windows(SMB)')
$cOsForce.SelectedIndex = 0
$gDst.Controls.AddRange(@($rbLocal, $rbServer, $lLocal, $tLocal, $bLocal, $lLocalHint, $lPlatform, $cPlatform, $lSrv, $tSrv, $lRpath, $tRpath, $bProbe, $lOs, $cOsForce))

# --- 凭据组 ---
$gCred = New-Object System.Windows.Forms.GroupBox; $gCred.Text = '3. 服务器账号(加密永久记住;git 账号在拉取时按需弹窗)'; $gCred.Location = '10,304'; $gCred.Size = '695,56'
$lSu = New-Object System.Windows.Forms.Label; $lSu.Text = '账号'; $lSu.Location = '12,22'; $lSu.AutoSize = $true
$tSu = New-Object System.Windows.Forms.TextBox; $tSu.Location = '50,19'; $tSu.Size = '160,23'
$lSp = New-Object System.Windows.Forms.Label; $lSp.Text = '密码'; $lSp.Location = '230,22'; $lSp.AutoSize = $true
$tSp = New-Object System.Windows.Forms.TextBox; $tSp.Location = '268,19'; $tSp.Size = '160,23'; $tSp.UseSystemPasswordChar = $true
$cbRemember = New-Object System.Windows.Forms.CheckBox; $cbRemember.Text = '永久记住'; $cbRemember.Location = '445,20'; $cbRemember.AutoSize = $true; $cbRemember.Checked = $true
$bClearCred = New-Object System.Windows.Forms.Button; $bClearCred.Text = '清除已存凭据'; $bClearCred.Location = '560,15'; $bClearCred.Size = '120,27'
$gCred.Controls.AddRange(@($lSu, $tSu, $lSp, $tSp, $cbRemember, $bClearCred))
$saved = Read-Secret $script:SrvSecret
if ($saved) { $tSu.Text = [string]$saved.user; $tSp.Text = [string]$saved.pass }

# --- 选项 ---
$cbDeploy = New-Object System.Windows.Forms.CheckBox; $cbDeploy.Text = 'Linux 上传后自动部署(stop/rm 容器 + unzip + deploy.sh,谨慎勾选)'
$cbDeploy.Location = '14,366'; $cbDeploy.AutoSize = $true; $cbDeploy.Checked = $false
$cbSkipBase = New-Object System.Windows.Forms.CheckBox; $cbSkipBase.Text = 'Linux 包不捆绑基础镜像(-SkipBaseImage,目标服务器需能联网)'
$cbSkipBase.Location = '14,390'; $cbSkipBase.AutoSize = $true; $cbSkipBase.Checked = ([bool]$cfg.dockerSkipBaseImage)

# --- 日志 ---
$lLog = New-Object System.Windows.Forms.Label; $lLog.Text = '4. 执行日志'; $lLog.Location = '10,414'; $lLog.AutoSize = $true
$script:LogBox = New-Object System.Windows.Forms.RichTextBox
$script:LogBox.Location = '10,436'; $script:LogBox.Size = '695,150'
$script:LogBox.ReadOnly = $true; $script:LogBox.BackColor = [System.Drawing.Color]::White
$script:LogBox.Font = New-Object System.Drawing.Font('Consolas', 9)

# --- 按钮 ---
$bStart = New-Object System.Windows.Forms.Button; $bStart.Text = '开始:拉取代码并打包'; $bStart.Location = '10,596'; $bStart.Size = '200,34'
$bStart.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 215); $bStart.ForeColor = [System.Drawing.Color]::White; $bStart.FlatStyle = 'Flat'
$form.Controls.AddRange(@($gGit, $gDst, $gCred, $cbDeploy, $cbSkipBase, $lLog, $script:LogBox, $bStart))
$form.AcceptButton = $bStart
if ((Test-Path $script:CrashLogFile) -and (Get-Item $script:CrashLogFile).Length -gt 0) {
  Write-Log ("[提示] 检测到上次异常记录:{0}(可发回排查,删除即不再提示)" -f $script:CrashLogFile) 'OrangeRed'
}

# --- 事件:目标切换 ---
$sync = { if ($rbServer.Checked) { $lLocal.Enabled = $false; $tLocal.Enabled = $false; $bLocal.Enabled = $false; $cPlatform.Enabled = $false
            $lSrv.Enabled = $true; $tSrv.Enabled = $true; $lRpath.Enabled = $true; $tRpath.Enabled = $true; $bProbe.Enabled = $true; $cOsForce.Enabled = $true; $gCred.Enabled = $true }
          else { $lLocal.Enabled = $true; $tLocal.Enabled = $true; $bLocal.Enabled = $true; $cPlatform.Enabled = $true
                 $lSrv.Enabled = $false; $tSrv.Enabled = $false; $lRpath.Enabled = $false; $tRpath.Enabled = $false; $bProbe.Enabled = $false; $cOsForce.Enabled = $false; $gCred.Enabled = $false } }
$rbLocal.Add_Click($sync); $rbServer.Add_Click($sync)
if ($cfg.targetType -eq 'server') { $rbServer.Checked = $true } else { $rbLocal.Checked = $true }
& $sync

$bRepo.Add_Click({
  $d = New-Object System.Windows.Forms.FolderBrowserDialog
  if ($d.ShowDialog() -eq 'OK') { $tRepo.Text = $d.SelectedPath }
})
$bLocal.Add_Click({
  $d = New-Object System.Windows.Forms.FolderBrowserDialog
  if ($d.ShowDialog() -eq 'OK') { $tLocal.Text = $d.SelectedPath }
})
$bProbe.Add_Click({
  $h = $tSrv.Text.Trim()
  if (-not $h) { [void][System.Windows.Forms.MessageBox]::Show('请先填服务器地址'); return }
  $lOs.Text = '探测中...'; [System.Windows.Forms.Application]::DoEvents()
  $force = @('', 'linux', 'windows')[$cOsForce.SelectedIndex]
  $os = Get-ServerOs $h $force
  $lOs.Text = switch ($os) { 'linux' { 'Linux(SSH/SFTP)'; break } 'windows' { 'Windows(SMB/UNC)'; break } default { '无法识别(22/445 都不通)' } }
  $lOs.ForeColor = switch ($os) { 'linux' { 'Green'; break } 'windows' { 'Green'; break } default { 'OrangeRed' } }
})
$bClearCred.Add_Click({
  Remove-Item $script:GitSecret, $script:SrvSecret -Force -ErrorAction SilentlyContinue
  $tSu.Text = ''; $tSp.Text = ''
  Write-Log '[凭据] 已清除全部已保存的账号密码' 'OrangeRed'
})

# --- 主流程 ---
$bStart.Add_Click({
  if ($bStart.Tag -eq 'running') { return }
  $bStart.Tag = 'running'; $bStart.Enabled = $false; $bStart.Text = '执行中...'
  $script:RepoDir = $tRepo.Text.Trim()
  $script:Cfg = [pscustomobject]@{
    repoDir = $script:RepoDir; gitUrl = $tUrl.Text.Trim(); targetType = $(if ($rbServer.Checked) { 'server' } else { 'local' })
    localOutDir = $tLocal.Text.Trim(); serverAddress = $tSrv.Text.Trim(); serverRemotePath = $tRpath.Text.Trim()
    dockerSkipBaseImage = $cbSkipBase.Checked
  }
  Save-ToolConfig $script:Cfg
  $script:LogBox.Clear()
  try {
    # 1. git 拉取
    Update-GitRepo -RepoDir $script:RepoDir -GitUrl $tUrl.Text.Trim()
    $head = (& git -C $script:RepoDir log -1 --format='%h %ci %s' 2>$null | Out-String).Trim()
    Write-Log "[git] 当前 HEAD:$head" 'Gray'
    # 2. 目标平台
    $artifacts = @()
    if ($rbServer.Checked) {
      $h = $tSrv.Text.Trim()
      if (-not $h) { throw '请填写服务器地址' }
      $force = @('', 'linux', 'windows')[$cOsForce.SelectedIndex]
      Write-Log "[目标] 服务器 $h,识别系统..."
      $os = Get-ServerOs $h $force
      if (-not $os) { throw "无法识别服务器系统(SSH 22 / SMB 445 均不通):$h" }
      Write-Log "[目标] 按 $(if ($os -eq 'linux') { 'Linux' } else { 'Windows' }) 处理" 'Green'
      # 服务器账号:界面有输入用界面,否则用已存,再没有就弹窗
      $user = $tSu.Text.Trim(); $pass = $tSp.Text
      if (-not $user) {
        $s = Read-Secret $script:SrvSecret
        $user = [string]$s.user; $pass = [string]$s.pass
      }
      if (-not $user) {
        Write-Log '[凭据] 需要服务器账号密码...' 'OrangeRed'
        $cd = Get-CredentialDialog -Title "服务器账号密码($h)"
        if (-not $cd) { throw '已取消:未提供服务器凭据' }
        $user = $cd.user; $pass = $cd.pass
        if ($cd.remember) { Write-Secret $script:SrvSecret @{ user = $user; pass = $pass }; Write-Log '[凭据] 服务器账号已加密保存' 'Gray' }
      } elseif ($cbRemember.Checked) { Write-Secret $script:SrvSecret @{ user = $user; pass = $pass } }
      $script:Version = Invoke-VersionBump
      if ($os -eq 'linux') { $artifacts += Invoke-BuildLinuxZip }
      else { $artifacts += Invoke-BuildWinExe }
      $rp = $tRpath.Text.Trim()
      if (-not $rp) { throw '请填写远程路径' }
      foreach ($a in $artifacts) {
        if ($os -eq 'linux') { Send-ToLinuxServer -HostName $h -User $user -Pass $pass -RemotePath $rp -Artifact $a -AutoDeploy:$cbDeploy.Checked }
        else { Send-ToWindowsServer -HostName $h -User $user -Pass $pass -RemotePath $rp -Artifact $a }
      }
    } else {
      Write-Log '[目标] 本地' 'Green'
      $script:Version = Invoke-VersionBump
      switch ($cPlatform.SelectedIndex) {
        0 { $artifacts += Invoke-BuildWinExe }
        1 { $artifacts += Invoke-BuildLinuxZip }
        2 { $artifacts += Invoke-BuildWinExe; $artifacts += Invoke-BuildLinuxZip }
      }
      $out = $tLocal.Text.Trim()
      if ($out) {
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        foreach ($a in $artifacts) { Copy-Item $a -Destination $out -Force; if (Test-Path "$a.sha256") { Copy-Item "$a.sha256" -Destination $out -Force } }
        Write-Log "[产物] 已复制到 $out" 'Green'
      }
    }
    Write-Log ('===== 全部完成(v{0})=====' -f $script:Version) 'Blue'
  } catch {
    Write-Log ("[错误] {0}" -f $_.Exception.Message) 'Red'
    Write-Log '===== 已中止 =====' 'Red'
  } finally {
    $bStart.Tag = $null; $bStart.Enabled = $true; $bStart.Text = '开始:拉取代码并打包'
  }
})

[void]$form.ShowDialog()
$form.Dispose()
