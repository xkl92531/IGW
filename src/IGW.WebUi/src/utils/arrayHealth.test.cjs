const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'arrayHealth.ts')
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

const health = loadModule()

test('数组健康按 Bad 和 Uncertain 排序并支持工位筛选', () => {
  const devices = [
    { name: 'LineA.PLC1', stats: { arrayTags: 2, arrayElements: 1000, arrayGood: 900, arrayUncertain: 50, arrayBad: 50 } },
    { name: 'LineB.PLC2', stats: { arrayTags: 1, arrayElements: 200, arrayGood: 200, arrayUncertain: 0, arrayBad: 0 } },
    { name: 'LineA.PLC3', stats: { arrayTags: 1, arrayElements: 200, arrayGood: 180, arrayUncertain: 20, arrayBad: 0 } },
    { name: 'LineA.scalar', stats: { arrayTags: 0 } },
  ]
  assert.deepEqual(health.selectArrayHealthDevices(devices, new Set()).map((item) => item.name),
    ['LineA.PLC1', 'LineA.PLC3', 'LineB.PLC2'])
  assert.deepEqual(health.selectArrayHealthDevices(devices, new Set(['LineB'])).map((item) => item.name),
    ['LineB.PLC2'])
})

test('数组容量使用可读字节单位', () => {
  assert.equal(health.formatArrayBytes(2048), '2.0 KB')
  assert.equal(health.formatArrayBytes(2 * 1024 * 1024), '2.0 MB')
})
