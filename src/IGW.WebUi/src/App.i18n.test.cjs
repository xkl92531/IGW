const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const app = fs.readFileSync(path.join(__dirname, 'App.vue'), 'utf8')
const main = fs.readFileSync(path.join(__dirname, 'main.ts'), 'utf8')

test('top bar owns one immediate language switch with no apply or save step', () => {
  assert.match(app, /class="language-switch"/)
  assert.match(app, /@command="handleLanguageCommand"/)
  assert.match(app, /command="zh-CN"/)
  assert.match(app, /command="en-US"/)
  assert.match(app, /中文/)
  assert.match(app, /English/)
  assert.doesNotMatch(app, /language-(?:apply|save)/)
  assert.match(app, /api\.updateUiPreferences\(command\)/)
  assert.match(app, /languageSaveQueue\s*=\s*languageSaveQueue/)
})

test('root switches Element Plus locale reactively and installs IGW language service', () => {
  assert.match(app, /<el-config-provider\s+:locale="elementLocale">/)
  assert.match(app, /locale\.value\s*===\s*'en-US'\s*\?\s*en\s*:\s*zhCn/)
  assert.match(main, /installI18n\(app\)/)
  assert.match(main, /api\.uiPreferences\(\)/)
  assert.match(main, /await restoreServerLocale\(\)/)
  assert.match(main, /app\.mount\('#app'\)/)
  assert.doesNotMatch(main, /ElementPlus,\s*\{\s*locale:\s*zhCn/)
})
