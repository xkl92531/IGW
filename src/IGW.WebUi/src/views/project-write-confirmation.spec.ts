import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const source = readFileSync(resolve(process.cwd(), 'src/views/Project.vue'), 'utf8')

describe('project tag write confirmation editor', () => {
  it('requires an explicit strategy for write-only tags', () => {
    expect(source).toContain("f.access === 'write'")
    expect(source).toContain('只写点必须选择“确认点”或“仅协议确认”')
  })

  it('offers all protocol-neutral confirmation modes', () => {
    expect(source).toContain('value="readBack"')
    expect(source).toContain('value="confirmationTag"')
    expect(source).toContain('value="protocolAcknowledged"')
  })

  it('shows a full actual path together with PLC address and description', () => {
    expect(source).toContain('`${prefix}.${path}.${tag.name}`')
    expect(source).toContain('[tag.address, tag.description]')
    expect(source).toContain(':label="option.label"')
  })

  it('routes protocol-specific forms using the real point owner', () => {
    expect(source).toContain('resolveTagProtocolContext(proj.value, t, list)')
    expect(source).toContain('<ModbusTagEditor')
    expect(source).toContain('<ProtocolDeviceSettings')
  })
})
