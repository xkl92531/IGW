const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('采集节点执行中表达整轮处理中，不能显示为普通节点正在执行', () => {
  assert.match(source, /n\.type === 'Trigger'\s*\?\s*'本轮处理中'/)
})

test('采集节点已触发不等于整条流程本轮完成', () => {
  assert.match(source, /n\.type === 'Trigger'\s*\?\s*'已触发'/)
})
