import { createPinia } from 'pinia'
import { shallowMount } from '@vue/test-utils'
import { ElMessage } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'

const mocks = vi.hoisted(() => ({
  health: vi.fn(),
  project: vi.fn(),
  summary: vi.fn(),
  shell: vi.fn(),
}))

vi.mock('../api', async importOriginal => {
  const original = await importOriginal<typeof import('../api')>()
  return {
    ...original,
    api: {
      ...original.api,
      health: mocks.health,
      project: mocks.project,
      drivers: vi.fn().mockResolvedValue([{ driverId: 'Modbus.Tcp', displayName: 'Modbus TCP', deviceSettings: [], channelSettings: [] }]),
      networkAdapters: vi.fn().mockResolvedValue([]),
    },
  }
})

vi.mock('../features/project/api', async importOriginal => {
  const original = await importOriginal<typeof import('../features/project/api')>()
  return {
    ...original,
    projectApi: { ...original.projectApi, summary: mocks.summary, shell: mocks.shell },
  }
})

import Project from './Project.vue'

async function settle() {
  for (let index = 0; index < 6; index++) {
    await Promise.resolve()
    await nextTick()
  }
}

describe('channel of an unsupported driver', () => {
  afterEach(() => vi.clearAllMocks())

  it('renders the old channel with an unsupported driver name and blocks new devices under it', async () => {
    const toolChannel = {
      id: 'c1', name: 'Tool', driverId: 'Legacy.Tcp', settings: {}, devices: [
        { id: 'd1', name: 'Station', endpoint: '10.0.0.5:4545', settings: { keepAliveMs: '5000' }, groups: [], tags: [
          { name: 'Torque', address: 'Torque', dataType: 'double', access: 'read' },
        ] },
      ],
    }
    mocks.health.mockResolvedValue({ tags: 1, devices: 1 })
    mocks.summary.mockResolvedValue({
      root: {
        id: 'project-1', kind: 'project', name: '旧工程', description: null,
        actualPath: '旧工程', hasChildren: true, directTagCount: 0, rowVersion: 1, schemaVersion: 1,
      },
      totalTagCount: 1,
      fullProjectSupported: false,
      configurationDegraded: false,
      configurationDegradedMessage: null,
    })
    mocks.shell.mockResolvedValue({
      project: { name: '旧工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {}, mesUpload: { tasks: [] }, channels: [toolChannel] },
      lazy: {},
      rev: '"project-1"',
    })
    const warning = vi.spyOn(ElMessage, 'warning').mockImplementation((() => undefined) as any)

    const wrapper = shallowMount(Project, {
      global: {
        plugins: [createPinia()],
        mocks: { $t: (key: string) => key },
        stubs: { ElAutoResizer: { template: '<div />' }, ElTableColumn: { template: '<div />' } },
      },
    })
    await settle()
    await vi.waitFor(() => expect(mocks.shell).toHaveBeenCalledOnce())
    await settle()

    expect(wrapper.find('.wrap').exists()).toBe(true)
    const state = (wrapper.vm.$ as any).setupState
    expect(state.driverName('Legacy.Tcp')).toBe('Legacy.Tcp(不支持)')
    expect(state.driverName('Modbus.Tcp')).toBe('Modbus TCP')
    expect(state.treeNodeTitle({ kind: 'channel', label: 'Tool', ref: toolChannel })).toContain('Legacy.Tcp(不支持)')

    await state.openDeviceWizard(toolChannel)
    await state.openDeviceDialog(null, toolChannel)
    expect(warning).toHaveBeenCalledTimes(2)
    expect(state.wiz.open).toBe(false)
    expect(toolChannel.devices).toHaveLength(1)
    warning.mockRestore()
    wrapper.unmount()
  })
})
