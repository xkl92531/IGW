import { flushPromises, shallowMount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../api', () => ({ http: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }, api: {
  anomalies: vi.fn(), connectionDiagnostics: vi.fn(), tagFaults: vi.fn(), health: vi.fn(),
} }))
vi.mock('vue-router', () => ({
  useRoute: () => ({ query: {} }), useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), onBeforeRouteLeave: vi.fn(),
}))
vi.mock('./MesFlow.vue', () => ({ default: { name: 'MesFlow', render: () => null } }))

import { api } from '../api'
import { clearSession, session, type AccountDto } from '../auth'
import { i18n } from '../i18n'
import Monitor from './Monitor.vue'

const fault = { tagId: 'L1.PLC.DB1.Flag', address: 'DB1.DBX0.0', channel: 'L1', cause: '地址越界', suggestion: '改地址',
  quality: 'Bad', active: true, occurrenceCount: 1, isolated: false, firstSeenAt: '', lastSeenAt: '' }
function account(permissions: Record<string, any>, isAdmin = false): AccountDto {
  return { username: 'op', displayName: '', isAdmin, mustChangePassword: false, disabled: false, createdUtc: '', permissions }
}
async function mountMonitor() {
  const wrapper = shallowMount(Monitor, { global: {
    stubs: { ElTable: { template: '<div class="table-stub"><slot /></div>' }, ElTableColumn: { props: ['label'], template: '<div class="column-stub" :data-label="label" />' } },
    directives: { loading: {} }, config: { globalProperties: { $t: i18n.t } as any },
  } })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.mocked(api.anomalies).mockResolvedValue({ entries: [], channels: [] } as any)
  vi.mocked(api.connectionDiagnostics).mockResolvedValue({ active: [], recovered: [] } as any)
  vi.mocked(api.tagFaults).mockResolvedValue({ active: [fault], recovered: [], trackedCount: 1, overflowCount: 0 } as any)
  vi.mocked(api.health).mockResolvedValue({} as any)
})
afterEach(() => { clearSession(); vi.clearAllMocks() })

describe('监控中心只读权限', () => {
  it('工程配置只能看时隐藏「移动到…」操作列和移动对话框，打不开移动', async () => {
    session.account = account({ monitor: 'Edit', project: 'View' })
    const wrapper = await mountMonitor()
    expect(wrapper.findAll('.column-stub').map(c => c.attributes('data-label'))).not.toContain('操作')
    expect(wrapper.findComponent({ name: 'MoveTagDialog' }).exists()).toBe(false)
    const state = (wrapper.vm.$ as any).setupState
    state.openMove(fault)
    expect(state.moveDialogOpen).toBe(false)
    wrapper.unmount()
  })

  it('工程配置可修改(或管理员)时保留移动点位', async () => {
    session.account = account({ monitor: 'View' }, true)
    const wrapper = await mountMonitor()
    expect(wrapper.findAll('.column-stub').map(c => c.attributes('data-label'))).toContain('操作')
    expect(wrapper.findComponent({ name: 'MoveTagDialog' }).exists()).toBe(true)
    const state = (wrapper.vm.$ as any).setupState
    state.openMove(fault)
    expect(state.moveDialogOpen).toBe(true)
    expect(state.movingTagPath).toBe(fault.tagId)
    wrapper.unmount()
  })
})
