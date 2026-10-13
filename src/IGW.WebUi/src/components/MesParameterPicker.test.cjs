const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesParameterPicker.vue'), 'utf8')

test('参数选择器使用远程虚拟下拉且允许保留手工值', () => {
  assert.match(source, /el-select-v2/)
  assert.match(source, /filterable/)
  assert.match(source, /remote/)
  assert.match(source, /allow-create/)
  assert.match(source, /api\.parameterOptions/)
  assert.match(source, /createMesParameterRequestCoordinator/)
})

test('参数选择器明确显示范围、截断、错误和旧值缺失状态', () => {
  for (const text of [
    '当前 DB', '当前 PLC', '其他 PLC', '当前工程中不存在',
    '继续输入以缩小范围', '重新加载候选', '未找到工程点位',
  ]) assert.match(source, new RegExp(text))
})

test('参数选择器在上下文变化和卸载时取消旧请求', () => {
  assert.match(source, /onBeforeUnmount\(\(\)\s*=>\s*\{[\s\S]*coordinator\.cancel\(\)/)
  assert.match(source, /watch\(\s*\(\)\s*=>\s*\[props\.modelValue, props\.scopeGroup, props\.kind, props\.access\]/)
  assert.match(source, /coordinator\.cancel\(\)/)
})

test('当前值先按完整路径核对，查询完成前不误报缺失且不改写模型', () => {
  assert.match(source, /findExactParameterOption/)
  assert.match(source, /mergeCurrentParameterOptions/)
  assert.match(source, /currentValidationCompleted/)
  assert.match(source, /\(props\.modelValue \|\| ''\)\.trim\(\)/)
  // 当前值只在打开下拉时随搜索一起核对；核对前按“待确认”展示，不误报缺失。
  assert.match(source, /onVisibleChange[\s\S]*request\(\(props\.modelValue \|\| ''\)\.trim\(\), true\)/)
  assert.doesNotMatch(source, /emit\('update:modelValue',\s*''\)/)
})
