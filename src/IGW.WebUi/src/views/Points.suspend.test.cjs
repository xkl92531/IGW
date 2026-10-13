const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

test('QC 缩小时关闭实时流并在恢复时重建当前范围实时流', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Points.vue'), 'utf8')
  assert.match(source, /suspended\?: boolean/)
  assert.match(source, /watch\(\(\) => props\.suspended/)
  assert.match(source, /suspended \? realtimeClient\.stop\(\) : startRealtime\(\)/)
  assert.doesNotMatch(source, /setInterval\(/)
})

test('QC 写入弹窗挂到页面顶层并始终高于 QC 和 MES 浮动窗口', () => {
  const source = fs.readFileSync(path.join(__dirname, 'Points.vue'), 'utf8')
  assert.match(source, /class="qc-write-dialog"/)
  assert.match(source, /append-to-body/)
  assert.match(source, /:z-index="5200"/)
})
