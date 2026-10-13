const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const flowSource = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')
const apiSource = fs.readFileSync(path.join(__dirname, '..', 'api.ts'), 'utf8')
const projectSource = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')

test('MES 保存只提交 mesUpload 且不在成功后重新下载整个工程', () => {
  const start = flowSource.indexOf('async function save(')
  const end = flowSource.indexOf('const autoSave =', start)
  assert.ok(start >= 0 && end > start, '应能定位 MES 保存函数')

  const saveSource = flowSource.slice(start, end)
  assert.match(saveSource, /cloneReactiveValue\(proj\.value\.mesUpload\)/)
  assert.match(saveSource, /api\.putMesUpload\(/)
  assert.doesNotMatch(saveSource, /structuredClone\(/)
  assert.doesNotMatch(saveSource, /await api\.project\(\)/)
})

test('API 提供独立 MES 配置保存端点并返回新版本号', () => {
  assert.match(apiSource, /putMesUpload:/)
  assert.match(apiSource, /\/api\/v1\/project\/mes-upload/)
  assert.match(apiSource, /x-igw-rev/)
})

test('工程保存直接使用 PUT 返回的新版本号，不再重新下载整个大型点位工程', () => {
  const start = projectSource.indexOf('async function saveApply(')
  const end = projectSource.indexOf('const autoSave =', start)
  assert.ok(start >= 0 && end > start, '应能定位工程保存函数')
  const saveSource = projectSource.slice(start, end)
  assert.match(apiSource, /putProject:[\s\S]*x-igw-rev/)
  assert.match(saveSource, /const result = await api\.putProject/)
  assert.match(saveSource, /if \(result\.rev\) rev\.value = result\.rev/)
  assert.doesNotMatch(saveSource, /await api\.project\(\)/)
})

test('Ctrl+S 在输入框焦点下也会阻止浏览器默认行为并保存当前流程', () => {
  const start = flowSource.indexOf('function onKey(')
  const end = flowSource.indexOf('onMounted(() => window.addEventListener', start)
  assert.ok(start >= 0 && end > start, '应能定位画布键盘处理器')

  const keyboardSource = flowSource.slice(start, end)
  assert.match(keyboardSource, /\(ev\.ctrlKey \|\| ev\.metaKey\).*ev\.key\.toLowerCase\(\) === 's'/)
  assert.match(keyboardSource, /ev\.preventDefault\(\)[\s\S]*?save\(false\)/)
  assert.ok(
    keyboardSource.indexOf("ev.key.toLowerCase() === 's'") < keyboardSource.indexOf("t.tagName === 'INPUT'"),
    '保存快捷键必须在输入框提前返回之前处理',
  )
})
