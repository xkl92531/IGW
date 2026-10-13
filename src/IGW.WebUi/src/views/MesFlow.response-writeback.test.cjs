const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const flowSource = fs.readFileSync(path.join(__dirname, 'MesFlow.vue'), 'utf8')
const pickerSource = fs.readFileSync(path.join(__dirname, '../components/MesParameterPicker.vue'), 'utf8')

test('节点菜单提供收到响应即执行的回写节点', () => {
  assert.match(flowSource, /ResponseWriteback:\s*\{\s*title:\s*t\('回写'\)/)
  assert.match(flowSource, /selNode\.type === 'Writeback' \|\| selNode\.type === 'ResponseWriteback'/)
  assert.match(flowSource, /回写接口响应到 PLC/)
  assert.match(flowSource, /网络失败和 HTTP 非 2xx 仍会拦住并等下轮重试/)
})

test('回写节点用「返回长这样→就这么填」对照说明，手工配置仅作覆盖', () => {
  assert.match(flowSource, /接口返回长这样/)
  assert.match(flowSource, /响应字段就这么填/)
  assert.match(flowSource, /不区分业务成败都会执行；要分 OK\/NG 写不同的值，在上报和回写中间加一个「判成败」节点。/)
  assert.match(flowSource, /自定义覆盖（可选）：响应字段 → PLC 点位/)
})

test('回写配置区和PLC参数候选不再沿用窄版宽度', () => {
  assert.match(flowSource, /\.cfg \{ width: 520px;/)
  assert.match(flowSource, /class="crow writeback-row manual-write-row"/)
  assert.doesNotMatch(flowSource, /placeholder="固定值"[^>]*style="width:62px"/)
  assert.doesNotMatch(flowSource, /placeholder="或复制自点"[^>]*width:124px/)
  assert.match(pickerSource, /popper-class="mes-parameter-dropdown"/)
  assert.match(pickerSource, /width: min\(680px, calc\(100vw - 32px\)\)/)
  assert.match(pickerSource, /\.parameter-option-path, \.parameter-option-address \{[^}]*white-space: normal[^}]*overflow-wrap: anywhere/s)
})
