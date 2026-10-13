const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')

const root = path.join(__dirname, '..')
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8')

// 数据迁移工具随安装包分发：C 盘被日志、报文撑满时，现场从开始菜单打开，把整个数据目录搬到其它盘。
test('installer ships the data mover tool with a start menu shortcut', () => {
  const source = read('installer/IGW.iss')
  assert.ok(source.includes('Source: "{#DataMoverDist}\\IGW.DataMover.exe"; DestDir: "{app}\\tools"'),
    'tool must be installed under {app}\\tools')
  assert.ok(source.includes('Name: "{group}\\IGW 数据迁移工具"; Filename: "{app}\\tools\\IGW.DataMover.exe"'),
    'missing start menu shortcut')
  assert.ok(!source.includes('\t'), 'installer script must not contain tab characters (a mistyped backslash-t in a path)')

  const build = read('installer/build.bat')
  const publish = build.indexOf('dotnet publish "%ROOT%\\src\\IGW.DataMover\\IGW.DataMover.csproj"')
  const compile = build.indexOf('"%ISCC%" "%~dp0IGW.iss"')
  assert.ok(publish >= 0 && publish < compile, 'build.bat must publish the data mover before compiling the installer')
})
