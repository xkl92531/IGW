const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesResponseFieldPicker.vue'), 'utf8')

test('响应字段选择器提供下拉候选并兼容手工输入和旧值', () => {
  assert.match(source, /el-select-v2/)
  assert.match(source, /:options="visibleOptions"/)
  assert.match(source, /filterable/)
  assert.match(source, /allow-create/)
  assert.match(source, /values\.add\(current\)/)
  assert.match(source, /\(props\.modelValue \|\| ''\)\.trim\(\)/)
})

test('没有候选时提示去上报节点填接口返回格式，也可手工输入', () => {
  assert.match(source, /:no-data-text="\$t\('在上报节点填「接口返回格式」就能选，也可直接输入完整路径'\)"/)
})
