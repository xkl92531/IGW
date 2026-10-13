const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const component = fs.readFileSync(path.join(__dirname, 'SiemensAuthenticationDialog.vue'), 'utf8')
const api = fs.readFileSync(path.join(__dirname, '..', 'api.ts'), 'utf8')

test('S7 Plus authentication dialog keeps credentials ephemeral and verifies online before save', () => {
  assert.match(component, /验证并保存/)
  assert.match(component, /autocomplete="username"/)
  assert.match(component, /autocomplete="new-password"/)
  assert.match(component, /show-password/)
  assert.match(component, /onBeforeUnmount\(clearForm\)/)
  assert.doesNotMatch(component, /localStorage/)
})

test('authentication API exposes status verify and delete with a 30 second verification timeout', () => {
  assert.match(api, /siemensAuthStatus/)
  assert.match(api, /verifySiemensAuth/)
  assert.match(api, /deleteSiemensAuth/)
  assert.match(api, /timeout:\s*30000/)
})
