const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const ts = require('typescript')

const originalTsLoader = require.extensions['.ts']
require.extensions['.ts'] = function loadTypeScript(module, filename) {
  const output = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
    fileName: filename,
  }).outputText
  module._compile(output, filename)
}

const { enUSMessages } = require(path.join(__dirname, 'catalog.ts'))

test.after(() => {
  if (originalTsLoader) require.extensions['.ts'] = originalTsLoader
  else delete require.extensions['.ts']
})

function vueFiles(folder) {
  return fs.readdirSync(folder, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(folder, entry.name)
    return entry.isDirectory() ? vueFiles(full) : entry.name.endsWith('.vue') ? [full] : []
  })
}

test('every fixed Chinese string routed through the language service has an English translation', () => {
  const sourceRoot = path.resolve(__dirname, '..')
  const pattern = /(?:\$t|(?<![\w$])t|(?<![\w$])tr)\(\s*(['"])([^'"\r\n]+)\1/g
  const lineLiteralPattern = /(['"])([^'"\r\n]*\p{Script=Han}[^'"\r\n]*)\1/gu
  const missing = new Set()
  for (const file of vueFiles(sourceRoot)) {
    const source = fs.readFileSync(file, 'utf8')
    for (const match of source.matchAll(pattern)) {
      const key = match[2]
      if (/\p{Script=Han}/u.test(key) && !enUSMessages[key]) missing.add(key)
    }
    for (const line of source.split(/\r?\n/)) {
      if (!/(?:\$t|(?<![\w$])t|(?<![\w$])tr)\(/.test(line)) continue
      for (const match of line.matchAll(lineLiteralPattern)) {
        if (!enUSMessages[match[2]]) missing.add(match[2])
      }
    }
  }
  assert.deepEqual([...missing].sort(), [])
})

test('English catalog values do not leave Chinese operator copy behind', () => {
  const untranslated = Object.entries(enUSMessages)
    .filter(([, value]) => /\p{Script=Han}/u.test(value))
    .map(([key]) => key)
  assert.deepEqual(untranslated, [])
})

test('project runtime and license presentation keys have English copy', () => {
  const required = [
    'MES 采集流程', '数据块', '● 采集中', '○ 采集停用', '● {count} 组采集',
    '离线', '在线', '故障', '待机', '连接中', '状态未知', '未下发',
    '运行状态：{state}', '原因：{error}', '{count}台离线', '全部在线',
    '授权有效', '7 天试用中', '本机试用', '授权有效期到 {date}。',
    '7 天试用中，到期 {date}（剩 {days} 天）。试用结束后需使用授权码激活。',
  ]
  const missing = required.filter((key) => !enUSMessages[key])
  assert.deepEqual(missing, [])
  assert.equal(required.some((key) => /\p{Script=Han}/u.test(enUSMessages[key])), false)
})
