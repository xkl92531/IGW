const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

test('工作窗提供独立缩小放大关闭、拖动和视口约束且缩小时不卸载内容', () => {
  const file = path.join(__dirname, 'WorkWindow.vue')
  assert.equal(fs.existsSync(file), true, 'WorkWindow.vue should exist')
  const source = fs.readFileSync(file, 'utf8')
  assert.match(source, /update:minimized/)
  assert.match(source, /update:maximized/)
  assert.match(source, /request-close/)
  assert.match(source, /pointerdown/)
  assert.match(source, /clampRect/)
  assert.match(source, /v-show="!minimized"/)
})

test('工作窗四边和四角均可拖动调整大小，并持续回传新的位置和尺寸', () => {
  const source = fs.readFileSync(path.join(__dirname, 'WorkWindow.vue'), 'utf8')
  for (const edge of ['n', 'e', 's', 'w', 'ne', 'se', 'sw', 'nw']) {
    assert.match(source, new RegExp(`resize-${edge}`))
  }
  assert.match(source, /function beginResize/)
  assert.match(source, /emit\('update:rect'/)
})

test('工作窗内的确认框和写入框统一使用高于工作窗的模态层级', () => {
  const project = fs.readFileSync(path.join(__dirname, '..', 'views', 'Project.vue'), 'utf8')
  const points = fs.readFileSync(path.join(__dirname, '..', 'views', 'Points.vue'), 'utf8')
  assert.match(project, /toolBackZ\s*=\s*2201/)
  assert.match(project, /toolFrontZ\s*=\s*2202/)
  assert.match(points, /:z-index="5200"/)
})

test('全局错误消息和确认弹层永远高于QC与采集流程工作窗', () => {
  const app = fs.readFileSync(path.join(__dirname, '..', 'App.vue'), 'utf8')
  assert.match(app, /\.el-overlay\s*\{[^}]*z-index:\s*8000\s*!important/s)
  assert.match(app, /\.el-popper\s*\{[^}]*z-index:\s*8500\s*!important/s)
  assert.match(app, /\.el-message\s*\{[^}]*z-index:\s*9000\s*!important/s)
  assert.match(app, /\.el-notification\s*\{[^}]*z-index:\s*9000\s*!important/s)
})
