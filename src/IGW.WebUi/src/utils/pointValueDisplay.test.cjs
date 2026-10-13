const test = require('node:test')
const assert = require('node:assert/strict')

test('byte buffers display bytes without interpreting PLC date layouts', async () => {
  const { pointValueDisplay } = await import('./pointValueDisplay.ts')
  assert.equal(pointValueDisplay('kAEAAAAAAAI=', 'byteArray'), '[144, 1, 0, 0, 0, 0, 0, 2]')
  assert.equal(pointValueDisplay('hello', 'string'), 'hello')
  assert.equal(pointValueDisplay('0', 'float'), '0')
  assert.equal(pointValueDisplay('—', 'byteArray'), '—')
  assert.equal(pointValueDisplay('[1,2]', 'byteArray'), '[1,2]')
})

test('Quick Client accepts numeric array element types from older gateways', async () => {
  const { pointTypeDisplay, pointValueDisplay } = await import('./pointValueDisplay.ts')
  assert.equal(pointTypeDisplay(1), 'Byte')
  assert.equal(pointTypeDisplay(0), 'Boolean')
  assert.equal(pointTypeDisplay('Float'), 'Float')
  assert.equal(pointTypeDisplay('int16'), 'Short')
  assert.equal(pointTypeDisplay(null), '—')
  assert.equal(pointValueDisplay('AQI=', 11), '[1, 2]')
})
