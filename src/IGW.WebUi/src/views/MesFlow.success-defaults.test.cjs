const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')

test('新建上报节点默认使用 httpStatusCode = 200', () => {
  assert.match(source, /successKey:\s*'httpStatusCode'/)
  assert.match(source, /successValue:\s*'200'/)
  assert.match(source, /successKey\s*\|\|\s*'httpStatusCode'/)
  assert.match(source, /successValue\s*\|\|\s*'200'/)
})
