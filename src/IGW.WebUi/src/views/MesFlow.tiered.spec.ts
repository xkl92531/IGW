import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import MesFlow from './MesFlow.vue'
import { i18n } from '../i18n'
import { session } from '../auth'

const project = {
  channels: [{
    name: 'Line',
    driver: 'SiemensTcp',
    devices: [{
      name: 'PLC',
      groups: [{ name: 'OP200Data', tags: [{ name: 'DataReady', dataType: 'Bool' }], subGroups: [] }],
    }],
  }],
  mesUpload: {
    tasks: [{
      id: 'task-1',
      name: 'OP200Data',
      groupRef: 'Line.PLC.OP200Data',
      deviceRef: 'Line.PLC',
      enabled: false,
      collectIntervalMs: 1000,
      trigger: { mode: 'Interval', intervalMs: 1000 },
      flow: {
        nodes: [{
          id: 'trigger-1', type: 'Trigger', title: '触发', x: 80, y: 80, inSide: 'Left', outSide: 'Right',
          conditions: [], branches: [], captures: [],
          payload: { mode: 'FieldMapping', fields: [], structs: [], append: {} },
          http: { url: '', method: 'POST', timeoutSec: 30, successKey: 'httpStatusCode', successValue: '200', bodyTemplate: '' },
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

async function open() {
  const wrapper = mount(MesFlow, {
    props: { focusGroup: 'Line.PLC.OP200Data', design: true },
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  await flushPromises()
  const state = (wrapper.vm.$ as any).setupState
  state.selIds = new Set(['trigger-1'])
  await nextTick()
  return wrapper
}

describe('分层采集开关', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    session.account = { username: 'admin', displayName: '', isAdmin: true, mustChangePassword: false, disabled: false, createdUtc: '', permissions: {} }
  })

  it('任务级三态写回 true/false/null，未设置时按跟随全局', async () => {
    const wrapper = await open()
    const state = (wrapper.vm.$ as any).setupState
    // 工程里没写这个字段时按「跟随全局」显示
    expect(state.tieredAcquisition).toBe('inherit')

    state.tieredAcquisition = 'off'
    expect(state.task.tieredAcquisition).toBe(false)
    state.tieredAcquisition = 'on'
    expect(state.task.tieredAcquisition).toBe(true)
    // 回到「跟随全局」必须清成 null，否则运行时仍按上一次的显式设置走
    state.tieredAcquisition = 'inherit'
    expect(state.task.tieredAcquisition).toBeNull()
    wrapper.unmount()
  }, 15000)

  it('全局开关默认开，关掉后写进 mesUpload 并标脏', async () => {
    const wrapper = await open()
    const state = (wrapper.vm.$ as any).setupState
    expect(state.globalTieredAcquisition).toBe(true)
    expect(wrapper.find('[data-test="mes-global-tier"]').exists()).toBe(true)

    state.globalTieredAcquisition = false
    state.md()
    await nextTick()
    expect(state.proj.mesUpload.tieredAcquisition).toBe(false)
    expect(state.dirty).toBe(true)
    wrapper.unmount()
  }, 15000)
})
