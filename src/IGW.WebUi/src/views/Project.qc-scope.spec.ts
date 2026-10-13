import { createPinia } from 'pinia'
import { shallowMount, type VueWrapper } from '@vue/test-utils'
import ElementPlus, { ElMessage } from 'element-plus'
import { defineComponent, nextTick, onMounted } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearSession, session } from '../auth'

const mocks = vi.hoisted(() => ({ project: vi.fn(), summary: vi.fn(), mutate: vi.fn() }))
vi.mock('../api', async importOriginal => {
  const original = await importOriginal<typeof import('../api')>()
  return { ...original, api: { ...original.api, project: mocks.project,
    health: vi.fn().mockResolvedValue({ tags: 1, devices: 1 }), drivers: vi.fn().mockResolvedValue([]),
    networkAdapters: vi.fn().mockResolvedValue([]), uiPreferences: vi.fn().mockResolvedValue({ projectAutoSave: false }),
  } }
})
vi.mock('../features/project/api', async importOriginal => {
  const original = await importOriginal<typeof import('../features/project/api')>()
  return { ...original, projectApi: { ...original.projectApi, summary: mocks.summary, mutate: mocks.mutate } }
})
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }), onBeforeRouteLeave: vi.fn() }))
import Project from './Project.vue'

const opened: { q: string; preferred: string }[] = []
let activeScope = ''
const PointsStub = defineComponent({
  name: 'MockPoints', props: { q: { default: '' }, preferredScope: { default: '' } },
  setup(props, { expose }) {
    expose({ getActiveScopePath: () => activeScope || props.q })
    onMounted(() => opened.push({ q: props.q, preferred: props.preferredScope }))
    return {}
  }, template: '<div class="mock-points" />',
})
const wrappers: VueWrapper[] = []
async function setup(readonly = false) {
  mocks.project.mockResolvedValue({ rev: 'r1', proj: {
    name: '工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {}, mesUpload: { tasks: [] },
    channels: [{ id: 'c1', name: 'PLC10', driverId: 'Simulator', settings: {}, devices: [{
      id: 'd1', name: 'PLC10', endpoint: '', settings: {}, tags: [], groups: [{
        id: 'g1', name: 'DB2050_HeatBeat/Time', subGroups: [],
        tags: [{ id: 't1', name: 'HeatBeat', address: 'D0', dataType: 'int16', access: 'readWrite', scanRateMs: 200 }],
      }],
    }] }],
  } })
  mocks.summary.mockResolvedValue({ totalTagCount: 1, fullProjectSupported: true })
  mocks.mutate.mockResolvedValue({ revision: 'r2' })
  session.account = { username: readonly ? 'viewer' : 'admin', isAdmin: !readonly, displayName: '',
    disabled: false, mustChangePassword: false, createdUtc: '', permissions: {} }
  const wrapper = shallowMount(Project, { global: {
    plugins: [createPinia(), ElementPlus], mocks: { $t: (key: string) => key },
    stubs: { Points: PointsStub, WorkWindow: { props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' },
      ElAutoResizer: { template: '<div />' }, ElDialog: true },
  } })
  wrappers.push(wrapper)
  const state = (wrapper.vm.$ as any).setupState
  await vi.waitFor(() => expect(state.proj).toBeTruthy())
  return { wrapper, state }
}
function node(state: any, kind: string): any {
  const visit = (list: any[]): any => {
    for (const item of list) { if (item.kind === kind) return item; const child = visit(item.children || []); if (child) return child }
  }
  return visit(state.treeData)
}
function renameChannel(state: any) {
  const channel = state.proj.channels[0]
  channel.name = 'PLC_Test'
  state.migrateMesRefs('PLC10', 'PLC_Test')
  state.markDirty(); state.bump()
}
function pendingSave() {
  let resolve!: (value: any) => void
  mocks.mutate.mockImplementationOnce(() => new Promise(done => { resolve = done }))
  return () => resolve({ revision: 'r2' })
}
beforeEach(() => {
  vi.spyOn(ElMessage, 'error').mockImplementation(() => ({ close: () => {} }))
  vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close: () => {} }))
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount()); opened.length = 0; activeScope = ''
  clearSession(); vi.resetAllMocks(); vi.restoreAllMocks()
})

describe('QC scopes follow saved project renames', () => {
  it('saves a renamed channel before mounting the point monitor', async () => {
    const { state } = await setup()
    renameChannel(state)
    const finish = pendingSave()
    const opening = state.quickClient(node(state, 'group'))
    await vi.waitFor(() => expect(mocks.mutate).toHaveBeenCalledOnce())
    expect(opened).toEqual([])
    expect(state.qcPreparing).toBe(true)
    finish(); await opening; await nextTick()
    expect(opened).toEqual([{ q: 'PLC_Test.PLC10.DB2050_HeatBeat/Time', preferred: '' }])
    expect(state.dirty).toBe(false)
  })

  it('rebases an already open channel scope while retaining its selected group', async () => {
    const { wrapper, state } = await setup()
    await state.quickClient(node(state, 'channel')); await nextTick()
    activeScope = 'PLC10.PLC10.DB2050_HeatBeat/Time'
    renameChannel(state); await nextTick()
    expect(wrapper.find('.mock-points').exists()).toBe(false)
    await state.saveApply(true); await nextTick()
    expect(opened[opened.length - 1]).toEqual({ q: 'PLC_Test', preferred: 'PLC_Test.PLC10.DB2050_HeatBeat/Time' })
  })

  it('keeps a global QC root but restores the renamed active group rather than a different PLC', async () => {
    const { wrapper, state } = await setup()
    await state.quickClient({ kind: 'project' }); await nextTick()
    activeScope = 'PLC10.PLC10.DB2050_HeatBeat/Time'
    renameChannel(state); await nextTick()
    expect(wrapper.find('.mock-points').exists()).toBe(false)
    await state.saveApply(true); await nextTick()
    expect(opened[opened.length - 1]).toEqual({ q: '', preferred: 'PLC_Test.PLC10.DB2050_HeatBeat/Time' })
  })

  it('does not open a renamed scope when saving fails', async () => {
    const { state } = await setup()
    renameChannel(state)
    mocks.mutate.mockRejectedValueOnce(new Error('save rejected'))
    await state.quickClient(node(state, 'group')); await nextTick()
    expect(opened).toEqual([])
    expect(state.qcOpen).toBe(false)
    expect(ElMessage.error).toHaveBeenCalledWith(expect.stringContaining('尚未保存'))
  })

  it('does not reopen a QC window closed while saving', async () => {
    const { state } = await setup()
    renameChannel(state)
    const finish = pendingSave()
    const opening = state.quickClient(node(state, 'group'))
    await vi.waitFor(() => expect(mocks.mutate).toHaveBeenCalledOnce())
    state.qcOpen = false; await nextTick()
    finish(); await opening; await nextTick()
    expect(opened).toEqual([])
    expect(state.qcOpen).toBe(false)
  })

  it('opens a clean project for a read-only account without saving', async () => {
    const { state } = await setup(true)
    await state.quickClient(node(state, 'group')); await nextTick()
    expect(mocks.mutate).not.toHaveBeenCalled()
    expect(opened).toEqual([{ q: 'PLC10.PLC10.DB2050_HeatBeat/Time', preferred: '' }])
  })

  it('clears pending scope changes when reloading and discarding a failed rename', async () => {
    const { state } = await setup()
    const savedProject = JSON.parse(JSON.stringify(state.proj))
    await state.quickClient(node(state, 'channel')); await nextTick()
    activeScope = 'PLC10.PLC10.DB2050_HeatBeat/Time'
    renameChannel(state)
    mocks.mutate.mockRejectedValueOnce(new Error('save rejected'))
    await state.saveApply(true)
    mocks.project.mockResolvedValueOnce({ proj: savedProject, rev: 'r1' })
    await state.load(); await nextTick()
    expect(state.qcPreparing).toBe(false)
    expect(state.qcOpen).toBe(false)
    await state.quickClient(node(state, 'group')); await nextTick()
    expect(opened[opened.length - 1].q).toBe('PLC10.PLC10.DB2050_HeatBeat/Time')
  })

  it('updates the MES title-bar QC entry after a channel rename', async () => {
    const { state } = await setup()
    state.mesFlowGroup = 'PLC10.PLC10.DB2050_HeatBeat/Time'
    state.mesFlowOpen = true
    renameChannel(state)
    await state.saveApply(true)
    await state.openQcForFlow(); await nextTick()
    expect(opened[opened.length - 1].q).toBe('PLC_Test.PLC10.DB2050_HeatBeat/Time')
  })

  it('waits for a manual save already in progress before opening QC', async () => {
    const { state } = await setup()
    renameChannel(state)
    const finish = pendingSave()
    const saving = state.saveApply(false)
    await vi.waitFor(() => expect(mocks.mutate).toHaveBeenCalledOnce())
    const opening = state.quickClient(node(state, 'group'))
    await new Promise(resolve => setTimeout(resolve, 75))
    expect(state.qcOpen).toBe(true)
    expect(opened).toEqual([])
    finish(); await saving; await opening; await nextTick()
    expect(opened[opened.length - 1].q).toBe('PLC_Test.PLC10.DB2050_HeatBeat/Time')
    expect(ElMessage.error).not.toHaveBeenCalled()
  })

  it('does not mistake a second unsent rename for the first saved name', async () => {
    const { state } = await setup()
    await state.quickClient(node(state, 'channel')); await nextTick()
    activeScope = 'PLC10.PLC10.DB2050_HeatBeat/Time'
    renameChannel(state)
    const finish = pendingSave()
    const saving = state.saveApply(true)
    await vi.waitFor(() => expect(mocks.mutate).toHaveBeenCalledOnce())
    state.proj.channels[0].name = 'PLC_Third'
    state.migrateMesRefs('PLC_Test', 'PLC_Third'); state.markDirty(); state.bump()
    finish(); await saving
    expect(state.dirty).toBe(true)
    await state.saveApply(true); await nextTick()
    expect(mocks.mutate).toHaveBeenCalledTimes(2)
    expect(opened[opened.length - 1]).toEqual({ q: 'PLC_Third', preferred: 'PLC_Third.PLC10.DB2050_HeatBeat/Time' })
  })

  it('maps old group events from the MES window opened before a channel rename', async () => {
    const { state } = await setup()
    await state.openMesFlowFor(node(state, 'group'))
    renameChannel(state); await state.saveApply(true)
    state.onMesFlowActiveGroupChanged('PLC10.PLC10.DB2050_HeatBeat/Time')
    await state.openQcForFlow(); await nextTick()
    expect(opened[opened.length - 1].q).toBe('PLC_Test.PLC10.DB2050_HeatBeat/Time')
  })

  it('does not reuse a MES QC entry from a replaced project or discard the old flow editor', async () => {
    const { state } = await setup()
    const savedProject = JSON.parse(JSON.stringify(state.proj))
    await state.openMesFlowFor(node(state, 'group'))
    renameChannel(state)
    mocks.project.mockResolvedValueOnce({ proj: savedProject, rev: 'r1' })
    await state.load()
    state.onMesFlowActiveGroupChanged('PLC_Test.PLC10.DB2050_HeatBeat/Time')
    await state.openQcForFlow(); await nextTick()
    expect(state.qcOpen).toBe(false)
    expect(state.mesFlowOpen).toBe(true)
    expect(ElMessage.error).toHaveBeenCalledWith(expect.stringContaining('重新打开采集流程'))
  })
})
