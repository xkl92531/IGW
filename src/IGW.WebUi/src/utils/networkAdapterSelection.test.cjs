const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

const filename = path.join(__dirname, 'networkAdapterSelection.ts')
const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
  fileName: filename,
}).outputText
const loaded = new Module(filename, module)
loaded.filename = filename
loaded.paths = module.paths
loaded._compile(output, filename)
const selection = loaded.exports

test('missing saved adapter remains visible as unavailable', () => {
  const options = selection.adapterOptions([], {
    networkAdapter: 'Old PLC LAN', networkAdapterId: 'old', networkAdapterIpv4: '10.1.0.20',
  })
  assert.equal(options.length, 1)
  assert.equal(options[0].unavailable, true)
  assert.equal(options[0].value, 'old|10.1.0.20')
})

test('selection writes stable id name and IPv4 together', () => {
  const row = { id: 'nic-1', name: 'PLC LAN', ipv4: '10.10.0.5', prefixLength: 24, label: 'PLC LAN — 10.10.0.5' }
  assert.deepEqual(selection.applyAdapterValue({}, 'nic-1|10.10.0.5', [row]), {
    networkAdapter: 'PLC LAN', networkAdapterId: 'nic-1', networkAdapterIpv4: '10.10.0.5',
  })
})

test('default removes stable identity fields', () => {
  assert.deepEqual(selection.applyAdapterValue({
    networkAdapter: 'PLC LAN', networkAdapterId: 'nic-1', networkAdapterIpv4: '10.10.0.5',
  }, '', []), { networkAdapter: '默认值' })
})

test('unique legacy adapter name is shown available and upgraded on save', () => {
  const row = { id: 'nic-1', name: 'PLC LAN', ipv4: '10.10.0.5', prefixLength: 24, label: 'PLC LAN — 10.10.0.5' }
  const settings = { networkAdapter: 'PLC LAN' }
  const options = selection.adapterOptions([row], settings)
  assert.equal(options.length, 1)
  assert.equal(options[0].value, 'legacy|PLC LAN')
  assert.equal(options[0].unavailable, false)
  assert.deepEqual(selection.applyAdapterValue(settings, options[0].value, [row]), {
    networkAdapter: 'PLC LAN', networkAdapterId: 'nic-1', networkAdapterIpv4: '10.10.0.5',
  })
})
