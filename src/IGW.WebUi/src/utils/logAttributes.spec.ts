import { describe, expect, it } from 'vitest'
import { logAttributeLabel, logAttributeRows } from './logAttributes'

describe('log attributes', () => {
  it('keeps the complete long value for expansion and copying', () => {
    const longTarget = `https://mes.example/report?payload=${'x'.repeat(3000)}`

    expect(logAttributeRows({ igw_target: longTarget })[0].value).toBe(longTarget)
  })

  it('uses a user-facing troubleshooting label for technical correlation ids', () => {
    expect(logAttributeLabel('igw_trace_id')).toBe('排障编号')
    expect(logAttributeLabel('trace_id')).toBe('排障编号')
    expect(logAttributeLabel('span_id')).toBe('排障分段编号')
  })

  it('labels exchange sizes and hides the internal payload reference', () => {
    const rows = logAttributeRows({ igw_request_bytes: 128, igw_response_bytes: 64, igw_payload_id: 'internal-id' })
    expect(rows.map(row => [row.label, row.value])).toEqual([
      ['发送字节数', '128'],
      ['响应字节数', '64'],
    ])
  })

  it('uses Chinese labels when reliable and preserves the original key for unknown fields', () => {
    const keys = [
      'igw_task_id', 'igw_http_status_code', 'endpoint', 'queueDelayMs',
      'exception.type', 'service_instance_id', 'new_vendor_field', '现场字段',
    ]
    const labels = keys.map(logAttributeLabel)

    expect(labels).toEqual([
      '任务编号', '接口状态码', '目标地址', '排队延迟毫秒',
      '异常类型', '服务实例编号', 'new_vendor_field', '现场字段',
    ])
    expect(logAttributeLabel('unmapped.otel.attribute')).toBe('unmapped.otel.attribute')
  })

  it('removes OpenObserve envelope fields that are already presented in the event header', () => {
    const rows = logAttributeRows({
      _timestamp: '2026-09-13T08:00:00Z', body: '采集完成', severity_text: 'INFO',
      category_name: 'Runtime', trace_id: 'trace', igw_device: 'PLC9',
    })

    expect(rows.map(row => [row.label, row.value])).toEqual([['设备', 'PLC9']])
  })
})
