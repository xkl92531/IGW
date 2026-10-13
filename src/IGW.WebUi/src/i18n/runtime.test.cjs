const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const Module = require('node:module')
const path = require('node:path')
const ts = require('typescript')

const originalTsLoader = require.extensions['.ts']
require.extensions['.ts'] = function loadTypeScript(module, filename) {
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: {
      target: ts.ScriptTarget.ES2020,
      module: ts.ModuleKind.CommonJS,
      esModuleInterop: true,
    },
    fileName: filename,
  }).outputText
  module._compile(output, filename)
}

const runtimeModule = path.join(__dirname, 'runtime.ts')
const { createI18nRuntime, normalizeLocale, STORAGE_KEY } = require(runtimeModule)

test.after(() => {
  if (originalTsLoader) require.extensions['.ts'] = originalTsLoader
  else delete require.extensions['.ts']
})

function memoryStorage(initial = {}) {
  const values = new Map(Object.entries(initial))
  return {
    getItem(key) { return values.has(key) ? values.get(key) : null },
    setItem(key, value) { values.set(key, String(value)) },
  }
}

test('invalid or missing saved locale defaults to Chinese', () => {
  assert.equal(normalizeLocale(undefined), 'zh-CN')
  assert.equal(normalizeLocale('de-DE'), 'zh-CN')
  assert.equal(createI18nRuntime(memoryStorage()).locale.value, 'zh-CN')
  assert.equal(createI18nRuntime(memoryStorage({ [STORAGE_KEY]: 'broken' })).locale.value, 'zh-CN')
})

test('switching language takes effect immediately and persists without a save action', () => {
  const storage = memoryStorage()
  const documentElement = { lang: '' }
  const runtime = createI18nRuntime(storage, documentElement, {
    '工程配置': 'Project',
    '已选 {count} 个': '{count} selected',
  })

  runtime.setLocale('en-US')

  assert.equal(runtime.locale.value, 'en-US')
  assert.equal(storage.getItem(STORAGE_KEY), 'en-US')
  assert.equal(documentElement.lang, 'en-US')
  assert.equal(runtime.t('工程配置'), 'Project')
  assert.equal(runtime.t('已选 {count} 个', { count: 2 }), '2 selected')
})

test('new runtime restores the saved language and unknown copy remains unchanged', () => {
  const storage = memoryStorage({ [STORAGE_KEY]: 'en-US' })
  const runtime = createI18nRuntime(storage, { lang: '' }, { '工程配置': 'Project' })

  assert.equal(runtime.locale.value, 'en-US')
  assert.equal(runtime.t('PLC1.DB2100'), 'PLC1.DB2100')
})
