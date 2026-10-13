const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')
const { reactive, toRaw } = require('vue')

function loadCloneModule() {
  const filename = path.join(__dirname, 'reactiveClone.ts')
  if (!fs.existsSync(filename)) return {}
  const source = fs.readFileSync(filename, 'utf8')
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  const loaded = new Module(filename, module)
  loaded.filename = filename
  loaded.paths = Module._nodeModulePaths(path.join(__dirname, '..', '..'))
  loaded._compile(output, filename)
  return loaded.exports
}

const clone = loadCloneModule()

test('copies a Vue reactive data-range draft into a plain independent value', () => {
  assert.equal(typeof clone.cloneReactiveValue, 'function')

  const draft = reactive([
    {
      source: { channel: 'PLC4', device: 'PLC4', group: 'LogCheck1' },
      output: 'PLC4.LogCheck1',
      excludePatterns: ['Reserved*'],
    },
  ])

  const result = clone.cloneReactiveValue(draft)

  assert.deepEqual(result, [
    {
      source: { channel: 'PLC4', device: 'PLC4', group: 'LogCheck1' },
      output: 'PLC4.LogCheck1',
      excludePatterns: ['Reserved*'],
    },
  ])
  assert.notEqual(result, draft)
  assert.notEqual(result[0], draft[0])
  result[0].output = 'changed'
  assert.equal(draft[0].output, 'PLC4.LogCheck1')
})

test('removes a nested Vue proxy before cloning serializable MES configuration', () => {
  const nestedProxy = reactive({ type: 'Upload', config: { url: '/mes' } })
  const flow = reactive({ nodes: [nestedProxy], edges: [] })

  // This is the exact shape that broke Save & Apply: unwrapping the root does
  // not guarantee that an already-inserted child proxy is unwrapped.
  assert.throws(() => structuredClone(toRaw(flow)), { name: 'DataCloneError' })

  const result = clone.cloneReactiveValue(flow)
  assert.deepEqual(result, {
    nodes: [{ type: 'Upload', config: { url: '/mes' } }],
    edges: [],
  })
  assert.doesNotThrow(() => structuredClone(result))
})
