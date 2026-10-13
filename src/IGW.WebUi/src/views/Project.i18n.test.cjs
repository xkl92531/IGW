const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')

test('engineering configuration routes fixed operator copy through the language service', () => {
  for (const text of ['重新载入工程数据（不刷新网页）', '自动生成标记', '诊断', '属性', '删除', '确定', '取消']) {
    const pattern = new RegExp(`(?:\\$t|t)\\(['\"]${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`)
    assert.ok(pattern.test(source), `${text} should use the language service`)
  }
})
