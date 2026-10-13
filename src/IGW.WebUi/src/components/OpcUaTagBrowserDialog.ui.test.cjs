const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const file = path.join(__dirname, 'OpcUaTagBrowserDialog.vue')
const source = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : ''

test('浏览器提供虚拟树多选、搜索、清空和冲突确认', () => {
  assert.match(source, /el-tree-v2/)
  assert.match(source, /node-key="id" lazy/)
  assert.match(source, /browseOpcUaChildren/)
  assert.match(source, /browseOpcUaBranch/)
  assert.match(source, /check-strictly/)
  assert.match(source, /show-checkbox/)
  assert.match(source, /\$t\('已选 \{count\} 个'/)
  assert.match(source, /清空选择/)
  assert.match(source, /覆盖同路径标记/)
  assert.match(source, /所选分支超过浏览数量或深度上限/)
  assert.match(source, /搜索名称、路径或 NodeId/)
})

test('截断、零选择或未确认冲突时禁止导入', () => {
  assert.match(source, /canApply/)
  assert.match(source, /truncated/)
  assert.match(source, /overwriteConflicts/)
  assert.match(source, /:disabled="!canApply"/)
})

test('结构和点位节点都提供目标名称编辑且校验会阻止导入', () => {
  assert.match(source, /toImportedOpcUaTag/)
  assert.match(source, /目标名称/)
  assert.match(source, /resetTargetName/)
  assert.match(source, /aliasErrors/)
  assert.match(source, /选择左侧设备、结构或点位可修改导入名称/)
})

test('扁平结构名通过统一路径解析生成树且数组索引按数字排序', () => {
  assert.match(source, /structuredOpcUaTagPath/)
  assert.match(source, /localeCompare\(b\.label[^\n]+numeric:\s*true/)
})

test('OPC UA 顶层节点按设备处理且一次只允许导入一个设备', () => {
  assert.match(source, /prepareOpcUaImport/)
  assert.match(source, /preparedDeviceRoots/)
  assert.match(source, /sourceDeviceName/)
  assert.match(source, /targetDeviceName/)
  assert.match(source, /一次只能导入一个设备，请只勾选一个顶层设备/)
  assert.match(source, /selectedNodeIsDevice/)
  assert.match(source, /selectedDeviceRoots\.value\.length > 1/)
  assert.doesNotMatch(source, /selectedDeviceRoots\.value\.length !== 1/)
})

test('右侧已选范围明确显示可换行的完整实际路径', () => {
  assert.match(source, /selected-range-path/)
  assert.match(source, /\$t\('实际路径'\)/)
  assert.match(source, /\.selected-range-path code\s*\{[^}]*white-space:\s*normal[^}]*overflow-wrap:\s*anywhere/s)
  assert.match(source, /\.source-name code\s*\{[^}]*white-space:\s*normal[^}]*overflow-wrap:\s*anywhere/s)
})
