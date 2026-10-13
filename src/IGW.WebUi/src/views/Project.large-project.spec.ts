import { createPinia } from 'pinia'
import { shallowMount } from '@vue/test-utils'
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
      drivers: vi.fn().mockResolvedValue([]),
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

describe('large project entry', () => {
  afterEach(() => vi.clearAllMocks())

  it('uses the normal project page with on-demand loading for one million configured points', async () => {
    mocks.health.mockResolvedValue({ tags: 0, devices: 0 })
    mocks.summary.mockResolvedValue({
      root: {
        id: 'project-1', kind: 'project', name: '现场工程', description: null,
        actualPath: '现场工程', hasChildren: true, directTagCount: 0, rowVersion: 1, schemaVersion: 1,
      },
      totalTagCount: 1_000_000,
      fullProjectSupported: false,
      configurationDegraded: false,
      configurationDegradedMessage: null,
    })
    mocks.project.mockRejectedValue(new Error('百万点工程不得请求完整工程'))
    mocks.shell.mockResolvedValue({
      project: {
        name: '现场工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {}, mesUpload: { tasks: [] },
        channels: [{ id: 'c1', name: 'C', driverId: 'Siemens.S7', settings: {}, devices: [
          { id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, groups: [], tags: [] },
        ] }],
      },
      lazy: { d1: { groups: 3, tags: 0, totalTags: 1_000_000 } },
      rev: '"project-1"',
    })

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
    expect(mocks.summary).toHaveBeenCalledOnce()
    expect(mocks.project).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
