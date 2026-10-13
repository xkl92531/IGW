param(
  [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $Root 'src\IGW.WebUi\src'
$files = @((Join-Path $sourceRoot 'App.vue'))
$files += Get-ChildItem (Join-Path $sourceRoot 'views') -Filter *.vue | ForEach-Object FullName
$files += Get-ChildItem (Join-Path $sourceRoot 'components') -Filter *.vue | ForEach-Object FullName

$pattern = [regex]'(?:\$t|(?<![\w$])t|(?<![\w$])tr)\(\s*([''\"])([^''\"\r\n]+)\1'
$lineLiteralPattern = [regex]'([''\"])([^''\"\r\n]*[\p{IsCJKUnifiedIdeographs}][^''\"\r\n]*)\1'
$keys = foreach ($file in $files) {
  $source = Get-Content $file -Raw -Encoding UTF8
  foreach ($match in $pattern.Matches($source)) {
    $value = $match.Groups[2].Value
    if ($value -match '[\p{IsCJKUnifiedIdeographs}]') { $value }
  }
  foreach ($line in $source -split "\r?\n") {
    if ($line -notmatch '(?:\$t|(?<![\w$])t|(?<![\w$])tr)\(') { continue }
    foreach ($match in $lineLiteralPattern.Matches($line)) { $match.Groups[2].Value }
  }
}
$keys = @($keys | Sort-Object -Unique)

$separator = '<<<IGW_SPLIT>>>'
$translations = @{}
$batchSize = 20
for ($offset = 0; $offset -lt $keys.Count; $offset += $batchSize) {
  $end = [Math]::Min($offset + $batchSize - 1, $keys.Count - 1)
  $batch = @($keys[$offset..$end])
  $query = $batch -join "`n$separator`n"
  $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=zh-CN&tl=en&dt=t&q=' + [Uri]::EscapeDataString($query)
  $response = Invoke-RestMethod -Uri $uri -TimeoutSec 30
  $translated = (($response[0] | ForEach-Object { $_[0] }) -join '')
  $parts = @($translated -split "\r?\n$separator\r?\n")
  if ($parts.Count -ne $batch.Count) {
    throw "Translation batch count mismatch at $offset ($($parts.Count) != $($batch.Count))"
  }
  for ($i = 0; $i -lt $batch.Count; $i++) { $translations[$batch[$i]] = $parts[$i].Trim() }
  Write-Host "Translated $($end + 1)/$($keys.Count)"
}

$overrides = @{
  '工业网关' = 'Industrial Gateway'
  '工程配置' = 'Engineering'
  '监控中心' = 'Monitoring'
  'IGW 设置' = 'IGW Settings'
  '采集周期' = 'Collection Cycle'
  '条件' = 'Condition'
  '上报' = 'Upload'
  '判成败' = 'Evaluate Result'
  '值分流' = 'Value Routing'
  '回写' = 'Write Back'
  '响应直写' = 'Response Write-Back'
  '自动生成标记…' = 'Auto-generate Tags…'
  'MES 采集流程(当前数据块)…' = 'MES Collection Flow (Current Data Block)…'
  'MES 采集流程(请在所属 PLC 数据块上设置)' = 'MES Collection Flow (Configure on the PLC Data Block)'
  '采集上报(MES,本数据块单独开关)' = 'MES Collection Upload (Per Data Block)'
  '在此处打开点位监控(Quick Client)' = 'Open Tag Monitor Here (Quick Client)'
  '重新载入工程数据（不刷新网页）' = 'Reload Project Data (Without Refreshing the Page)'
  '工位状态' = 'Station Status'
  '异常优先 · 点击卡片可多选' = 'Exceptions First · Click Cards to Multi-select'
  '保存并应用' = 'Save and Apply'
  '运行状态' = 'Runtime Status'
  '等待运行数据' = 'Waiting for Runtime Data'
  '授权与激活' = 'License and Activation'
  '激活授权' = 'Activate License'
  '项目' = 'Project'
}
foreach ($entry in $overrides.GetEnumerator()) { $translations[$entry.Key] = $entry.Value }

function Escape-Ts([string]$value) {
  return $value.Replace('\', '\\').Replace("'", "\'").Replace("`r", '').Replace("`n", '\n')
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('export const enUSMessages: Readonly<Record<string, string>> = {')
foreach ($key in $keys) {
  $lines.Add("  '$(Escape-Ts $key)': '$(Escape-Ts ([string]$translations[$key]))',")
}
$lines.Add('}')
$out = Join-Path $sourceRoot 'i18n\catalog.ts'
[IO.File]::WriteAllLines($out, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($keys.Count) translations to $out"


