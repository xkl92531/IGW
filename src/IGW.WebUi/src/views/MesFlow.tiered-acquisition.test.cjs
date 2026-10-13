const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('任务级分层采集是三态：跟随全局 / 分层 / 常驻', () => {
  assert.match(source, /const tieredAcquisition = computed\(\{/)
  // 读:true→on、false→off、其余(含 null/undefined)→inherit
  assert.match(source, /task\.value\?\.tieredAcquisition === true \? 'on' : task\.value\?\.tieredAcquisition === false \? 'off' : 'inherit'/)
  // 写:inherit 必须写回 null,否则旧的 true/false 会留在工程里，界面显示「跟随全局」而运行时不跟随
  assert.match(source, /mode === 'on' \? true : mode === 'off' \? false : null/)
  assert.match(source, /<option value="inherit">/)
  assert.match(source, /<option value="on">/)
  assert.match(source, /<option value="off">/)
})

test('全局分层采集开关缺省为开，写回 mesUpload.tieredAcquisition', () => {
  assert.match(source, /proj\.value\?\.mesUpload\?\.tieredAcquisition !== false/)
  assert.match(source, /proj\.value\.mesUpload\.tieredAcquisition = value/)
  assert.match(source, /data-test="mes-global-tier"/)
  // 改全局开关要标脏，否则保存按钮不亮、改动丢失
  assert.match(source, /globalTieredAcquisition = \$event === true; md\(\)/)
})
