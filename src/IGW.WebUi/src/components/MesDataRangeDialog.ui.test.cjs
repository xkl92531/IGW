const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const filename = path.join(__dirname, 'MesDataRangeDialog.vue')

test('配置数据按层加载含单个点，默认选中当前 DB，排除和改名移到报文高级设置', () => {
  assert.equal(fs.existsSync(filename), true)
  const source = fs.readFileSync(filename, 'utf8')
  for (const text of ['已选择的数据范围', '添加数据范围', '单个点', '移除', '来源离线时输出 null'])
    assert.match(source, new RegExp(text))
  // 按层懒加载，每层分页，上万个点也不一次拉到浏览器
  assert.match(source, /api\.mesRangeChildren/)
  assert.match(source, /lazy :load="loadNode"/)
  assert.match(source, /nextCursor/)
  assert.match(source, /loadMore/)
  // 没有范围时自动选上任务绑定的数据块
  assert.match(source, /!draft\.value\.length && props\.scopeGroup/)
  assert.match(source, /AbortController/)
  assert.match(source, /knownOptions/)
  assert.match(source, /loadExactOption/)
  // 排除字段不再在这个窗口里逐个勾，统一在「报文高级设置」
  assert.doesNotMatch(source, /排除少数字段/)
  assert.doesNotMatch(source, /api\.mesRangeOptions/)
})
