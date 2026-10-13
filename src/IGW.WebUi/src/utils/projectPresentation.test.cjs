const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const Module = require('node:module')
const ts = require('typescript')

function loadPresentationModule() {
  const filename = path.join(__dirname, 'projectPresentation.ts')
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
  '项目': 'Project',
  '连接性': 'Connectivity',
  '● 采集中': '● Collecting',
  '○ 采集停用': '○ Collection disabled',
  '● {count} 组采集': '● {count} collection flows',
  'MES 采集流程': 'MES Collection Flow',
  '数据块': 'Data Block',
}
const tr = (source, params = {}) => (messages[source] || source)
  .replace(/\{(\w+)\}/g, (_, key) => String(params[key] ?? `{${key}}`))

test('project system labels and collection marks follow the active language', () => {
  assert.equal(typeof presentation.systemTreeLabel, 'function')
  assert.equal(presentation.systemTreeLabel('project', tr), 'Project')
  assert.equal(presentation.systemTreeLabel('connectivity', tr), 'Connectivity')
  assert.equal(presentation.collectionStateMark('group', true, 0, tr).text, '● Collecting')
  assert.equal(presentation.collectionStateMark('group', false, 0, tr).text, '○ Collection disabled')
  assert.equal(presentation.collectionStateMark('device', undefined, 2, tr).text, '● 2 collection flows')
})

test('MES flow dialog translates only system copy and preserves the DB name', () => {
  assert.equal(typeof presentation.mesFlowDialogTitle, 'function')
  assert.equal(
    presentation.mesFlowDialogTitle('PLC1.DB2100_电芯OCV_DataDB', tr),
    'MES Collection Flow — DB2100_电芯OCV_DataDB',
  )
  assert.equal(presentation.mesFlowDialogTitle('', tr), 'MES Collection Flow — Data Block')
})

test('collection flow availability controls configuration independently from runtime state', () => {
  assert.equal(typeof presentation.isMesFlowConfigurationDisabled, 'function')
  assert.equal(presentation.isMesFlowConfigurationDisabled({ enabled: false, flowAvailable: true }), false)
  assert.equal(presentation.isMesFlowConfigurationDisabled({}), false)
  assert.equal(presentation.isMesFlowConfigurationDisabled({ enabled: true }), false)
  assert.equal(presentation.isMesFlowConfigurationDisabled({ enabled: false, flowAvailable: false }), true)
  assert.equal(presentation.isMesFlowConfigurationDisabled(null), true)
})

test('project tree shows a trimmed description only for tag groups', () => {
  assert.equal(typeof presentation.projectTreeDescription, 'function')
  assert.equal(presentation.projectTreeDescription('group', { description: '  电芯数据说明  ' }), '电芯数据说明')
  assert.equal(presentation.projectTreeDescription('group', { description: '   ' }), '')
  assert.equal(presentation.projectTreeDescription('device', { description: '设备说明' }), '')
  assert.equal(presentation.projectTreeDescription('group', null), '')
})
