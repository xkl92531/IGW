const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')

test('OPC UA端点属于通道且设备不再要求端点', () => {
  assert.match(source, /UA 服务器/)
  assert.match(source, /chForm\.settings\.url/)
  assert.match(source, /devPDriverId === 'OpcUa\.Client'/)
  assert.match(source, /旧设备端点/)
  assert.match(source, /wiz\.driverId !== 'OpcUa\.Client'/)
})

test('设备入口打开选择浏览器而不是直接全量导入', () => {
  assert.match(source, /自动生成标记…/)
  assert.match(source, /OpcUaTagBrowserDialog/)
  assert.match(source, /opcBrowserOpen/)
  assert.doesNotMatch(source, /for \(const bt of tags\)[\s\S]{0,500}dev\.tags/)
})

test('CSV先预览后原子应用层级结构', () => {
  assert.match(source, /parseKepTagCsv/)
  assert.match(source, /buildImportPreview/)
  assert.match(source, /applyTagImport/)
  assert.match(source, /CSV 导入预览/)
})

test('OPC UA导入按设备或组的真实子组属性回写', () => {
  assert.match(source, /replaceImportedTarget/)
  assert.doesNotMatch(source, /target\.groups\s*=\s*result\.target\.groups/)
})

test('OPC UA 浏览根映射为设备名称并在落盘前展开旧包装组', () => {
  assert.match(source, /sourceDeviceName\?: string/)
  assert.match(source, /targetDeviceName\?: string/)
  assert.match(source, /flattenLegacyOpcUaDeviceRoot/)
  assert.match(source, /设备名称已存在/)
  assert.match(source, /migrateMesRefs\(oldDevicePath,\s*newDevicePath\)/)
  assert.match(source, /旧设备根.*非空采集流程/)
  assert.match(source, /flattened\.changed\s*\?\s*markConfirmedDeletion/)
})
