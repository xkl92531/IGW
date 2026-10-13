const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadPresentationModule() {
  const filename = path.join(__dirname, 'licensePresentation.ts')
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

const presentation = loadPresentationModule()
const messages = {
  '授权有效': 'License valid',
  '7 天试用中': '7-day trial active',
  '本机试用': 'Local trial',
  '授权有效期到 {date}。': 'License valid through {date}.',
  '7 天试用中，到期 {date}（剩 {days} 天）。试用结束后需使用授权码激活。': '7-day trial active through {date} ({days} days left). A license code is required after the trial.',
  '正在读取授权状态…': 'Reading license status…',
}
const tr = (source, params = {}) => (messages[source] || source)
  .replace(/\{(\w+)\}/g, (_, key) => String(params[key] ?? `{${key}}`))

test('known license status is rendered from structured data instead of the Chinese backend message', () => {
  assert.equal(typeof presentation.licenseStatusLabel, 'function')
  assert.equal(typeof presentation.licenseStatusMessage, 'function')
  const license = {
    status: 'Valid', expiry: '2027-08-04', daysLeft: 357,
    message: '授权有效,到期 2027-08-04。',
  }
  assert.equal(presentation.licenseStatusLabel(license, tr), 'License valid')
  assert.equal(presentation.licenseStatusMessage(license, tr), 'License valid through 2027-08-04.')
})

test('only the IGW trial customer label is translated', () => {
  assert.equal(typeof presentation.licenseCustomerName, 'function')
  assert.equal(presentation.licenseCustomerName('本机试用', tr), 'Local trial')
  assert.equal(presentation.licenseCustomerName('南卡工厂', tr), '南卡工厂')
})

test('trial status uses structured expiry and days remaining', () => {
  const license = {
    status: 'Trial', expiry: '2026-08-18', daysLeft: 7,
    message: '7 天试用中，到期 2026-08-18（剩 7 天）。试用结束后需使用授权码激活。',
  }
  assert.equal(presentation.licenseStatusLabel(license, tr), '7-day trial active')
  assert.equal(
    presentation.licenseStatusMessage(license, tr),
    '7-day trial active through 2026-08-18 (7 days left). A license code is required after the trial.',
  )
})

test('unknown license status preserves the backend diagnostic message', () => {
  assert.equal(presentation.licenseStatusMessage({ status: 'FutureStatus', message: 'future detail' }, tr), 'future detail')
  assert.equal(presentation.licenseStatusMessage(undefined, tr), 'Reading license status…')
})
