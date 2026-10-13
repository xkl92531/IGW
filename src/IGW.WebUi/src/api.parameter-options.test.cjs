const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'api.ts'), 'utf8')

assert.match(source, /mesRangeOptions\s*:/)
assert.match(source, /['"]\/api\/v1\/project\/mes-range-options['"]/)
assert.match(source, /AbortSignal/)
assert.match(source, /MesRangeOptionsResponse/)

console.log('api.parameter-options.test.cjs passed')
