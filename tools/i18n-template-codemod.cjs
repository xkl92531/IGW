const fs = require('node:fs')
const path = require('node:path')

const root = path.resolve(__dirname, '..', 'src', 'IGW.WebUi', 'src')
const files = [
  path.join(root, 'views', 'Project.vue'),
  path.join(root, 'views', 'Monitor.vue'),
  path.join(root, 'views', 'GatewaySettings.vue'),
  path.join(root, 'views', 'MesFlow.vue'),
  ...fs.readdirSync(path.join(root, 'components'))
    .filter((name) => name.endsWith('.vue'))
    .map((name) => path.join(root, 'components', name)),
]

const hasChinese = (value) => /[\u3400-\u9fff]/.test(value)
const singleQuoted = (value) => `'${value.replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/\r?\n/g, ' ')}'`

function inComment(template, offset) {
  return template.lastIndexOf('<!--', offset) > template.lastIndexOf('-->', offset)
}

for (const file of files) {
  const source = fs.readFileSync(file, 'utf8')
  const start = source.indexOf('<template>')
  const end = source.lastIndexOf('</template>')
  if (start < 0 || end < 0) continue

  let template = source.slice(start, end + '</template>'.length)
  template = template.replace(/>([^<>]*[\u3400-\u9fff][^<>]*)</g, (whole, text, offset) => {
    if (inComment(template, offset) || text.includes('{{')) return whole
    const leading = text.match(/^\s*/)?.[0] ?? ''
    const trailing = text.match(/\s*$/)?.[0] ?? ''
    const value = text.trim().replace(/\s+/g, ' ')
    if (!value || !hasChinese(value)) return whole
    return `>${leading}{{ $t(${singleQuoted(value)}) }}${trailing}<`
  })

  const attributes = 'placeholder|title|label|empty-text|description|content|confirm-button-text|cancel-button-text|loading-text'
  template = template.replace(new RegExp(`(?<![:\\w-])(${attributes})="([^"\\r\\n]*[\\u3400-\\u9fff][^"\\r\\n]*)"`, 'g'),
    (whole, name, value) => `:${name}="$t(${singleQuoted(value)})"`)

  const output = source.slice(0, start) + template + source.slice(end + '</template>'.length)
  if (output !== source) fs.writeFileSync(file, output, 'utf8')
}
