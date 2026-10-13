const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')
const test = require('node:test')

const scriptPath = path.join(__dirname, 'bump_version.ps1')

function bumpVersion(initialVersion) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'igw-version-'))
  const tempScript = path.join(tempDir, 'bump_version.ps1')
  const versionFile = path.join(tempDir, 'version.txt')

  try {
    fs.copyFileSync(scriptPath, tempScript)
    fs.writeFileSync(versionFile, initialVersion, 'ascii')

    const output = execFileSync('powershell.exe', [
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', tempScript,
    ], { encoding: 'utf8' }).trim()

    return {
      output,
      savedVersion: fs.readFileSync(versionFile, 'ascii').trim(),
    }
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true })
  }
}

test('补丁版本超过99时进位到下一个次版本', () => {
  assert.deepEqual(bumpVersion('1.5.99'), {
    output: '1.6.0',
    savedVersion: '1.6.0',
  })
})

test('普通补丁版本仍然递增一位', () => {
  assert.deepEqual(bumpVersion('1.6.0'), {
    output: '1.6.1',
    savedVersion: '1.6.1',
  })
})
