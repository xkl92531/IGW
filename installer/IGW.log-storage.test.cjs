const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')

const root = path.join(__dirname, '..')
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8')

test('Windows installer retires the old log service and initializes log storage before Runtime starts', () => {
  const source = read('installer/IGW.iss')
  assert.ok(source.includes('CreateInputDirPage'), 'missing log directory wizard page')
  assert.ok(!source.includes('LogServiceDist'), 'installer must not package the retired log service')
  const nextButton = source.slice(source.indexOf('function NextButtonClick'), source.indexOf('end;', source.indexOf('function NextButtonClick')))
  assert.ok(!nextButton.includes('LogDirPage'), 'log directory checks must not run in Pascal Script')
  assert.ok(source.includes("sc.exe'), 'delete \"' + LOG_SVC"), 'upgrade must delete the old log service')
  assert.ok(source.includes("DelTree(ExpandConstant('{app}\\logservice')"), 'upgrade must remove the old log service files')

  const install = source.slice(source.indexOf('procedure CurStepChanged'), source.indexOf('procedure CurUninstallStepChanged'))
  const initialize = install.indexOf('--initialize-logs')
  const startRuntime = install.indexOf("'start \"' + SVC")
  assert.ok(initialize >= 0 && startRuntime > initialize, 'log storage must be initialized before Runtime starts')
  assert.ok(!source.includes('uninsdelete'), 'uninstall must preserve log data')
})

test('Windows installer never terminates OpenObserve instances owned by other products', () => {
  const source = read('installer/IGW.iss')
  assert.ok(!source.includes('/im openobserve.exe'), 'must not kill every OpenObserve process by image name')
  assert.ok(!source.includes("KillIfRunning('openobserve.exe')"), 'must not kill every OpenObserve process by image name')
  assert.match(source, /Get-Process [^\n]*openobserve[^\n]*Where-Object \{ \$_\.Path -like/, 'only the IGW-owned OpenObserve may be stopped')
})

test('Linux package ships only the Runtime service, retires igw-log and keeps data under var lib', () => {
  const build = read('installer/build-linux.sh')
  const install = read('tools/linux/install.sh')
  const gatewayUnit = read('tools/linux/igw.service')
  assert.ok(!/IGW\.LogService|openobserve|igw-log\.service/.test(build), 'Linux archive must not stage the retired log service')
  assert.ok(!fs.existsSync(path.join(root, 'tools/linux/igw-log.service')), 'retired unit file must be removed')
  assert.match(install, /systemctl disable --now igw-log\.service/)
  assert.match(install, /app\/IGW\.Runtime" \\\r?\n\s+--initialize-logs/)
  assert.ok(install.includes('/var/lib/igw'), 'Linux data root must be persistent')
  assert.ok(!install.includes('rm -rf /var/lib/igw'), 'installer must never delete existing IGW data')
  assert.ok(!gatewayUnit.includes('igw-log'), 'Runtime unit must not depend on the retired log service')
})
