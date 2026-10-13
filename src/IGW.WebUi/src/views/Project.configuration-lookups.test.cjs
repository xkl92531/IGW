const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const project = fs.readFileSync(path.join(__dirname, 'Project.vue'), 'utf8')

test('打开通道向导时重新拉取驱动和多网卡选项', () => {
  assert.match(project, /async function openChannelWizard\(\)[\s\S]*?await loadConfigurationLookups\(true\)[\s\S]*?wiz\.open = true/)
})

test('打开设备向导时重新拉取设备驱动元数据', () => {
  assert.match(project, /async function openDeviceWizard\(ch: any\)[\s\S]*?await loadDrivers\(true\)[\s\S]*?wiz\.open = true/)
})

test('配置选项刷新失败时保留上一次成功结果', () => {
  assert.doesNotMatch(project, /catch\s*\{\s*networkAdapters\.value\s*=\s*\[\]\s*\}/)
  assert.doesNotMatch(project, /catch\s*\{\s*drivers\.value\s*=\s*\[\]\s*\}/)
  assert.match(project, /async function loadConfigurationLookups\(force = false\)/)
})

test('新建通道的网卡下拉框默认显示操作系统默认项而不是空白', () => {
  assert.match(project, /const DEFAULT_ADAPTER_VALUE = '__os_default__'/)
  assert.match(project, /net: DEFAULT_ADAPTER_VALUE/)
  assert.match(project, /<el-option :value="DEFAULT_ADAPTER_VALUE"/)
  assert.match(project, /f\.net === DEFAULT_ADAPTER_VALUE \? '' : f\.net/)
})
