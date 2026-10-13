const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadRuntimeModule() {
  const filename = path.join(__dirname, 'deviceRuntime.ts')
  if (!fs.existsSync(filename)) return {}
  const source = fs.readFileSync(filename, 'utf8')
  const output = ts.transpileModule(source, {
    compilerOptions: {
      target: ts.ScriptTarget.ES2020,
      module: ts.ModuleKind.CommonJS,
      esModuleInterop: true,
    },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const runtime = loadRuntimeModule()

const englishMessages = {
  '离线': 'Offline',
  '故障': 'Fault',
  '运行状态：{state}': 'Runtime status: {state}',
  '原因：{error}': 'Reason: {error}',
  '{count} 台在线': '{count} online',
  '{count}台离线': '{count} offline',
  '运行状态：存在异常': 'Runtime status: abnormal devices found',
}
const english = (source, params = {}) => (englishMessages[source] || source)
  .replace(/\{(\w+)\}/g, (_, key) => String(params[key] ?? `{${key}}`))

test('工程树可以本地化 IGW 运行状态，同时保留原始设备错误', () => {
  assert.deepEqual(runtime.deviceRuntimeMark({
    name: 'Line.PLC', state: 'Disconnected', stats: { lastError: 'connection reset by peer' },
  }, true, english), {
    text: 'Offline',
    tone: 'offline',
    title: 'Runtime status: Offline · Reason: connection reset by peer',
  })

  const statuses = runtime.indexRuntimeDevices([
    { name: 'Line.PLC1', state: 'Connected' },
    { name: 'Line.PLC2', state: 'Disconnected' },
  ])
  assert.equal(
    runtime.channelRuntimeMark('Line', [{ name: 'PLC1' }, { name: 'PLC2' }], statuses, true, english).text,
    '1 offline',
  )
})

test('设备状态在树上使用明确的在线、连接中、离线和故障语义', () => {
  assert.equal(typeof runtime.deviceRuntimeMark, 'function')
  assert.deepEqual(runtime.deviceRuntimeMark({ name: 'C.D1', state: 'Connected', activeTags: 20, tagCount: 20 }), {
    text: '在线', tone: 'online', title: '运行状态：在线 · 活动点位：20 / 20',
  })
  assert.equal(runtime.deviceRuntimeMark({ name: 'C.D1', state: 'Connecting' }).text, '连接中')
  assert.equal(runtime.deviceRuntimeMark({ name: 'C.D1', state: 'Disconnected' }).text, '离线')
  assert.deepEqual(runtime.deviceRuntimeMark({
    name: 'C.D1', state: 'Faulted', stats: { lastError: '连接超时' },
  }), {
    text: '故障', tone: 'fault', title: '运行状态：故障 · 原因：连接超时',
  })
})

test('已连接但无采集需求的 OPC UA 设备显示在线并注明采集待机', () => {
  const mark = runtime.deviceRuntimeMark({
    name: 'Ua.Server', state: 'Connected', activeTags: 0, tagCount: 81, demandActive: false,
  })
  assert.equal(mark.text, '在线')
  assert.equal(mark.tone, 'online')
  assert.match(mark.title, /采集待机/)
})

test('S7 Plus 身份验证问题显示为明确的需验证而不是笼统故障', () => {
  assert.deepEqual(runtime.deviceRuntimeMark({
    name: 'Line.PLC', state: 'Connected', detailState: 'AuthenticationRequired', detailMessage: 'PLC 要求身份验证',
  }), {
    text: '需验证', tone: 'fault', title: 'PLC 要求身份验证',
  })
})

test('旧运行时误报 Connected 但从未读成功时按通信故障显示', () => {
  const falseConnected = {
    name: 'Line.PLC',
    state: 'Connected',
    stats: { readsOk: 0, readsErr: 23, successRate: 0, lastError: '全部点读取失败' },
  }
  assert.deepEqual(runtime.deviceRuntimeMark(falseConnected), {
    text: '故障',
    tone: 'fault',
    title: '运行状态：故障 · 原因：全部点读取失败',
  })
  const statuses = runtime.indexRuntimeDevices([falseConnected])
  assert.equal(runtime.channelRuntimeMark('Line', [{ name: 'PLC' }], statuses, true).text, '1台故障')
})

test('运行时没有设备时显示未下发，接口不可用时显示状态未知', () => {
  assert.equal(runtime.deviceRuntimeMark(undefined, true).text, '未下发')
  assert.equal(runtime.deviceRuntimeMark(undefined, false).text, '状态未知')
  assert.equal(runtime.deviceRuntimeMark({ name: 'Line.PLC', state: 'Connected' }, false).text, '状态未知')
})

test('工程树按结构化诊断显示网络和西门子协议或DB不匹配原因', () => {
  const network = runtime.deviceRuntimeMark({
    name: 'Line.PLC', state: 'Faulted', detailState: 'NetworkConnectFailure', detailMessage: '192.168.1.2:102 连接超时',
  })
  assert.equal(network.text, '网络异常')
  assert.match(network.title, /192\.168\.1\.2:102/)

  const mismatch = runtime.deviceRuntimeMark({
    name: 'Line.PLC', state: 'Connected', detailState: 'SymbolMismatch', detailMessage: 'DB2100 不存在或 S7 Plus 符号不匹配',
  })
  assert.equal(mismatch.text, 'DB/协议不匹配')
  assert.match(mismatch.title, /DB2100/)
})

test('工程树优先显示统一连接诊断分类、原因和处理建议', () => {
  const mark = runtime.deviceRuntimeMark({
    name: 'Line.PLC',
    state: 'Faulted',
    diagnostic: {
      code: 'ConnectionTimeout',
      category: '连接超时',
      scope: 'Transport',
      state: 'Faulted',
      cause: '连接或协议握手在限定时间内未完成。',
      suggestion: '检查网络丢包、端口和源网卡。',
      rawError: 'connect timed out',
      active: true,
      occurrenceCount: 4,
      retrying: true,
      nextRetryMs: 5000,
    },
  })

  assert.equal(mark.text, '连接超时')
  assert.equal(mark.tone, 'fault')
  assert.match(mark.title, /连接或协议握手/)
  assert.match(mark.title, /处理建议：检查网络丢包/)
  assert.match(mark.title, /已发生 4 次/)
  assert.match(mark.title, /5 秒后重试/)
})

test('已连接但存在点位或DB异常时，通道汇总显示数据异常而不是设备故障', () => {
  const statuses = runtime.indexRuntimeDevices([{
    name: 'Line.PLC', state: 'Connected',
    diagnostic: {
      code: 'DataAddressMismatch', category: 'DB/地址错误', scope: 'DataAccess', state: 'Connected',
      cause: 'DB 不存在。', suggestion: '核对 DB。', rawError: 'DB2100 not found',
      active: true, occurrenceCount: 1, retrying: false, nextRetryMs: 0,
    },
  }])

  assert.equal(runtime.deviceRuntimeMark(statuses['Line.PLC']).text, 'DB/地址错误')
  assert.equal(runtime.effectiveRuntimeState(statuses['Line.PLC']), 'Connected')
  assert.equal(runtime.channelRuntimeMark('Line', [{ name: 'PLC' }], statuses, true).text, '1台数据异常')
})

test('没有采集需求的断开设备显示待机而不是离线', () => {
  assert.deepEqual(runtime.deviceRuntimeMark({ name: 'Line.PLC', state: 'Standby' }), {
    text: '待机',
    tone: 'standby',
    title: '当前没有采集任务或数据消费者，设备保持断开以节省资源；产生需求后会自动连接',
  })
  const statuses = runtime.indexRuntimeDevices([{ name: 'Line.PLC', state: 'Standby' }])
  assert.equal(runtime.channelRuntimeMark('Line', [{ name: 'PLC' }], statuses, true).text, '全部待机')
})

test('IGW运行状态不再暴露内置模拟模式', () => {
  assert.equal(runtime.simulationRuntimeMark, undefined)
})

test('通道节点汇总异常设备，折叠状态下仍能定位有问题的通道', () => {
  const statuses = runtime.indexRuntimeDevices([
    { name: 'Line.PLC1', state: 'Connected' },
    { name: 'Line.PLC2', state: 'Disconnected' },
    { name: 'Line.PLC3', state: 'Faulted', stats: { lastError: '握手失败' } },
  ])
  const mark = runtime.channelRuntimeMark('Line', [{ name: 'PLC1' }, { name: 'PLC2' }, { name: 'PLC3' }], statuses, true)
  assert.equal(mark.text, '2台异常')
  assert.equal(mark.tone, 'fault')
  assert.match(mark.title, /离线 1 台/)
  assert.match(mark.title, /故障 1 台/)
})

test('同名设备优先按通道全名匹配，避免状态串到其他通道', () => {
  const statuses = runtime.indexRuntimeDevices([
    { name: 'LineA.PLC', state: 'Connected' },
    { name: 'LineB.PLC', state: 'Disconnected' },
  ])
  assert.equal(runtime.findRuntimeDevice(statuses, 'LineA', 'PLC').state, 'Connected')
  assert.equal(runtime.findRuntimeDevice(statuses, 'LineB', 'PLC').state, 'Disconnected')
  assert.equal(runtime.findRuntimeDevice(statuses, 'Unknown', 'PLC'), undefined)
})

test('全部在线的通道显示绿色汇总，缺失运行设备显示未下发数量', () => {
  const statuses = runtime.indexRuntimeDevices([
    { name: 'Line.PLC1', state: 'Connected' },
    { name: 'Line.PLC2', state: 'Connected' },
  ])
  assert.deepEqual(
    runtime.channelRuntimeMark('Line', [{ name: 'PLC1' }, { name: 'PLC2' }], statuses, true),
    { text: '全部在线', tone: 'online', title: '运行状态：2 台设备全部在线' },
  )
  assert.equal(
    runtime.channelRuntimeMark('Line', [{ name: 'PLC1' }, { name: 'PLC2' }, { name: 'PLC3' }], statuses, true).text,
    '1台未下发',
  )
})

test('监控中心复用工程树的有效状态判定，不能把零成功持续失败的设备显示为正常', () => {
  assert.equal(typeof runtime.reconcileMonitorRuntime, 'function')
  const result = runtime.reconcileMonitorRuntime({
    total: 0,
    entries: [],
    channels: [{ name: 'Line', level: 'ok', count: 0, devices: 1, online: 1 }],
  }, [{
    name: 'Line.PLC',
    state: 'Connected',
    stats: { readsOk: 0, readsErr: 12, successRate: 0, lastError: '全部点读取失败' },
  }])

  assert.equal(result.total, 1)
  assert.equal(result.channels[0].level, 'offline')
  assert.equal(result.channels[0].online, 0)
  assert.equal(result.channels[0].count, 1)
  assert.equal(result.entries[0].device, 'Line.PLC')
  assert.equal(result.entries[0].info, '全部点读取失败')
})

test('监控轮询只有持续时间变化时不触发界面重绘，状态内容变化时必须更新', () => {
  assert.equal(typeof runtime.sameMonitorPresentation, 'function')
  const before = {
    total: 1,
    channels: [{ name: 'Line', level: 'offline', count: 1, online: 0, devices: 1 }],
    entries: [{ severity: 'offline', device: 'Line.PLC', info: '连接超时', ageMs: 2_000 }],
  }
  const ageOnly = {
    ...before,
    entries: [{ ...before.entries[0], ageMs: 4_000 }],
  }
  const recovered = {
    total: 0,
    channels: [{ name: 'Line', level: 'ok', count: 0, online: 1, devices: 1 }],
    entries: [],
  }

  assert.equal(runtime.sameMonitorPresentation(before, ageOnly), true)
  assert.equal(runtime.sameMonitorPresentation(before, recovered), false)
})
