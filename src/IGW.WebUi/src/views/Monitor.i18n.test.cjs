const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'Monitor.vue'), 'utf8')

test('monitor fixed operator copy routes through the language service', () => {
  for (const text of ['工位状态', '异常优先 · 点击卡片可多选', '搜索工位名称…', '清空筛选']) {
    const pattern = new RegExp(`(?:\\$t|t)\\(['\"]${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`)
    assert.ok(pattern.test(source), `${text} should use the language service`)
  }
})
