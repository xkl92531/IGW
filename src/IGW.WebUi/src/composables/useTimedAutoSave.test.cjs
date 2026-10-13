const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')
const { ref } = require('vue')

function loadModule() {
  const filename = path.join(__dirname, 'useTimedAutoSave.ts')
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

const { useTimedAutoSave } = loadModule()

test('关闭时的 flush 会复用正在进行的静默保存而不会误报失败', async () => {
  const dirty = ref(true)
  const saving = ref(false)
  const changeVersion = ref(1)
  let finishSave
  const gate = new Promise((resolve) => { finishSave = resolve })
  let calls = 0
  const originalWarn = console.warn
  console.warn = () => {}
  let autoSave
  try {
    autoSave = useTimedAutoSave({
      dirty,
      saving,
      changeVersion,
      save: async () => {
        calls++
        saving.value = true
        await gate
        dirty.value = false
        saving.value = false
        return true
      },
    })
  } finally { console.warn = originalWarn }

  const background = autoSave.flush()
  await Promise.resolve()
  const closeFlush = autoSave.flush()
  finishSave()

  assert.equal(await background, true)
  assert.equal(await closeFlush, true)
  assert.equal(calls, 1)
})
