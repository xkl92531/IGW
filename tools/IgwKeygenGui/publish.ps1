<#
发布 IGW 发码工具：私钥用管理员密码加密后内嵌进 exe，拷到哪台电脑都不用再带 private.key，双击打开要输管理员密码。
  powershell -ExecutionPolicy Bypass -File tools\IgwKeygenGui\publish.ps1
会提示设置管理员密码(输两遍)，完成后和安装包一样输出到 installer\out\ (版本号取 installer\version.txt)：
  IGW-Keygen-<版本>.exe              发码工具本体
  IGW-Keygen-<版本>-win-x64.zip      交付用：IGW 发码工具.exe + README.txt
要改管理员密码：重新运行本脚本即可(旧 exe 作废)。
私钥读取规则同命令行版 IgwKeygen：IGW_KEYGEN_PRIVATE 环境变量 > tools\IgwKeygen\private.key。
#>
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "installer\out")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$guiProject = Join-Path $PSScriptRoot "IgwKeygenGui.csproj"
$cliProject = Join-Path $repoRoot "tools\IgwKeygen\IgwKeygen.csproj"
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("igw-keygen-publish-" + [Guid]::NewGuid().ToString("N"))
$protectedKey = Join-Path $workDir "private.key.protected"
$publishDir = Join-Path $workDir "out"
$version = (Get-Content -LiteralPath (Join-Path $repoRoot "installer\version.txt") -Raw).Trim()

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    dotnet build $cliProject -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "编译命令行发码工具失败" }
    dotnet run --project $cliProject -c $Configuration --no-build -- protect-key $protectedKey
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $protectedKey)) { throw "加密私钥失败，未发布" }

    dotnet publish $guiProject -c $Configuration -o $publishDir --nologo "-p:ProtectedKeyFile=$protectedKey" "-p:Version=$version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败(退出码 $LASTEXITCODE)" }

    $exe = @(Get-ChildItem -LiteralPath $publishDir -File -Filter "*.exe")
    if ($exe.Count -ne 1) { throw "发布目录里应只有一个 exe，实际 $($exe.Count) 个" }
    if (Test-Path -LiteralPath (Join-Path $publishDir "private.key")) { throw "发布目录里不应出现明文 private.key" }

    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $target = Join-Path $OutputDir "IGW-Keygen-$version.exe"
    try { Copy-Item -LiteralPath $exe[0].FullName -Destination $target -Force }
    catch { throw "无法覆盖 $target(发码工具是不是还开着？关掉后重试)：$($_.Exception.Message)" }

    $readme = @"
IGW 发码工具 $version（厂商内部使用）

双击“IGW 发码工具.exe”，输入管理员密码后使用；连续输错 5 次程序退出。
私钥已加密内嵌在程序里，不需要再带 private.key，也不用设置环境变量。
填写客户、机器码、有效期，勾选允许的驱动和功能模块，再生成授权码。
本版签出的授权码会明确记录驱动与功能范围，未来新增项目不会自动获得许可。
本程序等同发码权限，只交给授权人员，管理员密码不要写在程序旁边。
"@
    $zipDir = Join-Path $workDir "zip"
    New-Item -ItemType Directory -Path $zipDir | Out-Null
    Copy-Item -LiteralPath $exe[0].FullName -Destination (Join-Path $zipDir $exe[0].Name)
    [System.IO.File]::WriteAllText((Join-Path $zipDir "README.txt"), $readme, (New-Object System.Text.UTF8Encoding $true))
    $zip = Join-Path $OutputDir "IGW-Keygen-$version-win-x64.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($zipDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false, [System.Text.Encoding]::UTF8)

    Write-Output "✓ 已发布：$target"
    Write-Output "✓ 交付包：$zip"
    Write-Output "  私钥已加密内嵌，双击打开输入刚才设置的管理员密码即可使用。"
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($workDir)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ((Test-Path -LiteralPath $resolved) -and $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
