const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')

const root = path.join(__dirname, '..')
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8')

// 现场升级卡在 "Creating directories..."：[Dirs] 的 Permissions 每次安装都会重设数据目录权限，
// Windows 把继承权限逐个下发到目录里的每个文件。接口报文一小时就有几十万个文件，
// 升级在这一步卡几个小时，取消重装也一样(此时网关服务已被停掉)。
test('Windows installer only sets data directory permissions when the directory is created', () => {
  const source = read('installer/IGW.iss')
  const dirs = source.slice(source.indexOf('[Dirs]'), source.indexOf('[Files]'))
  const entry = dirs.split('\n').find(line => line.startsWith('Name: "{commonappdata}\\IGW"'))
  assert.ok(entry, 'missing data directory entry')
  assert.match(entry, /Permissions: users-modify/, 'fresh installs still need writable data directory')
  assert.match(entry, /Check: DataDirMissing/, 'upgrades must not re-apply permissions to an existing data directory')
  assert.match(source, /function DataDirMissing: Boolean;[\s\S]*?not DirExists\(ExpandConstant\('\{commonappdata\}\\IGW'\)\)/,
    'DataDirMissing must check the same directory')
})

// 本地 build.bat 出包前必须先跑安装脚本测试，否则上面这类回归只有 CI 能拦住，本地打的包照样带病出厂。
test('build.bat runs the installer script tests before compiling the installer', () => {
  const build = read('installer/build.bat')
  const tests = build.indexOf('node --test "%%~t"')
  const compile = build.indexOf('"%ISCC%" "%~dp0IGW.iss"')
  assert.ok(tests >= 0, 'build.bat must run installer/*.test.cjs')
  assert.ok(compile > tests, 'installer tests must run before ISCC compiles the package')
  assert.match(build, /if errorlevel 1 goto :installer_test_error/, 'a failing test must stop the build')
})
