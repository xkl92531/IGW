const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'mesParameterRequest.ts')
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

const { createMesParameterRequestCoordinator } = loadModule()
const wait = (ms = 0) => new Promise((resolve) => setTimeout(resolve, ms))

test('只有最后一次工程参数请求可以发布结果并且旧请求被取消', async () => {
  assert.equal(typeof createMesParameterRequestCoordinator, 'function')
  const pending = new Map()
  const coordinator = createMesParameterRequestCoordinator((query, signal) =>
    new Promise((resolve, reject) => pending.set(query, { resolve, reject, signal })), 0)
  const published = []

  coordinator.search('old', (value) => published.push(value), () => {})
  await wait(5)
  coordinator.search('new', (value) => published.push(value), () => {})
  await wait(5)

  assert.equal(pending.get('old').signal.aborted, true)
  pending.get('new').resolve({ items: [{ value: 'new' }], truncated: false })
  pending.get('old').resolve({ items: [{ value: 'old' }], truncated: false })
  await wait(0)
  assert.deepEqual(published.flatMap((value) => value.items.map((item) => item.value)), ['new'])
})

test('连续输入在防抖窗口内只发最后一次请求', async () => {
  const calls = []
  const coordinator = createMesParameterRequestCoordinator(async (query) => {
    calls.push(query)
    return query
  }, 20)

  coordinator.search('D', () => {}, () => {})
  coordinator.search('DB', () => {}, () => {})
  coordinator.search('DB1', () => {}, () => {})
  await wait(40)
  assert.deepEqual(calls, ['DB1'])
})

test('请求失败只报告错误不会伪造空结果清除当前候选', async () => {
  const coordinator = createMesParameterRequestCoordinator(
    async () => { throw new Error('offline') }, 0)
  const results = []
  const errors = []

  coordinator.search('DB', (value) => results.push(value), (error) => errors.push(error.message))
  await wait(5)

  assert.deepEqual(results, [])
  assert.deepEqual(errors, ['offline'])
})

test('组件销毁取消后不再发布结果或错误', async () => {
  let resolveRequest
  const coordinator = createMesParameterRequestCoordinator(() =>
    new Promise((resolve) => { resolveRequest = resolve }), 0)
  const published = []

  coordinator.search('DB', (value) => published.push(value), (error) => published.push(error))
  await wait(5)
  coordinator.cancel()
  resolveRequest({ items: [{ value: 'late' }], truncated: false })
  await wait(0)

  assert.deepEqual(published, [])
})
