import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('../api', () => ({
  api: {
    projectTagScope: vi.fn(),
    write: vi.fn(),
  },
}))

vi.mock('../utils/realtimeStream', () => ({
  RealtimeStreamClient: class {
    start() {}
    stop() {}
    requestSnapshot() {}
  },
}))

const AutoResizerStub = defineComponent({
  name: 'ElAutoResizer',
  setup(_, { slots }) {
    return () => h('div', slots.default?.({ height: 500, width: 1200 }))
  },
})

const GridStub = defineComponent({
  name: 'ElTableV2',
  props: {
    columns: { type: Array, default: () => [] },
    data: { type: Array, default: () => [] },
  },
  setup(props) {
    return () => h('div', { class: 'grid-stub' }, [
      h('div', { class: 'grid-headers' }, (props.columns as any[]).map((column, columnIndex) =>
        h('div', {
          class: 'grid-header',
          'data-column-key': String(column.key),
          'data-column-width': String(column.width),
        }, column.headerCellRenderer
          ? [column.headerCellRenderer({ column, columns: props.columns, columnIndex, headerIndex: 0 })]
          : [column.title]))),
      ...(props.data as any[]).map(row => h('div', { class: 'grid-row' },
        (props.columns as any[]).map(column => h('span', String(row[column.dataKey] ?? '—'))))),
    ])
  },
})

const TreeStub = defineComponent({
  name: 'ElTreeV2',
  props: { data: { type: Array, default: () => [] } },
  setup(props, { expose, slots }) {
    expose({ setCurrentKey: vi.fn() })
    const renderNode = (node: any): any[] => [
      h('div', { class: 'tree-node-stub' }, slots.default?.({ data: node })),
      ...(node.children || []).flatMap(renderNode),
    ]
    return () => h('div', { class: 'tree-stub' }, (props.data as any[]).flatMap(renderNode))
  },
})

function scope() {
  return {
    projectRevision: 'project-1',
    realtimeRevision: 'realtime-1',
    root: 'PLC9.OP270',
    groups: [{
      name: 'StationEntrance',
      path: 'PLC9.OP270.StationEntrance',
      kind: 'Group' as const,
      description: '进站采集数据',
    }],
    definitions: [{
      id: 'PLC9.OP270.MESFlag',
      name: 'MESFlag',
      address: 'ns=3;s=DB12031.StationEntrance.MESFlag',
      dataType: 'Int16',
      access: 'ReadWrite',
      description: '进站请求标记',
      engUnit: null,
      stringLength: null,
      array: null,
    }],
    nextCursor: null,
    total: 1,
  }
}

function mountQuickClient() {
  return mount(Points, {
    props: { q: 'PLC9.OP270', suspended: true },
    global: {
      directives: { loading: () => {} },
      stubs: {
        ElAutoResizer: AutoResizerStub,
        ElTreeV2: TreeStub,
        ElTableV2: GridStub,
        ElTable: true,
        ElTableColumn: true,
        ElDialog: true,
        ElInput: true,
        ElButton: true,
        ArrayValueViewer: true,
      },
    },
  })
}

describe('Quick Client descriptions and column sizing', () => {
  it('keeps page-loading feedback visible while remaining point pages are pending', async () => {
    let finish!: (value: any) => void
    vi.mocked(api.projectTagScope).mockResolvedValueOnce({ ...scope(), total: 2, nextCursor: 'page-2' })
      .mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = mountQuickClient()
    await flushPromises()
    expect(wrapper.get('.qgrid').attributes('aria-busy')).toBe('true')
    expect(wrapper.text()).toContain('正在加载点位，已读取 1 项')
    finish({ ...scope(), definitions: [{ ...scope().definitions[0], id: 'PLC9.OP270.Second' }] })
    await flushPromises()
    expect(wrapper.get('.qgrid').attributes('aria-busy')).toBe('false')
    expect(wrapper.text()).not.toContain('正在加载点位')
    wrapper.unmount()
  })

  it('clears point-loading feedback after a page request fails', async () => {
    vi.mocked(api.projectTagScope).mockRejectedValueOnce(new Error('network unavailable'))
    const wrapper = mountQuickClient()
    await flushPromises()
    expect(wrapper.get('.qgrid').attributes('aria-busy')).toBe('false')
    wrapper.unmount()
  })
  beforeEach(() => {
    localStorage.clear()
    vi.mocked(api.projectTagScope).mockResolvedValue(scope())
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('shows the field description returned by the project scope', async () => {
    const wrapper = mountQuickClient()
    await flushPromises()

    expect(wrapper.find('.grid-headers').text()).toContain('字段说明')
    expect(wrapper.find('.grid-stub').text()).toContain('进站请求标记')
    wrapper.unmount()
  })

  it('shows group descriptions and complete paths in the left tree', async () => {
    const wrapper = mountQuickClient()
    await flushPromises()

    const treeText = wrapper.find('.tree-stub').text()
    expect(treeText).toContain('StationEntrance')
    expect(treeText).toContain('进站采集数据')
    expect(treeText).toContain('PLC9.OP270.StationEntrance')
    wrapper.unmount()
  })

  it('resizes a grid column by dragging its header and persists the width', async () => {
    const wrapper = mountQuickClient()
    await flushPromises()

    const before = Number(wrapper.get('[data-column-key="itemId"]').attributes('data-column-width'))
    await wrapper.get('[data-column-resizer="itemId"]').trigger('mousedown', { clientX: 400 })
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 480 }))
    window.dispatchEvent(new MouseEvent('mouseup'))
    await flushPromises()

    const after = Number(wrapper.get('[data-column-key="itemId"]').attributes('data-column-width'))
    expect(after).toBe(before + 80)
    expect(JSON.parse(localStorage.getItem('igw.quick-client.column-widths') || '{}').itemId).toBe(before + 80)
    wrapper.unmount()

    const reopened = mountQuickClient()
    await flushPromises()
    expect(Number(reopened.get('[data-column-key="itemId"]').attributes('data-column-width'))).toBe(before + 80)
    reopened.unmount()
  })

  it('ends an active column resize when the browser window loses focus', async () => {
    const wrapper = mountQuickClient()
    await flushPromises()

    const before = Number(wrapper.get('[data-column-key="itemId"]').attributes('data-column-width'))
    await wrapper.get('[data-column-resizer="itemId"]').trigger('mousedown', { clientX: 400 })
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 440 }))
    window.dispatchEvent(new Event('blur'))
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 520 }))
    await flushPromises()

    const after = Number(wrapper.get('[data-column-key="itemId"]').attributes('data-column-width'))
    expect(after).toBe(before + 40)
    expect(JSON.parse(localStorage.getItem('igw.quick-client.column-widths') || '{}').itemId).toBe(before + 40)
    wrapper.unmount()
  })
})
