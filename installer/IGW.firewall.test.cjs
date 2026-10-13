const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const test = require('node:test')

const source = fs.readFileSync(path.join(__dirname, 'IGW.iss'), 'utf8')

test('安装器在服务启动前静默刷新全网络程序规则并在卸载时删除', () => {
  assert.ok(source.includes("FW_RULE = 'IGW Gateway Runtime TCP';"), '缺少安装器专属规则名')
  assert.ok(source.includes("ExpandConstant('{sys}\\netsh.exe')"), '未使用系统 netsh 管理规则')
  assert.ok(source.includes('dir=in action=allow'), '规则不是允许入站')
  assert.match(source, /program="' \+\s*ExpandConstant\('\{app\}\\IGW\.Runtime\.exe'\) \+ '"/, '规则没有绑定安装后的 Runtime 程序')
  assert.ok(source.includes('enable=yes profile=any protocol=TCP edge=no'), '规则没有覆盖全部网络或限制为 TCP')
  assert.ok(!source.includes('protocol=UDP'), '安装器不应放行 UDP')
  assert.ok(!source.includes('localport='), '程序级规则不应写死端口')

  const installStart = source.indexOf('procedure CurStepChanged')
  const uninstallStart = source.indexOf('procedure CurUninstallStepChanged')
  assert.ok(installStart >= 0 && uninstallStart > installStart, '未找到安装和卸载生命周期')

  const installSection = source.slice(installStart, uninstallStart)
  const refreshIndex = installSection.indexOf('RefreshFirewallRule();')
  const serviceStartIndex = installSection.indexOf("RunHidden(ExpandConstant('{sys}\\sc.exe'), 'start")
  assert.ok(refreshIndex >= 0, '安装或升级时没有刷新防火墙规则')
  assert.ok(serviceStartIndex >= 0, '未找到 Windows 服务启动操作')
  assert.ok(refreshIndex < serviceStartIndex, '必须在 Runtime 服务启动前创建防火墙规则')

  const uninstallSection = source.slice(uninstallStart)
  assert.ok(uninstallSection.includes('RemoveFirewallRule();'), '卸载时没有删除安装器管理的规则')
})
