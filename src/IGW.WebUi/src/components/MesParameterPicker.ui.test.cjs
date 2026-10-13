const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesParameterPicker.vue'), 'utf8')

test('虚拟参数列表按实际候选数收缩高度，不留大块空白', () => {
  assert.match(source, /const dropdownHeight = computed/)
  assert.match(source, /:height="dropdownHeight"/)
  assert.match(source, /:item-height="88"/)
  assert.match(source, /class="parameter-option-description"/)
  assert.match(source, /item\.description/)
  assert.match(source, /\.el-select-dropdown__item\) \{[^}]*width: 100%[^}]*height: 88px;/s)
})

test('候选项使用整个弹层宽度显示实际完整路径', () => {
  assert.match(source, /class="parameter-option-path"/)
  assert.match(source, /\.parameter-option \{[^}]*width: 100%/s)
  assert.match(source, /\.mes-parameter-dropdown \.el-select-dropdown\),/)
  assert.match(source, /\.mes-parameter-dropdown \.el-vl__wrapper\),[\s\S]*width: 100%/s)
  assert.match(source, /\.parameter-option-path, \.parameter-option-address \{[^}]*white-space: normal[^}]*overflow-wrap: anywhere/s)
})
