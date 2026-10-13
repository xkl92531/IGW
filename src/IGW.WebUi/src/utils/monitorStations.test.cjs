const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'monitorStations.ts')
  if (!fs.existsSync(filename)) return {}
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = module.paths
  loaded._compile(output, filename)
  return loaded.exports
}

const stations = loadModule()

test('点击工位卡逐项切换多选，不会用新选择覆盖已有选择', () => {
  assert.equal(typeof stations.toggleStationSelection, 'function')
  const one = stations.toggleStationSelection(new Set(), 'A')
  const two = stations.toggleStationSelection(one, 'B')
  const onlyB = stations.toggleStationSelection(two, 'A')

  assert.deepEqual([...two], ['A', 'B'])
  assert.deepEqual([...onlyB], ['B'])
})

test('多个已选工位按 OR 语义过滤直接异常和折叠子项', () => {
  assert.equal(typeof stations.filterEntriesByStations, 'function')
  const entries = [
    { id: 'a', channel: 'A', impacted: [] },
    { id: 'b-child', channel: 'Root', impacted: [{ channel: 'B' }] },
    { id: 'c', channel: 'C', impacted: [] },
  ]

  assert.deepEqual(
    stations.filterEntriesByStations(entries, new Set(['A', 'B'])).map((entry) => entry.id),
    ['a', 'b-child'],
  )
  assert.equal(stations.filterEntriesByStations(entries, new Set()).length, 3)
})

test('运行数据删除工位后清理失效选择，保留仍存在的工位', () => {
  assert.equal(typeof stations.retainExistingStations, 'function')
  const result = stations.retainExistingStations(new Set(['A', 'Deleted']), [{ name: 'A' }, { name: 'B' }])
  assert.deepEqual([...result], ['A'])
})
