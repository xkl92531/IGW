const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('新上报节点默认字段/结构映射(平铺)并显示范围摘要', () => {
  assert.match(source, /payload:\s*\{\s*mode:\s*'FieldMapping',\s*dataRanges:\s*\[\],\s*fields:\s*\[\],\s*structs:\s*\[\],\s*append:\s*\{\}\s*\}/)
  assert.match(source, /uploadRangeSummary/)
  assert.match(source, /MesDataRangeDialog/)
})

test('上报详情只有一种配置方式：配置数据选内容，报文高级设置做字段/结构映射', () => {
  for (const text of ['上报数据', '配置数据', '响应与成功判定', '报文高级设置', '响应变量', '字段/结构映射'])
    assert.match(source, new RegExp(text))
  assert.match(source, /<MesPayloadMappingEditor v-if="isRangeMode\(selNode\)"/)
  assert.doesNotMatch(source, /切换到字段\/结构映射/)
  assert.doesNotMatch(source, /切换到数据范围/)
  // 以前按数据范围嵌套格式上报的节点照常运行，只提示可改为字段/结构映射
  assert.match(source, /data-testid="range-format-note"/)
  // 配置数据选中的内容自动生成字段/结构映射，已改过的保留
  assert.match(source, /api\.mesFieldMapping/)
  assert.match(source, /mergeGeneratedMapping\(/)
})

test('切换上报模式只修改模式标记且不清空另一套配置', () => {
  const start = source.indexOf('async function switchUploadMode')
  const end = source.indexOf('async function openRangeDialog', start)
  assert.ok(start >= 0 && end > start, '应能定位上报模式切换函数')

  const switchSource = source.slice(start, end)
  assert.match(switchSource, /node\.payload\.mode\s*=\s*nextMode/)
  assert.doesNotMatch(switchSource, /node\.payload\.fields\s*=\s*\[\]/)
  assert.doesNotMatch(switchSource, /node\.payload\.structs\s*=\s*\[\]/)
  assert.doesNotMatch(switchSource, /delete\s+node\.payload\.dataRanges/)
  assert.doesNotMatch(switchSource, /node\.http\.bodyTemplate\s*=\s*''/)
  assert.doesNotMatch(switchSource, /ElMessageBox\.confirm/)
})
