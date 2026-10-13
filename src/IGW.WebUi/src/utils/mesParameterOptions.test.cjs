const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'mesParameterOptions.ts')
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

test('当前点位尚未完成工程校验时不能误报不存在', () => {
  const options = loadModule()
  assert.equal(typeof options.mergeCurrentParameterOptions, 'function')
  const [current] = options.mergeCurrentParameterOptions(
    [],
    'S7Sim.PLC1.DB2100.MESFlag',
    'tag',
    null,
    false,
  )

  assert.equal(current.missing, false)
  assert.equal(current.pending, true)
})

test('只有成功校验且精确路径不存在时才显示缺失', () => {
  const { mergeCurrentParameterOptions } = loadModule()
  const [current] = mergeCurrentParameterOptions(
    [],
    'S7Sim.PLC1.DB2100.MESFlag',
    'tag',
    null,
    true,
  )

  assert.equal(current.missing, true)
  assert.equal(current.pending, false)
})

test('精确候选使用工程描述且路径匹配不区分大小写', () => {
  const { findExactParameterOption, mergeCurrentParameterOptions } = loadModule()
  const exact = {
    value: 'S7Sim.PLC1.DB2100.MESFlag',
    label: 'MESFlag',
    fullPath: 'S7Sim.PLC1.DB2100.MESFlag',
    scope: 'CurrentGroup',
    kind: 'tag',
    description: 'MES 回写标志',
  }

  assert.equal(findExactParameterOption([exact], 's7sim.plc1.db2100.mesflag'), exact)
  const [current] = mergeCurrentParameterOptions([], exact.value, 'tag', exact, true)
  assert.equal(current.missing, false)
  assert.equal(current.pending, false)
  assert.equal(current.description, 'MES 回写标志')
})
