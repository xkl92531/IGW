const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadSyncModule() {
  const filename = path.join(__dirname, 'projectMesSync.ts')
  if (!fs.existsSync(filename)) return {}
  const source = fs.readFileSync(filename, 'utf8')
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const sync = loadSyncModule()

test('关闭采集流程时只同步流程数据并保留工程树节点身份', () => {
  assert.equal(typeof sync.applyMesFlowSnapshot, 'function')

  const device = { name: 'PLC1', groups: [{ name: 'DB1', tags: [] }] }
  const channels = [{ name: 'S7', devices: [device] }]
  const local = {
    name: '项目',
    channels,
    mesUpload: { tasks: [{ id: 'old', name: '旧流程' }] },
  }
  const remote = {
    name: '项目',
    channels: [{ name: '服务端重建的树', devices: [] }],
    mesUpload: { tasks: [{ id: 'new', name: '最新流程' }] },
  }

  const result = sync.applyMesFlowSnapshot(local, remote)

  assert.equal(result, local)
  assert.equal(result.channels, channels)
  assert.equal(result.channels[0].devices[0], device)
  assert.deepEqual(result.mesUpload, remote.mesUpload)
  assert.notEqual(result.mesUpload, remote.mesUpload)
})

test('工程配置的设备或通道入口可以批量开启或关闭范围内任务', () => {
  assert.equal(typeof sync.setAllMesTasksEnabled, 'function')
  const mesUpload = {
    enabled: false,
    tasks: [
      { id: 'first', enabled: false },
      { id: 'second', enabled: true },
      { id: 'third', enabled: false },
    ],
  }

  assert.equal(sync.setAllMesTasksEnabled(mesUpload, true), 3)
  assert.equal(mesUpload.enabled, true)
  assert.deepEqual(mesUpload.tasks.map(task => task.enabled), [true, true, true])

  assert.equal(sync.setAllMesTasksEnabled(mesUpload, false), 3)
  assert.equal(mesUpload.enabled, false)
  assert.deepEqual(mesUpload.tasks.map(task => task.enabled), [false, false, false])
})

test('一级任务开启时阻止二级任务开启且不会丢失二级配置', () => {
  assert.equal(typeof sync.setMesTaskEnabled, 'function')
  const mesUpload = { tasks: [
    { id: 'op030', groupRef: 'Line.PLC.OP030', enabled: true, flow: { nodes: [{ id: 'root' }] } },
    { id: 'entry', groupRef: 'Line.PLC.OP030.进站', enabled: false, flow: { nodes: [{ id: 'entry-node' }] } },
  ] }

  const result = sync.setMesTaskEnabled(mesUpload, 'entry', true)

  assert.deepEqual(result, { changed: false, conflictGroupRef: 'Line.PLC.OP030' })
  assert.equal(mesUpload.tasks[0].enabled, true)
  assert.equal(mesUpload.tasks[1].enabled, false)
  assert.equal(mesUpload.tasks[1].flow.nodes[0].id, 'entry-node')
})

test('二级同级任务可以同时开启但会阻止一级任务开启', () => {
  assert.equal(typeof sync.setMesTaskEnabled, 'function')
  const mesUpload = { tasks: [
    { id: 'op030', groupRef: 'Line.PLC.OP030', enabled: false },
    { id: 'entry', groupRef: 'Line.PLC.OP030.进站', enabled: false },
    { id: 'exit', groupRef: 'Line.PLC.OP030.出站', enabled: false },
  ] }

  assert.deepEqual(sync.setMesTaskEnabled(mesUpload, 'entry', true), { changed: true, conflictGroupRef: '' })
  assert.deepEqual(sync.setMesTaskEnabled(mesUpload, 'exit', true), { changed: true, conflictGroupRef: '' })
  assert.deepEqual(sync.setMesTaskEnabled(mesUpload, 'op030', true), {
    changed: false,
    conflictGroupRef: 'Line.PLC.OP030.进站',
  })
  assert.deepEqual(mesUpload.tasks.map((task) => task.enabled), [false, true, true])
})

test('范围批量开启时优先开启已配置的最细任务并避免父子同时运行', () => {
  const mesUpload = { enabled: false, tasks: [
    { id: 'op030', groupRef: 'Line.PLC.OP030', enabled: false },
    { id: 'entry', groupRef: 'Line.PLC.OP030.进站', enabled: false },
    { id: 'exit', groupRef: 'Line.PLC.OP030.出站', enabled: false },
    { id: 'op040', groupRef: 'Line.PLC.OP040', enabled: false },
  ] }

  assert.equal(sync.setAllMesTasksEnabled(mesUpload, true), 3)
  assert.deepEqual(mesUpload.tasks.map((task) => task.enabled), [false, true, true, true])
  assert.equal(mesUpload.enabled, true)
})

test('通道级批量开启只处理当前通道并保留其他通道状态', () => {
  const mesUpload = { enabled: false, tasks: [
    { id: 'line-parent', groupRef: 'Line.PLC.OP030', enabled: false },
    { id: 'line-child', groupRef: 'Line.PLC.OP030.进站', enabled: false },
    { id: 'other', groupRef: 'Other.PLC.OP040', enabled: false },
  ] }

  assert.equal(sync.setAllMesTasksEnabled(mesUpload, true, 'Line'), 1)
  assert.deepEqual(mesUpload.tasks.map((task) => task.enabled), [false, true, false])
})

test('停用正在运行的单独任务必须确认，取消时保持开启', async () => {
  assert.equal(typeof sync.confirmMesTaskDisable, 'function')
  let prompted = 0
  assert.equal(await sync.confirmMesTaskDisable(true, false, async () => { prompted++ }), true)
  assert.equal(prompted, 1)

  assert.equal(await sync.confirmMesTaskDisable(true, false, async () => {
    prompted++
    throw new Error('cancel')
  }), false)
  assert.equal(prompted, 2)
})

test('开启任务或状态未变化时不弹停用确认', async () => {
  let prompted = 0
  const confirm = async () => { prompted++ }

  assert.equal(await sync.confirmMesTaskDisable(false, true, confirm), true)
  assert.equal(await sync.confirmMesTaskDisable(true, true, confirm), true)
  assert.equal(await sync.confirmMesTaskDisable(false, false, confirm), true)
  assert.equal(prompted, 0)
})

test('取消停用确认不会修改正在运行的任务', async () => {
  assert.equal(typeof sync.setMesTaskEnabledWithConfirmation, 'function')
  const mesUpload = { tasks: [{ id: 'running', groupRef: 'Line.PLC.DB1', enabled: true }] }

  const cancelled = await sync.setMesTaskEnabledWithConfirmation(
    mesUpload,
    'running',
    false,
    async () => { throw new Error('cancel') },
  )
  assert.deepEqual(cancelled, { changed: false, conflictGroupRef: '', cancelled: true })
  assert.equal(mesUpload.tasks[0].enabled, true)

  const confirmed = await sync.setMesTaskEnabledWithConfirmation(
    mesUpload,
    'running',
    false,
    async () => {},
  )
  assert.deepEqual(confirmed, { changed: true, conflictGroupRef: '', cancelled: false })
  assert.equal(mesUpload.tasks[0].enabled, false)
})

test('属性开关首次开启时创建一个完整空任务且不会重复创建', () => {
  assert.equal(typeof sync.ensureMesTaskForGroup, 'function')
  const mesUpload = { tasks: [] }

  const first = sync.ensureMesTaskForGroup(
    mesUpload,
    'Line.PLC.OP030.进站',
    'new-task-id',
  )
  assert.equal(first.created, true)
  assert.deepEqual(first.task, {
    id: 'new-task-id',
    name: '进站',
    groupRef: 'Line.PLC.OP030.进站',
    deviceRef: 'Line.PLC',
    flowAvailable: true,
    enabled: false,
    collectIntervalMs: 1000,
    confirmRewrite: true,
    payload: { mode: 'DataRanges', dataRanges: [], fields: [], structs: [], append: {} },
    steps: [],
    flow: { nodes: [], edges: [] },
  })

  const second = sync.ensureMesTaskForGroup(
    mesUpload,
    'Line.PLC.OP030.进站',
    'ignored-id',
  )
  assert.equal(second.created, false)
  assert.equal(second.task, first.task)
  assert.equal(mesUpload.tasks.length, 1)
})

test('开启采集流程功能不会自动启动采集上报', () => {
  assert.equal(typeof sync.setMesTaskFlowAvailable, 'function')
  const task = {
    id: 'entry',
    groupRef: 'Line.PLC.OP030.进站',
    flowAvailable: false,
    enabled: false,
    flow: { nodes: [{ id: 'trigger' }], edges: [] },
  }
  const mesUpload = { tasks: [task] }

  assert.deepEqual(sync.setMesTaskFlowAvailable(mesUpload, 'entry', true), {
    changed: true,
    conflictGroupRef: '',
  })
  assert.equal(task.flowAvailable, true)
  assert.equal(task.enabled, false)
  assert.equal(task.flow.nodes[0].id, 'trigger')
})

test('关闭采集流程功能会停止上报但保留原流程', () => {
  assert.equal(typeof sync.setMesTaskFlowAvailable, 'function')
  const task = {
    id: 'entry',
    groupRef: 'Line.PLC.OP030.进站',
    flowAvailable: true,
    enabled: true,
    flow: { nodes: [{ id: 'upload' }], edges: [{ from: 'trigger', to: 'upload' }] },
  }

  assert.deepEqual(sync.setMesTaskFlowAvailable({ tasks: [task] }, 'entry', false), {
    changed: true,
    conflictGroupRef: '',
  })
  assert.equal(task.flowAvailable, false)
  assert.equal(task.enabled, false)
  assert.deepEqual(task.flow, {
    nodes: [{ id: 'upload' }],
    edges: [{ from: 'trigger', to: 'upload' }],
  })
})

test('重复任务 ID 作为定位值时拒绝误关任何采集流程', () => {
  const first = { id: 'duplicate', groupRef: 'Line.PLC.DB1', flowAvailable: true, enabled: true }
  const second = { id: 'duplicate', groupRef: 'Line.PLC.DB2', flowAvailable: true, enabled: true }
  const mesUpload = { tasks: [first, second] }

  assert.deepEqual(sync.setMesTaskFlowAvailable(mesUpload, 'duplicate', false), {
    changed: false,
    conflictGroupRef: '',
  })
  assert.deepEqual(mesUpload.tasks.map((task) => [task.flowAvailable, task.enabled]), [
    [true, true],
    [true, true],
  ])
})

test('完整组路径精确关闭当前采集流程且不受重复任务 ID 影响', () => {
  const first = { id: 'duplicate', groupRef: 'Line.PLC.DB1', flowAvailable: true, enabled: true }
  const second = { id: 'duplicate', groupRef: 'Line.PLC.DB2', flowAvailable: true, enabled: true }
  const mesUpload = { tasks: [first, second] }

  assert.deepEqual(sync.setMesTaskFlowAvailable(mesUpload, {
    id: 'duplicate',
    groupRef: 'Line.PLC.DB2',
  }, false), {
    changed: true,
    conflictGroupRef: '',
  })
  assert.deepEqual(mesUpload.tasks.map((task) => [task.flowAvailable, task.enabled]), [
    [true, true],
    [false, false],
  ])
})

test('完整组路径精确切换当前运行状态且不受重复任务 ID 影响', () => {
  const first = { id: 'duplicate', groupRef: 'Line.PLC.DB1', flowAvailable: true, enabled: true }
  const second = { id: 'duplicate', groupRef: 'Line.PLC.DB2', flowAvailable: true, enabled: true }
  const mesUpload = { tasks: [first, second] }

  assert.deepEqual(sync.setMesTaskEnabled(mesUpload, {
    id: 'duplicate',
    groupRef: 'Line.PLC.DB2',
  }, false), {
    changed: true,
    conflictGroupRef: '',
  })
  assert.deepEqual(mesUpload.tasks.map((task) => task.enabled), [true, false])
})

test('旧工程已有任务默认允许使用采集流程，未创建任务则不允许', () => {
  assert.equal(typeof sync.isMesFlowAvailable, 'function')
  assert.equal(sync.isMesFlowAvailable({ id: 'legacy', enabled: false }), true)
  assert.equal(sync.isMesFlowAvailable({ id: 'closed', flowAvailable: false }), false)
  assert.equal(sync.isMesFlowAvailable(null), false)
})

test('未创建任务的子组同样受父级已开放流程互斥限制', () => {
  assert.equal(typeof sync.findMesGroupAvailabilityConflict, 'function')
  const mesUpload = { tasks: [
    { id: 'parent', groupRef: 'Line.PLC.OP030', flowAvailable: true, enabled: false },
    { id: 'sibling', groupRef: 'Line.PLC.OP040.进站', flowAvailable: true, enabled: false },
  ] }

  assert.equal(sync.findMesGroupAvailabilityConflict(mesUpload, 'Line.PLC.OP030.进站'), 'Line.PLC.OP030')
  assert.equal(sync.findMesGroupAvailabilityConflict(mesUpload, 'Line.PLC.OP040.出站'), '')
})

test('范围批量入口不会启动已关闭采集流程功能的任务', () => {
  const mesUpload = { tasks: [
    { id: 'available', groupRef: 'Line.PLC.OP030', flowAvailable: true, enabled: false },
    { id: 'closed', groupRef: 'Line.PLC.OP040', flowAvailable: false, enabled: false },
  ] }

  assert.equal(sync.setAllMesTasksEnabled(mesUpload, true), 1)
  assert.deepEqual(mesUpload.tasks.map((task) => task.enabled), [true, false])
})

test('设备一键开启只开放直属第一层任务并关闭更深层任务', () => {
  assert.equal(typeof sync.configureDeviceFirstLevelMesFlows, 'function')
  const deepFlow = { nodes: [{ id: 'upload' }], edges: [{ from: 'trigger', to: 'upload' }] }
  const otherDeviceTask = {
    id: 'other-device', groupRef: 'Line.PLC2.DB9', deviceRef: 'Line.PLC2',
    flowAvailable: true, enabled: true,
  }
  const mesUpload = { tasks: [
    {
      id: 'direct-db1', name: 'DB1', groupRef: 'Line.PLC.DB1', deviceRef: 'Line.PLC',
      flowAvailable: false, enabled: false, flow: { nodes: [], edges: [] },
    },
    {
      id: 'deep-child', name: 'Child', groupRef: 'Line.PLC.DB1.Child', deviceRef: 'Line.PLC',
      flowAvailable: true, enabled: true, flow: deepFlow,
    },
    otherDeviceTask,
  ] }
  const groups = [
    { name: 'DB1', tags: [], subGroups: [{ name: 'Child', tags: [], subGroups: [] }] },
    { name: 'DB2', tags: [], subGroups: [] },
  ]

  assert.deepEqual(
    sync.configureDeviceFirstLevelMesFlows(mesUpload, 'Line', 'PLC', groups, () => 'direct-db2'),
    { direct: 2, created: 1, opened: 2, closed: 1 },
  )
  const byRef = Object.fromEntries(mesUpload.tasks.map((task) => [task.groupRef, task]))
  assert.equal(byRef['Line.PLC.DB1'].flowAvailable, true)
  assert.equal(byRef['Line.PLC.DB1'].enabled, false)
  assert.equal(byRef['Line.PLC.DB2'].flowAvailable, true)
  assert.equal(byRef['Line.PLC.DB2'].enabled, false)
  assert.equal(byRef['Line.PLC.DB1.Child'].flowAvailable, false)
  assert.equal(byRef['Line.PLC.DB1.Child'].enabled, false)
  assert.equal(byRef['Line.PLC.DB1.Child'].flow, deepFlow)
  assert.equal(otherDeviceTask.flowAvailable, true)
  assert.equal(otherDeviceTask.enabled, true)
})
