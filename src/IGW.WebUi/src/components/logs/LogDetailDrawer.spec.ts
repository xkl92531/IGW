import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import LogDetailDrawer from './LogDetailDrawer.vue'

const entry = {
  timestamp: '2026-09-13T14:00:00Z', level: 'Information', category: 'interface', message: 'HTTP 接口请求完成',
  traceId: 'trace-1', payloadId: 'payload-1', attributes: {
    igw_module: 'northbound', igw_feature: 'rest_api', instrumentation_library_name: 'IgwRuntime',
  },
}

const payload = {
  id: 'payload-1', request: '{"scope":"PLC8.OP140"}', response: '{"items":[]}',
  requestTruncated: false, responseTruncated: false, split: true,
}

describe('LogDetailDrawer', () => {
  it('shows the request and response before technical fields and gives long native keys their own wrapping cell', () => {
    const wrapper = mount(LogDetailDrawer, {
      props: { entry: entry as any, payload: payload as any },
      global: { stubs: {
        ElDrawer: { template: '<aside><slot /></aside>' },
        ElButton: { template: '<button><slot /></button>' },
        ElPagination: { template: '<nav />' },
      } },
    })

    const exchange = wrapper.get('[data-testid="actual-http-exchange"]')
    const fields = wrapper.get('[data-testid="structured-log-fields"]')
    expect(exchange.element.compareDocumentPosition(fields.element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    // 只显示发送内容和返回内容，JSON 自动缩进；不再混入请求行和 Content-Type。
    expect(wrapper.get('[data-testid="exchange-request"]').text()).toContain('发送内容')
    expect(wrapper.get('[data-testid="exchange-request"]').text()).toContain('"scope": "PLC8.OP140"')
    expect(wrapper.get('[data-testid="exchange-response"]').text()).toContain('返回内容')
    expect(exchange.text()).not.toContain('Content-Type')
    const nativeKey = wrapper.get('[data-testid="log-attribute-key"][title="instrumentation_library_name"]')
    expect(nativeKey.text()).toBe('instrumentation_library_name')
    expect(nativeKey.classes()).toContain('attribute-key')
  })

  it('explains why an older interface event has no request or response content', () => {
    const wrapper = mount(LogDetailDrawer, {
      props: { entry: { ...entry, payloadId: null } as any, payload: null },
      global: { stubs: {
        ElDrawer: { template: '<aside><slot /></aside>' },
        ElButton: { template: '<button><slot /></button>' },
        ElPagination: { template: '<nav />' },
      } },
    })

    expect(wrapper.get('[data-testid="actual-http-exchange"]').text()).toContain('历史记录无法补录')
  })

  it('uses one scrolling surface when the actual exchange is expanded', async () => {
    const longPayload = { ...payload, request: JSON.stringify({ values: Array.from({ length: 40 }, (_, i) => i) }) }
    const wrapper = mount(LogDetailDrawer, {
      props: { entry: entry as any, payload: longPayload as any },
      global: { stubs: {
        ElDrawer: { template: '<aside><slot /></aside>' },
        ElButton: { template: '<button><slot /></button>' },
        ElPagination: { template: '<nav />' },
      } },
    })

    // 报文默认展开，直接能看到全文；可以手动收起。
    const container = wrapper.get('.payload-shell')
    const content = wrapper.get('.payload-shell [data-testid="log-value-content"]')
    expect(container.classes()).not.toContain('expanded')
    expect(content.classes()).toContain('expanded')
    await wrapper.get('.payload-shell [data-testid="toggle-log-value"]').trigger('click')
    expect(wrapper.get('.payload-shell [data-testid="log-value-content"]').classes()).toContain('collapsed')
  })

  describe('PLC tag values', () => {
    const stubs = { ElDrawer: { template: '<aside><slot /></aside>' }, ElButton: { template: '<button><slot /></button>' } }
    const values = JSON.stringify({
      total: 250, omitted: 247,
      points: [['PLC6.D1.OP051.Speed', 12.5], ['PLC6.D1.OP051.Barcode', 'SN-778'], ['PLC6.D1.OP052.Run', null, 'BadTimeout']],
    })
    const collection = {
      timestamp: '2026-10-08T08:00:00Z', level: 'Information', category: 'collection', message: '采集批次汇总', traceId: null, payloadId: null,
      attributes: { igw_module: 'acquisition', igw_feature: 'plc_scan', igw_device: 'PLC6.D1', igw_tag_values: values },
    }

    it('shows the values of a collection record as a tag / value / quality table and notes omitted tags', () => {
      const wrapper = mount(LogDetailDrawer, { props: { entry: collection as any }, global: { stubs } })

      const section = wrapper.get('[data-testid="log-tag-values"]')
      expect(section.text()).toContain('点位值')
      expect(section.findAll('th').map(cell => cell.text())).toEqual(['点位', '值', '质量'])
      const rows = wrapper.findAll('[data-testid="log-tag-value-row"]').map(row => row.findAll('td').map(cell => cell.text()))
      expect(rows).toEqual([
        ['PLC6.D1.OP051.Speed', '12.5', '正常'],
        ['PLC6.D1.OP051.Barcode', 'SN-778', '正常'],
        ['PLC6.D1.OP052.Run', '—', 'BadTimeout'],
      ])
      expect(wrapper.findAll('[data-testid="log-tag-value-row"]')[2].classes()).toContain('bad')
      expect(wrapper.get('[data-testid="log-tag-values-omitted"]').text()).toContain('247')
      // 原始 JSON 不再在结构化字段里重复一遍，其他字段照常显示
      const keys = wrapper.findAll('[data-testid="log-attribute-key"]').map(key => key.attributes('title'))
      expect(keys).not.toContain('igw_tag_values')
      expect(keys).toContain('igw_device')
    })

    it('marks the tags under the project path the log was opened for', () => {
      const wrapper = mount(LogDetailDrawer, { props: { entry: collection as any, highlightPath: 'PLC6.D1.OP051' }, global: { stubs } })

      const marked = wrapper.findAll('[data-testid="log-tag-value-row"]').map(row => row.classes().includes('highlighted'))
      expect(marked).toEqual([true, true, false])
    })

    it('says when only bad tags were recorded and keeps unreadable values as a plain field', () => {
      const bad = { ...collection, attributes: { ...collection.attributes, igw_tag_values: JSON.stringify({ total: 1, badOnly: true, points: [['PLC6.D1.X', null, 'BadCommFailure']] }) } }
      const badOnly = mount(LogDetailDrawer, { props: { entry: bad as any }, global: { stubs } })
      expect(badOnly.get('[data-testid="log-tag-values"]').text()).toContain('只列出质量异常的点位')
      expect(badOnly.find('[data-testid="log-tag-values-omitted"]').exists()).toBe(false)

      const broken = { ...collection, attributes: { ...collection.attributes, igw_tag_values: '{"total":3,"points":[["PLC6' } }
      const fallback = mount(LogDetailDrawer, { props: { entry: broken as any }, global: { stubs } })
      expect(fallback.find('[data-testid="log-tag-values"]').exists()).toBe(false)
      expect(fallback.findAll('[data-testid="log-attribute-key"]').map(key => key.attributes('title'))).toContain('igw_tag_values')
    })
  })
})
