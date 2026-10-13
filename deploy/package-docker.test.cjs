const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const source = fs.readFileSync(path.join(__dirname, 'package-docker.ps1'), 'utf8')

test('Docker 包的发布带上版本号：现场在「IGW 设置 → 网关版本」和日志导出里能看到真实版本', () => {
  const start = source.indexOf('dotnet publish')
  assert.ok(start >= 0, '应能定位 dotnet publish')
  const publish = source.slice(start, start + 400)
  assert.match(publish, /-p:Version=\$version/)
  // 版本来源仍是 installer\version.txt，与 Windows 安装包同一个源
  assert.match(source, /installer\\version\.txt/)
})
