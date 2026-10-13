import { describe, expect, it } from 'vitest'
import { buildLogPresentation } from './logPresentation'

describe('log presentation', () => {
  it('shows module feature task interface and PLC scope from structured fields', () => {
    const result = buildLogPresentation({
      timestamp: '2026-09-13T08:00:00Z', level: 'INFO', category: 'interface', message: 'MES HTTP 请求完成',
      attributes: {
        igw_module: 'mes', igw_feature: 'http_upload', igw_task_name: 'OP140 出站',
        igw_task_id: 'task-140', igw_method: 'POST', igw_target: 'http://mes.local/report',
        igw_channel: 'PLC8', igw_device: 'PLC8.OP140', igw_db: 'StationEXIT',
      },
    })
    expect(result.module).toBe('mes')
    expect(result.feature).toBe('http_upload')
    expect(result.task).toBe('OP140 出站 (task-140)')
    expect(result.interfaceOperation).toBe('POST http://mes.local/report')
    expect(result.plcScope).toBe('PLC8 · PLC8.OP140 · StationEXIT')
  })

  it('marks missing module and feature context as unknown for legacy records', () => {
    const result = buildLogPresentation({
      timestamp: '2026-09-13T08:00:00Z', level: 'INFO', category: 'Runtime', message: '旧日志', attributes: {},
    })
    expect(result.module).toBe('unknown')
    expect(result.feature).toBe('unknown')
    expect(result.task).toBe('')
  })
})
