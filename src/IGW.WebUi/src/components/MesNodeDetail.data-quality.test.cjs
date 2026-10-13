const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesNodeDetail.vue'), 'utf8')

test('部分数据使用独立质量详情而不是HTTP失败样式', () => {
  assert.match(source, /d\?\.kind === 'data-quality'/)
  assert.match(source, /离线数据范围/)
  assert.match(source, /质量异常点位/)
  assert.match(source, /quality-panel/)
})

test('MES节点明确区分PLC通信异常、MES无响应和业务NG', () => {
  assert.match(source, /PLC 通信异常/)
  assert.match(source, /MES 无响应/)
  assert.match(source, /业务 NG/)
  assert.match(source, /d\.value\?\.category/)
})
