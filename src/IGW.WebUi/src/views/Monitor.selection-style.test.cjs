const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const monitorSource = fs.readFileSync(path.join(__dirname, 'Monitor.vue'), 'utf8')

test('工位选择不再渲染复选框，并由整张卡片表达选择状态', () => {
  assert.doesNotMatch(monitorSource, /<el-checkbox[^>]*class="stationcheck"/)
  assert.match(monitorSource, /:aria-pressed="isStationSelected\(c\.name\)"/)
  assert.match(monitorSource, /class="selectedmark"/)
})

test('已选工位使用深色反差样式，同时保留状态点', () => {
  assert.match(monitorSource, /\.tile\.on\s*\{[^}]*background:\s*#253858/s)
  assert.match(monitorSource, /\.tile\.on\s*\{[^}]*color:\s*#fff/s)
  assert.match(monitorSource, /\.tile\.on\s+\.dot\s*\{[^}]*box-shadow:/s)
})
