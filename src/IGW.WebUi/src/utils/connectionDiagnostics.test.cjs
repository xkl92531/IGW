const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadModule() {
  const filename = path.join(__dirname, 'connectionDiagnostics.ts')
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

const diagnostics = loadModule()

const activeTimeout = {
  device: 'LineA.PLC1', channel: 'LineA', protocol: 'siemens-s7', code: 'ConnectionTimeout',
  category: '连接超时', scope: 'Transport', state: 'Faulted', cause: '握手超时', suggestion: '检查网络',
  rawError: 'connect timed out', firstOccurredAt: '2026-08-17T08:00:00Z', lastOccurredAt: '2026-08-17T08:00:04Z',
  recoveredAt: null, occurrenceCount: 3, retrying: true, nextRetryMs: 5000, active: true,
}
const recoveredSecurity = {
  ...activeTimeout, device: 'LineB.OPC', channel: 'LineB', protocol: 'opcua-client', code: 'SecurityPolicyMismatch',
  category: '安全策略不匹配', cause: '证书不可信', suggestion: '信任证书', rawError: 'BadSecurityChecksFailed',
  lastOccurredAt: '2026-08-17T07:00:00Z', recoveredAt: '2026-08-17T07:01:00Z', active: false, retrying: false, nextRetryMs: 0,
}

test('连接异常默认活动优先并支持状态、工位、类别、协议和全文筛选', () => {
  const source = { active: [activeTimeout], recovered: [recoveredSecurity] }
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, {}).map((item) => item.device), ['LineA.PLC1', 'LineB.OPC'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { state: 'active' }).map((item) => item.device), ['LineA.PLC1'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { channels: new Set(['LineB']) }).map((item) => item.device), ['LineB.OPC'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { category: 'SecurityPolicyMismatch' }).map((item) => item.device), ['LineB.OPC'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { protocol: 'opcua-client' }).map((item) => item.device), ['LineB.OPC'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { query: '信任证书' }).map((item) => item.device), ['LineB.OPC'])
  assert.deepEqual(diagnostics.filterConnectionDiagnostics(source, { query: 'BadSecurity' }).map((item) => item.device), ['LineB.OPC'])
})

test('筛选项从活动和已恢复异常一起生成并去重排序', () => {
  const options = diagnostics.connectionDiagnosticOptions({ active: [activeTimeout], recovered: [recoveredSecurity] })
  assert.deepEqual(options.categories, [
    { value: 'ConnectionTimeout', label: '连接超时' },
    { value: 'SecurityPolicyMismatch', label: '安全策略不匹配' },
  ])
  assert.deepEqual(options.protocols, ['opcua-client', 'siemens-s7'])
})
