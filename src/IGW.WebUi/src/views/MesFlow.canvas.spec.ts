import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import MesFlow from './MesFlow.vue'
import { api, http } from '../api'
import { i18n } from '../i18n'
import { session } from '../auth'

const project = {
  channels: [{
    name: 'Line',
    driver: 'SiemensTcp',
    devices: [{
      name: 'PLC',
      groups: [{
        name: 'OP200Data',
        tags: [{ name: 'DataReady', dataType: 'Bool' }],
        subGroups: [],
      }, {
        name: 'OP090_1',
        tags: [{ name: 'Request', dataType: 'Bool' }],
        subGroups: [],
      }],
    }],
  }],
  mesUpload: {
    tasks: [{
      id: 'task-1',
      name: 'OP200Data',
      groupRef: 'Line.PLC.OP200Data',
      deviceRef: 'Line.PLC',
      enabled: false,
      flow: {
        nodes: [{
          id: 'upload-1',
          type: 'Upload',
          title: '上报',
          x: 80,
          y: 80,
          inSide: 'Left',
          outSide: 'Right',
          payload: {
            mode: 'FieldMapping',
            fields: [{ tagId: 'Line.PLC.OP200Data.DataReady', fieldName: 'DataReady' }],
            structs: [],
            append: {},
          },
          http: {
            url: '',
            method: 'POST',
            timeoutSec: 30,
            successKey: 'httpStatusCode',
            successValue: '200',
            bodyTemplate: '',
          },
          captures: [],
          writeBack: { fromResponse: [], manual: [] },
        }],
        edges: [],
      },
    }],
  },
}

vi.mock('../api', () => ({
  http: {
    get: vi.fn(async () => ({ data: { items: [], total: 0, offset: 0, limit: 100 }, headers: { etag: 'rev-1' } })),
    post: vi.fn(async () => ({ data: { applied: true, revision: '"project-2"' }, headers: { etag: '"project-2"' } })),
  },
  setActiveProjectRevision: vi.fn(),
  api: {
    projectTagScope: vi.fn(),
    project: vi.fn(async () => ({ proj: structuredClone(project), rev: 'rev-1' })),
    mes: vi.fn(async () => ({ tasks: [] })),
    mesTimeline: vi.fn(async () => []),
    parameterOptions: vi.fn(async () => ({ items: [], truncated: false })),
    putMesUpload: vi.fn(async () => ({ rev: 'rev-2' })),
  },
}))

describe('MES flow canvas stays responsive', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    session.account = { username: 'admin', displayName: '', isAdmin: true, mustChangePassword: false, disabled: false, createdUtc: '', permissions: {} }
    i18n.setLocale('zh-CN')
    vi.mocked(http.get).mockReset()
    vi.mocked(http.get).mockRejectedValue({ response: { status: 404 } })
  })

  async function view() {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    return { wrapper, state: (wrapper.vm.$ as any).setupState }
  }

  it('auto layout terminates on a flow that loops back to an earlier node', async () => {
    const { wrapper, state } = await view()
    state.flow.nodes = [
      { id: 't', type: 'Trigger', x: 0, y: 0 }, { id: 'a', type: 'Upload', x: 0, y: 0 },
      { id: 'b', type: 'Branch', x: 0, y: 0 }, { id: 'c', type: 'Writeback', x: 0, y: 0 },
    ]
    state.flow.edges = [
      { from: 't', fromPort: 'out', to: 'a' }, { from: 'a', fromPort: 'out', to: 'b' },
      { from: 'b', fromPort: 'fail', to: 'a' }, { from: 'b', fromPort: 'success', to: 'c' },
    ]
    state.autoLayout()
    const x = Object.fromEntries(state.flow.nodes.map((n: any) => [n.id, n.x]))
    expect(x.t).toBeLessThan(x.a)
    expect(x.a).toBeLessThan(x.b)
    expect(x.b).toBeLessThan(x.c)
    wrapper.unmount()
  })

  it('ends a node drag when the mouse is released outside the canvas', async () => {
    const { wrapper, state } = await view()
    expect(wrapper.find('.cfg').exists()).toBe(false)
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    expect(wrapper.find('.cfg').exists()).toBe(true)
    expect(state.drag.mode).toBe('node')
    window.dispatchEvent(new MouseEvent('mouseup', { clientX: 2000, clientY: 2000 }))
    expect(state.drag.mode).toBe('')
    await wrapper.find('svg.paper').trigger('mousedown', { clientX: 400, clientY: 400 })
    window.dispatchEvent(new MouseEvent('mouseup', { clientX: 400, clientY: 400 }))
    expect(wrapper.find('.cfg').exists()).toBe(false)
    wrapper.unmount()
  })

  it('records canvas operations and restores the flow to before one of them', async () => {
    vi.useFakeTimers()
    try {
      const { wrapper, state } = await view()
      const before = state.flow.nodes.map((n: any) => n.id)
      state.newNode('Delay')
      vi.advanceTimersByTime(2000)
      state.delSelected()
      vi.advanceTimersByTime(2000)
      const labels = state.flowHistory.entries.value.map((e: any) => e.label)
      expect(labels).toEqual(['删除节点', '添加节点'])
      const added = state.flow.nodes.length
      state.restoreHistory(state.flowHistory.entries.value[0])   // 回退“删除节点”：节点回来
      expect(state.flow.nodes).toHaveLength(added + 1)
      expect(state.flow.nodes.slice(0, before.length).map((n: any) => n.id)).toEqual(before)
      expect(state.dirty).toBe(true)
      expect(state.flowHistory.entries.value[0].label).toContain('回退')
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })
})
