const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'projectGridClipboard.ts')
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

const grid = loadModule()

test('网格行对象重建后仍按稳定键返回全部三个选中项', () => {
  assert.equal(typeof grid.resolveProjectGridTargets, 'function')
  const firstRender = [
    { selectionKey: 'tag-a', kind: 'tag', def: { name: 'A' } },
    { selectionKey: 'tag-b', kind: 'tag', def: { name: 'B' } },
    { selectionKey: 'tag-c', kind: 'tag', def: { name: 'C' } },
  ]
  const selectedKeys = new Set(firstRender.map((row) => row.selectionKey))
  const rerendered = firstRender.map((row) => ({ ...row, def: { ...row.def } }))

  assert.deepEqual(
    grid.resolveProjectGridTargets(rerendered, selectedKeys, 'tag-c').map((row) => row.def.name),
    ['A', 'B', 'C'],
  )
})

test('复制只保存一次快照且每次粘贴得到独立副本', () => {
  assert.equal(typeof grid.createProjectClipboardItems, 'function')
  assert.equal(typeof grid.materializeProjectClipboardItem, 'function')
  const original = { name: 'DB1', tags: [{ name: 'Point1', address: 'DB1.DBX0.0' }] }
  const [item] = grid.createProjectClipboardItems([
    { kind: 'group', def: original, sourcePath: 'Line.PLC.DB1' },
  ], 'copy')
  original.tags[0].name = 'ChangedAfterCopy'

  const firstPaste = grid.materializeProjectClipboardItem(item)
  const secondPaste = grid.materializeProjectClipboardItem(item)
  assert.equal(firstPaste.tags[0].name, 'Point1')
  assert.notEqual(firstPaste, secondPaste)
  assert.notEqual(firstPaste.tags, secondPaste.tags)
})

test('剪切粘贴复用原对象而不再深拷贝大型标记组', () => {
  const original = { name: 'DB1', tags: Array.from({ length: 5000 }, (_, index) => ({ name: `P${index}` })) }
  const [item] = grid.createProjectClipboardItems([
    { kind: 'group', def: original, sourcePath: 'Line.PLC.DB1' },
  ], 'cut')

  assert.equal(grid.materializeProjectClipboardItem(item), original)
  assert.equal(item.snapshot, undefined)
})
