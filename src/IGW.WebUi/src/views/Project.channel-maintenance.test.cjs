const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const webRoot = path.resolve(__dirname, '..')
const repoRoot = path.resolve(webRoot, '..', '..', '..')
const project = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
const api = fs.readFileSync(path.join(webRoot, 'api.ts'), 'utf8')
const runtimeProgram = fs.readFileSync(path.join(repoRoot, 'src', 'IGW.Runtime', 'Program.cs'), 'utf8')
const runtimeProject = fs.readFileSync(path.join(repoRoot, 'src', 'IGW.Runtime', 'IGW.Runtime.csproj'), 'utf8')

test('通道右键提供立即重连并调用专用接口', () => {
  assert.match(project, /立即重连/)
  assert.match(project, /api\.reconnectChannel/)
  assert.match(api, /reconnectChannel/)
  assert.match(api, /channels\/\$\{encodeURIComponent\(channel\)\}\/reconnect/)
})

test('诊断窗口复用工程树实时状态而不是打开时复制快照', () => {
  assert.doesNotMatch(project, /const diagStatus/)
  assert.match(project, /findRuntimeDevice\(runtimeDevices\.value/)
  assert.match(project, /deviceRuntimeMark\(s \|\| undefined, runtimeStatusAvailable\.value\)/)
  assert.match(project, /runtimeStatusError/)
  assert.match(project, /runtimeLastUpdated/)
  assert.match(project, /最近更新时间/)
})

test('现场通道不再提供临时模拟开关，独立模拟器驱动仍可用于测试工程', () => {
  assert.doesNotMatch(project, /模拟模式/)
  assert.doesNotMatch(project, /toggleSim|confirmSim|simOpen|simSpeed|simDelayMs/)
  assert.match(runtimeProgram, /SimulatorDriverFactory/)
  assert.match(runtimeProject, /IGW\.Drivers\.Simulator/)
})

test('新建通道默认自动批处理且不再写入普通 maxGap', () => {
  const start = project.indexOf('function wizFinish')
  const creation = project.slice(start, project.indexOf('// device', start))
  assert.match(creation, /batchingMode: 'auto'/)
  assert.doesNotMatch(creation, /maxGap: '8'/)
})
