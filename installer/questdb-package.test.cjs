const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');

test('all distributions stage a pinned offline QuestDB runtime', () => {
  const manifest = JSON.parse(read('installer/questdb-release.json'));
  assert.equal(manifest.version, '10.0.1');
  for (const platform of ['windows', 'linux']) {
    assert.match(manifest[platform].sha256, /^[a-f0-9]{64}$/);
    assert.match(manifest[platform].asset, /-rt-/);
  }
  assert.match(read('installer/build.bat'), /package-questdb\.ps1/);
  assert.match(read('installer/build-linux.sh'), /package-questdb\.sh/);
  assert.match(read('deploy/package-docker.ps1'), /package-questdb\.ps1.*-Platform linux/);
  assert.match(read('deploy/Dockerfile'), /chmod \+x.*questdb\/bin\/java/);
  assert.match(read('tools/linux/install.sh'), /chmod 0755.*questdb\/bin\/java/);
  assert.match(read('tools/linux/igw.service'), /KillMode=control-group/);
});

test('cross-platform archive staging checks hashes and confines flattened symlinks', () => {
  assert.match(read('installer/package-questdb.ps1'), /Get-FileHash.*SHA256/);
  assert.match(read('installer/package-questdb.sh'), /sha256sum --check/);
  assert.match(read('installer/QuestDbPackage/Program.cs'), /Archive path escapes extraction root/);
  assert.match(read('installer/QuestDbPackage/Program.cs'), /Unresolved or circular archive link/);
  assert.match(read('installer/package-questdb.ps1'), /LICENSE\.txt/);
});
