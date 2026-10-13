const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('MES flow fixed operator copy routes through the language service', () => {
  for (const text of ['保存并应用', '＋ 添加节点', '运行状态', '等待运行数据']) {
    const pattern = new RegExp(`(?:\\$t|t)\\(['\"]${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`)
    assert.ok(pattern.test(source), `${text} should use the language service`)
  }
})
