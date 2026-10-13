import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const source = readFileSync(resolve(process.cwd(), 'src/views/Points.vue'), 'utf8')

describe('Quick Client realtime architecture', () => {
  it('loads exact project scope pages instead of downloading the whole project', () => {
    expect(source).toContain('api.projectTagScope')
    expect(source).not.toContain('api.project()')
  })

  it('streams scalar values and uses a virtualized table without polling', () => {
    expect(source).toContain('RealtimeStreamClient')
    expect(source).toContain('<el-table-v2')
    expect(source).not.toContain('setInterval(')
    expect(source).not.toContain('api.values(')
  })

  it('does not optimistically overwrite PLC values after a write', () => {
    expect(source).not.toMatch(/applyVal\(it, \{ value: v/)
  })

  it('applies a large snapshot across multiple animation frames', () => {
    expect(source).toContain('UI_VALUES_PER_FRAME')
    expect(source).toContain('fullSyncCursor')
    expect(source).toContain('requestAnimationFrame(flushUiValues)')
  })
})
