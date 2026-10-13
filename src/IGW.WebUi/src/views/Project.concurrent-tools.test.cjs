const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

test('工程页使用两个可重叠的独立浮动窗口，点击置顶且不再强制平铺或联动最小化', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  assert.match(source, /import WorkWindow/)
  assert.match(source, /<WorkWindow[\s\S]*qcOpen/)
  assert.match(source, /<WorkWindow[\s\S]*mesFlowOpen/)
  assert.doesNotMatch(source, /<el-dialog v-model="qcOpen"/)
  assert.doesNotMatch(source, /<el-dialog v-model="mesFlowOpen"/)
  assert.match(source, /class="workbar"/)
  assert.match(source, /restoreTool\('qc'\)/)
  assert.match(source, /restoreTool\('mes'\)/)
  assert.match(source, /requestMesFlowClose/)
  assert.match(source, /beforeMesFlowClose/)
  assert.match(source, /@focus="focusTool\('qc'\)"/)
  assert.match(source, /@focus="focusTool\('mes'\)"/)
  assert.doesNotMatch(source, /function tileTools/)
  assert.doesNotMatch(source, /mesFlowMinimized\.value = true/)
  assert.doesNotMatch(source, /qcMinimized\.value = true/)
})

test('MES 和 QC 初始窗口尺寸按当前浏览器可用空间生成而不是固定窄宽度', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  assert.match(source, /window\.innerWidth/)
  assert.match(source, /function initialToolRect/)
  assert.doesNotMatch(source, /mesFlowRect = ref<WorkWindowRect>\(\{ x: 28, y: 36, width: 1480/)
})

test('从工程配置打开采集流程时默认最大化，QC 仍保持普通独立窗口', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  assert.match(source, /function prepareToolOpen\(tool: ToolName, maximized = false\)/)
  assert.match(source, /prepareToolOpen\('mes', true\)/)
  assert.match(source, /prepareToolOpen\('qc'\)/)
})

test('浮动窗口置顶层级使用固定上下两层，不能无限增长越过业务弹窗', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  assert.match(source, /const toolBackZ = 2201/)
  assert.match(source, /const toolFrontZ = 2202/)
  assert.doesNotMatch(source, /\+\+toolZ/)
})

test('MES 模板弹窗挂到页面顶层且高于工作窗，不能被裁剪或挡住', () => {
  const source = fs.readFileSync(path.join(__dirname, '../components/FlowTemplatePanel.vue'), 'utf8')
  assert.match(source, /append-to-body/)
  assert.match(source, /:z-index="5200"/)
})

test('MES 模板菜单传送到页面顶层，避免被工作窗裁剪或遮住', () => {
  const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')
  assert.match(source, /@command="applyTemplate"/)
  assert.match(source, /:teleported="true"/)
  assert.match(source, /command="_manage"/)
})

test('MES 工作窗标题跟随画布当前任务，而不是停留在最初右键打开的数据块', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  assert.match(source, /@active-group-change="mesFlowGroup = \$event"/)
})

test('打开大型采集流程先显示准备窗口并让浏览器完成一帧，再保存工程和挂载画布', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')
  const start = source.indexOf('async function openMesFlowFor')
  const end = source.indexOf('function mesMenuItems', start)
  assert.ok(start >= 0 && end > start, '应能定位采集流程打开函数')
  const openSource = source.slice(start, end)
  assert.ok(openSource.indexOf('mesFlowOpen.value = true') < openSource.indexOf('autoSave.flush()'))
  assert.match(openSource, /mesFlowPreparing\.value = true/)
  assert.match(openSource, /await nextPaint\(\)/)
  assert.match(source, /v-if="mesFlowPreparing" class="mes-flow-preparing"/)
  assert.match(source, /<MesFlow v-else-if="mesFlowOpen"/)
})
