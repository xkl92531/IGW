const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadTopologyModule() {
  const filename = path.join(__dirname, 'projectTopology.ts')
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

const topology = loadTopologyModule()

test('用户确认删除的结构差额被授权给后台自动保存', () => {
  assert.equal(typeof topology.authorizeDeletion, 'function')
  const saved = { channels: 1, devices: 2, groups: 4, tags: 20, mesTasks: 2 }
  const afterDelete = { channels: 1, devices: 1, groups: 2, tags: 8, mesTasks: 1 }
  const allowed = topology.authorizeDeletion(topology.emptyProjectShape(), saved, afterDelete)

  assert.deepEqual(allowed, { channels: 0, devices: 1, groups: 2, tags: 12, mesTasks: 1 })
  assert.deepEqual(topology.unapprovedShapeLoss(saved, afterDelete, allowed), [])
})

test('超过用户确认范围的额外结构丢失仍被识别并阻止', () => {
  const saved = { channels: 2, devices: 3, groups: 4, tags: 20, mesTasks: 3 }
  const afterDelete = { channels: 2, devices: 2, groups: 3, tags: 15, mesTasks: 2 }
  const allowed = topology.authorizeDeletion(topology.emptyProjectShape(), saved, afterDelete)
  const accidentallyTruncated = { channels: 1, devices: 1, groups: 1, tags: 3, mesTasks: 0 }

  assert.deepEqual(
    topology.unapprovedShapeLoss(saved, accidentallyTruncated, allowed),
    ['通道 2→1', '设备 3→1', '标记组 4→1', '点位 20→3', 'MES 任务 3→0'],
  )
})

test('保存成功只消费保存快照已有的授权，保留保存期间产生的新删除', () => {
  const current = { channels: 1, devices: 2, groups: 3, tags: 10, mesTasks: 2 }
  const includedInSnapshot = { channels: 1, devices: 1, groups: 1, tags: 4, mesTasks: 1 }
  assert.deepEqual(
    topology.consumeDeletionAuthorization(current, includedInSnapshot),
    { channels: 0, devices: 1, groups: 2, tags: 6, mesTasks: 1 },
  )
})

test('工程结构统计包含设备直属点位和所有嵌套组', () => {
  const project = { channels: [{ devices: [{
    tags: [{}, {}],
    groups: [{ tags: [{}], subGroups: [{ tags: [{}, {}], subGroups: [] }] }],
  }] }] }
  assert.deepEqual(topology.projectShape(project), { channels: 1, devices: 1, groups: 2, tags: 5, mesTasks: 0 })
})
