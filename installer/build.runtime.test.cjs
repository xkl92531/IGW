const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')

const source = fs.readFileSync(path.join(__dirname, 'build.bat'), 'utf8')

test('使用 Codex 内置 pnpm 时同时使用配套 Node 运行时', () => {
  assert.ok(source.includes('set "CODEX_DEPS=%USERPROFILE%\\.cache\\codex-runtimes\\codex-primary-runtime\\dependencies"'), '缺少内置依赖根目录')
  assert.ok(source.includes('if exist "%CODEX_DEPS%\\bin\\fallback\\pnpm.cmd" set "PNPM_CMD=%CODEX_DEPS%\\bin\\fallback\\pnpm.cmd"'), '没有选择内置 pnpm')
  assert.ok(source.includes('if exist "%CODEX_DEPS%\\node\\bin\\node.exe" set "PATH=%CODEX_DEPS%\\node\\bin;%PATH%"'), '没有把配套 Node 加入 PATH')

  const nodePathIndex = source.indexOf('set "PATH=%CODEX_DEPS%\\node\\bin;%PATH%"')
  const webBuildIndex = source.indexOf('echo [1/4] Building embedded Web UI...')
  assert.ok(nodePathIndex >= 0 && nodePathIndex < webBuildIndex, '必须在构建 Web UI 前配置 Node')
})

test('发布前确定版本号并写入 Runtime 的程序集版本', () => {
  // 现场启动日志和采集诊断导出按程序集版本区分安装包；不写入时所有版本都显示 1.0.0.0。
  const versionIndex = source.indexOf('bump_version.ps1')
  const runtimePublish = source.indexOf('dotnet publish "%ROOT%\\src\\IGW.Runtime\\IGW.Runtime.csproj"')
  assert.ok(versionIndex >= 0 && runtimePublish > versionIndex,'必须在发布 Runtime 之前确定版本号')
  const line = source.slice(runtimePublish, source.indexOf('\n', runtimePublish))
  assert.ok(line.includes('-p:Version=%APPVER%'), `发布命令缺少版本号：${line}`)
  assert.ok(!source.includes('IGW.LogService'), '日志已内置于 Runtime，不再发布独立日志服务')
  assert.ok(source.includes('if not defined APPVER goto :missing_version'), '版本号为空时必须中止打包')
  assert.ok(source.indexOf('"%ISCC%" "%~dp0IGW.iss"') > runtimePublish, '安装器仍应在发布之后编译')
})

test('自动识别 winget 当前用户安装的 Inno Setup 编译器', () => {
  assert.ok(
    source.includes('%LOCALAPPDATA%\\Programs\\InnoSetupBuild\\ISCC.exe'),
    '缺少 winget 当前用户 Inno Setup 安装路径',
  )
})
