const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'GatewaySettings.vue'), 'utf8')

test('IGW settings fixed operator copy routes through the language service', () => {
  for (const text of ['IGW 设置', '运行状态', '授权与激活', '激活授权']) {
    const pattern = new RegExp(`(?:\\$t|t)\\(['\"]${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`)
    assert.ok(pattern.test(source), `${text} should use the language service`)
  }
})
