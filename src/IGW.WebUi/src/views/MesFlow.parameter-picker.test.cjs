const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('全部八类PLC参数字段使用共享选择器并传入当前任务范围', () => {
  assert.match(source, /import MesParameterPicker/)
  const pickers = source.match(/<MesParameterPicker\b[\s\S]*?\/>/g) || []
  assert.equal(pickers.length, 8)
  assert.ok(pickers.every((picker) => /:scope-group="task\?\.groupRef \|\| ''"/.test(picker)))

  for (const model of [
    'c.tagId', 'f.tagId', 'st.baseGroup', 'st.countTagId', 'selNode.switchTag',
    'w.tagId', 'w.copyFromTagId',
  ]) assert.ok(pickers.some((picker) => picker.includes(`v-model="${model}"`)), `缺少 ${model} 选择器`)
})

test('结构组和回写目标使用正确模式与读写权限', () => {
  const pickers = source.match(/<MesParameterPicker\b[\s\S]*?\/>/g) || []
  const baseGroup = pickers.find((picker) => picker.includes('v-model="st.baseGroup"')) || ''
  assert.match(baseGroup, /kind="group"/)
  assert.match(baseGroup, /access="read"/)

  const writeTargets = pickers.filter((picker) => picker.includes('v-model="w.tagId"'))
  assert.equal(writeTargets.length, 2)
  assert.ok(writeTargets.every((picker) => /access="write"/.test(picker)))
  const copySource = pickers.find((picker) => picker.includes('v-model="w.copyFromTagId"')) || ''
  assert.match(copySource, /access="read"/)
})

test('不再从运行时全量点位接口加载候选', () => {
  assert.doesNotMatch(source, /tagSuggest/)
  assert.doesNotMatch(source, /api\.tags\(\)/)
})

test('结构成员、JSON字段名和参数码保持普通输入，捕获字段从本节点响应格式选', () => {
  assert.match(source, /<MesResponseFieldPicker v-model="c\.responseField" :options="ownResponseFields"/)
  assert.match(source, /<el-input v-model="m\.tagId"/)
  assert.match(source, /<el-input v-model="f\.fieldName"/)
  assert.match(source, /<el-input v-model="f\.parameterCode"/)
})

test('判成败和响应回写字段使用上游响应字段下拉', () => {
  assert.match(source, /import MesResponseFieldPicker/)
  const pickers = (source.match(/<MesResponseFieldPicker\b[\s\S]*?\/>/g) || [])
    .filter((picker) => !picker.includes('v-model="c.responseField"'))
  assert.equal(pickers.length, 2)
  assert.ok(pickers.every((picker) => /:options="upstreamResponseFields"/.test(picker)))
  assert.ok(pickers.some((picker) => picker.includes('v-model="c.field"')))
  assert.ok(pickers.some((picker) => picker.includes('v-model="w.responseField"')))
})

test('上游响应字段候选包含成功键和捕获字段', () => {
  assert.match(source, /const upstreamResponseFields = computed/)
  assert.match(source, /node\.http\?\.successKey/)
  assert.match(source, /capture\.responseField/)
  assert.match(source, /incoming\.get\(selected\.id\)/)
})
