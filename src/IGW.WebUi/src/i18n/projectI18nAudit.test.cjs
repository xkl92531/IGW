const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const webRoot = path.resolve(__dirname, '..')
const covered = [
  path.join(webRoot, 'views', 'Project.vue'),
  ...fs.readdirSync(path.join(webRoot, 'components'))
    .filter((name) => name.endsWith('.vue'))
    .map((name) => path.join(webRoot, 'components', name)),
]

const han = /\p{Script=Han}/u
const i18nCall = /(?:\$t|(?<![\w$])t|(?<![\w$])tr)\s*\(/

function findings(file) {
  const source = fs.readFileSync(file, 'utf8')
  const lines = source.split(/\r?\n/)
  let inBlockComment = false
  let inSfcTemplate = false
  return lines.flatMap((line, index) => {
    const trimmed = line.trim()
    if (trimmed === '<template>') inSfcTemplate = true
    if (trimmed.startsWith('<style')) inSfcTemplate = false
    if (trimmed.startsWith('<!--')) inBlockComment = true
    if (trimmed.startsWith('/*') || trimmed.startsWith('/**')) inBlockComment = true
    const commentLine = inBlockComment || trimmed.startsWith('//')
    if (trimmed.includes('-->') || trimmed.includes('*/')) inBlockComment = false
    if (commentLine || !han.test(line)) return []

    // Ignore trailing source comments. A protocol URL such as opc.tcp:// is not
    // affected because the comment marker must be preceded by whitespace.
    const code = line.replace(/\s+\/\/.*$/, '')
    if (!han.test(code)) return []

    // User/PLC/protocol data is allowed when it is only shown through interpolation.
    const withoutInterpolations = code.replace(/\{\{[\s\S]*?\}\}/g, '')
    if (!han.test(withoutInterpolations)) return []

    const visibleTemplate = inSfcTemplate && /(^|>)\s*[^<{]*\p{Script=Han}[^<{]*(?=<|$)/u.test(withoutInterpolations)
    const visibleAttribute = inSfcTemplate && /(?:title|label|placeholder|description|confirm-button-text|cancel-button-text)\s*=\s*["'][^"']*\p{Script=Han}/u.test(code)
    const scriptMessage = !inSfcTemplate && /(?:ElMessage|ElMessageBox|throw new Error)\b/.test(code)
    const untranslatedEnum = inSfcTemplate && /<el-option\b[^>]*\bvalue=["'][^"']*\p{Script=Han}[^"']*["'][^>]*\/>/u.test(code) && !/:label\s*=/.test(code)
    const translated = i18nCall.test(code)
    if ((visibleTemplate || visibleAttribute || scriptMessage || untranslatedEnum) && !translated)
      return [`${path.relative(webRoot, file)}:${index + 1}: ${trimmed}`]
    return []
  })
}

test('engineering Web UI contains no uncovered fixed Chinese operator copy', () => {
  const uncovered = covered.flatMap(findings)
  assert.deepEqual(uncovered, [])
})
