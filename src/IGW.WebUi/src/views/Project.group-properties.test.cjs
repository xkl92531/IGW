const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')

test('工程配置右侧列表列宽可拖动并在浏览器保存', () => {
  assert.match(source, /const GRID_COLUMN_WIDTHS_KEY/)
  assert.match(source, /localStorage\.setItem\(GRID_COLUMN_WIDTHS_KEY/)
  assert.match(source, /@header-dragend="onGridColumnResize"/)
  assert.match(source, /:column-key="gridColumnKey\(col\)"/)
  assert.match(source, /:resizable="true"/)
})

test('一级和二级标记组属性使用独立的流程可用开关且不会直接启动任务', () => {
  const openStart = source.indexOf('function openGroupDialog')
  const confirmStart = source.indexOf('function confirmGroup', openStart)
  const confirmEnd = source.indexOf('// -- 标记属性', confirmStart)
  assert.ok(openStart >= 0 && confirmStart > openStart && confirmEnd > confirmStart)

  const openSource = source.slice(openStart, confirmStart)
  const confirmSource = source.slice(confirmStart, confirmEnd)
  assert.match(openSource, /isMesBoundaryGroup\(node\)/)
  assert.match(openSource, /taskFor\(grpEditPath\.value\)/)
  assert.match(openSource, /flowAvailable/)
  assert.match(confirmSource, /setMesTaskFlowAvailable/)
  assert.match(confirmSource, /setMesTaskEnabledWithConfirmation/)
  assert.match(source, /<el-form-item[^>]*:label="\$t\('采集流程'\)"/)
  assert.match(source, /<el-switch v-model="grpForm\.flowAvailable"/)
  assert.doesNotMatch(source, /<el-table-column[^>]*:label="\$t\('采集任务'\)"/)
})

test('从右键菜单切换到其他树节点或表格行会立即关闭旧菜单', () => {
  assert.match(source, /function onNodeClick\(data: any\) \{ hideMenu\(\); if \(data\?\.kind !== 'loading'\) pickNode\(data\) \}/)
  assert.match(source, /sel\.value = \{[^}]*parent: node\.parent/s,
    'tree selection must retain its parent chain so Quick Client receives the exact channel.device.group path')
  assert.match(source, /function onRowClick\(row: any,[^)]*\) \{\s*hideMenu\(\);/)
})

test('通道高级属性为明确的 Siemens Classic 和地址驱动显示手动最大间隙', () => {
  assert.match(source, /const chSupportsManualBatchGap = computed/)
  assert.match(source, /const chSiemensClassicOnly = computed/)
  assert.match(source, /S7-200.*S7-200Smart.*S7-300.*S7-400/s)
  assert.match(source, /chUsesProtocolCapacityBatching/)
  assert.match(source, /v-if="chForm\.settings\.batchingMode === 'manual'"/)
  assert.match(source, /\$t\('手动最大间隙'\)/)
  assert.match(source, /\$t\('由协议容量自动决定'\)/)
})
