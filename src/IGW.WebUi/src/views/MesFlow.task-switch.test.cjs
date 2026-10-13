const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('流程页面不再提供会批量关闭任务的“全部任务”控件', () => {
  assert.doesNotMatch(source, /setAllMesTasksEnabled/)
  assert.doesNotMatch(source, /mesTaskSwitchState/)
  assert.doesNotMatch(source, /const allTaskSwitch = computed/)
  assert.doesNotMatch(source, /@change="toggleAllTasks"/)
  assert.doesNotMatch(source, />\s*\{\{ \$t\('全部任务'\) \}\}\s*<\/el-checkbox>/)
})

test('流程运行标识仍直接来自数据块任务 enabled', () => {
  assert.match(source, /task\.enabled \? '运行中' : '尚未启用'/)
  assert.doesNotMatch(source, /flow\.enabled/)
})

test('流程工具栏显式导入下拉图标，不能在浏览器持续产生未注册组件警告', () => {
  assert.match(source, /import \{ ArrowDown, Search \} from '@element-plus\/icons-vue'/)
})
