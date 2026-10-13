const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const flowSource = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')
const panelSource = fs.readFileSync(path.join(__dirname, '..', 'components', 'FlowTemplatePanel.vue'), 'utf8')
const apiSource = fs.readFileSync(path.join(__dirname, '..', 'api.ts'), 'utf8')
const restSource = fs.readFileSync(path.join(__dirname, '..', '..', '..', 'IGW.Northbound.RestApi', 'TemplateApi.cs'), 'utf8')

test('单组流程模板接口显式传递覆盖开关且默认关闭', () => {
  assert.match(apiSource, /flowTemplateApply:\s*\(templateName: string, targetGroup: string, replaceExisting = false, taskName\?: string\)/)
  assert.match(apiSource, /\{ templateName, targetGroup, taskName, replaceExisting \}/)
  assert.match(restSource, /record FlowApplyBody\([^)]*bool ReplaceExisting = false\)/)
  assert.match(restSource, /body\.ReplaceExisting,\s*out report/)
})

test('新建采集任务保持空画布且不自动铺标准握手流程', () => {
  assert.doesNotMatch(flowSource, /if \(task\.value && !\(task\.value\.flow\?\.nodes\?\.length\)\) applyTemplate\('handshake'\)/)
})

test('内置模板只有在用户确认后才替换当前画布', () => {
  const start = flowSource.indexOf('async function applyTemplate(')
  const end = flowSource.indexOf('// ---------- 运行态高亮', start)
  assert.ok(start >= 0 && end > start, '应能定位异步模板套用函数')
  const block = flowSource.slice(start, end)
  assert.match(block, /await ElMessageBox\.confirm\(/)
  assert.ok(block.indexOf('await ElMessageBox.confirm(') < block.indexOf('flow.value.nodes = []'))
  assert.match(block, /catch \{ return \}/)
})

test('模板目标使用统一 MES 边界并区分空任务与已有流程', () => {
  // 目标组由服务端 MES 流程目录统一给出(与任务边界同源)，前端不再自行推导。
  assert.match(panelSource, /props\.catalog\?\.groups/)
  assert.match(panelSource, /hasFlow/)
  assert.match(panelSource, /已有流程，将覆盖/)
  assert.match(panelSource, /await ElMessageBox\.confirm\(/)
  assert.match(panelSource, /api\.flowTemplateApply\(selectedTemplate, selectedTarget, hasTask\)/)
})

test('批量模板只禁用非空流程并允许空任务作为目标', () => {
  const bulkStart = panelSource.indexOf('<template v-else>')
  const bulkEnd = panelSource.indexOf('</template>', bulkStart)
  const bulkBlock = panelSource.slice(bulkStart, bulkEnd)
  assert.match(bulkBlock, /:disabled="g\.hasFlow"/)
  assert.match(panelSource, /filter\(\(g\) => !g\.hasFlow\)/)
})
