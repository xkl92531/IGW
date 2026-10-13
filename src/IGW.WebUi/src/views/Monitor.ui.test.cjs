const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'Monitor.vue'), 'utf8')

test('工位卡通过整卡点击多选，并提供已选数量和清空入口', () => {
  assert.doesNotMatch(source, /<el-checkbox[^>]*class="stationcheck"/)
  assert.match(source, /@click="pickChannel\(c\.name\)"/)
  assert.match(source, /\$t\('已选 \{count\} 个',\s*\{ count: selectedCount \}\)/)
  assert.match(source, /清空筛选/)
  assert.doesNotMatch(source, /const\s+chFilter\s*=\s*ref/)
})

test('数据面明确表达为内部数据通道，不能让用户误以为代表 PLC 在线状态', () => {
  assert.match(source, /内部数据通道/)
  assert.match(source, /不代表 PLC 在线/)
  assert.match(source, /无积压.*无丢弃/)
})
